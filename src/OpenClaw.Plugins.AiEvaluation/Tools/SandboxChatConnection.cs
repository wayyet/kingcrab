using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Security;
using OpenClaw.Plugins.AiEvaluation.Configs;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

public sealed class SandboxChatConnection(SandboxEndpointConfig endpoint, ILogger logger) : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task<string> SendMessageAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint.WsUrl))
            throw new InvalidOperationException("Target sandbox WsUrl is not configured.");

        var ws = await GetOrConnectAsync(ct);
        var requestId = Environment.TickCount;

        var sendJson = BuildChatMessage(requestId, message);
        await SendJsonAsync(ws, sendJson, ct);

        var requestTimeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.RequestTimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(requestTimeout);

        try
        {
            while (true)
            {
                var msg = await ReceiveJsonAsync(ws, cts.Token);
                var msgType = msg.TryGetProperty("type", out var mt) ? mt.GetString() : null;

                if (string.Equals(msgType, "result", StringComparison.Ordinal))
                {
                    var id = msg.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt32()
                        : -1;
                    if (id != requestId)
                        continue;

                    if (!msg.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.True)
                    {
                        var err = msg.TryGetProperty("error", out var e) ? e.ToString() : "(unknown error)";
                        throw new InvalidOperationException($"Target sandbox call failed: {err}");
                    }

                    var text = msg.TryGetProperty("result", out var res)
                        && res.TryGetProperty("text", out var t)
                        && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? ""
                        : msg.GetRawText();
                    return text;
                }

                if (string.Equals(msgType, "error", StringComparison.Ordinal))
                {
                    var errorMsg = msg.TryGetProperty("message", out var em) ? em.GetString() : "unknown";
                    throw new InvalidOperationException($"Target sandbox error: {errorMsg}");
                }

                if (string.Equals(msgType, "event", StringComparison.Ordinal))
                {
                    logger.LogDebug("Target sandbox event ignored");
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await DisconnectAsync();
            throw new TimeoutException($"Target sandbox request timed out after {endpoint.RequestTimeoutSeconds}s.");
        }
        catch (WebSocketException ex)
        {
            await DisconnectAsync();
            throw new InvalidOperationException($"Target sandbox WebSocket error: {ex.Message}", ex);
        }
    }

    private async Task<ClientWebSocket> GetOrConnectAsync(CancellationToken ct)
    {
        if (_ws is { State: WebSocketState.Open })
            return _ws;

        await _lock.WaitAsync(ct);
        try
        {
            if (_ws is { State: WebSocketState.Open })
                return _ws;

            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            var url = BuildWebSocketUrl(endpoint.WsUrl!);
            var connectTimeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.ConnectTimeoutSeconds));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(connectTimeout);

            await ws.ConnectAsync(url, cts.Token);

            var first = await ReceiveJsonAsync(ws, ct);
            var firstType = first.TryGetProperty("type", out var ft) ? ft.GetString() : null;

            if (string.Equals(firstType, "auth_required", StringComparison.Ordinal))
            {
                var token = SecretResolver.Resolve(endpoint.AuthToken);
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Target sandbox token not configured.");

                await SendJsonAsync(ws, BuildAuth(token), ct);
                var authReply = await ReceiveJsonAsync(ws, ct);
                var authType = authReply.TryGetProperty("type", out var at) ? at.GetString() : null;
                if (!string.Equals(authType, "auth_ok", StringComparison.Ordinal))
                {
                    var authMessage = authReply.TryGetProperty("message", out var am) ? am.GetString() : null;
                    throw new InvalidOperationException($"Target sandbox auth failed: {authType} {authMessage}");
                }
            }

            _ws = ws;
            logger.LogInformation("Target sandbox connected to {Url}", endpoint.WsUrl);
            return ws;
        }
        catch
        {
            var stale = _ws;
            _ws = null;
            stale?.Dispose();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DisconnectAsync()
    {
        var ws = _ws;
        _ws = null;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
                }
            }
            catch { }
            ws.Dispose();
            logger.LogInformation("Target sandbox disconnected");
        }
    }

    private static Uri BuildWebSocketUrl(string url)
    {
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid sandbox WsUrl: {url}");

        var scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new UriBuilder(baseUri) { Scheme = scheme }.Uri;
    }

    private static byte[] BuildChatMessage(int id, string message)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("type", "chat");
            writer.WriteString("prompt", message);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static byte[] BuildAuth(string token)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "auth");
            writer.WriteString("access_token", token);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, byte[] json, CancellationToken ct)
        => await ws.SendAsync(json.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("WebSocket closed.");

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        var text = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DisconnectAsync();
        _lock.Dispose();
    }
}
