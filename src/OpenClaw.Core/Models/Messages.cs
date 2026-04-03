namespace OpenClaw.Core.Models;

/// <summary>
/// Inbound message from any channel adapter.
/// </summary>
public sealed record InboundMessage
{
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public string? SessionId { get; init; }
    public string? CronJobName { get; init; }
    public string? Type { get; init; }
    public required string Text { get; init; }
    public string? SenderName { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public bool IsSystem { get; init; }
    public string? Subject { get; init; }
    public string? ApprovalId { get; init; }
    public bool? Approved { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    // Group chat fields
    public bool IsGroup { get; init; }
    public string? GroupId { get; init; }
    public string? GroupName { get; init; }
    public string[]? MentionedIds { get; init; }

    // Media fields
    public string? MediaType { get; init; }
    public string? MediaUrl { get; init; }
    public string? MediaMimeType { get; init; }
    public string? MediaFileName { get; init; }

    /// <summary>
    /// Multiple media attachments (e.g. several images in one message).
    /// When present, each attachment generates its own marker line in the pipeline text.
    /// </summary>
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }
}

/// <summary>
/// A single media file attached to an <see cref="InboundMessage"/>.
/// </summary>
public sealed record MediaAttachment
{
    /// <summary>"image", "video", "audio", "document"</summary>
    public required string MediaType { get; init; }
    public string? Url { get; init; }
    public string? MimeType { get; init; }
    public string? FileName { get; init; }
}

/// <summary>
/// Outbound message to be routed back through a channel adapter.
/// </summary>
public sealed record OutboundMessage
{
    public required string ChannelId { get; init; }
    public required string RecipientId { get; init; }
    public required string Text { get; init; }
    public string? SessionId { get; init; }
    public string? CronJobName { get; init; }
    public string? Subject { get; init; }
    public string? ReplyToMessageId { get; init; }
}
