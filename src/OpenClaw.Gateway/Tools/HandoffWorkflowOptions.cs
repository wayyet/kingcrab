using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

internal sealed class HandoffWorkflowRegistry
{
    public required string DefaultWorkflowId { get; init; }
    public required HandoffWorkflowOptions[] Workflows { get; init; }

    public static HandoffWorkflowRegistry FromConfig(HandoffConfig config)
    {
        var workflows = config.Workflows
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(static pair => HandoffWorkflowOptions.FromConfig(pair.Key, pair.Value))
            .ToArray();

        var defaultWorkflowId = string.IsNullOrWhiteSpace(config.DefaultWorkflowId)
            ? workflows.FirstOrDefault()?.WorkflowId ?? ""
            : config.DefaultWorkflowId.Trim();

        return new HandoffWorkflowRegistry
        {
            DefaultWorkflowId = defaultWorkflowId,
            Workflows = workflows
        };
    }
}

internal sealed class HandoffWorkflowOptions
{
    public required string WorkflowId { get; init; }
    public string Kind { get; init; } = "handoff_todo";
    public string DefaultStatus { get; init; } = "drafting";
    public string[] NewItemStatuses { get; init; } = ["drafting", "ready_to_dispatch"];
    public string[] Stages { get; init; } = [];
    public string[] TargetSkills { get; init; } = [];
    public string[] Statuses { get; init; } = [];
    public IReadOnlyDictionary<string, string[]> Transitions { get; init; } = new Dictionary<string, string[]>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> IdPrefixes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool IsValidStage(string stage)
        => Stages.Contains(stage, StringComparer.Ordinal);

    public bool IsValidTargetSkill(string targetSkill)
        => TargetSkills.Contains(targetSkill, StringComparer.Ordinal);

    public bool IsValidStatus(string status)
        => Statuses.Contains(status, StringComparer.Ordinal);

    public bool IsValidNewItemStatus(string status)
        => NewItemStatuses.Contains(status, StringComparer.Ordinal);

    public bool CanTransition(string currentStatus, string nextStatus)
        => string.Equals(currentStatus, nextStatus, StringComparison.Ordinal) ||
           (Transitions.TryGetValue(currentStatus, out var nextStatuses) && nextStatuses.Contains(nextStatus, StringComparer.Ordinal));

    public string GetIdPrefix(string stage)
        => IdPrefixes.TryGetValue(stage, out var prefix) && !string.IsNullOrWhiteSpace(prefix) ? prefix.Trim() : "h";

    public static HandoffWorkflowOptions FromConfig(string workflowId, HandoffWorkflowConfig config)
        => new()
        {
            WorkflowId = workflowId.Trim(),
            Kind = NormalizeScalar(config.Kind, "handoff_todo"),
            DefaultStatus = NormalizeScalar(config.DefaultStatus, "drafting"),
            NewItemStatuses = NormalizeArray(config.NewItemStatuses),
            Stages = NormalizeArray(config.Stages),
            TargetSkills = NormalizeArray(config.TargetSkills),
            Statuses = NormalizeArray(config.Statuses),
            Transitions = NormalizeTransitions(config.Transitions),
            IdPrefixes = NormalizeDictionary(config.IdPrefixes)
        };

    private static string NormalizeScalar(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string[] NormalizeArray(string[]? values)
        => values is null || values.Length == 0
            ? []
            : values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static IReadOnlyDictionary<string, string> NormalizeDictionary(IReadOnlyDictionary<string, string>? values)
        => values is null || values.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : values
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(static pair => pair.Key.Trim(), static pair => pair.Value.Trim(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string[]> NormalizeTransitions(IReadOnlyDictionary<string, string[]>? transitions)
        => transitions is null || transitions.Count == 0
            ? new Dictionary<string, string[]>(StringComparer.Ordinal)
            : transitions
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(static pair => pair.Key.Trim(), static pair => NormalizeArray(pair.Value), StringComparer.Ordinal);
}
