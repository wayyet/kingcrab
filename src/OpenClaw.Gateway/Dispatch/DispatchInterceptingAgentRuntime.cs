using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Gateway.Dispatch;

internal sealed class DispatchInterceptingAgentRuntime : IAgentRuntime
{
    private readonly IAgentRuntime _inner;
    private readonly WorkflowDispatchCoordinator _coordinator;

    public DispatchInterceptingAgentRuntime(IAgentRuntime inner, WorkflowDispatchCoordinator coordinator)
    {
        _inner = inner;
        _coordinator = coordinator;
    }

    public CircuitState CircuitBreakerState => _inner.CircuitBreakerState;

    public IReadOnlyList<string> LoadedSkillNames => _inner.LoadedSkillNames;

    public IReadOnlyList<AITool> LoadedTools => _inner.LoadedTools;

    public event Action<IReadOnlyList<SkillDefinition>>? SkillsReloaded
    {
        add => _inner.SkillsReloaded += value;
        remove => _inner.SkillsReloaded -= value;
    }

    public async Task<string> RunAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        bool isSystemEvent = false)
    {
        if (isSystemEvent)
            userMessage = ProcessInboundSystemControlBlocks(session, userMessage);

        var rawText = await _inner.RunAsync(session, userMessage, ct, approvalCallback, responseSchema, isSystemEvent);
        var extraction = ControlBlockExtractor.Extract(rawText);
        if (extraction.Blocks.Count == 0)
            return rawText;

        ReplaceLastAssistantText(session, rawText, extraction.VisibleText);
        _coordinator.ProcessControlBlocks(session, extraction.Blocks);
        return extraction.VisibleText;
    }

    public Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default)
        => _inner.ReloadSkillsAsync(ct);

    public Task ApplyMcpToolChangesAsync(
        IReadOnlyList<ITool> toAdd,
        IReadOnlyList<string> toRemove,
        CancellationToken ct = default)
        => _inner.ApplyMcpToolChangesAsync(toAdd, toRemove, ct);

    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        bool isSystemEvent = false)
    {
        if (isSystemEvent)
            userMessage = ProcessInboundSystemControlBlocks(session, userMessage);

        var filter = new ControlBlockExtractor.StreamingControlBlockFilter();
        var visibleText = new StringBuilder();
        var processedBlocks = 0;

        await foreach (var evt in _inner.RunStreamingAsync(session, userMessage, ct, approvalCallback, isSystemEvent)
                           .ConfigureAwait(false))
        {
            if (evt.Type == AgentStreamEventType.TextDelta)
            {
                foreach (var chunk in filter.Append(evt.Content))
                {
                    visibleText.Append(chunk);
                    yield return AgentStreamEvent.TextDelta(chunk);
                }

                ProcessNewBlocks(session, filter.Blocks, ref processedBlocks);
                continue;
            }

            if (evt.Type == AgentStreamEventType.Done)
            {
                foreach (var chunk in filter.Complete())
                {
                    visibleText.Append(chunk);
                    yield return AgentStreamEvent.TextDelta(chunk);
                }

                ProcessNewBlocks(session, filter.Blocks, ref processedBlocks);
                ReplaceLastAssistantText(session, null, visibleText.ToString());
                yield return evt;
                continue;
            }

            yield return evt;
        }
    }

    private void ProcessNewBlocks(Session session, IReadOnlyList<ControlBlock> blocks, ref int processedBlocks)
    {
        if (processedBlocks >= blocks.Count)
            return;

        var newBlocks = blocks.Skip(processedBlocks).ToArray();
        processedBlocks = blocks.Count;
        _coordinator.ProcessControlBlocks(session, newBlocks);
    }

    private string ProcessInboundSystemControlBlocks(Session session, string userMessage)
    {
        var extraction = ControlBlockExtractor.Extract(userMessage);
        if (extraction.Blocks.Count == 0)
            return userMessage;

        _coordinator.ProcessControlBlocks(session, extraction.Blocks);
        var visibleText = extraction.VisibleText.Trim();
        var callbackSummaries = extraction.Blocks
            .Where(static block => block.Kind == ControlBlockKind.DispatchCallback)
            .Select(static block => DispatchSignalParser.TryParseCallback(block.Json, out var callback, out _)
                ? $"{callback.SourceDispatchTarget}: {callback.UserSummary}"
                : null)
            .Where(static summary => !string.IsNullOrWhiteSpace(summary))
            .ToArray();
        if (callbackSummaries.Length == 0 && visibleText.Length > 0)
            return visibleText;
        if (callbackSummaries.Length == 0)
            return "A workflow control event was received. Check the current session state and continue.";

        var callbackNotice = "A downstream dispatch callback was received:\n"
                             + string.Join("\n", callbackSummaries.Select(static summary => $"- {summary}"))
                             + "\nBriefly summarize the result for the user and ask for confirmation. Do not mark Handoff todos as confirmed automatically.";
        return visibleText.Length == 0 ? callbackNotice : $"{visibleText}\n{callbackNotice}";
    }

    private static void ReplaceLastAssistantText(Session session, string? expectedRawText, string visibleText)
    {
        for (var index = session.History.Count - 1; index >= 0; index--)
        {
            var turn = session.History[index];
            if (!string.Equals(turn.Role, "assistant", StringComparison.Ordinal))
                continue;

            if (expectedRawText is not null && !string.Equals(turn.Content, expectedRawText, StringComparison.Ordinal))
                continue;

            session.History[index] = new ChatTurn
            {
                Role = turn.Role,
                Content = visibleText,
                Timestamp = turn.Timestamp,
                ToolCalls = turn.ToolCalls
            };
            return;
        }
    }
}
