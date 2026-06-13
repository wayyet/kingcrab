using System.Text.Json;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Integrations;

/// <summary>
/// Publishes <see cref="SessionTokenUsageEvent"/>s to Kafka, keyed by agent_id so events for
/// one digital employee stay ordered within a partition. Publish only enqueues into a bounded
/// in-memory channel (oldest dropped when full); all network IO happens on this background
/// service, so a Kafka outage can never stall the chat hot path.
/// </summary>
public sealed class KafkaTokenUsagePublisher : BackgroundService, ITokenUsageEventSink
{
    private readonly TokenUsageKafkaConfig _config;
    private readonly ILogger<KafkaTokenUsagePublisher> _logger;
    private readonly Channel<SessionTokenUsageEvent> _queue;
    private long _dropped;

    public KafkaTokenUsagePublisher(TokenUsageKafkaConfig config, ILogger<KafkaTokenUsagePublisher> logger)
    {
        _config = config;
        _logger = logger;
        _queue = Channel.CreateBounded<SessionTokenUsageEvent>(
            new BoundedChannelOptions(Math.Max(16, config.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            OnEventDropped);
    }

    /// <summary>Called on the LLM hot path; enqueue only, never blocks.</summary>
    public void Publish(SessionTokenUsageEvent evt)
    {
        if (!_config.Enabled)
            return;

        _queue.Writer.TryWrite(evt);
    }

    private void OnEventDropped(SessionTokenUsageEvent evt)
    {
        var dropped = Interlocked.Increment(ref _dropped);
        if (dropped % 100 == 1)
            _logger.LogWarning("Token usage queue full; dropped {Dropped} events so far", dropped);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Kafka token usage publisher disabled.");
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kafka token usage publisher error; restarting in {Delay}s", backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _config.BootstrapServers,
            ClientId = _config.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 5,
            LingerMs = Math.Max(0, _config.LingerMs),
            CompressionType = CompressionType.Lz4
        };

        if (!string.Equals(_config.SecurityProtocol, "plaintext", StringComparison.OrdinalIgnoreCase))
        {
            producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
            producerConfig.SaslMechanism = SaslMechanism.ScramSha512;
            producerConfig.SaslUsername = SecretResolver.Resolve(_config.SaslUsernameRef, _logger);
            producerConfig.SaslPassword = SecretResolver.Resolve(_config.SaslPasswordRef, _logger);
        }

        using var producer = new ProducerBuilder<string, string>(producerConfig)
            .SetErrorHandler((_, e) => _logger.LogWarning("Kafka producer error: {Reason}", e.Reason))
            .Build();

        try
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(evt, TokenUsageJsonContext.Default.SessionTokenUsageEvent);
                try
                {
                    // Fire-and-forget into librdkafka's batching buffer; delivery failures are
                    // reported via the handler. Awaiting each delivery would cap throughput at
                    // one message per linger window.
                    producer.Produce(
                        _config.Topic,
                        new Message<string, string> { Key = evt.AgentId, Value = json },
                        report =>
                        {
                            if (report.Error.IsError)
                            {
                                _logger.LogWarning(
                                    "Kafka delivery failed (session={SessionId} agent={AgentId}): {Reason}",
                                    evt.SessionId, evt.AgentId, report.Error.Reason);
                            }
                        });
                }
                catch (ProduceException<string, string> ex)
                {
                    if (ex.Error.Code == ErrorCode.Local_QueueFull)
                        OnEventDropped(evt);
                    else
                        _logger.LogWarning(
                            "Kafka produce failed (session={SessionId} agent={AgentId}): {Reason}",
                            evt.SessionId, evt.AgentId, ex.Error.Reason);
                }
            }
        }
        finally
        {
            producer.Flush(TimeSpan.FromSeconds(5));
        }
    }
}
