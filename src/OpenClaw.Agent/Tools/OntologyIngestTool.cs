using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Ingests arbitrary uploaded files into sandbox ontology slices.
/// Supports recursive ZIP traversal, common office containers, PDFs, plain text,
/// and a binary fallback that extracts printable strings.
/// </summary>
public sealed class OntologyIngestTool : ITool
{
    private const int MaxDocumentChars = 200_000;
    private const int MaxInputBytes = 50 * 1024 * 1024;
    private const int MaxZipDepth = 5;
    private const int MaxZipEntries = 512;
    private const long MaxZipExpandedBytes = 100L * 1024 * 1024;
    private const int MaxZipEntryBytes = 25 * 1024 * 1024;
    private readonly ToolingConfig _config;

    public OntologyIngestTool(ToolingConfig config) => _config = config;

    public string Name => "ontology_ingest";

    public string Description =>
        "Parse uploaded files of any format, extract ontology-like slices, and write them into the sandbox ontology directory. " +
        "Supports recursive ZIP parsing, same-name overwrite with archive, incremental removal for the same source, and returns a summary grouped by added, modified, and removed nodes.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "paths": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 1,
              "description": "Uploaded file paths, [FILE_URL:/media/...] markers, /media/... URLs, or absolute paths."
            },
            "ontology_dir": {
              "type": "string",
              "description": "Sandbox ontology directory. Defaults to 'ontology' under the workspace root."
            },
            "mode": {
              "type": "string",
              "enum": ["incremental"],
              "default": "incremental",
              "description": "Ingestion mode. Only incremental is currently supported."
            }
          },
          "required": ["paths"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!TryParseArguments(argumentsJson, out var inputPaths, out var ontologyDirArg, out var mode, out var error))
            return error!;

        if (!string.Equals(mode, "incremental", StringComparison.OrdinalIgnoreCase))
            return $"Error: Unsupported mode '{mode}'. Only 'incremental' is supported.";

        var resolvedInputs = new List<ResolvedInput>(inputPaths.Count);
        foreach (var rawPath in inputPaths)
        {
            var resolvedPath = ResolveInputPath(rawPath);
            if (resolvedPath is null)
                return $"Error: File not found: {rawPath}";

            if (!ToolPathPolicy.IsReadAllowed(_config, resolvedPath))
                return $"Error: Read access denied for path: {rawPath}";

            resolvedInputs.Add(new ResolvedInput(rawPath, resolvedPath, BuildOriginKey(resolvedPath)));
        }

        var workspaceRoot = ResolveWorkspaceRoot();
        var ontologyDir = ResolveOntologyDirectory(workspaceRoot, ontologyDirArg);
        if (!ToolPathPolicy.IsWriteAllowed(_config, ontologyDir))
            return $"Error: Write access denied for ontology directory: {ontologyDir}";

        Directory.CreateDirectory(ontologyDir);
        var archiveDir = Path.Combine(ontologyDir, "_archived");
        Directory.CreateDirectory(archiveDir);

        var extractedNodes = new Dictionary<string, PersistedOntologyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in resolvedInputs)
        {
            IReadOnlyList<ParsedDocument> parsedDocuments;
            try
            {
                parsedDocuments = await ParseInputAsync(input, ct);
            }
            catch (InvalidDataException ex)
            {
                return $"Error: Failed to parse '{input.RawPath}': {ex.Message}";
            }
            catch (InvalidOperationException ex)
            {
                return $"Error: Failed to parse '{input.RawPath}': {ex.Message}";
            }

            foreach (var document in parsedDocuments)
            {
                foreach (var node in BuildNodes(document, input.OriginKey))
                {
                    extractedNodes[node.Slug] = node;
                }
            }
        }

        if (extractedNodes.Count == 0)
            return "Error: No ontology nodes were extracted from the supplied files.";

        var existingNodes = LoadExistingNodes(ontologyDir);
        var incomingOrigins = resolvedInputs
            .Select(item => item.OriginKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        var modified = new List<string>();
        var removed = new List<string>();

        foreach (var (slug, node) in extractedNodes)
        {
            var destinationPath = Path.Combine(ontologyDir, slug + ".json");
            if (!existingNodes.TryGetValue(slug, out var existing))
            {
                await WriteNodeAsync(destinationPath, node, ct);
                added.Add(node.Name);
                continue;
            }

            if (NodesEquivalent(existing, node))
                continue;

            ArchiveNode(existing.FilePath, archiveDir, "modified");
            await WriteNodeAsync(destinationPath, node, ct);
            modified.Add(node.Name);
        }

        foreach (var existing in existingNodes.Values)
        {
            if (!existing.GeneratedByTool)
                continue;

            if (!existing.SourceOriginKeys.Any(origin => incomingOrigins.Contains(origin)))
                continue;

            if (extractedNodes.ContainsKey(existing.Slug))
                continue;

            ArchiveNode(existing.FilePath, archiveDir, "removed");
            removed.Add(existing.Name);
        }

        var summary = BuildSummary(ontologyDir, added, modified, removed);
        return summary;
    }

    private static string BuildSummary(string ontologyDir, IReadOnlyList<string> added, IReadOnlyList<string> modified, IReadOnlyList<string> removed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ontology ingest completed into {ontologyDir}");
        sb.AppendLine($"新增: {(added.Count == 0 ? "无" : string.Join("、", added.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)))}");
        sb.AppendLine($"修改: {(modified.Count == 0 ? "无" : string.Join("、", modified.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)))}");
        sb.Append($"移除: {(removed.Count == 0 ? "无" : string.Join("、", removed.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)))}");
        return sb.ToString();
    }

    private static bool NodesEquivalent(ExistingNode existing, PersistedOntologyNode current)
        => string.Equals(existing.Name, current.Name, StringComparison.Ordinal)
        && string.Equals(existing.Content, current.Content, StringComparison.Ordinal)
        && existing.GeneratedByTool;

    private static async Task WriteNodeAsync(string destinationPath, PersistedOntologyNode node, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(destinationPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        });

        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true
        });

        writer.WriteStartObject();
        writer.WriteString("generated_by", node.GeneratedBy);
        writer.WriteString("name", node.Name);
        writer.WriteString("slug", node.Slug);
        writer.WriteString("summary", node.Summary);
        writer.WriteString("content", node.Content);
        writer.WriteStartArray("source_origin_keys");
        foreach (var originKey in node.SourceOriginKeys)
            writer.WriteStringValue(originKey);
        writer.WriteEndArray();

        writer.WriteStartArray("source_files");
        foreach (var sourceFile in node.SourceFiles)
            writer.WriteStringValue(sourceFile);
        writer.WriteEndArray();

        writer.WriteString("parser", node.Parser);
        writer.WriteString("updated_at", node.UpdatedAt);
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private static void ArchiveNode(string sourcePath, string archiveDir, string reason)
    {
        var archivedName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{reason}-{Path.GetFileName(sourcePath)}";
        var archivedPath = Path.Combine(archiveDir, archivedName);
        if (File.Exists(archivedPath))
            archivedPath = Path.Combine(archiveDir, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{reason}-{Path.GetFileName(sourcePath)}");

        File.Move(sourcePath, archivedPath, overwrite: false);
    }

    private static Dictionary<string, ExistingNode> LoadExistingNodes(string ontologyDir)
    {
        var result = new Dictionary<string, ExistingNode>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ontologyDir))
            return result;

        foreach (var file in Directory.GetFiles(ontologyDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "_archived", StringComparison.OrdinalIgnoreCase))
                continue;

            var slug = Path.GetFileNameWithoutExtension(fileName);
            var content = File.ReadAllText(file);
            var generatedByTool = false;
            var name = slug;
            var sourceOriginKeys = Array.Empty<string>();
            var nodeContent = content;

            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("slug", out var slugProp) && slugProp.ValueKind == JsonValueKind.String)
                        slug = slugProp.GetString() ?? slug;
                    if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                        name = nameProp.GetString() ?? name;
                    if (root.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                        nodeContent = contentProp.GetString() ?? nodeContent;
                    if (root.TryGetProperty("generated_by", out var generatedByProp) && generatedByProp.ValueKind == JsonValueKind.String)
                        generatedByTool = string.Equals(generatedByProp.GetString(), "ontology_ingest", StringComparison.Ordinal);
                    if (root.TryGetProperty("source_origin_keys", out var originsProp) && originsProp.ValueKind == JsonValueKind.Array)
                    {
                        sourceOriginKeys = originsProp
                            .EnumerateArray()
                            .Where(static item => item.ValueKind == JsonValueKind.String)
                            .Select(static item => item.GetString())
                            .Where(static item => !string.IsNullOrWhiteSpace(item))
                            .Select(static item => item!)
                            .ToArray();
                    }
                }
                catch
                {
                    // Keep fallback values when an existing file is not one of our generated nodes.
                }
            }

            result[slug] = new ExistingNode(file, slug, name, nodeContent, generatedByTool, sourceOriginKeys);
        }

        return result;
    }

    private string ResolveWorkspaceRoot()
    {
        var workspaceRaw = SecretResolver.Resolve(_config.WorkspaceRoot)
            ?? SecretResolver.Resolve("env:OPENCLAW_WORKSPACE")
            ?? SecretResolver.Resolve("env:OPENCLAW_WORKSPACE_ROOT");

        if (string.IsNullOrWhiteSpace(workspaceRaw))
            workspaceRaw = Directory.GetCurrentDirectory();

        return Path.GetFullPath(workspaceRaw);
    }

    private static string ResolveOntologyDirectory(string workspaceRoot, string? ontologyDirArg)
    {
        if (string.IsNullOrWhiteSpace(ontologyDirArg))
            return Path.Combine(workspaceRoot, "ontology");

        if (Path.IsPathRooted(ontologyDirArg))
            return Path.GetFullPath(ontologyDirArg);

        return Path.GetFullPath(Path.Combine(workspaceRoot, ontologyDirArg));
    }

    private static bool TryParseArguments(
        string argumentsJson,
        out List<string> inputPaths,
        out string? ontologyDir,
        out string mode,
        out string? error)
    {
        inputPaths = [];
        ontologyDir = null;
        mode = "incremental";
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("paths", out var pathsProp) || pathsProp.ValueKind != JsonValueKind.Array)
            {
                error = "Error: 'paths' must be a non-empty array.";
                return false;
            }

            foreach (var item in pathsProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var path = item.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                    inputPaths.Add(path);
            }

            if (inputPaths.Count == 0)
            {
                error = "Error: 'paths' must contain at least one file path.";
                return false;
            }

            if (root.TryGetProperty("ontology_dir", out var ontologyDirProp) && ontologyDirProp.ValueKind == JsonValueKind.String)
                ontologyDir = ontologyDirProp.GetString();

            if (root.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(modeProp.GetString()))
                mode = modeProp.GetString()!;

            return true;
        }
        catch (Exception ex)
        {
            error = $"Error: Invalid JSON arguments — {ex.Message}";
            return false;
        }
    }

    private async Task<IReadOnlyList<ParsedDocument>> ParseInputAsync(ResolvedInput input, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(input.ResolvedPath, ct);
        if (bytes.Length > MaxInputBytes)
            throw new InvalidOperationException($"Input file is too large ({bytes.Length} bytes). Max allowed is {MaxInputBytes} bytes.");

        var budget = new ZipParseBudget();
        return await ParseBytesAsync(bytes, Path.GetFileName(input.ResolvedPath), input.ResolvedPath, input.OriginKey, budget, depth: 0, ct);
    }

    private async Task<IReadOnlyList<ParsedDocument>> ParseBytesAsync(
        byte[] bytes,
        string entryName,
        string virtualPath,
        string originKey,
        ZipParseBudget budget,
        int depth,
        CancellationToken ct)
    {
        var extension = Path.GetExtension(entryName).ToLowerInvariant();
        if (extension == ".zip")
            return await ParseZipAsync(bytes, virtualPath, originKey, budget, depth + 1, ct);

        if (IsOpenXmlContainer(extension))
        {
            var openXmlText = ExtractArchiveText(bytes, entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            return CreateDocumentList(entryName, virtualPath, originKey, extension, openXmlText);
        }

        if (extension == ".pdf")
        {
            var pdfText = await ExtractPdfTextAsync(bytes, virtualPath, ct);
            return CreateDocumentList(entryName, virtualPath, originKey, extension, pdfText);
        }

        if (IsTextLike(extension))
        {
            var text = DecodeText(bytes);
            return CreateDocumentList(entryName, virtualPath, originKey, extension, text);
        }

        var fallbackText = ExtractPrintableText(bytes);
        return CreateDocumentList(entryName, virtualPath, originKey, extension, fallbackText);
    }

    private async Task<IReadOnlyList<ParsedDocument>> ParseZipAsync(byte[] bytes, string virtualPath, string originKey, ZipParseBudget budget, int depth, CancellationToken ct)
    {
        if (depth > MaxZipDepth)
            throw new InvalidOperationException($"ZIP nesting is too deep. Max depth is {MaxZipDepth}.");

        var results = new List<ParsedDocument>();
        using var ms = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            budget.EntryCount++;
            if (budget.EntryCount > MaxZipEntries)
                throw new InvalidOperationException($"ZIP contains too many files. Max entries is {MaxZipEntries}.");

            if (entry.Length > MaxZipEntryBytes)
                throw new InvalidOperationException($"ZIP entry '{entry.FullName}' is too large ({entry.Length} bytes). Max entry size is {MaxZipEntryBytes} bytes.");

            budget.ExpandedBytes += entry.Length;
            if (budget.ExpandedBytes > MaxZipExpandedBytes)
                throw new InvalidOperationException($"ZIP expanded content is too large. Max expanded size is {MaxZipExpandedBytes} bytes.");

            await using var entryStream = entry.Open();
            using var entryBuffer = new MemoryStream();
            await entryStream.CopyToAsync(entryBuffer, ct);
            var nestedVirtualPath = virtualPath + "::" + entry.FullName.Replace('\\', '/');
            var nestedDocuments = await ParseBytesAsync(entryBuffer.ToArray(), entry.Name, nestedVirtualPath, originKey, budget, depth, ct);
            results.AddRange(nestedDocuments);
        }

        return results;
    }

    private IEnumerable<PersistedOntologyNode> BuildNodes(ParsedDocument document, string originKey)
    {
        var normalizedText = NormalizeText(document.Text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            yield break;

        var sections = SplitSections(normalizedText).ToList();
        if (sections.Count == 0)
        {
            yield return CreateNode(document.Title, normalizedText, originKey, document);
            yield break;
        }

        foreach (var section in sections)
        {
            yield return CreateNode(section.Title, section.Content, originKey, document);
        }
    }

    private static PersistedOntologyNode CreateNode(string rawTitle, string rawContent, string originKey, ParsedDocument document)
    {
        var title = string.IsNullOrWhiteSpace(rawTitle)
            ? Path.GetFileNameWithoutExtension(document.SourcePath)
            : rawTitle.Trim();
        var content = NormalizeText(rawContent);
        var summary = content.Length <= 160 ? content : content[..160].TrimEnd() + "...";
        return new PersistedOntologyNode(
            "ontology_ingest",
            title,
            Slugify(title),
            summary,
            content,
            [originKey],
            [document.SourcePath],
            document.Parser,
            DateTimeOffset.UtcNow.ToString("O"));
    }

    private static IEnumerable<(string Title, string Content)> SplitSections(string text)
    {
        var matches = Regex.Matches(text, @"(?m)^#{1,6}\s+(?<title>.+?)\s*$");
        if (matches.Count == 0)
            yield break;

        for (var index = 0; index < matches.Count; index++)
        {
            var current = matches[index];
            var title = current.Groups["title"].Value.Trim();
            var bodyStart = current.Index + current.Length;
            var bodyEnd = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            var content = text[bodyStart..bodyEnd].Trim();
            if (string.IsNullOrWhiteSpace(content))
                content = title;
            yield return (title, content);
        }
    }

    private static IReadOnlyList<ParsedDocument> CreateDocumentList(string entryName, string virtualPath, string originKey, string parser, string text)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return
        [
            new ParsedDocument(
                originKey,
                Path.GetFileNameWithoutExtension(entryName),
                virtualPath,
                parser.TrimStart('.'),
                normalized.Length <= MaxDocumentChars ? normalized : normalized[..MaxDocumentChars])
        ];
    }

    private static string ExtractArchiveText(byte[] bytes, Func<ZipArchiveEntry, bool> includeEntry)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        var sb = new StringBuilder();
        foreach (var entry in archive.Entries.Where(includeEntry))
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var text = DecodeText(buffer.ToArray());
            text = StripMarkup(text);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (sb.Length > 0)
                sb.AppendLine().AppendLine();

            sb.AppendLine(text.Trim());
        }

        return sb.ToString();
    }

    private static async Task<string> ExtractPdfTextAsync(byte[] bytes, string virtualPath, CancellationToken ct)
    {
        var directPath = File.Exists(virtualPath) ? virtualPath : null;
        if (directPath is not null)
        {
            var external = await TryPdfToTextAsync(directPath, ct);
            if (!string.IsNullOrWhiteSpace(external))
                return external;
        }

        return ExtractPdfTextBasic(bytes);
    }

    private static async Task<string?> TryPdfToTextAsync(string path, CancellationToken ct)
    {
        try
        {
            var probe = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            probe.ArgumentList.Add("pdftotext");

            using var check = System.Diagnostics.Process.Start(probe);
            if (check is null)
                return null;
            await check.WaitForExitAsync(ct);
            if (check.ExitCode != 0)
                return null;
        }
        catch
        {
            return null;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pdftotext",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-layout");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-");

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
            return null;

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? output : null;
    }

    private static string ExtractPdfTextBasic(byte[] bytes)
    {
        var raw = Encoding.Latin1.GetString(bytes);
        var sb = new StringBuilder();
        var inText = false;

        for (var index = 0; index < raw.Length - 1; index++)
        {
            if (raw[index] == 'B' && raw[index + 1] == 'T')
            {
                inText = true;
                index++;
                continue;
            }

            if (raw[index] == 'E' && raw[index + 1] == 'T')
            {
                inText = false;
                sb.AppendLine();
                index++;
                continue;
            }

            if (!inText || raw[index] != '(')
                continue;

            index++;
            while (index < raw.Length && raw[index] != ')')
            {
                if (raw[index] == '\\' && index + 1 < raw.Length)
                {
                    index++;
                    sb.Append(raw[index] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => raw[index]
                    });
                    index++;
                    continue;
                }

                var c = raw[index];
                if (!char.IsControl(c))
                    sb.Append(c);
                index++;
            }
        }

        return sb.Length == 0 ? ExtractPrintableText(bytes) : sb.ToString();
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return utf8.GetString(bytes);
        }
        catch
        {
        }

        try
        {
            return Encoding.Unicode.GetString(bytes);
        }
        catch
        {
        }

        try
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }
        catch
        {
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static string ExtractPrintableText(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var matches = Regex.Matches(text, @"[\p{L}\p{N}\p{P}\p{Zs}]{4,}");
        if (matches.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (Match match in matches)
        {
            var value = match.Value.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(value);
        }

        return sb.ToString();
    }

    private static string StripMarkup(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var stripped = Regex.Replace(text, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(stripped);
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = StripMarkup(normalized);
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        normalized = Regex.Replace(normalized, @"[\t\x0B\f ]{2,}", " ");
        return normalized.Trim();
    }

    private static bool IsOpenXmlContainer(string extension)
        => extension is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp";

    private static bool IsTextLike(string extension)
        => extension is ".txt" or ".md" or ".markdown" or ".json" or ".jsonl" or ".yaml" or ".yml" or ".xml" or ".csv" or ".tsv" or ".html" or ".htm" or ".cs" or ".js" or ".ts" or ".py" or ".java" or ".sql";

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "node-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))))[..8].ToLowerInvariant();

        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var c in value.Trim())
        {
            if (invalidChars.Contains(c))
                continue;

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is '-' or '_' or '.' or '/' or '\\')
            {
                if (!lastWasDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }
        }

        var slug = sb.ToString().Trim('-');
        if (!string.IsNullOrWhiteSpace(slug))
            return slug;

        return "node-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
    }

    private static string BuildOriginKey(string resolvedPath)
    {
        var fullPath = ToolPathPolicy.ResolveRealPath(resolvedPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..12].ToLowerInvariant();
        var name = Slugify(Path.GetFileNameWithoutExtension(fullPath));
        return string.IsNullOrWhiteSpace(name) ? hash : $"{name}-{hash}";
    }

    internal static string? ResolveInputPath(string path)
    {
        var normalized = path.Trim();
        if (normalized.StartsWith("[FILE_URL:", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(']'))
            normalized = normalized[10..^1].Trim();

        var exact = ToolPathPolicy.ResolveRealPath(normalized);
        if (File.Exists(exact))
            return exact;

        var workspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        string[] mediaCacheDirs =
        [
            Path.Combine(workspace, "memory", "media-cache"),
            Path.Combine(Directory.GetCurrentDirectory(), "memory", "media-cache")
        ];

        if (normalized.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
        {
            var mediaId = normalized["/media/".Length..].Trim('/');
            if (!string.IsNullOrWhiteSpace(mediaId) && !mediaId.Contains('/') && !mediaId.Contains('\\') && !mediaId.Contains('.'))
            {
                foreach (var dir in mediaCacheDirs)
                {
                    if (!Directory.Exists(dir))
                        continue;

                    var matches = Directory.GetFiles(dir, mediaId + ".*");
                    if (matches.Length > 0)
                        return matches[0];
                }
            }
        }

        var fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (var dir in mediaCacheDirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private sealed record ResolvedInput(string RawPath, string ResolvedPath, string OriginKey);

    private sealed record ParsedDocument(string OriginKey, string Title, string SourcePath, string Parser, string Text);

    private sealed class ZipParseBudget
    {
        public int EntryCount { get; set; }
        public long ExpandedBytes { get; set; }
    }

    private sealed record ExistingNode(string FilePath, string Slug, string Name, string Content, bool GeneratedByTool, IReadOnlyList<string> SourceOriginKeys);

    private sealed record PersistedOntologyNode(
        string GeneratedBy,
        string Name,
        string Slug,
        string Summary,
        string Content,
        IReadOnlyList<string> SourceOriginKeys,
        IReadOnlyList<string> SourceFiles,
        string Parser,
        string UpdatedAt);
}