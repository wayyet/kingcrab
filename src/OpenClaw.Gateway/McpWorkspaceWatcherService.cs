using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Mcp;

namespace OpenClaw.Gateway;

/// <summary>
/// Watches <c>{WorkspacePath}/.kingcrab/mcp.json</c> and hot-reloads workspace
/// MCP servers without restarting the service.  Follows the same
/// <c>Start(CancellationToken)</c> pattern as <see cref="SkillWatcherService"/>.
/// <para>
/// Also integrates with <see cref="McpConfigStore"/> so that configs saved via the
/// admin API (stored in the memory data volume) are picked up immediately without
/// relying on <see cref="FileSystemWatcher"/>.
/// </para>
/// </summary>
internal sealed class McpWorkspaceWatcherService : IDisposable
{
    private static readonly string McpJsonRelativePath =
        Path.Combine(".kingcrab", "mcp.json");

    private readonly McpServerToolRegistry _registry;
    private readonly IAgentRuntime _agentRuntime;
    private readonly string? _workspacePath;
    private readonly McpConfigStore? _configStore;
    private readonly ILogger<McpWorkspaceWatcherService> _logger;

    // Bounded channel (capacity 1, DropOldest) acts as a debounce queue:
    // multiple rapid file-system events collapse into a single reload.
    private readonly Channel<bool> _reloadChannel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private bool _started;
    private bool _disposed;

    public McpWorkspaceWatcherService(
        McpServerToolRegistry registry,
        IAgentRuntime agentRuntime,
        string? workspacePath,
        ILogger<McpWorkspaceWatcherService> logger,
        McpConfigStore? configStore = null)
    {
        _registry = registry;
        _agentRuntime = agentRuntime;
        _workspacePath = workspacePath;
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues an immediate reload without waiting for a file-system event.
    /// Called by the admin API after saving MCP config to <see cref="McpConfigStore"/>.
    /// </summary>
    public void TriggerReload() => _reloadChannel.Writer.TryWrite(true);

    /// <summary>Starts the file watcher and the background reload loop.</summary>
    public void Start(CancellationToken stoppingToken)
    {
        if (_started || _disposed)
            return;

        _started = true;

        // Trigger initial load if memory store or workspace file has a config.
        var hasWorkspaceFile = !string.IsNullOrEmpty(_workspacePath) &&
            File.Exists(Path.Combine(_workspacePath, McpJsonRelativePath));

        if (_configStore is not null || hasWorkspaceFile)
            _reloadChannel.Writer.TryWrite(true);

        stoppingToken.Register(Dispose);
        _ = RunReloadLoopAsync(stoppingToken);
    }

    // ── Reload loop ───────────────────────────────────────────────────────────

    private async Task RunReloadLoopAsync(CancellationToken ct)
    {
        await foreach (var _ in _reloadChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ExecuteReloadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "McpWorkspaceWatcher: unhandled error during MCP reload.");
            }
        }
    }

    private async Task ExecuteReloadAsync(CancellationToken ct)
    {
        Dictionary<string, McpServerConfig>? servers = null;

        // Priority 1: memory-store config (written by admin API, reliable in containers).
        if (_configStore is not null)
        {
            servers = await _configStore.TryLoadServersAsync(ct).ConfigureAwait(false);
            if (servers is null)
                _logger.LogWarning("McpWorkspaceWatcher: memory-store config missing or unparseable — will fall back to workspace file.");
            else if (servers.Count == 0)
                _logger.LogInformation("McpWorkspaceWatcher: memory-store config has Enabled=false or no servers.");
            else
                _logger.LogInformation("McpWorkspaceWatcher: memory-store config loaded, {Count} server(s) defined.", servers.Count);
        }

        // Priority 2: fallback to workspace file (manual edits / legacy path).
        if (servers is null && !string.IsNullOrEmpty(_workspacePath))
        {
            var filePath = Path.Combine(_workspacePath, McpJsonRelativePath);
            servers = await TryReadConfigAsync(filePath, ct).ConfigureAwait(false);
            if (servers is not null)
                _logger.LogInformation("McpWorkspaceWatcher: fell back to workspace file, {Count} server(s) defined.", servers.Count);
        }

        // null means file was missing/invalid → pass empty dict to remove all workspace tools
        var result = await _registry
            .ReloadWorkspaceServersAsync(servers, ct)
            .ConfigureAwait(false);

        if (result.AddedTools.Count == 0 && result.RemovedToolNames.Count == 0)
        {
            _logger.LogInformation("McpWorkspaceWatcher: reload produced no tool changes (servers in registry: {Count}).",
                servers?.Count ?? 0);
            return;
        }

        await _agentRuntime
            .ApplyMcpToolChangesAsync(result.AddedTools, result.RemovedToolNames, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "McpWorkspaceWatcher: applied workspace MCP reload — " +
            "+{Added} tool(s) added, -{Removed} tool(s) removed.",
            result.AddedTools.Count,
            result.RemovedToolNames.Count);
    }

    private async Task<Dictionary<string, McpServerConfig>?> TryReadConfigAsync(
        string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogInformation(
                "McpWorkspaceWatcher: {File} not found; removing all workspace MCP servers.",
                filePath);
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);

            var config = await JsonSerializer
                .DeserializeAsync<McpPluginsConfig>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    ct)
                .ConfigureAwait(false);

            if (config is null)
            {
                _logger.LogWarning(
                    "McpWorkspaceWatcher: {File} deserialized to null; skipping reload.",
                    filePath);
                return null;
            }

            if (!config.Enabled)
            {
                _logger.LogInformation(
                    "McpWorkspaceWatcher: {File} has Enabled=false; removing all workspace servers.",
                    filePath);
                return [];
            }

            return config.Servers;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "McpWorkspaceWatcher: failed to parse {File}; skipping reload.",
                filePath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "McpWorkspaceWatcher: I/O error reading {File}; skipping reload.",
                filePath);
            return null;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _reloadChannel.Writer.TryComplete();
    }
}
