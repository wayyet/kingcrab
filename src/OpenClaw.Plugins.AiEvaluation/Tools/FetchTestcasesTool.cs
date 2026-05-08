using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Plugins.AiEvaluation.Configs;
using OpenClaw.Plugins.AiEvaluation.Models;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

public sealed class FetchTestcasesTool(AiEvaluationConfig config, TestcaseSandboxConnectionPool pool) : IToolWithContext
{
    public string Name => "fetch_testcases";

    public string Description =>
        "Fetch structured test cases from configured AI sandboxes via WebSocket. "
        + "Supports fetch (generate), validate (review), chat (direct prompt), and status (connection health) actions.";

    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["fetch","validate","chat","status"],"default":"fetch"},
        "target":{"type":"string","enum":["generator","validator"]},
        "prompt":{"type":"string"},
        "testcases":{"type":"array"},
        "title_filter":{"type":"string"},
        "priority_filter":{"type":"string"},
        "max_count":{"type":"integer"}
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: fetch_testcases requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var action = GetString(root, "action") ?? "fetch";

        return action switch
        {
            "fetch" => await FetchAsync(root, ct),
            "validate" => await ValidateAsync(root, ct),
            "chat" => await ChatAsync(root, ct),
            "status" => Status(root),
            _ => "Error: Unknown action. Valid actions are fetch, validate, chat, and status."
        };
    }

    private async ValueTask<string> FetchAsync(JsonElement root, CancellationToken ct)
    {
        var prompt = GetString(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: prompt is required for fetch action.";

        var maxCount = GetInt32(root, "max_count", config.MaxTestcasesPerFetch);
        var fullPrompt = BuildFetchPrompt(prompt, maxCount);

        try
        {
            var rawResult = await pool.SendPromptAsync("generator", fullPrompt, ct);
            var testcases = ParseTestcases(rawResult);

            if (testcases.Length > maxCount)
                testcases = testcases[..maxCount];

            var result = new TestcaseFetchResult
            {
                TotalCount = testcases.Length,
                Source = "generator",
                Testcases = testcases,
                RawResponse = rawResult.TryGetProperty("text", out var text)
                    ? text.GetString()
                    : null
            };

            if (config.EnableDualValidation && testcases.Length > 0)
            {
                try
                {
                    result = await RunValidationAsync(testcases, ct);
                }
                catch (Exception ex)
                {
                    result.ValidationNotes = $"Validation unavailable: {ex.Message}";
                }
            }

            return Serialize(result);
        }
        catch (Exception ex)
        {
            return $"Error: fetch failed - {ex.Message}";
        }
    }

    private async ValueTask<string> ValidateAsync(JsonElement root, CancellationToken ct)
    {
        var testcases = ParseTestcasesFromParams(root);
        if (testcases.Length == 0)
            return "Error: testcases array is required for validate action.";

        try
        {
            var result = await RunValidationAsync(testcases, ct);
            return Serialize(result);
        }
        catch (Exception ex)
        {
            return $"Error: validation failed - {ex.Message}";
        }
    }

    private async ValueTask<string> ChatAsync(JsonElement root, CancellationToken ct)
    {
        var target = GetString(root, "target") ?? "generator";
        var prompt = GetString(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: prompt is required for chat action.";

        try
        {
            var rawResult = await pool.SendPromptAsync(target, prompt, ct);
            var text = rawResult.TryGetProperty("text", out var t)
                ? t.GetString() ?? rawResult.GetRawText()
                : rawResult.GetRawText();
            return text;
        }
        catch (Exception ex)
        {
            return $"Error: chat failed - {ex.Message}";
        }
    }

    private string Status(JsonElement root)
    {
        var statuses = new List<TestcaseSandboxStatus>();

        foreach (var role in new[] { "generator", "validator" })
        {
            var endpoint = role == "generator" ? config.Generator : config.Validator;
            if (string.IsNullOrWhiteSpace(endpoint.WsUrl))
                continue;

            statuses.Add(new TestcaseSandboxStatus
            {
                Role = role,
                Connected = pool.IsConnected(role),
                WsUrl = endpoint.WsUrl
            });
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("sandboxes");
            writer.WriteStartArray();
            foreach (var s in statuses)
            {
                writer.WriteStartObject();
                writer.WriteString("role", s.Role);
                writer.WriteBoolean("connected", s.Connected);
                writer.WriteString("ws_url", s.WsUrl);
                if (s.LastError is not null)
                    writer.WriteString("last_error", s.LastError);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async ValueTask<TestcaseFetchResult> RunValidationAsync(TestcaseEntry[] testcases, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Validator.WsUrl))
            throw new InvalidOperationException("Validator WsUrl is not configured.");

        var validationPrompt = BuildValidationPrompt(testcases);
        var rawResult = await pool.SendPromptAsync("validator", validationPrompt, ct);

        var validated = ParseTestcases(rawResult);
        var notes = rawResult.TryGetProperty("text", out var text)
            ? text.GetString()
            : null;

        return new TestcaseFetchResult
        {
            TotalCount = validated.Length > 0 ? validated.Length : testcases.Length,
            Source = validated.Length > 0 ? "validator" : "generator",
            Testcases = validated.Length > 0 ? validated : testcases,
            ValidationNotes = notes
        };
    }

    private static TestcaseEntry[] ParseTestcases(JsonElement result)
    {
        if (result.TryGetProperty("testcases", out var tcArray)
            && tcArray.ValueKind == JsonValueKind.Array)
        {
            return ParseTestcaseArray(tcArray);
        }

        if (result.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            var textValue = text.GetString() ?? "";
            var start = textValue.IndexOf('[');
            var end = textValue.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                try
                {
                    using var doc = JsonDocument.Parse(textValue[start..(end + 1)]);
                    return ParseTestcaseArray(doc.RootElement);
                }
                catch { }
            }
        }

        return [];
    }

    private static TestcaseEntry[] ParseTestcaseArray(JsonElement array)
    {
        var list = new List<TestcaseEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            list.Add(new TestcaseEntry
            {
                Id = item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : Guid.NewGuid().ToString("N")[..8],
                Title = item.TryGetProperty("title", out var title)
                    && title.ValueKind == JsonValueKind.String ? title.GetString() ?? "" : "",
                Description = item.TryGetProperty("description", out var desc)
                    && desc.ValueKind == JsonValueKind.String ? desc.GetString() ?? "" : "",
                Steps = ParseStringArray(item, "steps"),
                ExpectedResult = item.TryGetProperty("expected_result", out var er)
                    && er.ValueKind == JsonValueKind.String ? er.GetString() ?? "" : "",
                Priority = item.TryGetProperty("priority", out var pri)
                    && pri.ValueKind == JsonValueKind.String ? pri.GetString() : null,
                Tags = ParseStringArray(item, "tags"),
                Metadata = item.TryGetProperty("metadata", out var meta)
                    && meta.ValueKind == JsonValueKind.Object ? ParseMetadata(meta) : null
            });
        }

        return list.ToArray();
    }

    private static TestcaseEntry[] ParseTestcasesFromParams(JsonElement root)
    {
        if (!root.TryGetProperty("testcases", out var tcArray)
            || tcArray.ValueKind != JsonValueKind.Array)
            return [];

        return ParseTestcaseArray(tcArray);
    }

    private static string[] ParseStringArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop)
            || prop.ValueKind != JsonValueKind.Array)
            return [];

        return prop.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToArray();
    }

    private static Dictionary<string, JsonElement> ParseMetadata(JsonElement meta)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in meta.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    private static string BuildFetchPrompt(string userPrompt, int maxCount)
    {
        return $"""
            Generate up to {maxCount} test cases based on the following requirement.
            Return the test cases as a JSON array under the "testcases" key.
            Each test case must include: id, title, description, steps (string array),
            expected_result, priority (high/medium/low), and optional tags (string array).

            Requirement: {userPrompt}
            """.ReplaceLineEndings(" ");
    }

    private static string BuildValidationPrompt(TestcaseEntry[] testcases)
    {
        var tcJson = JsonSerializer.Serialize(testcases, AiEvaluationJsonContext.Default.TestcaseEntryArray);
        return $"""
            Review and validate the following test cases. Check for completeness, clarity,
            and correctness. Return improved test cases as a JSON array under the "testcases" key.
            Include a summary of changes in the "text" field.

            Test cases: {tcJson}
            """.ReplaceLineEndings(" ");
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int GetInt32(JsonElement root, string propertyName, int defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
            return defaultValue;
        return value;
    }

    private static string Serialize(TestcaseFetchResult result)
        => JsonSerializer.Serialize(result, AiEvaluationJsonContext.Default.TestcaseFetchResult);
}
