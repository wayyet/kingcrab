namespace OpenClaw.TokenCollector;

/// <summary>
/// Kafka producer settings for the out-of-sandbox collector. This config (and the Kafka client and
/// SASL credentials it carries) deliberately lives only here — never in the gateway or the sandbox
/// image. The collector is the single long-lived process that holds the broker connection.
/// </summary>
public sealed class TokenUsageKafkaConfig
{
    /// <summary>The collector exists to publish to Kafka, so this defaults to on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Kafka bootstrap servers, comma-separated for multiple brokers.</summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string Topic { get; set; } = "session-token-metrics";

    public string ClientId { get; set; } = "openclaw-token-usage";

    /// <summary>In-memory queue capacity; oldest events are dropped when full to protect ingest latency.</summary>
    public int QueueCapacity { get; set; } = 4096;

    public int LingerMs { get; set; } = 100;

    /// <summary>SASL secret refs resolved via SecretResolver (env:VAR / raw:literal); never plaintext in config.</summary>
    public string? SaslUsernameRef { get; set; }
    public string? SaslPasswordRef { get; set; }
    public string SecurityProtocol { get; set; } = "plaintext"; // plaintext | sasl_ssl
}
