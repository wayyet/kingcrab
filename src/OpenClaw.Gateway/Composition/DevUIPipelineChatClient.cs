using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Mcp;

namespace OpenClaw.Gateway.Composition;

/// <summary>
/// An IChatClient that bridges DevUI conversations into OpenClaw's full
/// IAgentRuntime pipeline (tools, memory, approval hooks, telemetry, etc.).
///
/// Strategy:
///   1. Intercepts ChatClientAgent's IChatClient calls at the request level.
///   2. Resolves the IAgentRuntime lazily via GatewayRuntimeHolder so that DI
///      registration happens at builder time and the runtime is set post-Build.
///   3. Uses AIAgent.CurrentRunContext to identify the current AgentSession
///      and maps it to a stable OpenClaw Session ID via ConditionalWeakTable.
///   4. Extracts only the LAST user message from the accumulated history
///      (OpenClaw manages its own per-session history, so we avoid duplication).
///
/// Tool invocation:
///   Returns non-null from GetService(FunctionInvokingChatClient) to signal
///   to ChatClientAgent that tool invocation is already handled inside
///   IAgentRuntime.  This prevents ChatClientAgent from inserting its own
///   FunctionInvokingChatClient middleware, which would otherwise try to
///   re-invoke tools a second time and cause a second full pipeline run.
///
///   Tool call events from RunStreamingAsync are emitted as structured
///   FunctionCallContent / FunctionResultContent in the stream so that
///   DevUI's conversation panel shows them as proper tool call records.
/// </summary>
internal sealed class DevUIPipelineChatClient : IChatClient
{
    private readonly GatewayRuntimeHolder _holder;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<DevUIPipelineChatClient> _logger;

    // Maps each AgentSession object (from the DevUI Conversations API) to a
    // stable OpenClaw session ID.  ConditionalWeakTable means the entry is
    // automatically removed when the AgentSession is GC-collected.
    private readonly ConditionalWeakTable<AgentSession, StableId> _sessionMap = new();

    public ChatClientMetadata Metadata { get; } =
        new("openclaw-pipeline", null, "openclaw");

