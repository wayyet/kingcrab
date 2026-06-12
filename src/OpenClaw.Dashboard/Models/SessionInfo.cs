namespace OpenClaw.Dashboard.Models;

public record SessionInfo(
    string SessionId,
    string? ChannelId,
    string? SenderId,
    DateTime? LastActive,
    Dictionary<string, object>? Metadata
);

public record SessionDetail(
    string SessionId,
    string? ChannelId,
    string? SenderId,
    DateTime? LastActive,
    Dictionary<string, object>? Metadata,
    List<SessionMessage>? Messages,
    List<ProviderTurnUsageEntry>? ProviderTurns
);

public record SessionMessage(
    string Role,
    string? Content,
    DateTime? Timestamp
);

public record ProviderTurnUsageEntry(
    DateTimeOffset TimestampUtc,
    string SessionId,
    string ChannelId,
    string ProviderId,
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens
);
