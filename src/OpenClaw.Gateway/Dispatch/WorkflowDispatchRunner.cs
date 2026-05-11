using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Dispatch;

internal interface IWorkflowDispatchRunner
{
    void Enqueue(WorkflowDispatchExecutionRequest request);
}

internal sealed record WorkflowDispatchExecutionRequest(
    string ParentSessionId,
    string ChannelId,
    string SenderId,
    string DispatchId,
    string Target,
    string[] HandoffIds,
    string? Mode,
    string? Note,
    IReadOnlyList<SessionHandoffItem> HandoffItems);

internal sealed class WorkflowDispatchRunner : IWorkflowDispatchRunner
{
    private readonly IAgentRuntime _agentRuntime;
    private readonly SessionManager _sessions;
    private readonly MessagePipeline _pipeline;
    private readonly ILogger<WorkflowDispatchRunner> _logger;
    private readonly CancellationToken _stoppingToken;

    public WorkflowDispatchRunner(
        IAgentRuntime agentRuntime,
        SessionManager sessions,
        MessagePipeline pipeline,
        ILogger<WorkflowDispatchRunner> logger,
        CancellationToken stoppingToken)
    {
        _agentRuntime = agentRuntime;
        _sessions = sessions;
        _pipeline = pipeline;
        _logger = logger;
        _stoppingToken = stoppingToken;
    }

    public void Enqueue(WorkflowDispatchExecutionRequest request)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await ExecuteAsync(request, _stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Workflow dispatch runner failed for dispatch {DispatchId} target={Target}.",
                        request.DispatchId,
                        request.Target);
                    await TryInjectFailureCallbackAsync(request, ex.Message, CancellationToken.None).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    private async Task ExecuteAsync(WorkflowDispatchExecutionRequest request, CancellationToken ct)
    {
        var childSessionId = $"{request.ParentSessionId}:dispatch:{request.DispatchId}";
        var child = await _sessions.GetOrCreateByIdAsync(childSessionId, "dispatch", "system", ct).ConfigureAwait(false);
        var prompt = BuildChildPrompt(request);

        _logger.LogInformation(
            "Starting workflow dispatch {DispatchId} target={Target} childSession={ChildSessionId}.",
            request.DispatchId,
            request.Target,
            childSessionId);

        var response = await _agentRuntime.RunAsync(
            child,
            prompt,
            ct,
            approvalCallback: null,
            responseSchema: null,
            isSystemEvent: true).ConfigureAwait(false);
        await _sessions.PersistAsync(child, ct).ConfigureAwait(false);

        var callbackJson = ExtractMatchingCallbackJson(response, request)
            ?? BuildFailureCallbackJson(request, "Downstream response did not include a valid matching dispatch_callback block.");
        await InjectParentCallbackAsync(request, callbackJson, ct).ConfigureAwait(false);
    }

    private async Task TryInjectFailureCallbackAsync(WorkflowDispatchExecutionRequest request, string error, CancellationToken ct)
    {
        try
        {
            await InjectParentCallbackAsync(request, BuildFailureCallbackJson(request, error), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject dispatch failure callback for {DispatchId}.", request.DispatchId);
        }
    }

    private async Task InjectParentCallbackAsync(WorkflowDispatchExecutionRequest request, string callbackJson, CancellationToken ct)
    {
        var inbound = new InboundMessage
        {
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            SessionId = request.ParentSessionId,
            Text = $"<dispatch_callback>{callbackJson}</dispatch_callback>\nDispatch {request.DispatchId} returned from {request.Target}.",
            IsSystem = true
        };

        await _pipeline.InboundWriter.WriteAsync(inbound, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Injected dispatch callback for {DispatchId} into parent session {ParentSessionId}.",
            request.DispatchId,
            request.ParentSessionId);
    }

    private static string BuildChildPrompt(WorkflowDispatchExecutionRequest request)
    {
        var handoffTodosJson = JsonSerializer.Serialize(
            request.HandoffItems.ToList(),
            CoreJsonContext.Default.ListSessionHandoffItem);
        var dispatchJson = BuildDispatchJson(request);

        return $$"""
You are running an OpenClaw workflow dispatch on behalf of a parent conversation.

Target skill: {{request.Target}}
Parent session id: {{request.ParentSessionId}}
Dispatch id: {{request.DispatchId}}

Follow the target skill contract exactly. Process only the Handoff todos listed below. Do not ask the user for clarification from this child dispatch run. If data is missing or unreadable, report that per Handoff todo in the callback.

Return exactly one control block in this format, with valid JSON and todo_results covering every handoff_id:
<dispatch_callback>{...}</dispatch_callback>

Dispatch envelope:
```json
{{dispatchJson}}
```

handoff_todos:
```json
{{handoffTodosJson}}
```
""";
    }

    private static string? ExtractMatchingCallbackJson(string response, WorkflowDispatchExecutionRequest request)
    {
        var extraction = ControlBlockExtractor.Extract(response);
        foreach (var block in extraction.Blocks.Where(static block => block.Kind == ControlBlockKind.DispatchCallback))
        {
            if (!DispatchSignalParser.TryParseCallback(block.Json, out var callback, out _))
                continue;
            if (!string.Equals(callback.SourceDispatchTarget, request.Target, StringComparison.Ordinal))
                continue;
            if (!new HashSet<string>(request.HandoffIds, StringComparer.Ordinal).SetEquals(callback.HandoffIds))
                continue;
            if (!new HashSet<string>(request.HandoffIds, StringComparer.Ordinal)
                    .SetEquals(callback.TodoResults.Select(static result => result.HandoffId)))
                continue;

            return block.Json;
        }

        return null;
    }

    private static string BuildDispatchJson(WorkflowDispatchExecutionRequest request)
    {
        var writer = CreateJsonWriter(out var buffer);
        writer.WriteStartObject();
        writer.WriteString("target", request.Target);
        WriteStringArray(writer, "handoff_ids", request.HandoffIds);
        if (!string.IsNullOrWhiteSpace(request.Mode))
            writer.WriteString("mode", request.Mode);
        if (!string.IsNullOrWhiteSpace(request.Note))
            writer.WriteString("note", request.Note);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildFailureCallbackJson(WorkflowDispatchExecutionRequest request, string error)
    {
        var writer = CreateJsonWriter(out var buffer);
        writer.WriteStartObject();
        writer.WriteString("source_dispatch_target", request.Target);
        WriteStringArray(writer, "handoff_ids", request.HandoffIds);
        writer.WriteString("user_summary", $"Dispatch {request.Target} failed before returning usable results: {error}");
        writer.WritePropertyName("todo_results");
        writer.WriteStartArray();
        foreach (var handoffId in request.HandoffIds)
        {
            writer.WriteStartObject();
            writer.WriteString("handoff_id", handoffId);
            writer.WriteString("status", "failed");
            WriteStringArray(writer, "artifacts", []);
            WriteStringArray(writer, "errors", [error]);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("status", "failed");
        WriteStringArray(writer, "errors", [error]);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Utf8JsonWriter CreateJsonWriter(out ArrayBufferWriter<byte> buffer)
    {
        buffer = new ArrayBufferWriter<byte>();
        return new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values.Where(static value => !string.IsNullOrWhiteSpace(value)))
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
