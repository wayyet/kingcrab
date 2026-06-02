using Microsoft.Extensions.Configuration;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Core;
using OpenSandbox.Models;
using System.Net.Http.Headers;

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
    public int GatewayPort { get; set; } = 18789;
    public string[] Entrypoint { get; set; } = ["/app/OpenClaw.Gateway"];
    public KingCrabGatewaySettings KingCrab { get; set; } = new();

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
/// KingCrab 网关运行时配置，对应 appsettings.json 中 OpenSandbox:KingCrab 节点
/// </summary>
public sealed class KingCrabGatewaySettings
{
    public string AuthToken { get; set; } = "king-crab-demo-token";
    public string OidcAuthority { get; set; } = "http://test-passport.zyagi.cn:1080/realms/ai4cbrain";
    public string OidcAudience { get; set; } = "account";
    public string AllowedOrigin { get; set; } = "http://183.6.65.92:90";
    public string LlmModel { get; set; } = "MiniMax-M2.5";
    public string LlmEndpoint { get; set; } = "https://api.minimaxi.com/v1";
    public string LlmApiKey { get; set; } = string.Empty;
    public string[] NetworkEgressAllowHosts { get; set; } = ["test-passport.zyagi.cn", "api.minimaxi.com"];

    public Dictionary<string, string> BuildRuntimeEnv(int gatewayPort) => new()
    {
        ["Logging__LogLevel__Default"] = "Debug",
        ["Logging__LogLevel__Microsoft"] = "Debug",
        ["Logging__LogLevel__Microsoft.AspNetCore"] = "Debug",
        ["OpenClaw__BindAddress"] = "0.0.0.0",
        ["OpenClaw__Port"] = gatewayPort.ToString(),
        ["OpenClaw__AuthToken"] = AuthToken,
        ["OpenClaw__Security__AlwaysRequireAuth"] = "true",
        ["OpenClaw__Security__AllowQueryStringToken"] = "false",
        ["OpenClaw__Security__AllowedOrigins__0"] = AllowedOrigin,
        ["OpenClaw__Security__AuthMode"] = "oidc",
        ["OpenClaw__Security__Oidc__Authority"] = OidcAuthority,
        ["OpenClaw__Security__Oidc__Audience"] = OidcAudience,
        ["OpenClaw__Security__Oidc__RequireHttpsMetadata"] = "false",
        ["OpenClaw__Security__AllowUnsafeToolingOnPublicBind"] = "true",
        ["OpenClaw__Security__AllowPluginBridgeOnPublicBind"] = "true",
        ["OpenClaw__Security__AllowRawSecretRefsOnPublicBind"] = "true",
        ["OpenClaw__Plugins__Enabled"] = "true",
        ["OpenClaw__Tooling__AllowShell"] = "true",
        ["OpenClaw__Tooling__WorkspaceRoot"] = "/workspace",
        ["OpenClaw__Memory__StoragePath"] = "/app/memory",
        ["MODEL_PROVIDER_KEY"] = LlmApiKey,
        ["MODEL_PROVIDER_MODEL"] = LlmModel,
        ["MODEL_PROVIDER_ENDPOINT"] = LlmEndpoint,
    };
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

        // 合并环境变量：KingCrab 模板 + 用户覆盖（extraEnv 优先）
        var env = _settings.KingCrab.BuildRuntimeEnv(_settings.GatewayPort);
        if (extraEnv != null)
            foreach (var kv in extraEnv)
                env[kv.Key] = kv.Value;

        // 出站网络策略
        NetworkPolicy? networkPolicy = _settings.KingCrab.NetworkEgressAllowHosts.Length > 0
            ? new NetworkPolicy
            {
                DefaultAction = NetworkRuleAction.Allow,
                Egress = [.. _settings.KingCrab.NetworkEgressAllowHosts.Select(h =>
                    new NetworkRule { Action = NetworkRuleAction.Allow, Target = h })]
            }
            : null;

        Console.WriteLine($"  正在创建沙箱 (image={_settings.Image}, timeout={timeout}s) ...");
        Console.WriteLine($"  入口程序 : {string.Join(" ", _settings.Entrypoint)}");
        Console.WriteLine($"  网关端口 : {_settings.GatewayPort}");

