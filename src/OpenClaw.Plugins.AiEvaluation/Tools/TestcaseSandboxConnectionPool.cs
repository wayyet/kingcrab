using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Security;
using OpenClaw.Plugins.AiEvaluation.Configs;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

public sealed class TestcaseSandboxConnectionPool(AiEvaluationConfig config, ILogger logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ClientWebSocket> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private bool _disposed;

    public bool IsConnected(string role)
    {
        return _connections.TryGetValue(role, out var ws)
            && ws.State == WebSocketState.Open;
    }

    public async Task<JsonElement> SendPromptAsync(string role, string prompt, CancellationToken ct)
    {
        SandboxEndpointConfig endpoint = ResolveEndpoint(role);
        if (string.IsNullOrWhiteSpace(endpoint.WsUrl))
            throw new InvalidOperationException($"Sandbox '{role}' WsUrl is not configured.");

        var ws = await GetOrConnectAsync(role, endpoint, ct);
        var requestId = Environment.TickCount;

        var sendJson = BuildChatMessage(requestId, prompt, endpoint.SystemPrompt);
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
                        throw new InvalidOperationException($"Sandbox '{role}' call failed: {err}");
                    }

                    if (!msg.TryGetProperty("result", out var res))
                        throw new InvalidOperationException($"Sandbox '{role}': missing result.");
                    return res.Clone();
                }

                if (string.Equals(msgType, "error", StringComparison.Ordinal))
                {
                    var errorMsg = msg.TryGetProperty("message", out var em) ? em.GetString() : "unknown";
                    throw new InvalidOperationException($"Sandbox '{role}' error: {errorMsg}");
                }

                if (string.Equals(msgType, "event", StringComparison.Ordinal))
                {
                    logger.LogDebug("Sandbox '{Role}' event ignored", role);
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await DisconnectAsync(role);
            throw new TimeoutException($"Sandbox '{role}' request timed out after {endpoint.RequestTimeoutSeconds}s.");
        }
        catch (WebSocketException ex)
        {
            await DisconnectAsync(role);
            throw new InvalidOperationException($"Sandbox '{role}' WebSocket error: {ex.Message}", ex);
        }
    }

    private async Task<ClientWebSocket> GetOrConnectAsync(
        string role, SandboxEndpointConfig endpoint, CancellationToken ct)
    {
        if (_connections.TryGetValue(role, out var existing)
            && existing.State == WebSocketState.Open)
            return existing;

        await _connectionLock.WaitAsync(ct);
        try
        {
            if (_connections.TryGetValue(role, out var doubleCheck)
                && doubleCheck.State == WebSocketState.Open)
                return doubleCheck;

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
                    throw new InvalidOperationException($"Sandbox '{role}' token not configured.");

                await SendJsonAsync(ws, BuildAuth(token), ct);
                var authReply = await ReceiveJsonAsync(ws, ct);
                var authType = authReply.TryGetProperty("type", out var at) ? at.GetString() : null;
                if (!string.Equals(authType, "auth_ok", StringComparison.Ordinal))
                {
                    var message = authReply.TryGetProperty("message", out var msg) ? msg.GetString() : null;
                    throw new InvalidOperationException($"Sandbox '{role}' auth failed: {authType} {message}");
                }
            }

            _connections[role] = ws;
            logger.LogInformation("Sandbox '{Role}' connected to {Url}", role, endpoint.WsUrl);
            return ws;
        }
        catch
        {
            _connections.TryRemove(role, out var stale);
            stale?.Dispose();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task DisconnectAsync(string role)
    {
        if (_connections.TryRemove(role, out var ws))
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
            logger.LogInformation("Sandbox '{Role}' disconnected", role);
        }
    }

    private SandboxEndpointConfig ResolveEndpoint(string role)
    {
        return role switch
        {
            "generator" => config.Generator,
            "validator" => config.Validator,
            _ => throw new ArgumentException($"Unknown sandbox role: '{role}'.", nameof(role))
        };
    }

    private static Uri BuildWebSocketUrl(string url)
    {
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid sandbox WsUrl: {url}");

        var scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new UriBuilder(baseUri) { Scheme = scheme }.Uri;
    }

    private static byte[] BuildChatMessage(int id, string prompt, string systemPrompt)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("type", "chat");
            writer.WriteString("prompt", prompt);
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                writer.WriteString("system_prompt", systemPrompt);
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

        foreach (var (role, _) in _connections.ToArray())
            await DisconnectAsync(role);

        _connectionLock.Dispose();
    }
}
