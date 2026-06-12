using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ApplyPatchToolTests
{
    [Fact]
    public async Task ExecuteAsync_PatchTextEnvelope_UpdatesFile()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var target = Path.Combine(workspace, "sample.txt");
        await File.WriteAllTextAsync(target, "line1\nline2\nline3");

        var config = new GatewayConfig();
        config.Tooling.AllowedWriteRoots = [workspace];
        config.Tooling.WorkspaceRoot = workspace;
        var tool = new ApplyPatchTool(config.Tooling);

        var result = await tool.ExecuteAsync(
            $$"""
            {"patchText":"*** Begin Patch\n*** Update File: {{target.Replace("\\", "\\\\")}}\n@@\n-line2\n+LINE2 (patched)\n*** End Patch"}
            """,
            CancellationToken.None);

        Assert.Contains("Applied patch", result, StringComparison.OrdinalIgnoreCase);
        var content = await File.ReadAllTextAsync(target);
        Assert.Contains("LINE2 (patched)", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_LegacyPatchField_AcceptsBeginPatchEnvelope()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var target = Path.Combine(workspace, "sample.txt");
        await File.WriteAllTextAsync(target, "line1\nline2\nline3");

        var config = new GatewayConfig();
        config.Tooling.AllowedWriteRoots = [workspace];
        config.Tooling.WorkspaceRoot = workspace;
        var tool = new ApplyPatchTool(config.Tooling);

        var escapedTarget = target.Replace("\\", "\\\\");
        var result = await tool.ExecuteAsync(
            $$"""
            {"path":"{{escapedTarget}}","patch":"*** Begin Patch\n*** Update File: {{escapedTarget}}\n@@\n-line2\n+LINE2 (patched)\n*** End Patch"}
            """,
            CancellationToken.None);

        Assert.Contains("Applied patch", result, StringComparison.OrdinalIgnoreCase);
        var content = await File.ReadAllTextAsync(target);
        Assert.Contains("LINE2 (patched)", content, StringComparison.Ordinal);
    }
}