    public DevUIPipelineChatClient(
        GatewayRuntimeHolder holder,
        SessionManager sessionManager,
        ILogger<DevUIPipelineChatClient> logger)
    {
        _holder = holder;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    //  IChatClient implementation

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = _holder.Runtime.AgentRuntime;
        var (session, userMessage) = await ResolveContextAsync(chatMessages, cancellationToken);

        var responseText = await runtime.RunAsync(session, userMessage, cancellationToken);

        return new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, responseText)]);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runtime = _holder.Runtime.AgentRuntime;
        var (session, userMessage) = await ResolveContextAsync(chatMessages, cancellationToken);

        // Track pending tool calls: toolName → queue of call IDs waiting for
        // a matching ToolResult.  Supports multiple concurrent calls to the
        // same tool (though OpenClaw is currently sequential).
        var pendingCalls = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
        int callCounter = 0;

        await foreach (var evt in runtime.RunStreamingAsync(session, userMessage, cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (evt.Type)
            {
                case AgentStreamEventType.TextDelta:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, evt.Content);
                    break;

                case AgentStreamEventType.ToolStart:
                {
                    // Emit a FunctionCallContent so DevUI's conversation panel
                    // shows the tool call with its arguments.
                    var callId = $"call_{callCounter++}";
                    if (!pendingCalls.TryGetValue(evt.ToolName!, out var q))
                        pendingCalls[evt.ToolName!] = q = new Queue<string>();
                    q.Enqueue(callId);

                    IDictionary<string, object?>? args = null;
                    if (!string.IsNullOrWhiteSpace(evt.ToolArguments))
                    {
                        try
                        {
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                evt.ToolArguments,
                                _jsonOptions);
                        }
                        catch (JsonException) { /* leave args null */ }
                    }

                    var update = new ChatResponseUpdate(null, string.Empty);
                    update.Contents.Clear();
                    update.Contents.Add(new FunctionCallContent(callId, evt.ToolName!, args));
                    yield return update;
                    break;
                }

                case AgentStreamEventType.ToolResult:
                {
                    // Emit a FunctionResultContent paired to the matching call ID.
                    if (pendingCalls.TryGetValue(evt.ToolName!, out var q) && q.Count > 0)
                    {
                        var callId = q.Dequeue();
                        var result = new ChatResponseUpdate(null, string.Empty);
                        result.Contents.Clear();
                        result.Contents.Add(new FunctionResultContent(callId, evt.Content));
                        yield return result;
                    }
                    break;
                }

                case AgentStreamEventType.Error:
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        $"\n⚠️  {evt.Content}");
                    break;

                case AgentStreamEventType.Done:
                    yield break;
            }
        }
    }

    /// <summary>
    /// Returns non-null for <see cref="FunctionInvokingChatClient"/> to signal
    /// that tool invocation is already handled inside the IAgentRuntime pipeline.
    /// This prevents <see cref="ChatClientAgent"/> from inserting its own
    /// <see cref="FunctionInvokingChatClient"/> middleware, which would cause
    /// a double-execution loop.
    /// </summary>
    public object? GetService(Type serviceType, object? key = null)
    {
        if (serviceType == typeof(FunctionInvokingChatClient))
            return this; // non-null → ChatClientAgent won't wrap us
        return null;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose() { }

    // -----------------------------------------------------------------------
    //  Session resolution

    private async Task<(Session openClawSession, string userMessage)> ResolveContextAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken ct)
    {
        // Determine the OpenClaw session ID from the current AgentSession.
        // AIAgent.CurrentRunContext is propagated as AsyncLocal by the framework,
        // so it is available inside IChatClient calls made by ChatClientAgent.
        var mafSession = AIAgent.CurrentRunContext?.Session;
        string openClawSessionId;

        if (mafSession is not null)
        {
            // Thread-safe: CreateValue only runs if the key is absent.
            // StableId is mutable so we can lazy-initialise it in one shot.
            var stableId = _sessionMap.GetOrCreateValue(mafSession);
            lock (stableId)
            {
                if (stableId.Value is null)
                {
                    stableId.Value = $"devui:{Guid.NewGuid():N}";
                    _logger.LogDebug(
                        "DevUI: created OpenClaw session {SessionId} for new AgentSession",
                        stableId.Value);
                }

                openClawSessionId = stableId.Value;
            }
        }
        else
        {
            // Fallback: no ambient session context (e.g. Responses API without
            // Conversations middleware).  Use a transient per-call session.
            openClawSessionId = $"devui:transient:{Guid.NewGuid():N}";
            _logger.LogDebug(
                "DevUI: no ambient AgentSession; using transient id {SessionId}",
                openClawSessionId);
        }

        // GetOrCreateByIdAsync loads history from the persistent store on first
        // access after a restart, then caches the Session in memory.
        var session = await _sessionManager.GetOrCreateByIdAsync(
            openClawSessionId, DevUIChannelId, DevUIUserId, ct);

        // Extract only the LAST user message from the accumulated history that
        // ChatClientAgent passes in.  OpenClaw's runtime manages its own session
        // history, so we must avoid re-sending the entire history each turn.
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var lastUser = messageList.LastOrDefault(m => m.Role == ChatRole.User);
        var userText = lastUser?.Text
            ?? string.Join(" ",
                messageList
                    .Where(m => m.Role == ChatRole.User)
                    .SelectMany(m => m.Contents.OfType<TextContent>())
                    .Select(c => c.Text));

        if (string.IsNullOrWhiteSpace(userText))
            userText = "(empty)";

        return (session, userText);
    }

    // -----------------------------------------------------------------------

    private const string DevUIChannelId = "devui";
    private const string DevUIUserId = "devui-user";

    /// <summary>Mutable box used as a value in ConditionalWeakTable.</summary>
    private sealed class StableId
    {
        public string? Value { get; set; }
    }
}