        var sandbox = await Sandbox.CreateAsync(new SandboxCreateOptions
        {
            ConnectionConfig = _connection,
            Image = _settings.Image,
            TimeoutSeconds = timeout,
            Metadata = metadata,
            Entrypoint = _settings.Entrypoint,
            NetworkPolicy = networkPolicy,
            Env = env,
            ManualCleanup = true //默认创建的沙箱不会超时被清理 需要自己清理
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

        // Running 状态时自动展示网关访问信息
        using var stateDoc = System.Text.Json.JsonDocument.Parse(body);
        if (stateDoc.RootElement.TryGetProperty("status", out var stEl)
            && stEl.TryGetProperty("state", out var stateEl)
            && stateEl.GetString() == "Running")
        {
            await PrintGatewayAccessInfoAsync(sandboxId);
        }
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
    //  KingCrab 网关访问信息
    // ------------------------------------------------------------------ //

    /// <summary>
    /// 解析网关端点并打印完整访问地址与 curl 示例，便于用户直接测试
    /// </summary>
    public async Task PrintGatewayAccessInfoAsync(string sandboxId)
    {
        if (_settings.GatewayPort <= 0) return;

        var http = _connection.GetHttpClient();
        var url = $"{_connection.GetBaseUrl().TrimEnd('/')}/sandboxes/{Uri.EscapeDataString(sandboxId)}/endpoints/{_settings.GatewayPort}";
        try
        {
            using var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  [网关端点] 获取失败: HTTP {(int)resp.StatusCode}");
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var endpointRaw = doc.RootElement.TryGetProperty("endpoint", out var epEl)
                ? epEl.GetString() ?? "" : "";
            var baseUrl = NormalizeUrl(endpointRaw);

            var routeHeaders = new Dictionary<string, string>();
            if (doc.RootElement.TryGetProperty("headers", out var hEl)
                && hEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var h in hEl.EnumerateObject())
                    routeHeaders[h.Name] = h.Value.GetString() ?? "";
            }

            var token = _settings.KingCrab.AuthToken;
            var extraH = routeHeaders.Count > 0
                ? " " + string.Join(" ", routeHeaders.Select(h => $"-H \"{h.Key}: {h.Value}\""))
                : "";
            var shortId = sandboxId.Length >= 8 ? sandboxId[..8] + "..." : sandboxId;

            Console.WriteLine();
            Console.WriteLine($"  ┌─── 网关访问信息 ({shortId}) ──────────────────────────────────────");
            Console.WriteLine($"  │  Base URL   : {baseUrl}");
            Console.WriteLine($"  │  健康检查   : {baseUrl}/health");
            Console.WriteLine($"  │  对话接口   : {baseUrl}/chat");
            Console.WriteLine($"  │  根路径     : {baseUrl}/");
            Console.WriteLine($"  │  Auth Token : {token}");
            if (routeHeaders.Count > 0)
            {
                Console.WriteLine("  │  路由请求头 :");
                foreach (var h in routeHeaders)
                    Console.WriteLine($"  │    {h.Key}: {h.Value}");
            }
            Console.WriteLine("  │");
            Console.WriteLine("  │  curl 健康检查:");
            Console.WriteLine($"  │    curl -H \"Authorization: Bearer {token}\"{extraH} {baseUrl}/health");
            Console.WriteLine("  │  curl 对话 (SSE):");
            Console.WriteLine($"  │    curl -H \"Authorization: Bearer {token}\"{extraH} \\");
            Console.WriteLine( "  │         -H \"Content-Type: application/json\" \\");
            Console.WriteLine($"  │         -d '{{\"messages\":[{{\"role\":\"user\",\"content\":\"你好\"}}]}}' \\");
            Console.WriteLine($"  │         {baseUrl}/chat");
            Console.WriteLine("  └──────────────────────────────────────────────────────────────────");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [网关端点] 查询异常: {ex.Message}");
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

    private static string NormalizeUrl(string endpointAddress)
        => endpointAddress.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? endpointAddress.TrimEnd('/')
            : $"http://{endpointAddress}".TrimEnd('/');

    private static string FormatDateTime(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "—")
            return "—";
        return DateTimeOffset.TryParse(raw, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : raw;
    }
}
