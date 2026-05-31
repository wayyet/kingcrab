using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Agent;

public interface IAgentRuntime
{
    CircuitState CircuitBreakerState { get; }
    IReadOnlyList<string> LoadedSkillNames { get; }

    /// <summary>
    /// Snapshot of the currently loaded skill definitions. Used by the
    /// <c>load_skill</c> tool to resolve a skill body on demand (progressive disclosure).
    /// </summary>
    IReadOnlyList<SkillDefinition> LoadedSkills { get; }

    /// <summary>
    /// Snapshot of the currently registered AITool declarations (kingcrab extension,
    /// used by the dev UI / observability endpoints).
    /// </summary>
    IReadOnlyList<AITool> LoadedTools => [];

    Task<string> RunAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        bool isSystemEvent = false);

    Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default);

    /// <summary>
    /// Hot-swap the workspace MCP tool surface. kingcrab extension consumed by
    /// <c>McpWorkspaceWatcherService</c> when MCP servers are added or removed.
    /// </summary>
    Task ApplyMcpToolChangesAsync(
        IReadOnlyList<OpenClaw.Core.Abstractions.ITool> toAdd,
        IReadOnlyList<string> toRemove,
        CancellationToken ct = default) => Task.CompletedTask;

    IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        bool isSystemEvent = false);
}
