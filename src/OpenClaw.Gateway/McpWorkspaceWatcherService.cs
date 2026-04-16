using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway;

/// <summary>
/// Watches <c>{WorkspacePath}/.kingcrab/mcp.json</c> and hot-reloads workspace
/// MCP servers without restarting the service.  Follows the same
/// <c>Start(CancellationToken)</c> pattern as <see cref="SkillWatcherService"/>.
/// </summary>
internal sealed class McpWorkspaceWatcherService : IDisposable
{
    private static readonly string McpJsonRelativePath =
        Path.Combine(".kingcrab", "mcp.json");

    private readonly McpServerToolRegistry _registry;
    private readonly IAgentRuntime _agentRuntime;
    private readonly string? _workspacePath;
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

    private FileSystemWatcher? _watcher;
    private bool _started;
    private bool _disposed;

    public McpWorkspaceWatcherService(
        McpServerToolRegistry registry,
        IAgentRuntime agentRuntime,
        string? workspacePath,
        ILogger<McpWorkspaceWatcherService> logger)
    {
        _registry = registry;
        _agentRuntime = agentRuntime;
        _workspacePath = workspacePath;
        _logger = logger;
    }

    /// <summary>Starts the file watcher and the background reload loop.</summary>
    public void Start(CancellationToken stoppingToken)
    {
        if (_started || _disposed)
            return;

        _started = true;

        // Perform an initial load if the file already exists at startup.
        if (!string.IsNullOrEmpty(_workspacePath))
        {
            var initialFile = Path.Combine(_workspacePath, McpJsonRelativePath);
            if (File.Exists(initialFile))
                _reloadChannel.Writer.TryWrite(true);

            StartWatcher(_workspacePath, stoppingToken);
        }
        else
        {
            _logger.LogInformation(
                "McpWorkspaceWatcher: no workspace path configured; workspace MCP hot-reload disabled.");
        }

        _ = RunReloadLoopAsync(stoppingToken);
    }

    // ── FileSystemWatcher ─────────────────────────────────────────────────────

    private void StartWatcher(string watchRoot, CancellationToken stoppingToken)
    {
        // Watch the workspace root with subdirectory recursion so that we also
        // get a Created event when the .kingcrab/ directory is created later.
        if (!Directory.Exists(watchRoot))
            return;

        try
        {
            _watcher = new FileSystemWatcher(watchRoot)
            {
                IncludeSubdirectories = true,
                Filter = "mcp.json",
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnRenamedEvent;

            stoppingToken.Register(Dispose);

            _logger.LogInformation(
                "McpWorkspaceWatcher: watching {Root} for .kingcrab/mcp.json changes.",
                watchRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "McpWorkspaceWatcher: failed to create FileSystemWatcher on {Root}. " +
                "Workspace MCP hot-reload will be unavailable.", watchRoot);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsMcpJsonPath(e.FullPath))
            _reloadChannel.Writer.TryWrite(true);
    }

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        // Trigger on either the old or new name matching (handles atomic-write
        // patterns that rename a temp file to mcp.json).
        if (IsMcpJsonPath(e.FullPath) || IsMcpJsonPath(e.OldFullPath))
            _reloadChannel.Writer.TryWrite(true);
    }

    private bool IsMcpJsonPath(string fullPath) =>
        fullPath.EndsWith(Path.Combine(".kingcrab", "mcp.json"),
            StringComparison.OrdinalIgnoreCase);

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

        if (!string.IsNullOrEmpty(_workspacePath))
        {
            var filePath = Path.Combine(_workspacePath, McpJsonRelativePath);
            servers = await TryReadConfigAsync(filePath, ct).ConfigureAwait(false);
        }

        // null means file was missing/invalid → pass empty dict to remove all workspace tools
        var result = await _registry
            .ReloadWorkspaceServersAsync(servers, ct)
            .ConfigureAwait(false);

        if (result.AddedTools.Count == 0 && result.RemovedToolNames.Count == 0)
        {
            _logger.LogInformation("McpWorkspaceWatcher: reload produced no tool changes.");
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
        _watcher?.Dispose();
    }
}
