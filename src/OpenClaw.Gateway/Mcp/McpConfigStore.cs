using System.Text.Json;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// Persists workspace MCP config to <c>{StoragePath}/mcp/mcp.json</c>.
/// Lives on the memory data volume — independent of OPENCLAW_WORKSPACE and FileSystemWatcher.
/// Pattern mirrors <see cref="OpenClaw.Gateway.Channels.ChannelConfigStore"/>.
/// </summary>
internal sealed class McpConfigStore
{
    private const string McpDirName = "mcp";
    private const string McpFileName = "mcp.json";

    private readonly string _dir;
    private readonly ILogger<McpConfigStore> _logger;

    public McpConfigStore(string storagePath, ILogger<McpConfigStore> logger)
    {
        var root = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _dir = Path.Combine(root, McpDirName);
        _logger = logger;
    }

    private string FilePath => Path.Combine(_dir, McpFileName);

    /// <summary>Returns the raw JSON string, or <c>null</c> if the file doesn't exist or can't be read.</summary>
    public async Task<string?> TryLoadRawAsync(CancellationToken ct = default)
    {
        var path = FilePath;
        if (!File.Exists(path))
            return null;

        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "McpConfigStore: failed to read '{Path}'.", path);
            return null;
        }
    }

    /// <summary>
    /// Deserializes the config and returns the servers dictionary.
    /// Returns <c>null</c> if file is missing or unparseable.
    /// Returns an empty dict if the file has <c>Enabled=false</c>.
    /// </summary>
    public async Task<Dictionary<string, McpServerConfig>?> TryLoadServersAsync(CancellationToken ct = default)
    {
        var path = FilePath;
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);

            var config = await JsonSerializer.DeserializeAsync<McpPluginsConfig>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);

            if (config is null)
            {
                _logger.LogWarning("McpConfigStore: '{Path}' deserialized to null.", path);
                return null;
            }

            if (!config.Enabled)
            {
                _logger.LogInformation("McpConfigStore: '{Path}' has Enabled=false; treating as empty.", path);
                return [];
            }

            return config.Servers;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "McpConfigStore: failed to parse '{Path}'.", path);
            return null;
        }
    }

    /// <summary>Atomically writes the JSON to the storage path (temp-file + rename).</summary>
    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = FilePath;
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, path, overwrite: true);
            _logger.LogInformation("McpConfigStore: saved workspace MCP config to '{Path}'.", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "McpConfigStore: failed to persist MCP config.");
            throw;
        }
    }
}
