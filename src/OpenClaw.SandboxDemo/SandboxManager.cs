using Microsoft.Extensions.Configuration;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Core;
using OpenSandbox.Models;

namespace OpenClaw.SandboxDemo;

/// <summary>
/// 从 appsettings.json 读取的 OpenSandbox 配置项
/// </summary>
public sealed class OpenSandboxSettings
{
    public string Domain { get; set; } = "opensandbox-server.zyagi.cn:1080";
    public string Protocol { get; set; } = "Http";
    public string Image { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 43200;
    public string DefaultMetadataCreatedBy { get; set; } = "csharp-sandbox-demo";

    public ConnectionConfig BuildConnectionConfig()
    {
        var protocol = Enum.Parse<ConnectionProtocol>(Protocol, ignoreCase: true);
        return new ConnectionConfig(new ConnectionConfigOptions
        {
            Domain = Domain,
            Protocol = protocol,
        });
    }
}

/// <summary>
/// 沙箱生命周期与列出管理的核心服务
/// </summary>
public sealed class SandboxLifecycleManager
{
    private readonly OpenSandboxSettings _settings;
    private readonly ConnectionConfig _connection;

    public SandboxLifecycleManager(OpenSandboxSettings settings)
    {
        _settings = settings;
        _connection = settings.BuildConnectionConfig();
    }

    // ------------------------------------------------------------------ //
    //  创建
    // ------------------------------------------------------------------ //

    /// <summary>
    /// 创建沙箱并等待进入 Running 状态
    /// </summary>
    public async Task<Sandbox> CreateAsync(
        string? label = null,
        int? timeoutSeconds = null,
        Dictionary<string, string>? extraEnv = null,
        CancellationToken ct = default)
    {
        var timeout = timeoutSeconds ?? _settings.TimeoutSeconds;
        var metadata = new Dictionary<string, string>
        {
            ["created-by"] = _settings.DefaultMetadataCreatedBy,
        };
        if (!string.IsNullOrWhiteSpace(label))
            metadata["label"] = label;

        Console.WriteLine($"  正在创建沙箱 (image={_settings.Image}, timeout={timeout}s) ...");

        var sandbox = await Sandbox.CreateAsync(new SandboxCreateOptions
        {
            ConnectionConfig = _connection,
            Image = _settings.Image,
            TimeoutSeconds = timeout,
            Metadata = metadata,
            Env = extraEnv,
        }, ct);

        Console.WriteLine($"  沙箱已创建: {sandbox.Id}");
        return sandbox;
    }

    // ------------------------------------------------------------------ //
    //  查询 / 列出
    // ------------------------------------------------------------------ //

    /// <summary>
    /// 获取单个沙箱详情
    /// </summary>
    public async Task PrintSandboxDetailAsync(string sandboxId)
    {
        var http = _connection.GetHttpClient();
        var url = $"{_connection.GetBaseUrl().TrimEnd('/')}/sandboxes/{Uri.EscapeDataString(sandboxId)}";
        using var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  [ERROR] HTTP {(int)resp.StatusCode}: {body}");
            return;
        }

        PrintSandboxJson(body);
    }

    /// <summary>
    /// 分页列出所有沙箱（可按 state 过滤）
    /// </summary>
    public async Task ListSandboxesAsync(string? stateFilter = null, int page = 1, int pageSize = 20)
    {
        var http = _connection.GetHttpClient();
        var baseUrl = _connection.GetBaseUrl().TrimEnd('/');

        var query = $"page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(stateFilter))
            query += $"&state={Uri.EscapeDataString(stateFilter)}";

        var url = $"{baseUrl}/sandboxes?{query}";
        using var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  [ERROR] HTTP {(int)resp.StatusCode}: {body}");
            return;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        var items = root.TryGetProperty("items", out var itemsEl)
            ? itemsEl.EnumerateArray().ToList()
            : [];

        var pagination = root.TryGetProperty("pagination", out var pgEl) ? pgEl : default;
        int totalItems = pagination.ValueKind != System.Text.Json.JsonValueKind.Undefined
            && pagination.TryGetProperty("totalItems", out var tiEl) ? tiEl.GetInt32() : items.Count;
        int totalPages = pagination.ValueKind != System.Text.Json.JsonValueKind.Undefined
            && pagination.TryGetProperty("totalPages", out var tpEl) ? tpEl.GetInt32() : 1;

        Console.WriteLine($"\n  共 {totalItems} 个沙箱，第 {page}/{totalPages} 页，每页 {pageSize} 条：");
        Console.WriteLine($"  {"ID",-36}  {"状态",-12}  {"创建时间",-25}  {"过期时间",-25}  {"标签"}");
        Console.WriteLine("  " + new string('-', 120));

        foreach (var item in items)
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var state = item.TryGetProperty("status", out var statusEl)
                && statusEl.TryGetProperty("state", out var stEl)
                ? stEl.GetString() ?? "" : "";
            var createdAt = item.TryGetProperty("createdAt", out var caEl) ? caEl.GetString() ?? "" : "";
            var expiresAt = item.TryGetProperty("expiresAt", out var eaEl) ? eaEl.GetString() ?? "—" : "—";
            var labelVal = item.TryGetProperty("metadata", out var metaEl)
                && metaEl.TryGetProperty("label", out var lbEl)
                ? lbEl.GetString() ?? "" : "";

            Console.WriteLine($"  {id,-36}  {state,-12}  {FormatDateTime(createdAt),-25}  {FormatDateTime(expiresAt),-25}  {labelVal}");
        }

