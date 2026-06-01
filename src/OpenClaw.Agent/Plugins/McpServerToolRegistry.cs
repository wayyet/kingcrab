using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Discovers tools from configured MCP servers and registers them as native OpenClaw tools.
/// </summary>
public sealed class McpServerToolRegistry : IDisposable, IAsyncDisposable
{
    private readonly McpPluginsConfig _config;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly List<DiscoveredMcpTool> _tools = [];
    private readonly List<McpClient> _clients = [];
    private Task? _disposeTask;
    private bool _loaded;
    private bool _registered;
    private bool _disposed;

    // Workspace-sourced servers (from .kingcrab/mcp.json) — tracked separately so they can be diffed and reloaded.
    private readonly Dictionary<string, (McpClient Client, List<DiscoveredMcpTool> Tools, McpServerConfig Config)> _workspaceServers
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a registry for configured MCP servers.
    /// </summary>
    public McpServerToolRegistry(McpPluginsConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Connects to configured MCP servers and registers discovered tools into the native registry.
    /// </summary>
    public async Task RegisterToolsAsync(NativePluginRegistry nativeRegistry, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_registered)
                return;

            var tools = await LoadInternalAsync(ct);
            foreach (var tool in tools)
                nativeRegistry.RegisterExternalTool(tool.Tool, tool.PluginId, tool.Detail);

            _registered = true;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    internal async Task<IReadOnlyList<DiscoveredMcpTool>> LoadAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            return _loaded ? _tools : await LoadInternalAsync(ct);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private async Task<IReadOnlyList<DiscoveredMcpTool>> LoadInternalAsync(CancellationToken ct)
    {
        if (_loaded)
            return _tools;

        if (!_config.Enabled)
        {
            _loaded = true;
            return _tools;
        }

        var discoveredTools = new List<DiscoveredMcpTool>();
        var discoveredClients = new List<McpClient>();

        foreach (var (serverId, serverConfig) in _config.Servers ?? [])
        {
            if (!serverConfig.Enabled)
                continue;

            McpClient? client = null;
            try
            {
                var transport = CreateTransport(serverId, serverConfig);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(serverConfig.StartupTimeoutSeconds));
                client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);

                var displayName = string.IsNullOrWhiteSpace(serverConfig.Name) ? serverId : serverConfig.Name!;
                var pluginId = $"mcp:{serverId}";

                var tools = await LoadToolsFromClientAsync(client, serverId, pluginId, displayName, serverConfig, ct);

                foreach (var tool in tools)
                {
                    discoveredTools.Add(new DiscoveredMcpTool(
                        pluginId,
                        new McpNativeTool(client, tool.LocalName, tool.RemoteName, tool.Description, tool.InputSchemaText),
                        displayName));
                }

                discoveredClients.Add(client);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (client is not null)
                    DisposeClient(client);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MCP server '{ServerId}' failed to connect or load tools and will be skipped. Check the server URL and SSL configuration.",
                    serverId);
                if (client is not null)
                    DisposeClient(client);
            }
        }

        _clients.AddRange(discoveredClients);
        _tools.AddRange(discoveredTools);
        _loaded = true;
        return _tools;
    }

    private async Task<IReadOnlyList<McpToolDescriptor>> LoadToolsFromClientAsync(
        McpClient client,
        string serverId,
        string pluginId,
        string displayName,
        McpServerConfig config,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));

        var allTools = new List<Tool>();
        var listParams = new ListToolsRequestParams();
        do
        {
            var page = await client.ListToolsAsync(listParams, cancellationToken: timeoutCts.Token);
            allTools.AddRange(page.Tools);
            var next = page.NextCursor;
            listParams = new ListToolsRequestParams { Cursor = string.IsNullOrEmpty(next) ? null : next };
        }
        while (listParams.Cursor is not null);

        var tools = new List<McpToolDescriptor>();
        foreach (var tool in allTools)
        {
            var remoteName = tool.Name;
            if (string.IsNullOrWhiteSpace(remoteName))
                throw new InvalidOperationException($"MCP server '{displayName}' returned a tool entry with an empty name.");

            var localName = ResolveToolName(serverId, config.ToolNamePrefix, remoteName);
            var description = !string.IsNullOrWhiteSpace(tool.Description)
                ? $"{tool.Description} (from MCP server '{displayName}')"
                : $"MCP tool '{remoteName}' from server '{displayName}'.";
            var inputSchema = ResolveInputSchemaText(tool.InputSchema);
            tools.Add(new McpToolDescriptor(localName, remoteName, description, inputSchema));
        }

        _logger.LogInformation("MCP server enabled: {ServerId} ({DisplayName}) with {ToolCount} tool(s)",
            serverId, displayName, tools.Count);
        return tools;
    }

    private static string ResolveToolName(string serverId, string? toolNamePrefix, string remoteName)
    {
        var prefix = toolNamePrefix;
        if (prefix is null)
            prefix = $"{SanitizePrefixPart(serverId)}.";

        var sanitizedRemoteName = SanitizeLlmToolNamePart(remoteName);
        return string.IsNullOrEmpty(prefix) ? sanitizedRemoteName : prefix + sanitizedRemoteName;
    }

    private static string SanitizePrefixPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "mcp";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsLlmToolNameChar(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch > 0x7F)
                sb.Append($"_u{(int)ch:x4}");
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "mcp" : sb.ToString();
    }

    /// <summary>
    /// Sanitizes a string so every character satisfies the LLM tool-name pattern <c>^[a-zA-Z0-9_.\-]+$</c>.
    /// Non-conforming characters (e.g. CJK, spaces) are replaced with <c>_uXXXX</c> (lowercase hex code point).
    /// </summary>
    private static string SanitizeLlmToolNamePart(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsLlmToolNameChar(ch))
                sb.Append(ch);
            else
                sb.Append($"_u{(int)ch:x4}");
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static bool IsLlmToolNameChar(char ch)
        => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
           || ch is '_' or '-' or '.';

    /// <summary>
    /// Hot-reloads workspace MCP servers from <paramref name="newServers"/>.
    /// Diffs against the previously loaded workspace servers: removes servers no longer present,
    /// adds new ones. Returns the changed tool sets so callers can update the LLM tool list.
    /// </summary>
    public async Task<McpWorkspaceReloadResult> ReloadWorkspaceServersAsync(
        Dictionary<string, McpServerConfig>? newServers,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var addedTools = new List<ITool>();
            var removedNames = new List<string>();

            newServers ??= new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);

            // Remove servers that are gone, disabled, or whose config has changed
            var toRemove = _workspaceServers.Keys
                .Where(id =>
                    !newServers.TryGetValue(id, out var cfg) ||
                    !cfg.Enabled ||
                    !IsSameConfig(cfg, _workspaceServers[id].Config))
                .ToList();

            foreach (var id in toRemove)
            {
                var (client, tools, _) = _workspaceServers[id];
                removedNames.AddRange(tools.Select(t => t.Tool.Name));
                _workspaceServers.Remove(id);
                try { DisposeClient(client); } catch { }
            }

            // Add servers that are new or were just removed due to config change
            foreach (var (serverId, serverConfig) in newServers)
            {
                if (!serverConfig.Enabled || _workspaceServers.ContainsKey(serverId))
                    continue;

                try
                {
                    var transport = CreateTransport(serverId, serverConfig);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(serverConfig.StartupTimeoutSeconds));
                    var client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);

                    var displayName = string.IsNullOrWhiteSpace(serverConfig.Name) ? serverId : serverConfig.Name!;
                    var pluginId = $"mcp:{serverId}";
                    var descriptors = await LoadToolsFromClientAsync(
                        client, serverId, pluginId, displayName, serverConfig, ct);

                    var discovered = descriptors
                        .Select(d => new DiscoveredMcpTool(
                            pluginId,
                            new McpNativeTool(client, d.LocalName, d.RemoteName, d.Description, d.InputSchemaText),
                            displayName))
                        .ToList();

                    _workspaceServers[serverId] = (client, discovered, serverConfig);
                    addedTools.AddRange(discovered.Select(d => d.Tool));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Workspace MCP: failed to connect to server '{ServerId}', skipping", serverId);
                }
            }

            return new McpWorkspaceReloadResult(addedTools, removedNames);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private static string ResolveInputSchemaText(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "{}";

        return inputSchema.GetRawText();
    }

    /// <summary>
    /// Returns true when the connection-relevant fields of two configs are identical.
    /// Any change to transport/URL/credentials/command forces a reconnect.
    /// </summary>
    private static bool IsSameConfig(McpServerConfig a, McpServerConfig b)
    {
        if (!string.Equals(a.NormalizeTransport(), b.NormalizeTransport(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(a.Url, b.Url, StringComparison.Ordinal))
            return false;
        if (!string.Equals(a.Command, b.Command, StringComparison.Ordinal))
            return false;
        if (!string.Equals(a.WorkingDirectory, b.WorkingDirectory, StringComparison.Ordinal))
            return false;
        if (!string.Equals(a.ToolNamePrefix, b.ToolNamePrefix, StringComparison.Ordinal))
            return false;
        if (!(a.Arguments ?? []).SequenceEqual(b.Arguments ?? [], StringComparer.Ordinal))
            return false;
        if (!DictEqual(a.Headers, b.Headers))
            return false;
        if (!DictEqual(a.Environment, b.Environment))
            return false;
        return true;
    }

    private static bool DictEqual(
        Dictionary<string, string> x,
        Dictionary<string, string> y)
    {
        if (x.Count != y.Count)
            return false;
        foreach (var (k, v) in x)
            if (!y.TryGetValue(k, out var yv) || !string.Equals(v, yv, StringComparison.Ordinal))
                return false;
        return true;
    }

    public void Dispose()
    {
        bool acquired = false;
        try
        {
            acquired = _loadSemaphore.Wait(TimeSpan.FromSeconds(5));
            if (!acquired)
            {
                _logger.LogWarning("McpServerToolRegistry.Dispose() timed out waiting for load semaphore, waiting indefinitely to ensure load completes");
                _loadSemaphore.Wait();
                acquired = true;
            }
        }
        catch (ObjectDisposedException)
        {
            _logger.LogWarning("McpServerToolRegistry.Dispose() encountered disposed semaphore, load may have completed concurrently");
            return;
        }

        try
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var client in _clients)
            {
                try { DisposeClient(client); }
                catch { }
            }
            _clients.Clear();

            foreach (var (_, entry) in _workspaceServers)
            {
                try { DisposeClient(entry.Client); }
                catch { }
            }
            _workspaceServers.Clear();
        }
        finally
        {
            if (acquired)
                _loadSemaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static IClientTransport CreateTransport(string serverId, McpServerConfig config)
    {
        var transport = config.NormalizeTransport();
        return transport switch
        {
            "stdio" => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command!,
                Arguments = config.Arguments ?? [],
                WorkingDirectory = config.WorkingDirectory,
                EnvironmentVariables = ResolveEnv(config.Environment),
                Name = serverId,
            }),
            "http" => CreateHttpTransport(serverId, config, HttpTransportMode.StreamableHttp),
            "sse"  => CreateHttpTransport(serverId, config, HttpTransportMode.Sse),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{config.Transport}' for server '{serverId}'.")
        };
    }

    private static HttpClientTransport CreateHttpTransport(string serverId, McpServerConfig config, HttpTransportMode mode)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(config.Url!),
            AdditionalHeaders = ResolveHeaders(config.Headers),
            TransportMode = mode,
            Name = serverId,
        };
        var httpClient = new HttpClient(new RemoveCharsetDelegatingHandler());
        return new HttpClientTransport(options, httpClient, ownsHttpClient: true);
    }

    private static Dictionary<string, string?>? ResolveEnv(Dictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return null;

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, rawValue) in environment)
        {
            if (rawValue is null)
            {
                resolved[name] = null;
                continue;
            }
            var value = SecretResolver.Resolve(rawValue);
            if (value is null && rawValue.StartsWith("env:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Environment variable '{name}' references unset env var '{rawValue[4..]}'");
            resolved[name] = value ?? rawValue;
        }

        return resolved;
    }

    private static Dictionary<string, string>? ResolveHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rawValue) in headers)
        {
            if (rawValue is null)
            {
                resolved[name] = string.Empty;
                continue;
            }
            var value = SecretResolver.Resolve(rawValue);
            if (value is null && rawValue.StartsWith("env:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Header '{name}' references unset env var '{rawValue[4..]}'");
            resolved[name] = value ?? rawValue;
        }

        return resolved;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpServerToolRegistry));
    }

    private static void DisposeClient(McpClient client)
    {
        if (client is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        if (client is IDisposable disposable)
            disposable.Dispose();
    }

    private sealed class RemoveCharsetDelegatingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content?.Headers?.ContentType is { CharSet: not null } contentType)
                contentType.CharSet = null;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<McpClient> clients;

        await _loadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            clients = [.. _clients];
            _clients.Clear();
            _tools.Clear();
        }
        finally
        {
            _loadSemaphore.Release();
        }

        foreach (var client in clients)
        {
            try
            {
                await DisposeClientAsync(client).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async ValueTask DisposeClientAsync(McpClient client)
    {
        if (client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (client is IDisposable disposable)
            disposable.Dispose();
    }


    internal sealed record DiscoveredMcpTool(string PluginId, ITool Tool, string Detail);
    private sealed record McpToolDescriptor(string LocalName, string RemoteName, string Description, string InputSchemaText);
}

/// <summary>Result of a workspace MCP server hot-reload: tools to add and tool names to remove.</summary>
public sealed record McpWorkspaceReloadResult(
    IReadOnlyList<ITool> AddedTools,
    IReadOnlyList<string> RemovedToolNames);
