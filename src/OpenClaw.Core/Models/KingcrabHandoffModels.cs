// kingcrab-only handoff/stage-gate types restored from upstream-overwritten files.
// Source: openclaw-copy *.bak preserved in this repo:
//   - GatewayConfig.cs.bak           (HandoffConfig / HandoffWorkflowConfig, lines 321-337)
//   - OperatorApiModels.cs.bak       (SessionHandoff* response types,        lines 319-422)
//   - WebSocketEnvelopes.cs.bak      (SkillStageGateEvent record,            lines 61-68)
// These were dropped when content-sync replaced the common files with their
// upstream variants. They are required by Gateway tooling (HandoffTool,
// HandoffWorkflowOptions, SkillArtifactRuntime) and remain kingcrab-specific.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

public sealed class HandoffConfig
{
    public string DefaultWorkflowId { get; set; } = "";
    public Dictionary<string, HandoffWorkflowConfig> Workflows { get; set; } = new(StringComparer.Ordinal);
}

public sealed class HandoffWorkflowConfig
{
    public string Kind { get; set; } = "handoff_todo";
    public string DefaultStatus { get; set; } = "drafting";
    public string[] NewItemStatuses { get; set; } = [];
    public string[] Stages { get; set; } = [];
    public string[] TargetSkills { get; set; } = [];
    public string[] Statuses { get; set; } = [];
    public Dictionary<string, string[]> Transitions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> IdPrefixes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SessionHandoffItem
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("workflow_id")]
    public string WorkflowId { get; init; } = "employment-coach";

    [JsonPropertyName("handoff_id")]
    public required string HandoffId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "handoff_todo";

    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "";

    [JsonPropertyName("target_skill")]
    public string TargetSkill { get; init; } = "";

    [JsonPropertyName("intent")]
    public string? Intent { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("acceptance")]
    public string? Acceptance { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "drafting";

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = "";

    [JsonPropertyName("related_todos")]
    public string[] RelatedTodos { get; init; } = [];

    [JsonPropertyName("related_files")]
    public string[] RelatedFiles { get; init; } = [];

    [JsonPropertyName("revision")]
    public int Revision { get; init; } = 1;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("dispatch_id")]
    public string? DispatchId { get; init; }

    [JsonPropertyName("callback_summary")]
    public string? CallbackSummary { get; init; }
}

public sealed class SessionHandoffListResponse
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<SessionHandoffItem> Items { get; init; } = [];
}

public sealed class SessionHandoffMutationResponse
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("item")]
    public required SessionHandoffItem Item { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<SessionHandoffItem> Items { get; init; } = [];
}

public sealed class SessionHandoffRemoveResponse
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("handoff_id")]
    public required string HandoffId { get; init; }

    [JsonPropertyName("removed")]
    public bool Removed { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<SessionHandoffItem> Items { get; init; } = [];
}

public sealed record SkillStageGateEvent
{
    public required string SkillName { get; init; }
    public required string CompletedStage { get; init; }
    public required string NextStage { get; init; }
    public required bool CanProceed { get; init; }
    public string? BlockedReason { get; init; }
}
