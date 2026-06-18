using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using OpenClaw.TokenHubSink.Observability;

namespace OpenClaw.Agent;

public sealed class AgentRuntimeFactoryContext
{
    public required IServiceProvider Services { get; init; }
    public required GatewayConfig Config { get; init; }
    public required GatewayRuntimeState RuntimeState { get; init; }
    public required IChatClient ChatClient { get; init; }
    public required IReadOnlyList<ITool> Tools { get; init; }
    public required IMemoryStore MemoryStore { get; init; }
    public required RuntimeMetrics RuntimeMetrics { get; init; }
    public required ProviderUsageTracker ProviderUsage { get; init; }
    public required ILlmExecutionService LlmExecutionService { get; init; }
    public required IReadOnlyList<SkillDefinition> Skills { get; init; }
    public required SkillsConfig SkillsConfig { get; init; }
    public required string? WorkspacePath { get; init; }
    public required IReadOnlyList<string> PluginSkillDirs { get; init; }
    public required ILogger Logger { get; init; }
    public required IReadOnlyList<IToolHook> Hooks { get; init; }
    public required bool RequireToolApproval { get; init; }
    public required IReadOnlyList<string> ApprovalRequiredTools { get; init; }
    public ITurnTokenUsageObserver? TurnTokenUsageObserver { get; init; }
    public IToolSandbox? ToolSandbox { get; init; }
    public IToolGovernanceService? ToolGovernance { get; init; }
    public IPlanExecuteVerifyOrchestrator? PlanExecuteVerify { get; init; }
    public ToolUsageTracker? ToolUsageTracker { get; init; }
    public ToolAuditLog? ToolAuditLog { get; init; }
    public Func<Session, bool>? IsContractTokenBudgetExceeded { get; init; }
    public Func<Session, bool>? IsContractRuntimeBudgetExceeded { get; init; }
    public Action<Session, string, string, long, long>? RecordContractTurnUsage { get; init; }
    public Action<Session, string>? AppendContractSnapshot { get; init; }

    /// <summary>
    /// Optional TokenHub thin-client sink. When set (HTTP sink), the runtime publishes one
    /// incremental <see cref="SessionTokenUsageEvent"/> per LLM call on the usage write path.
    /// Null or the no-op sink disables publishing at zero hot-path cost.
    /// </summary>
    public ITokenUsageEventSink? TokenUsageEventSink { get; init; }

    /// <summary>Fixed digital-employee id for token usage events; null/empty falls back to Session.SenderId.</summary>
    public string? TokenUsageAgentId { get; init; }
}

public interface IAgentRuntimeFactory
{
    string OrchestratorId { get; }

    IAgentRuntime Create(AgentRuntimeFactoryContext context);
}

public static class AgentRuntimeFactorySelector
{
    public static IAgentRuntimeFactory Select(
        IEnumerable<IAgentRuntimeFactory> factories,
        string? orchestratorId)
    {
        var normalizedOrchestrator = RuntimeOrchestrator.Normalize(orchestratorId);
        var factory = factories.FirstOrDefault(candidate =>
            string.Equals(candidate.OrchestratorId, normalizedOrchestrator, StringComparison.OrdinalIgnoreCase));

        if (factory is not null)
            return factory;

        throw new InvalidOperationException(
            normalizedOrchestrator == RuntimeOrchestrator.Maf
                ? "Runtime.Orchestrator='maf' requires the Microsoft Agent Framework adapter. Set OpenClaw:Runtime:Orchestrator='maf' in a build that includes OpenClaw.MicrosoftAgentFrameworkAdapter."
                : $"No agent runtime factory is registered for orchestrator '{normalizedOrchestrator}'.");
    }
}