        if (items.Count == 0)
            Console.WriteLine("  （无数据）");
    }

    // ------------------------------------------------------------------ //
    //  暂停 / 恢复
    // ------------------------------------------------------------------ //

    public async Task PauseSandboxAsync(string sandboxId)
    {
        var http = _connection.GetHttpClient();
        var url = $"{_connection.GetBaseUrl().TrimEnd('/')}/sandboxes/{Uri.EscapeDataString(sandboxId)}/pause";
        using var resp = await http.PostAsync(url, null);
        HandleEmptyResponse(resp, "暂停");
    }

    public async Task ResumeSandboxAsync(string sandboxId)
    {
        var http = _connection.GetHttpClient();
        var url = $"{_connection.GetBaseUrl().TrimEnd('/')}/sandboxes/{Uri.EscapeDataString(sandboxId)}/resume";
        using var resp = await http.PostAsync(url, null);
        HandleEmptyResponse(resp, "恢复");
    }

    // ------------------------------------------------------------------ //
    //  续期
    // ------------------------------------------------------------------ //

    public async Task RenewExpirationAsync(string sandboxId, int additionalSeconds)
    {
        // 先查询当前沙箱得到现有过期时间，然后追加
        var http = _connection.GetHttpClient();
        var baseUrl = _connection.GetBaseUrl().TrimEnd('/');

        var getUrl = $"{baseUrl}/sandboxes/{Uri.EscapeDataString(sandboxId)}";
        using var getResp = await http.GetAsync(getUrl);
        if (!getResp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  [ERROR] 查询沙箱失败: HTTP {(int)getResp.StatusCode}");
            return;
        }

        var getBody = await getResp.Content.ReadAsStringAsync();
        using var getDoc = System.Text.Json.JsonDocument.Parse(getBody);

        DateTimeOffset newExpiry;
        if (getDoc.RootElement.TryGetProperty("expiresAt", out var existingEl)
            && existingEl.ValueKind == System.Text.Json.JsonValueKind.String
            && DateTimeOffset.TryParse(existingEl.GetString(), out var existing))
        {
            newExpiry = existing.AddSeconds(additionalSeconds);
        }
        else
        {
            newExpiry = DateTimeOffset.UtcNow.AddSeconds(additionalSeconds);
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            expiresAt = newExpiry.ToString("o"),
        });

        var renewUrl = $"{baseUrl}/sandboxes/{Uri.EscapeDataString(sandboxId)}/renew-expiration";
        using var renewResp = await http.PostAsync(
            renewUrl,
            new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        if (renewResp.IsSuccessStatusCode)
        {
            var renewBody = await renewResp.Content.ReadAsStringAsync();
            using var renewDoc = System.Text.Json.JsonDocument.Parse(renewBody);
            var newExp = renewDoc.RootElement.TryGetProperty("expiresAt", out var nEl) ? nEl.GetString() : null;
            Console.WriteLine($"  续期成功，新过期时间: {FormatDateTime(newExp ?? "")}");
        }
        else
        {
            var errBody = await renewResp.Content.ReadAsStringAsync();
            Console.WriteLine($"  [ERROR] 续期失败: HTTP {(int)renewResp.StatusCode} {errBody}");
        }
    }

    // ------------------------------------------------------------------ //
    //  删除
    // ------------------------------------------------------------------ //

    public async Task DeleteSandboxAsync(string sandboxId)
    {
        var http = _connection.GetHttpClient();
        var url = $"{_connection.GetBaseUrl().TrimEnd('/')}/sandboxes/{Uri.EscapeDataString(sandboxId)}";
        using var resp = await http.DeleteAsync(url);
        HandleEmptyResponse(resp, "删除");
    }

    // ------------------------------------------------------------------ //
    //  轮询等待状态
    // ------------------------------------------------------------------ //

    /// <summary>
    /// 轮询直到沙箱到达目标状态（或超时/失败）
    /// </summary>
    public async Task<string?> WaitForStateAsync(
        string sandboxId,
        string targetState,
        int maxWaitSeconds = 120,
        int pollIntervalMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        string? lastState = null;

        Console.Write($"  等待沙箱进入 [{targetState}] 状态 ");
        while (DateTime.UtcNow < deadline)
        {
            var http = _connection.GetHttpClient();
            var url = $"{_connection.GetBaseUrl().TrimEnd('/')}/sandboxes/{Uri.EscapeDataString(sandboxId)}";
            try
            {
                using var resp = await http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("status", out var statusEl)
                        && statusEl.TryGetProperty("state", out var stEl))
                    {
                        lastState = stEl.GetString();
                        if (string.Equals(lastState, targetState, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($" -> {lastState}");
                            return lastState;
                        }

                        if (lastState is "Terminated" or "Failed")
                        {
                            Console.WriteLine($" -> {lastState} (终止)");
                            return lastState;
                        }
                    }
                }
            }
            catch { /* 忽略单次网络抖动 */ }

            Console.Write(".");
            await Task.Delay(pollIntervalMs);
        }

        Console.WriteLine($" -> 超时 (last={lastState ?? "unknown"})");
        return lastState;
    }

    // ------------------------------------------------------------------ //
    //  获取端点
    // ------------------------------------------------------------------ //

    public async Task PrintEndpointAsync(string sandboxId, int port)
    {
        var http = _connection.GetHttpClient();
        var url = $"{_connection.GetBaseUrl().TrimEnd('/')}/sandboxes/{Uri.EscapeDataString(sandboxId)}/endpoints/{port}";
        using var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  [ERROR] HTTP {(int)resp.StatusCode}: {body}");
            return;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var endpoint = doc.RootElement.TryGetProperty("endpoint", out var epEl) ? epEl.GetString() : null;
        Console.WriteLine($"  端点: {endpoint ?? "(无)"}");

        if (doc.RootElement.TryGetProperty("headers", out var headersEl)
            && headersEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var h in headersEl.EnumerateObject())
                Console.WriteLine($"    {h.Name}: {h.Value.GetString()}");
        }
    }

    // ------------------------------------------------------------------ //
    //  私有辅助
    // ------------------------------------------------------------------ //

    private static void PrintSandboxJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
        var createdAt = root.TryGetProperty("createdAt", out var caEl) ? caEl.GetString() ?? "" : "";
        var expiresAt = root.TryGetProperty("expiresAt", out var eaEl) ? eaEl.GetString() ?? "—" : "—";

        string state = "", reason = "", message = "";
        if (root.TryGetProperty("status", out var statusEl))
        {
            state = statusEl.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "" : "";
            reason = statusEl.TryGetProperty("reason", out var reEl) ? reEl.GetString() ?? "" : "";
            message = statusEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";
        }

        string imageUri = "";
        if (root.TryGetProperty("image", out var imageEl))
            imageUri = imageEl.TryGetProperty("uri", out var uriEl) ? uriEl.GetString() ?? "" : "";

        var entrypoint = root.TryGetProperty("entrypoint", out var epEl2)
            ? string.Join(" ", epEl2.EnumerateArray().Select(e => e.GetString()))
            : "";

        Console.WriteLine($"  ID         : {id}");
        Console.WriteLine($"  状态       : {state}  reason={reason}  message={message}");
        Console.WriteLine($"  镜像       : {imageUri}");
        Console.WriteLine($"  入口       : {entrypoint}");
        Console.WriteLine($"  创建时间   : {FormatDateTime(createdAt)}");
        Console.WriteLine($"  过期时间   : {FormatDateTime(expiresAt)}");

        if (root.TryGetProperty("metadata", out var metaEl)
            && metaEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var kv in metaEl.EnumerateObject())
                Console.WriteLine($"  meta[{kv.Name}] : {kv.Value.GetString()}");
        }
    }

    private static void HandleEmptyResponse(System.Net.Http.HttpResponseMessage resp, string operation)
    {
        if ((int)resp.StatusCode is 202 or 204 or 200)
            Console.WriteLine($"  {operation}请求已接受 (HTTP {(int)resp.StatusCode})");
        else
        {
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Console.WriteLine($"  [ERROR] {operation}失败: HTTP {(int)resp.StatusCode} {body}");
        }
    }

    private static string FormatDateTime(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "—")
            return "—";
        return DateTimeOffset.TryParse(raw, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : raw;
    }
}
