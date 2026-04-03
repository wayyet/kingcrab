using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenSandbox;
using OpenSandbox.Core;

namespace OpenClawNet.Sandbox.OpenSandbox;

public sealed class OpenSandboxToolSandbox : IToolSandbox, IAsyncDisposable
{
    private readonly OpenSandboxOptions _options;
    private readonly ILogger<OpenSandboxToolSandbox>? _logger;
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private readonly Dictionary<string, SandboxEntry> _leases = new(StringComparer.Ordinal);
    private bool _disposed;

    public OpenSandboxToolSandbox(
        OpenSandboxOptions options,
        ILogger<OpenSandboxToolSandbox>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<SandboxResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(request.Command))
            throw new ToolSandboxException("Error: Sandbox command is required.");

        if (string.IsNullOrWhiteSpace(request.Template))
            throw new ToolSandboxException("Error: Sandbox template is required.");

        var ttl = request.TimeToLiveSeconds is > 0
            ? request.TimeToLiveSeconds.Value
            : _options.DefaultTTL;

        await EvictExpiredLeasesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(request.LeaseKey))
        {
            var oneShotEntry = await CreateEntryAsync(request.Template, ttl, leaseKey: null, cancellationToken);
            try
            {
                return await RunCommandAsync(oneShotEntry.Sandbox, request, ttl, cancellationToken);
            }
            finally
            {
                await KillBestEffortAsync(oneShotEntry.Sandbox, CancellationToken.None);
            }
        }

        var entry = await EnsureEntryAsync(request.LeaseKey, request.Template, ttl, cancellationToken);
        return await RunCommandWithRecoveryAsync(entry, request, ttl, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        List<SandboxEntry> entries;
        await _leaseGate.WaitAsync();
        try
        {
            entries = [.. _leases.Values];
            _leases.Clear();
        }
        finally
        {
            _leaseGate.Release();
        }

        foreach (var entry in entries)
            await KillBestEffortAsync(entry.Sandbox, CancellationToken.None);

        _leaseGate.Dispose();
    }

    // ── Execution ────────────────────────────────────────────────────────────

    private async Task<SandboxResult> RunCommandWithRecoveryAsync(
        SandboxEntry entry,
        SandboxExecutionRequest request,
        int ttl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunCommandAsync(entry.Sandbox, request, ttl, cancellationToken);
        }
        catch (SandboxGoneException)
        {
            if (string.IsNullOrWhiteSpace(request.LeaseKey))
                throw new ToolSandboxException("Error: One-shot sandbox was unexpectedly evicted.");

            await RemoveEntryAsync(request.LeaseKey);
            var recreated = await EnsureEntryAsync(request.LeaseKey, request.Template!, ttl, cancellationToken);
            return await RunCommandAsync(recreated.Sandbox, request, ttl, cancellationToken);
        }
    }

    private async Task<SandboxResult> RunCommandAsync(
        global::OpenSandbox.Sandbox sandbox,
        SandboxExecutionRequest request,
        int ttl,
        CancellationToken cancellationToken)
    {
        try
        {
            await sandbox.RenewAsync(ttl, cancellationToken);

            var command = BuildCommandText(request);
            var result = await sandbox.Commands.RunAsync(command, cancellationToken: cancellationToken);

            var stdout = string.Concat(result.Logs.Stdout.Select(static m => m.Text));
            var stderr = string.Concat(result.Logs.Stderr.Select(static m => m.Text));

            return new SandboxResult
            {
                ExitCode = result.ExitCode ?? 0,
                Stdout = stdout,
                Stderr = stderr,
            };
        }
        catch (SandboxApiException ex) when (IsSandboxGone(ex))
        {
            throw new SandboxGoneException();
        }
        catch (SandboxApiException ex) when (IsServerError(ex))
        {
            throw new ToolSandboxUnavailableException(
                $"OpenSandbox returned a server error: {ex.Error?.Message ?? ex.Message}", ex);
        }
        catch (SandboxApiException ex)
        {
            throw new ToolSandboxException(
                $"OpenSandbox request failed: {ex.Error?.Message ?? ex.Message}", ex);
        }
        catch (SandboxException ex)
        {
            throw new ToolSandboxUnavailableException("OpenSandbox is unreachable.", ex);
        }
    }

    // ── Lease management ─────────────────────────────────────────────────────

    private async Task<SandboxEntry> EnsureEntryAsync(
        string leaseKey,
        string template,
        int ttl,
        CancellationToken cancellationToken)
    {
        await _leaseGate.WaitAsync(cancellationToken);
        SandboxEntry? stale = null;
        try
        {
            if (_leases.TryGetValue(leaseKey, out var existing) &&
                existing.ExpiresAt > DateTimeOffset.UtcNow &&
                string.Equals(existing.Template, template, StringComparison.Ordinal))
            {
                return existing;
            }

            if (existing is not null)
            {
                stale = existing;
                _leases.Remove(leaseKey);
            }

            var created = await CreateEntryAsync(template, ttl, leaseKey, cancellationToken);
            _leases[leaseKey] = created;
            return created;
        }
        finally
        {
            _leaseGate.Release();
            if (stale is not null)
                _ = KillBestEffortAsync(stale.Sandbox, CancellationToken.None);
        }
    }

    private async Task<SandboxEntry> CreateEntryAsync(
        string template,
        int ttl,
        string? leaseKey,
        CancellationToken cancellationToken)
    {
        var metadata = leaseKey is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["leaseKey"] = leaseKey,
                ["toolTemplate"] = template
            };

        _logger?.LogDebug("Creating OpenSandbox container (image={Image}, ttl={Ttl}s)", template, ttl);

        var sandbox = await global::OpenSandbox.Sandbox.CreateAsync(new global::OpenSandbox.SandboxCreateOptions
        {
            ConnectionConfig = _options.BuildConnectionConfig(),
            Image = template,
            TimeoutSeconds = ttl,
            Metadata = metadata,
        }, cancellationToken);

        _logger?.LogDebug("OpenSandbox container created: {SandboxId}", sandbox.Id);

        return new SandboxEntry(sandbox, template, DateTimeOffset.UtcNow.AddSeconds(ttl));
    }

    private async Task EvictExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        List<SandboxEntry> expired;
        await _leaseGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var expiredKeys = _leases
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(pair => pair.Key)
                .ToList();

            expired = expiredKeys.Select(k => _leases[k]).ToList();
            foreach (var key in expiredKeys)
                _leases.Remove(key);
        }
        finally
        {
            _leaseGate.Release();
        }

        foreach (var entry in expired)
            await KillBestEffortAsync(entry.Sandbox, cancellationToken);
    }

    private async Task RemoveEntryAsync(string leaseKey)
    {
        await _leaseGate.WaitAsync();
        try
        {
            _leases.Remove(leaseKey);
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private async Task KillBestEffortAsync(global::OpenSandbox.Sandbox sandbox, CancellationToken cancellationToken)
    {
        try
        {
            await sandbox.KillAsync(cancellationToken);
            _logger?.LogDebug("Killed OpenSandbox container: {SandboxId}", sandbox.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to kill OpenSandbox container {SandboxId}", sandbox.Id);
        }
        finally
        {
            await sandbox.DisposeAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildCommandText(SandboxExecutionRequest request)
    {
        var builder = new StringBuilder();
        var environment = request.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (environment.Count > 0)
        {
            foreach (var pair in environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append("export ");
                builder.Append(pair.Key);
                builder.Append('=');
                builder.Append(SandboxCommandLine.Quote(pair.Value));
                builder.Append("; ");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            builder.Append("cd ");
            builder.Append(SandboxCommandLine.Quote(request.WorkingDirectory));
            builder.Append(" && ");
        }

        builder.Append(SandboxCommandLine.BuildCommand(request.Command, request.Arguments));
        return builder.ToString();
    }

    private static bool IsSandboxGone(SandboxApiException ex)
    {
        var code = ex.Error?.Code ?? string.Empty;
        return code.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServerError(SandboxApiException ex)
    {
        var code = ex.Error?.Code ?? string.Empty;
        return code.StartsWith("INTERNAL", StringComparison.OrdinalIgnoreCase) ||
               code.StartsWith("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
               code.StartsWith("SERVER", StringComparison.OrdinalIgnoreCase);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class SandboxEntry(global::OpenSandbox.Sandbox sandbox, string template, DateTimeOffset expiresAt)
    {
        public global::OpenSandbox.Sandbox Sandbox { get; } = sandbox;
        public string Template { get; } = template;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }

    private sealed class SandboxGoneException : Exception;
}