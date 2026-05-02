using System.IO.Compression;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Plugins.EmploymentCoachWorkflow;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OntologyIngestToolTests
{
    [Fact]
    public async Task ExecuteAsync_WritesNewNodesIntoOntologyDirectory()
    {
        var root = CreateTempDir();
        var sourcePath = Path.Combine(root, "requirements.md");
        await File.WriteAllTextAsync(sourcePath, "# 用户管理\n支持新增、删除、冻结账号", CancellationToken.None);

        var tool = CreateTool(root);

        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { sourcePath } }), CancellationToken.None);

        Assert.Contains("新增: 用户管理", result, StringComparison.Ordinal);
        var nodePath = Path.Combine(root, "ontology", "用户管理.json");
        Assert.True(File.Exists(nodePath));
    }

    [Fact]
    public async Task ExecuteAsync_ArchivesModifiedAndRemovedNodesForSameOrigin()
    {
        var root = CreateTempDir();
        var sourcePath = Path.Combine(root, "domain.md");
        await File.WriteAllTextAsync(sourcePath, "# Alpha\nold\n\n# Beta\nlegacy", CancellationToken.None);

        var tool = CreateTool(root);
        await tool.ExecuteAsync(ToJson(new { paths = new[] { sourcePath } }), CancellationToken.None);

        await File.WriteAllTextAsync(sourcePath, "# Alpha\nnew content", CancellationToken.None);
        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { sourcePath } }), CancellationToken.None);

        Assert.Contains("修改: Alpha", result, StringComparison.Ordinal);
        Assert.Contains("移除: Beta", result, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "ontology", "alpha.json")));
        Assert.False(File.Exists(Path.Combine(root, "ontology", "beta.json")));

        var archivedDir = Path.Combine(root, "ontology", "_archived");
        Assert.True(Directory.Exists(archivedDir));
        Assert.True(Directory.GetFiles(archivedDir).Length >= 2);
    }

    [Fact]
    public async Task ExecuteAsync_ParsesNestedZipRecursively()
    {
        var root = CreateTempDir();
        var outerZip = Path.Combine(root, "bundle.zip");
        var innerZipBytes = CreateZipBytes(("nested.md", "# Nested Topic\nzip content"));

        using (var stream = File.Create(outerZip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("inner.zip");
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(innerZipBytes, CancellationToken.None);
        }

        var tool = CreateTool(root);
        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { outerZip } }), CancellationToken.None);

        Assert.Contains("新增: Nested Topic", result, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "ontology", "nested-topic.json")));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsZipWhenEntryBudgetIsExceeded()
    {
        var root = CreateTempDir();
        var zipPath = Path.Combine(root, "oversized.zip");
        var entries = Enumerable.Range(0, 513)
            .Select(index => ($"topic-{index}.md", $"# Topic {index}\ncontent"))
            .ToArray();
        await File.WriteAllBytesAsync(zipPath, CreateZipBytes(entries), CancellationToken.None);

        var tool = CreateTool(root);

        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { zipPath } }), CancellationToken.None);

        Assert.Contains("Error:", result, StringComparison.Ordinal);
        Assert.Contains("too many files", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_UsesFullSourceIdentityForIncrementalRemoval()
    {
        var root = CreateTempDir();
        var sourceA = Path.Combine(root, "a", "domain.md");
        var sourceB = Path.Combine(root, "b", "domain.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceB)!);
        await File.WriteAllTextAsync(sourceA, "# Alpha\nfrom A", CancellationToken.None);
        await File.WriteAllTextAsync(sourceB, "# Beta\nfrom B", CancellationToken.None);

        var tool = CreateTool(root);
        await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceA } }), CancellationToken.None);
        await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceB } }), CancellationToken.None);

        await File.WriteAllTextAsync(sourceA, "# Alpha\nfrom A updated", CancellationToken.None);
        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceA } }), CancellationToken.None);

        Assert.Contains("修改: Alpha", result, StringComparison.Ordinal);
        Assert.DoesNotContain("移除: Beta", result, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "ontology", "beta.json")));
    }

    [Fact]
    public async Task ExecuteAsync_FullReplaceRemovesStaleGeneratedNodesOnly()
    {
        var root = CreateTempDir();
        var sourceA = Path.Combine(root, "a.md");
        var sourceB = Path.Combine(root, "b.md");
        await File.WriteAllTextAsync(sourceA, "# Alpha\nfrom A", CancellationToken.None);
        await File.WriteAllTextAsync(sourceB, "# Beta\nfrom B", CancellationToken.None);

        var tool = CreateTool(root);
        await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceA } }), CancellationToken.None);
        await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceB } }), CancellationToken.None);

        var manualPath = Path.Combine(root, "ontology", "manual.json");
        await File.WriteAllTextAsync(manualPath, "{\"name\":\"Manual\"}", CancellationToken.None);

        var sourceC = Path.Combine(root, "c.md");
        await File.WriteAllTextAsync(sourceC, "# Gamma\nfrom C", CancellationToken.None);
        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceC }, mode = "full_replace" }), CancellationToken.None);

        Assert.Contains("新增: Gamma", result, StringComparison.Ordinal);
        Assert.Contains("移除: Alpha、Beta", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "ontology", "alpha.json")));
        Assert.False(File.Exists(Path.Combine(root, "ontology", "beta.json")));
        Assert.True(File.Exists(Path.Combine(root, "ontology", "gamma.json")));
        Assert.True(File.Exists(manualPath));

        var archivedDir = Path.Combine(root, "ontology", "_archived");
        Assert.True(Directory.GetFiles(archivedDir).Length >= 2);
    }

    [Fact]
    public async Task ExecuteAsync_ParsesDocxContainerAsOntologySource()
    {
        var root = CreateTempDir();
        var docxPath = Path.Combine(root, "specs.docx");
        await File.WriteAllBytesAsync(docxPath, CreateZipBytes(
            ("word/document.xml", "<w:document><w:body><w:p><w:r><w:t>客户档案</w:t></w:r></w:p><w:p><w:r><w:t>需要支持主数据和状态同步</w:t></w:r></w:p></w:body></w:document>")), CancellationToken.None);

        var tool = CreateTool(root);
        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { docxPath } }), CancellationToken.None);

        Assert.Contains("新增: specs", result, StringComparison.Ordinal);
        var nodePath = Path.Combine(root, "ontology", "specs.json");
        Assert.True(File.Exists(nodePath));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(nodePath, CancellationToken.None));
        var content = json.RootElement.GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public async Task ExecuteAsync_MergesProvenanceWhenSameContentArrivesFromDifferentSources()
    {
        var root = CreateTempDir();
        var sourceA = Path.Combine(root, "a", "shared.md");
        var sourceB = Path.Combine(root, "b", "shared.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceB)!);
        await File.WriteAllTextAsync(sourceA, "# Shared\nsame content", CancellationToken.None);
        await File.WriteAllTextAsync(sourceB, "# Shared\nsame content", CancellationToken.None);

        var tool = CreateTool(root);
        await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceA } }), CancellationToken.None);
        var result = await tool.ExecuteAsync(ToJson(new { paths = new[] { sourceB } }), CancellationToken.None);

        Assert.Contains("修改: Shared", result, StringComparison.Ordinal);

        var nodePath = Path.Combine(root, "ontology", "shared.json");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(nodePath, CancellationToken.None));
        var sourceFiles = json.RootElement.GetProperty("source_files").EnumerateArray().Select(item => item.GetString()).ToArray();
        var sourceOriginKeys = json.RootElement.GetProperty("source_origin_keys").EnumerateArray().Select(item => item.GetString()).ToArray();

        Assert.Contains(sourceA, sourceFiles);
        Assert.Contains(sourceB, sourceFiles);
        Assert.Equal(2, sourceOriginKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static OntologyIngestTool CreateTool(string root)
        => new(new ToolingConfig
        {
            WorkspaceRoot = root,
            AllowedReadRoots = [root],
            AllowedWriteRoots = [root]
        });

    private static string ToJson(object value) => JsonSerializer.Serialize(value);

    private static byte[] CreateZipBytes(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}