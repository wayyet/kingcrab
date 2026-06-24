using System.Net;
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
        WarnOnInsecureAuthConfig(http);

        // Disable auto-redirect: this is a fixed ingest endpoint, so a 3xx response can only be a
        // misconfiguration or a hijack. Following it would replay the Authorization: Bearer header
        // to whatever host the redirect points at, leaking the collector token off-box.
        _httpClient = HttpClientFactory.Create(allowAutoRedirect: false);
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

    /// <summary>
    /// Startup-time sanity checks for the Bearer auth setup. This is best-effort telemetry, so the
    /// checks only warn — they never throw and never block gateway startup.
    /// </summary>
    private void WarnOnInsecureAuthConfig(TokenUsageHttpConfig http)
    {
        // A token ref was configured but resolved to nothing (e.g. env:VAR is unset). The sink then
        // posts with no Authorization header, the collector answers 401, IsPermanentFailure treats it
        // as fatal, and every batch is silently dropped — with no obvious root cause. Surface it loudly.
        if (!string.IsNullOrWhiteSpace(http.AuthTokenRef) && string.IsNullOrEmpty(_authToken))
            _logger.LogWarning(
                "Token usage AuthTokenRef is configured but resolved to an empty value (e.g. the referenced " +
                "environment variable is unset). Requests will be sent WITHOUT an Authorization header, so the " +
                "collector will likely reject every batch with 401.");

        // A Bearer token would be sent in cleartext over plain http to a non-loopback host, where it
        // can be sniffed on the wire. Warn but don't block: loopback http is fine and is the default.
        if (!string.IsNullOrEmpty(_authToken)
            && string.Equals(_collectorUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !_collectorUrl.IsLoopback)
            _logger.LogWarning(
                "Token usage collector URL {CollectorUrl} uses plaintext http to a non-loopback host while an " +
                "Authorization Bearer token is configured; the token will be transmitted in cleartext. Use https " +
                "or a loopback address.",
                _collectorUrl);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HTTP token usage sink enabled; collector={CollectorUrl}", _collectorUrl);

        // Events collected but not yet shipped when shutdown is signalled; handed to the final
        // flush so an in-flight partial batch isn't dropped on the floor.
        List<SessionTokenUsageEvent>? pending = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await CollectBatchAsync(stoppingToken);
                if (batch.Count == 0)
                {
                    // An empty batch from a completed channel means no event will ever arrive again;
                    // break instead of spinning at 100% CPU. Nothing Completes the writer today, but
                    // guard against a future caller that does.
                    if (_queue.Reader.Completion.IsCompleted)
                        break;
                    continue;
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    // Collected while shutting down: SendBatchWithRetryAsync is bound to the cancelled
                    // token and would no-op, so hand this batch to the final flush instead.
                    pending = batch;
                    break;
                }

                await SendBatchWithRetryAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }

        await FlushRemainingAsync(pending);
    }

    /// <summary>
    /// Blocks until at least one event is available, then drains up to <see cref="_batchSize"/>
    /// events, waiting at most <see cref="_flushInterval"/> for a partial batch to fill.
    /// </summary>
    private async Task<List<SessionTokenUsageEvent>> CollectBatchAsync(CancellationToken ct)
    {
        var reader = _queue.Reader;
        var batch = new List<SessionTokenUsageEvent>(_batchSize);

        // Park here while idle. On shutdown return the (empty) batch instead of throwing, so the
        // caller can still run its final flush.
        try
        {
            if (!await reader.WaitToReadAsync(ct))
                return batch;
        }
        catch (OperationCanceledException)
        {
            return batch;
        }

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
        catch (OperationCanceledException)
        {
            // Either the flush interval elapsed or shutdown was requested; in both cases ship what
            // we've already collected rather than discarding it.
        }

        return batch;
    }

    // Cap retries for transient failures so a sustained collector outage can't pin the single
    // consumer loop forever (which would silently drop every later event via the bounded queue).
    // ~1+2+4+8+16+30+30 ≈ 91s of total backoff before the batch is abandoned.
    private const int MaxSendAttempts = 8;

    private async Task SendBatchWithRetryAsync(IReadOnlyList<SessionTokenUsageEvent> batch, CancellationToken ct)
    {
        // Serialize once up front and reuse the payload across retries; re-serializing the same batch
        // on every attempt is wasted CPU. A serialization failure can never be fixed by a re-POST, so
        // drop the batch immediately.
        string json;
        try
        {
            json = SerializeBatch(batch);
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex, "Token usage batch ({Count} events) failed to serialize; dropping batch", batch.Count);
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await PostJsonAsync(json, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsPermanentFailure(ex))
            {
                // Re-posting the same payload can never fix this (bad request / auth / serialization);
                // drop the batch and let the consumer move on instead of retrying forever.
                _logger.LogError(
                    ex, "Token usage collector rejected batch ({Count} events) with a non-retryable error; dropping batch",
                    batch.Count);
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= MaxSendAttempts)
                {
                    _logger.LogError(
                        ex, "Token usage collector POST failed after {Attempts} attempts ({Count} events); dropping batch",
                        attempt, batch.Count);
                    return;
                }

                _logger.LogWarning(
                    ex, "Token usage collector POST failed ({Count} events, attempt {Attempt}/{Max}); retrying in {Delay}s",
                    batch.Count, attempt, MaxSendAttempts, backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    /// <summary>
    /// True for failures that re-posting the same batch can never recover from: serialization errors,
    /// unexpected 3xx redirects (auto-redirect is disabled for this fixed endpoint, so a redirect is a
    /// misconfiguration/hijack), and 4xx client errors — except 408 Request Timeout and 429 Too Many
    /// Requests, which are transient and worth retrying with backoff.
    /// </summary>
    private static bool IsPermanentFailure(Exception ex) => ex switch
    {
        JsonException => true,
        HttpRequestException { StatusCode: { } code } =>
            (int)code is >= 300 and < 500
                && code is not HttpStatusCode.RequestTimeout
                && code is not HttpStatusCode.TooManyRequests,
        _ => false,
    };

    private static string SerializeBatch(IReadOnlyList<SessionTokenUsageEvent> batch)
    {
        var array = batch as SessionTokenUsageEvent[] ?? batch.ToArray();
        return JsonSerializer.Serialize(array, TokenUsageJsonContext.Default.SessionTokenUsageEventArray);
    }

    private async Task PostJsonAsync(string json, CancellationToken ct)
    {
        // A fresh request/content per call: HttpContent can't be re-sent once consumed. Only the
        // serialized json is reused across retries (cheap StringContent wrapping, no re-serialize).
        using var request = new HttpRequestMessage(HttpMethod.Post, _collectorUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_authToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Best-effort drain at shutdown, on a fresh short-lived deadline. Any <paramref name="pending"/>
    /// batch collected just before cancellation is shipped first, then whatever is still queued.
    /// </summary>
    private async Task FlushRemainingAsync(List<SessionTokenUsageEvent>? pending = null)
    {
        var remaining = pending ?? new List<SessionTokenUsageEvent>();
        while (_queue.Reader.TryRead(out var evt))
            remaining.Add(evt);

        if (remaining.Count == 0)
            return;

        using var cts = new CancellationTokenSource(_httpClient.Timeout);
        try
        {
            await PostJsonAsync(SerializeBatch(remaining), cts.Token);
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
