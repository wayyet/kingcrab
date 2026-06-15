namespace OpenClaw.TokenCollector;

/// <summary>Root options for the token collector, bound from the "Collector" config section.</summary>
public sealed class CollectorOptions
{
    /// <summary>Kestrel bind URL. Override in containers via Collector__BindUrl.</summary>
    public string BindUrl { get; set; } = "http://0.0.0.0:8088";

    /// <summary>Bearer token ref the ingest endpoint requires. Resolved via SecretResolver (env:VAR / raw:literal).</summary>
    public string? AuthTokenRef { get; set; } = "env:TOKEN_COLLECTOR_TOKEN";

    /// <summary>Reject ingest bodies larger than this (events are ~400 bytes; a full batch is tens of KB).</summary>
    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    public TokenUsageKafkaConfig Kafka { get; set; } = new();
}
