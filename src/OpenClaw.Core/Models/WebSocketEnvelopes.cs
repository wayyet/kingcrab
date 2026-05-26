namespace OpenClaw.Core.Models;

/// <summary>
/// Optional JSON envelope used by WebSocket clients.
/// Raw-text clients may continue sending plain text.
/// </summary>
public sealed record WsClientEnvelope
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? Content { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }

    // Tool approval decision (client -> server)
    public string? ApprovalId { get; init; }
    public bool? Approved { get; init; }
}

/// <summary>
/// JSON envelope sent by the gateway when a client opts into envelopes.
/// </summary>
public sealed record WsServerEnvelope
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? InReplyToMessageId { get; init; }

    // Tool approval request/status (server -> client)
    public string? ApprovalId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsPreview { get; init; }
    public bool? Approved { get; init; }

    // File attachment delivery (server -> client, type = "file_attachment")
    public string? FileUrl { get; init; }
    public string? FileName { get; init; }
    public string? MimeType { get; init; }
    public long? FileSizeBytes { get; init; }

    /// <summary>
    /// Optional semantic type for artifact deliveries emitted by the emit_artifact tool.
    /// Well-known values: "template_package", "skill_package", "ontology", "generic".
    /// </summary>
    public string? ArtifactType { get; init; }

    /// <summary>
    /// Unified artifact payload (type = "artifact"). Carries the full <see cref="SkillArtifact"/>
    /// object for both file and data artifacts. Clients should prefer this over the flat
    /// FileUrl/FileName/etc. fields which are kept only for backward compatibility.
    /// </summary>
    public SkillArtifact? Artifact { get; init; }

    /// <summary>
    /// Stage gate transition event (type = "skill_stage_gate") emitted after a terminal artifact.
    /// </summary>
    public SkillStageGateEvent? StageGate { get; init; }
}

public sealed record SkillStageGateEvent
{
    public required string SkillName { get; init; }
    public required string CompletedStage { get; init; }
    public required string NextStage { get; init; }
    public required bool CanProceed { get; init; }
    public string? BlockedReason { get; init; }
}
