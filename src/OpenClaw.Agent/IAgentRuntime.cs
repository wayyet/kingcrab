using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

/// <summary>
/// Delegate for interactive tool approval. Returns true to allow, false to deny.
/// </summary>
public delegate ValueTask<bool> ToolApprovalCallback(string toolName, string arguments, CancellationToken ct);

public interface IAgentRuntime
{
    CircuitState CircuitBreakerState { get; }
    IReadOnlyList<string> LoadedSkillNames { get; }

    /// <summary>
    /// The full set of AI tools currently available to this runtime, including name,
    /// description, and JSON schema. Populated after skill/plugin loading completes.
    /// </summary>
    IReadOnlyList<AITool> LoadedTools { get; }

    Task<string> RunAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        bool isSystemEvent = false);

    Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies a diff of workspace MCP tools: registers <paramref name="toAdd"/> and
    /// unregisters <paramref name="toRemove"/> (by tool name) without restarting.
    /// </summary>
    Task ApplyMcpToolChangesAsync(
        IReadOnlyList<ITool> toAdd,
        IReadOnlyList<string> toRemove,
        CancellationToken ct = default);

    IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        bool isSystemEvent = false);
}
