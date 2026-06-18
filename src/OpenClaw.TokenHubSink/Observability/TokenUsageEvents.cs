using System.Text.Json.Serialization;

namespace OpenClaw.TokenHubSink.Observability;

/// <summary>
/// Token usage event produced once per LLM call (incremental counts, not session totals).
/// Pushed to an external pipeline (TokenHub.Collector -> Kafka -> Doris) for per-agent aggregation.
/// <para>
/// The snake_case JSON property names below are the cross-repo wire contract: they must stay
/// byte-identical to TokenHub.Core's <c>SessionTokenUsageEvent</c> and the Doris Routine Load
/// jsonpaths. Do not rename a JSON field without updating both ends.
/// </para>
/// </summary>
public sealed record SessionTokenUsageEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("event_time")]
    public DateTimeOffset EventTime { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Digital employee id. Defaults to the session's SenderId unless a fixed id is configured.</summary>
    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; init; } = "";

    [JsonPropertyName("provider_id")]
    public string ProviderId { get; init; } = "";

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = "";

    /// <summary>Input tokens for this single LLM call (increment; safe to SUM downstream).</summary>
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    /// <summary>input + output, matching <c>Session.GetTotalTokens()</c> semantics (cache write excluded).</summary>
    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }

    /// <summary>Session running totals at event time. Reconciliation snapshot only; never SUM these.</summary>
    [JsonPropertyName("session_total_input_tokens")]
    public long SessionTotalInputTokens { get; init; }

    [JsonPropertyName("session_total_output_tokens")]
    public long SessionTotalOutputTokens { get; init; }

    [JsonPropertyName("session_total_cache_read_tokens")]
    public long SessionTotalCacheReadTokens { get; init; }

    [JsonPropertyName("session_total_tokens")]
    public long SessionTotalTokens { get; init; }
}

/// <summary>
/// Outbound sink for token usage events. Implementations must never block:
/// Publish is invoked on the LLM hot path, so only in-memory enqueueing is allowed.
/// </summary>
public interface ITokenUsageEventSink
{
    void Publish(SessionTokenUsageEvent evt);
}

/// <summary>No-op sink injected when no external pipeline is configured.</summary>
public sealed class NullTokenUsageEventSink : ITokenUsageEventSink
{
    public static readonly NullTokenUsageEventSink Instance = new();

    private NullTokenUsageEventSink()
    {
    }

    public void Publish(SessionTokenUsageEvent evt)
    {
    }
}

[JsonSerializable(typeof(SessionTokenUsageEvent))]
[JsonSerializable(typeof(SessionTokenUsageEvent[]))]
public sealed partial class TokenUsageJsonContext : JsonSerializerContext;
