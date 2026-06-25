namespace OpenClaw.Core.Models;

public sealed record TurnTokenUsageRecord
{
    public string? CorrelationId { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public required InputTokenComponentEstimate EstimatedInputTokensByComponent { get; init; }
    public bool IsEstimated { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    // In-process session snapshot (does NOT widen the cross-process wire contract). Carried on the
    // record so a singleton ITurnTokenUsageObserver — which only receives the record — can map the
    // TokenHub event without holding a Session reference.
    public string SenderId { get; init; } = "";              // agentId fallback when no fixed id is configured
    public long SessionTotalInputTokens { get; init; }
    public long SessionTotalOutputTokens { get; init; }
    public long SessionTotalCacheReadTokens { get; init; }
    public long SessionTotalTokens { get; init; }
}