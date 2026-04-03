using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
// Within namespace OpenClawNet.Sandbox.OpenSandbox, bare 'Sandbox' resolves to the
// enclosing namespace segment 'OpenClawNet.Sandbox' (parent namespace lookup wins over
// using-alias lookup per the C# spec). Using a distinct alias name sidesteps the
// collision cleanly, without the original global:: workaround on every reference.
using SdkSandbox = OpenSandbox.Sandbox;
using SandboxCreateOptions = OpenSandbox.SandboxCreateOptions;
using OpenSandbox.Config;
using OpenSandbox.Core;

namespace OpenClawNet.Sandbox.OpenSandbox;

public sealed class OpenSandboxToolSandbox : IToolSandbox, IAsyncDisposable
{
    private readonly OpenSandboxOptions _options;
    private readonly ILogger<OpenSandboxToolSandbox>? _logger;
    // ConnectionConfig is stateless and safe to reuse across calls.
    private readonly ConnectionConfig _connectionConfig;
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private readonly Dictionary<string, SandboxEntry> _leases = new(StringComparer.Ordinal);
    private bool _disposed;

    public OpenSandboxToolSandbox(
        OpenSandboxOptions options,
        ILogger<OpenSandboxToolSandbox>? logger = null)
    {
        _options = options;
        _connectionConfig = options.BuildConnectionConfig();
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
            // One-shot: the container was just created with `ttl` seconds already set;
            // there is no need to RenewAsync before running: skip the extra round-trip.
            var oneShotEntry = await CreateEntryAsync(request.Template, ttl, leaseKey: null, cancellationToken);
            try
            {
                return await RunCommandAsync(oneShotEntry.Sandbox, request, ttl, renewBeforeRun: false, cancellationToken);
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
            var result = await RunCommandAsync(entry.Sandbox, request, ttl, renewBeforeRun: true, cancellationToken);
            entry.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl);
            return result;
        }
        catch (SandboxGoneException)
        {
            if (string.IsNullOrWhiteSpace(request.LeaseKey))
                throw new ToolSandboxException("Error: One-shot sandbox was unexpectedly evicted.");

            _logger?.LogWarning("Leased sandbox gone for key {LeaseKey}, recreating", request.LeaseKey);
            await RemoveEntryAsync(request.LeaseKey);
            var recreated = await EnsureEntryAsync(request.LeaseKey, request.Template!, ttl, cancellationToken);
            // Freshly created — SDK already waited for Ready state; skip Renew.
            var recovered = await RunCommandAsync(recreated.Sandbox, request, ttl, renewBeforeRun: false, cancellationToken);
            return recovered;
        }
    }

    private async Task<SandboxResult> RunCommandAsync(
        SdkSandbox sandbox,
        SandboxExecutionRequest request,
        int ttl,
        bool renewBeforeRun,
        CancellationToken cancellationToken)
    {
        try
        {
            // Leased containers are renewed before each command to reset the server-side TTL.
            // One-shot containers skip this: they were just created with `ttl` seconds already.
            if (renewBeforeRun)
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
        // ── Fast path: return existing valid lease without creating anything ──────
        SandboxEntry? stale = null;
        await _leaseGate.WaitAsync(cancellationToken);
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
        }
        finally
        {
            _leaseGate.Release();
        }

        // Kill stale entry outside the lock so we don't block other lease lookups.
        if (stale is not null)
            _ = KillBestEffortAsync(stale.Sandbox, CancellationToken.None);

        // ── Slow path: create sandbox outside the lock ────────────────────────────
        // Network I/O must not hold _leaseGate; doing so would serialize all
        // concurrent sandbox operations (including lookups for different lease keys)
        // behind a single container-creation round-trip.
        var created = await CreateEntryAsync(template, ttl, leaseKey, cancellationToken);

        // Register under lock. If a concurrent call for the same key beat us,
        // discard our duplicate and return the winner.
        await _leaseGate.WaitAsync(cancellationToken);
        try
        {
            if (_leases.TryGetValue(leaseKey, out var winner) &&
                winner.ExpiresAt > DateTimeOffset.UtcNow &&
                string.Equals(winner.Template, template, StringComparison.Ordinal))
            {
                _ = KillBestEffortAsync(created.Sandbox, CancellationToken.None);
                return winner;
            }

            _leases[leaseKey] = created;
            return created;
        }
        finally
        {
            _leaseGate.Release();
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
                // Kubernetes label values only allow [a-zA-Z0-9._-]; sanitize colons
                // from values like "websocket:0HNKHBFTMIA4B:shell" or "alpine:3.23".
                ["leaseKey"] = SanitizeLabelValue(leaseKey),
                ["toolTemplate"] = SanitizeLabelValue(template)
            };

        _logger?.LogDebug("Creating OpenSandbox container (image={Image}, ttl={Ttl}s)", template, ttl);

        var sandbox = await SdkSandbox.CreateAsync(new SandboxCreateOptions
        {
            ConnectionConfig = _connectionConfig,
            Image = template,
            TimeoutSeconds = ttl,
            ReadyTimeoutSeconds = Math.Max(_options.ReadyTimeoutSeconds, 60),
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

        // Use CancellationToken.None: entries are already deregistered from _leases above.
        // Honouring the caller's token here would silently abort the kill (exception caught
        // inside KillBestEffortAsync) and leak the containers.
        foreach (var entry in expired)
            await KillBestEffortAsync(entry.Sandbox, CancellationToken.None);
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

    private async Task KillBestEffortAsync(SdkSandbox sandbox, CancellationToken cancellationToken)
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
        // HTTP 404 with no parseable JSON body yields SandboxErrorCodes.UnexpectedResponse,
        // not "NOT_FOUND", so check the status code as the primary signal.
        if (ex.StatusCode == 404)
            return true;
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

    /// <summary>
    /// Replaces characters not allowed in Kubernetes label values with underscores.
    /// Valid chars: [a-zA-Z0-9._-], max 63 chars.
    /// </summary>
    private static string SanitizeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var span = value.Length > 63 ? value.AsSpan(0, 63) : value.AsSpan();
        Span<char> buf = stackalloc char[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            buf[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
        }
        return new string(buf);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class SandboxEntry(SdkSandbox sandbox, string template, DateTimeOffset expiresAt)
    {
        public SdkSandbox Sandbox { get; } = sandbox;
        public string Template { get; } = template;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }

    private sealed class SandboxGoneException : Exception;
}