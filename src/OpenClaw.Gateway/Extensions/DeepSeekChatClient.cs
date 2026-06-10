using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace OpenClaw.Gateway.Extensions;

/// <summary>
/// A custom <see cref="IChatClient"/> for DeepSeek's OpenAI-compatible API that correctly
/// handles the <c>reasoning_content</c> field present in DeepSeek thinking-capable models
/// (e.g. deepseek-reasoner, deepseek-v4-pro).
///
/// The standard OpenAI SDK silently drops <c>reasoning_content</c>. This client:
/// - Emits <see cref="TextReasoningContent"/> items in both streaming and non-streaming responses.
/// - Serializes <c>reasoning_content</c> back into assistant messages when building the
///   request history, which is required by the DeepSeek API for tool-call turns.
/// - Optionally injects <c>{"thinking": {"type": "enabled"}}</c> into every request body.
/// </summary>
internal sealed class DeepSeekChatClient : IChatClient
{
    private const string DefaultEndpoint = "https://api.deepseek.com/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly HttpClient _httpClient;
    private readonly bool _enableThinking;

    private static bool SupportsThinking(string model)
        => model.Contains("reasoner", StringComparison.OrdinalIgnoreCase)
        || model.Contains("thinking", StringComparison.OrdinalIgnoreCase)
        || model.Contains("r1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(model, "deepseek-v4-pro", StringComparison.OrdinalIgnoreCase)
        || string.Equals(model, "deepseek-v4-flash", StringComparison.OrdinalIgnoreCase);

    public DeepSeekChatClient(string model, string apiKey, string? endpoint, HttpClient httpClient, bool enableThinking = true)
    {
        _model = model;
        _apiKey = apiKey;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');
        _httpClient = httpClient;
        _enableThinking = enableThinking;
    }

    // -------------------------------------------------------------------------
    // IChatClient implementation
    // -------------------------------------------------------------------------

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = BuildRequestBody(messages, options, stream: false);
        using var request = CreateRequest(body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, content);

        var completion = JsonSerializer.Deserialize<CompletionResponse>(content, JsonOptions)
            ?? throw new JsonException("DeepSeek returned an empty chat completion response.");

        var choice = completion.Choices?.FirstOrDefault();
        var message = choice?.Message ?? new MessageDto();
        var chatMessage = BuildAssistantMessage(message);

        return new ChatResponse(chatMessage)
        {
            ResponseId = completion.Id,
            ModelId = completion.Model ?? _model,
            CreatedAt = UnixToOffset(completion.Created),
            FinishReason = MapFinishReason(choice?.FinishReason),
            Usage = MapUsage(completion.Usage),
            RawRepresentation = content
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildRequestBody(messages, options, stream: true);

        // Add stream_options so the final chunk carries usage info.
        body["stream_options"] = new JsonObject { ["include_usage"] = true };

        using var request = CreateRequest(body);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, err);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        string? responseId = null;
        string? modelId = null;
        DateTimeOffset? createdAt = null;
        var messageId = Guid.NewGuid().ToString("N");
        var toolAccumulator = new Dictionary<int, ToolCallBuilder>();
        var emittedToolCalls = false;

        await foreach (var line in ReadSseAsync(stream, cancellationToken))
        {
            if (line.Equals("[DONE]", StringComparison.Ordinal))
                break;

            ChunkResponse chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChunkResponse>(line, JsonOptions)
                    ?? throw new JsonException("DeepSeek returned an empty stream chunk.");
            }
            catch (JsonException ex)
            {
                throw new JsonException($"Invalid DeepSeek stream payload: {Trim600(line)}", ex);
            }

            responseId ??= chunk.Id;
            modelId = chunk.Model ?? modelId ?? _model;
            createdAt ??= UnixToOffset(chunk.Created);

            // Usage-only chunk (emitted by stream_options).
            if (chunk.Usage != null)
            {
                yield return new ChatResponseUpdate
                {
                    ResponseId = responseId,
                    MessageId = messageId,
                    ModelId = modelId,
                    CreatedAt = createdAt,
                    Contents = [new UsageContent(MapUsage(chunk.Usage)!)],
                    RawRepresentation = line
                };
            }

            if (chunk.Choices is not { Count: > 0 })
                continue;

            foreach (var choice in chunk.Choices)
            {
                var delta = choice.Delta;

                // ── reasoning_content delta ──────────────────────────────────
                if (!string.IsNullOrEmpty(delta?.ReasoningContent))
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new TextReasoningContent(delta.ReasoningContent)])
                    {
                        ResponseId = responseId,
                        MessageId = messageId,
                        ModelId = modelId,
                        CreatedAt = createdAt,
                        RawRepresentation = line
                    };
                }

                // ── content delta ────────────────────────────────────────────
                if (!string.IsNullOrEmpty(delta?.Content))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, delta.Content)
                    {
                        ResponseId = responseId,
                        MessageId = messageId,
                        ModelId = modelId,
                        CreatedAt = createdAt,
                        RawRepresentation = line
                    };
                }

                // ── tool call deltas ─────────────────────────────────────────
                AccumulateToolCalls(toolAccumulator, delta?.ToolCalls);

                var finishReason = MapFinishReason(choice.FinishReason);
                if (finishReason == ChatFinishReason.ToolCalls && toolAccumulator.Count > 0)
                {
                    emittedToolCalls = true;
                    yield return BuildToolCallsUpdate(
                        toolAccumulator, responseId, messageId, modelId, createdAt, finishReason, line);
                }
                else if (finishReason != null)
                {
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        ResponseId = responseId,
                        MessageId = messageId,
                        ModelId = modelId,
                        CreatedAt = createdAt,
                        FinishReason = finishReason,
                        RawRepresentation = line
                    };
                }
            }
        }

        // Safety net: if the stream ended without a ToolCalls finish reason.
        if (!emittedToolCalls && toolAccumulator.Count > 0)
        {
            yield return BuildToolCallsUpdate(
                toolAccumulator, responseId, messageId,
                modelId ?? _model, createdAt,
                ChatFinishReason.ToolCalls, null);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    // -------------------------------------------------------------------------
    // Request building
    // -------------------------------------------------------------------------

    private JsonObject BuildRequestBody(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool stream)
    {
        var msgArray = SerializeMessages(messages);
        PrependInstructions(msgArray, options?.Instructions);

        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? _model,
            ["stream"] = stream,
            ["messages"] = msgArray
        };

        // Always send the thinking block explicitly for models that support it.
        // DeepSeek API defaults to enabled, so "disabled" must be sent to actually turn it off.
        var effectiveModel = (string?)body["model"] ?? _model;
        if (SupportsThinking(effectiveModel))
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = _enableThinking ? "enabled" : "disabled"
            };
        }

        if (options == null)
            return body;

        if (options.MaxOutputTokens.HasValue)
            body["max_tokens"] = options.MaxOutputTokens.Value;

        // temperature / top_p / penalties are silently ignored by DeepSeek when thinking
        // is enabled, but we still send them for non-thinking models / disabled thinking.
        if (options.Temperature.HasValue)
            body["temperature"] = (double)options.Temperature.Value;
        if (options.TopP.HasValue)
            body["top_p"] = (double)options.TopP.Value;
        if (options.FrequencyPenalty.HasValue)
            body["frequency_penalty"] = (double)options.FrequencyPenalty.Value;
        if (options.PresencePenalty.HasValue)
            body["presence_penalty"] = (double)options.PresencePenalty.Value;
        if (options.Seed.HasValue)
            body["seed"] = options.Seed.Value;

        if (options.StopSequences is { Count: > 0 })
        {
            var stops = new JsonArray();
            foreach (var s in options.StopSequences)
                stops.Add(s);
            body["stop"] = stops;
        }

        // reasoning_effort passed via AdditionalProperties (e.g. set by MafAgentRuntime).
        if (options.AdditionalProperties?.TryGetValue("reasoning_effort", out var effort) == true
            && effort is string effortStr
            && !string.IsNullOrWhiteSpace(effortStr))
        {
            body["reasoning_effort"] = effortStr;
        }

        // Structured output / JSON mode.
        if (options.ResponseFormat is ChatResponseFormatJson jsonFmt)
        {
            body["response_format"] = jsonFmt.Schema.HasValue
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "response",
                        ["strict"] = true,
                        ["schema"] = JsonNode.Parse(jsonFmt.Schema.Value.GetRawText())
                    }
                }
                : new JsonObject { ["type"] = "json_object" };
        }

        // Tool definitions.
        SerializeTools(body, options);

        return body;
    }

    private static void SerializeTools(JsonObject body, ChatOptions options)
    {
        if (options.Tools is not { Count: > 0 })
            return;

        var tools = new JsonArray();
        foreach (var tool in options.Tools.OfType<AIFunction>())
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.JsonSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined
                        ? JsonNode.Parse(tool.JsonSchema.GetRawText())
                        : new JsonObject { ["type"] = "object" }
                }
            });
        }

        if (tools.Count == 0)
            return;

        body["tools"] = tools;
        body["tool_choice"] = options.ToolMode switch
        {
            NoneChatToolMode => "none",
            RequiredChatToolMode req when !string.IsNullOrWhiteSpace(req.RequiredFunctionName)
                => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = req.RequiredFunctionName }
                },
            RequiredChatToolMode => (JsonNode)JsonValue.Create("required")!,
            _ => (JsonNode)JsonValue.Create("auto")!
        };
    }

    /// <summary>
    /// Serializes chat messages to JSON, preserving <c>reasoning_content</c> on assistant
    /// messages so that DeepSeek's API receives it back in tool-call turns.
    /// </summary>
    private static JsonArray SerializeMessages(IEnumerable<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var msg in messages)
        {
            // Tool results → individual "tool" role messages.
            var toolResults = msg.Contents.OfType<FunctionResultContent>().ToList();
            if (toolResults.Count > 0)
            {
                foreach (var result in toolResults)
                {
                    array.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId,
                        ["content"] = SerializeToolResult(result.Result)
                    });
                }
                continue;
            }

            var role = ToRole(msg.Role);
            var node = new JsonObject { ["role"] = role };

            var text = string.Concat(msg.Contents.OfType<TextContent>().Select(c => c.Text));
            var functionCalls = msg.Contents.OfType<FunctionCallContent>().ToList();

            node["content"] = string.IsNullOrEmpty(text) && functionCalls.Count > 0 ? null : text;

            // ── reasoning_content on assistant messages ──────────────────────
            // Required by DeepSeek API when the previous assistant turn contained tool calls.
            // For non-tool-call turns the API ignores it, so it's safe to always include.
            if (role == "assistant")
            {
                var reasoning = ExtractReasoningText(msg);
                if (!string.IsNullOrEmpty(reasoning))
                    node["reasoning_content"] = reasoning;
            }

            // ── tool_calls on assistant messages ────────────────────────────
            if (functionCalls.Count > 0)
            {
                var toolCalls = new JsonArray();
                foreach (var call in functionCalls)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = JsonSerializer.Serialize(call.Arguments, JsonOptions)
                        }
                    });
                }
                node["tool_calls"] = toolCalls;
            }

            array.Add(node);
        }
        return array;
    }

    private static void PrependInstructions(JsonArray messages, string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions) || HasSystemMessage(messages))
            return;

        messages.Insert(0, new JsonObject
        {
            ["role"] = "system",
            ["content"] = instructions
        });
    }

    private static bool HasSystemMessage(JsonArray messages)
    {
        foreach (var message in messages)
        {
            if (message is JsonObject node &&
                node["role"]?.GetValue<string>().Equals("system", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractReasoningText(ChatMessage message)
    {
        // Prefer TextReasoningContent items.
        var fromTyped = string.Concat(
            message.Contents.OfType<TextReasoningContent>().Select(c => c.Text));
        if (!string.IsNullOrEmpty(fromTyped))
            return fromTyped;

        // Fallback: check AdditionalProperties (e.g. populated by legacy code).
        if (message.AdditionalProperties?.TryGetValue("reasoning_content", out var raw) == true
            && raw is string s && !string.IsNullOrEmpty(s))
            return s;

        return string.Empty;
    }

    private HttpRequestMessage CreateRequest(JsonObject body)
    {
        var uri = _endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? _endpoint
            : $"{_endpoint}/chat/completions";

        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                body.ToJsonString(JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    // -------------------------------------------------------------------------
    // Response mapping
    // -------------------------------------------------------------------------

    private static ChatMessage BuildAssistantMessage(MessageDto message)
    {
        var contents = new List<AIContent>();

        if (!string.IsNullOrEmpty(message.ReasoningContent))
            contents.Add(new TextReasoningContent(message.ReasoningContent));

        if (!string.IsNullOrEmpty(message.Content))
            contents.Add(new TextContent(message.Content));

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Function?.Name))
                    continue;
                contents.Add(new FunctionCallContent(
                    call.Id,
                    call.Function.Name,
                    ParseArguments(call.Function.Arguments)));
            }
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static ChatResponseUpdate BuildToolCallsUpdate(
        Dictionary<int, ToolCallBuilder> toolCalls,
        string? responseId, string messageId, string? modelId,
        DateTimeOffset? createdAt, ChatFinishReason? finishReason, string? rawPayload)
    {
        var contents = toolCalls
            .OrderBy(p => p.Key)
            .Select(p => p.Value)
            .Where(b => !string.IsNullOrWhiteSpace(b.Id) && !string.IsNullOrWhiteSpace(b.Name))
            .Select(b => (AIContent)new FunctionCallContent(
                b.Id!,
                b.Name!,
                ParseArguments(b.Arguments.ToString())))
            .ToList();

        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ResponseId = responseId,
            MessageId = messageId,
            ModelId = modelId,
            CreatedAt = createdAt,
            FinishReason = finishReason,
            RawRepresentation = rawPayload
        };
    }

    private static void AccumulateToolCalls(
        Dictionary<int, ToolCallBuilder> accumulator,
        IReadOnlyList<ToolCallDeltaDto>? deltas)
    {
        if (deltas is not { Count: > 0 })
            return;

        for (var i = 0; i < deltas.Count; i++)
        {
            var delta = deltas[i];
            var index = delta.Index ?? i;
            if (!accumulator.TryGetValue(index, out var builder))
            {
                builder = new ToolCallBuilder();
                accumulator[index] = builder;
            }
            if (!string.IsNullOrWhiteSpace(delta.Id)) builder.Id = delta.Id;
            if (!string.IsNullOrWhiteSpace(delta.Function?.Name)) builder.Name = delta.Function.Name;
            if (!string.IsNullOrEmpty(delta.Function?.Arguments)) builder.Arguments.Append(delta.Function.Arguments);
        }
    }

    // -------------------------------------------------------------------------
    // SSE reader
    // -------------------------------------------------------------------------

    private static async IAsyncEnumerable<string> ReadSseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (ct.IsCancellationRequested) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (string.IsNullOrEmpty(payload)) continue;
            yield return payload;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string ToRole(ChatRole role)
    {
        if (role == ChatRole.System) return "system";
        if (role == ChatRole.Assistant) return "assistant";
        if (role == ChatRole.Tool) return "tool";
        return "user";
    }

    private static ChatFinishReason? MapFinishReason(string? reason) =>
        reason?.ToLowerInvariant() switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "content_filter" => ChatFinishReason.ContentFilter,
            null or "" => null,
            _ => new ChatFinishReason(reason!)
        };

    private static UsageDetails? MapUsage(UsageDto? usage)
    {
        if (usage == null) return null;
        return new UsageDetails
        {
            InputTokenCount = usage.PromptTokens,
            OutputTokenCount = usage.CompletionTokens,
            TotalTokenCount = usage.TotalTokens,
            AdditionalCounts = usage.CompletionTokensDetails?.ReasoningTokens is int rt
                ? new AdditionalPropertiesDictionary<long> { ["reasoning_tokens"] = rt }
                : null
        };
    }

    private static string SerializeToolResult(object? result) =>
        result switch
        {
            null => "null",
            string s => s,
            System.Text.Json.JsonElement e => e.GetRawText(),
            JsonNode n => n.ToJsonString(JsonOptions),
            _ => JsonSerializer.Serialize(result, JsonOptions)
        };

    private static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, object?> { ["value"] = doc.RootElement.Clone() };
            // Return JsonElement.Clone() values so the downstream source-generated
            // CoreJsonContext can serialize them — JsonElement has built-in support
            // whereas converted native types like List<object> do not.
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?> { ["value"] = json };
        }
    }

    private static DateTimeOffset? UnixToOffset(long? unix) =>
        unix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(unix.Value) : null;

    private static void EnsureSuccess(HttpResponseMessage response, string? content)
    {
        if (response.IsSuccessStatusCode) return;
        var msg = string.IsNullOrWhiteSpace(content)
            ? response.ReasonPhrase
            : Trim600(content);
        throw new HttpRequestException(
            $"DeepSeek request failed with HTTP {(int)response.StatusCode}: {msg}",
            null,
            response.StatusCode);
    }

    private static string Trim600(string text)
    {
        var t = text.Trim();
        return t.Length <= 600 ? t : t[..600];
    }

    // -------------------------------------------------------------------------
    // DTO types
    // -------------------------------------------------------------------------

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private sealed class CompletionResponse
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("created")] public long? Created { get; init; }
        [JsonPropertyName("choices")] public List<ChoiceDto>? Choices { get; init; }
        [JsonPropertyName("usage")] public UsageDto? Usage { get; init; }
    }

    private sealed class ChunkResponse
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("created")] public long? Created { get; init; }
        [JsonPropertyName("choices")] public List<StreamChoiceDto>? Choices { get; init; }
        [JsonPropertyName("usage")] public UsageDto? Usage { get; init; }
    }

    private sealed class ChoiceDto
    {
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
        [JsonPropertyName("message")] public MessageDto? Message { get; init; }
    }

    private sealed class StreamChoiceDto
    {
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
        [JsonPropertyName("delta")] public DeltaDto? Delta { get; init; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; init; }
        [JsonPropertyName("tool_calls")] public List<ToolCallDto>? ToolCalls { get; init; }
    }

    private sealed class DeltaDto
    {
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; init; }
        [JsonPropertyName("tool_calls")] public List<ToolCallDeltaDto>? ToolCalls { get; init; }
    }

    private sealed class ToolCallDto
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("function")] public FunctionDto? Function { get; init; }
    }

    private sealed class ToolCallDeltaDto
    {
        [JsonPropertyName("index")] public int? Index { get; init; }
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("function")] public FunctionDeltaDto? Function { get; init; }
    }

    private sealed class FunctionDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("arguments")] public string? Arguments { get; init; }
    }

    private sealed class FunctionDeltaDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("arguments")] public string? Arguments { get; init; }
    }

    private sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; init; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; init; }
        [JsonPropertyName("total_tokens")] public int? TotalTokens { get; init; }
        [JsonPropertyName("completion_tokens_details")] public CompletionTokensDetailsDto? CompletionTokensDetails { get; init; }
    }

    private sealed class CompletionTokensDetailsDto
    {
        [JsonPropertyName("reasoning_tokens")] public int? ReasoningTokens { get; init; }
    }
}
