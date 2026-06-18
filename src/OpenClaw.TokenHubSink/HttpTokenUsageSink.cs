using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.TokenHubSink.Http;
using OpenClaw.TokenHubSink.Models;
using OpenClaw.TokenHubSink.Observability;
using OpenClaw.TokenHubSink.Security;

namespace OpenClaw.TokenHubSink;

/// <summary>
/// Ships <see cref="SessionTokenUsageEvent"/>s to an out-of-sandbox collector over HTTP. This is the
/// gateway-side thin client: it never touches Kafka, holds no broker credentials, and only needs the
/// sandbox network policy to allow a single collector endpoint. Publish only enqueues into a bounded
/// in-memory channel (oldest dropped when full); all network IO happens on this background service,
/// so a collector outage can never stall the chat hot path.
/// </summary>
public sealed class HttpTokenUsageSink : BackgroundService, ITokenUsageEventSink
{
    private readonly ILogger<HttpTokenUsageSink> _logger;
    private readonly Channel<SessionTokenUsageEvent> _queue;
    private readonly HttpClient _httpClient;
    private readonly Uri _collectorUrl;
    private readonly string? _authToken;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private long _dropped;

    public HttpTokenUsageSink(TokenUsageConfig config, ILogger<HttpTokenUsageSink> logger)
    {
        _logger = logger;

        var http = config.Http;
        _collectorUrl = new Uri(http.CollectorUrl);
        _authToken = SecretResolver.Resolve(http.AuthTokenRef, logger);
        _batchSize = Math.Max(1, http.BatchSize);
        _flushInterval = TimeSpan.FromMilliseconds(Math.Max(1, http.FlushIntervalMs));

        _httpClient = HttpClientFactory.Create();
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, http.TimeoutSeconds));

        _queue = Channel.CreateBounded<SessionTokenUsageEvent>(
            new BoundedChannelOptions(Math.Max(16, http.QueueCapacity))
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
        _logger.LogInformation("HTTP token usage sink enabled; collector={CollectorUrl}", _collectorUrl);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await CollectBatchAsync(stoppingToken);
                if (batch.Count == 0)
                    continue;

                await SendBatchWithRetryAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }

        await FlushRemainingAsync();
    }

    /// <summary>
    /// Blocks until at least one event is available, then drains up to <see cref="_batchSize"/>
    /// events, waiting at most <see cref="_flushInterval"/> for a partial batch to fill.
    /// </summary>
    private async Task<List<SessionTokenUsageEvent>> CollectBatchAsync(CancellationToken ct)
    {
        var reader = _queue.Reader;
        var batch = new List<SessionTokenUsageEvent>(_batchSize);

        // Park here while idle (cancelled on shutdown).
        if (!await reader.WaitToReadAsync(ct))
            return batch;

        while (batch.Count < _batchSize && reader.TryRead(out var evt))
            batch.Add(evt);

        if (batch.Count >= _batchSize)
            return batch;

        // Partial batch: give late events a short window to coalesce before flushing.
        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        flushCts.CancelAfter(_flushInterval);
        try
        {
            while (batch.Count < _batchSize && await reader.WaitToReadAsync(flushCts.Token))
            {
                while (batch.Count < _batchSize && reader.TryRead(out var evt))
                    batch.Add(evt);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Flush interval elapsed; send whatever we have.
        }

        return batch;
    }

    private async Task SendBatchWithRetryAsync(IReadOnlyList<SessionTokenUsageEvent> batch, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PostBatchAsync(batch, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Token usage collector POST failed ({Count} events); retrying in {Delay}s",
                    batch.Count, backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task PostBatchAsync(IReadOnlyList<SessionTokenUsageEvent> batch, CancellationToken ct)
    {
        var array = batch as SessionTokenUsageEvent[] ?? batch.ToArray();
        var json = JsonSerializer.Serialize(array, TokenUsageJsonContext.Default.SessionTokenUsageEventArray);

        using var request = new HttpRequestMessage(HttpMethod.Post, _collectorUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_authToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Best-effort drain of any queued events at shutdown, on a fresh short-lived deadline.</summary>
    private async Task FlushRemainingAsync()
    {
        var remaining = new List<SessionTokenUsageEvent>();
        while (_queue.Reader.TryRead(out var evt))
            remaining.Add(evt);

        if (remaining.Count == 0)
            return;

        using var cts = new CancellationTokenSource(_httpClient.Timeout);
        try
        {
            await PostBatchAsync(remaining, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final token usage flush failed; dropped {Count} events", remaining.Count);
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
