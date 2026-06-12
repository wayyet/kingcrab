using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Apply a unified diff patch to a file. Supports multi-hunk patches.
/// </summary>
public sealed class ApplyPatchTool : ITool
{
    private readonly ToolingConfig _config;

    public ApplyPatchTool(ToolingConfig config) => _config = config;

    public string Name => "apply_patch";
    public string Description => "Apply a unified diff patch to a file. Supports multi-hunk patches for complex edits.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "path":{"type":"string","description":"File path to patch for legacy single-file mode."},
        "patch":{"type":"string","description":"Legacy single-file unified diff patch or full Begin Patch envelope."},
        "patchText":{"type":"string","description":"OpenCode-style full patch text using *** Begin Patch / *** End Patch markers."}
      }
    }
    """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (_config.ReadOnlyMode)
            return "Error: apply_patch is disabled because Tooling.ReadOnlyMode is enabled.";

        try
        {
            using var args = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = args.RootElement;
            var patchText = GetString(root, "patchText");
            var path = GetString(root, "path");
            var patch = GetString(root, "patch");

            if (!string.IsNullOrWhiteSpace(patchText))
                return await ApplyBeginPatchAsync(patchText, ct);

            if (!string.IsNullOrWhiteSpace(patch) && ContainsBeginPatchEnvelope(patch))
                return await ApplyBeginPatchAsync(patch, ct);

            if (string.IsNullOrWhiteSpace(path))
                return "Error: 'path' is required when patchText is not provided.";
            if (string.IsNullOrWhiteSpace(patch))
                return "Error: 'patch' is required.";

            return await ApplyLegacySingleFilePatchAsync(path, patch, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : $"Error: {ex.Message}";
        }
    }

    private async Task<string> ApplyLegacySingleFilePatchAsync(string path, string patch, CancellationToken ct)
    {
        var resolvedPath = ToolPathPolicy.ResolveRealPath(path);

        if (!ToolPathPolicy.IsWriteAllowed(_config, resolvedPath))
            return $"Error: Write access denied for path: {path}";

        if (!File.Exists(resolvedPath))
            return $"Error: File not found: {path}";

        var originalLines = await File.ReadAllLinesAsync(resolvedPath, ct);
        var hunks = ParseLegacyHunks(patch);

        if (hunks.Count == 0)
            return "Error: No valid hunks found in patch. Use @@ -start,count +start,count @@ headers.";

        var result = ApplyLegacyHunks(originalLines, hunks);
        await WriteAllLinesAtomicAsync(resolvedPath, result, ct);
        return $"Applied {hunks.Count} hunk(s) to {path}.";
    }

    private async Task<string> ApplyBeginPatchAsync(string patchText, CancellationToken ct)
    {
        var operations = ParseBeginPatchOperations(patchText);
        if (operations.Count == 0)
            return "Error: patch rejected: empty patch";

        foreach (var operation in operations)
        {
            var resolvedPath = ToolPathPolicy.ResolveRealPath(operation.Path);
            if (!ToolPathPolicy.IsWriteAllowed(_config, resolvedPath))
                return $"Error: Write access denied for path: {operation.Path}";

            switch (operation.Kind)
            {
                case BeginPatchOperationKind.Add:
                    if (File.Exists(resolvedPath))
                        return $"Error: File already exists: {operation.Path}";
                    Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath) ?? resolvedPath);
                    await WriteTextAtomicAsync(resolvedPath, string.Join(Environment.NewLine, operation.AddContent), ct);
                    break;

                case BeginPatchOperationKind.Delete:
                    if (!File.Exists(resolvedPath))
                        return $"Error: File not found: {operation.Path}";
                    File.Delete(resolvedPath);
                    break;

                case BeginPatchOperationKind.Update:
                    if (!File.Exists(resolvedPath))
                        return $"Error: Failed to read file to update: {operation.Path}";

                    var fileLines = new List<string>(await File.ReadAllLinesAsync(resolvedPath, ct));
                    foreach (var chunk in operation.UpdateChunks)
                    {
                        var apply = ApplyUpdateChunk(fileLines, chunk);
                        if (!apply.Success)
                            return $"Error: apply_patch verification failed: {apply.Error}";
                    }

                    await WriteAllLinesAtomicAsync(resolvedPath, fileLines, ct);
                    break;
            }
        }

        return $"Applied patch to {operations.Count} file(s).";
    }

    private sealed record Hunk(int OriginalStart, List<string> RemoveLines, List<string> AddLines);
    private sealed record BeginPatchOperation(BeginPatchOperationKind Kind, string Path, List<string> AddContent, List<UpdateChunk> UpdateChunks);
    private sealed record UpdateChunk(string? Anchor, List<PatchLine> Lines);
    private sealed record PatchLine(char Kind, string Text);
    private sealed record ApplyChunkResult(bool Success, string Error);

    private enum BeginPatchOperationKind
    {
        Add,
        Delete,
        Update
    }

    private static List<Hunk> ParseLegacyHunks(string patch)
    {
        var hunks = new List<Hunk>();
        var lines = patch.Split('\n');
        Hunk? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current is not null)
                    hunks.Add(current);

                var origStart = ParseHunkStart(line);
                current = new Hunk(origStart, [], []);
            }
            else if (current is not null)
            {
                if (line.StartsWith('-'))
                    current.RemoveLines.Add(line[1..]);
                else if (line.StartsWith('+'))
                    current.AddLines.Add(line[1..]);
                // Context lines (starting with space) are skipped — we trust line numbers
            }
        }

        if (current is not null)
            hunks.Add(current);

        return hunks;
    }

    private static List<string> ApplyLegacyHunks(IReadOnlyList<string> originalLines, IReadOnlyList<Hunk> hunks)
    {
        var result = new List<string>(originalLines);
        var offset = 0;

        foreach (var hunk in hunks)
        {
            var startLine = hunk.OriginalStart - 1 + offset;
            if (startLine < 0 || startLine > result.Count)
                throw new InvalidOperationException($"Hunk at line {hunk.OriginalStart} is out of range (file has {result.Count} lines).");

            if (startLine + hunk.RemoveLines.Count > result.Count)
                throw new InvalidOperationException($"Hunk at line {hunk.OriginalStart} expects {hunk.RemoveLines.Count} lines to remove, but only {result.Count - startLine} lines remain.");

            for (var i = 0; i < hunk.RemoveLines.Count; i++)
            {
                var expected = hunk.RemoveLines[i];
                var actual = result[startLine + i];
                if (!string.Equals(expected.TrimEnd(), actual.TrimEnd(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Hunk at line {hunk.OriginalStart + i} mismatch. Expected: \"{Truncate(expected, 60)}\" Got: \"{Truncate(actual, 60)}\"");
            }

            for (var i = 0; i < hunk.RemoveLines.Count; i++)
                result.RemoveAt(startLine);

            for (var i = hunk.AddLines.Count - 1; i >= 0; i--)
                result.Insert(startLine, hunk.AddLines[i]);

            offset += hunk.AddLines.Count - hunk.RemoveLines.Count;
        }

        return result;
    }

    private static bool ContainsBeginPatchEnvelope(string value)
        => value.Contains("*** Begin Patch", StringComparison.Ordinal) && value.Contains("*** End Patch", StringComparison.Ordinal);

    private static List<BeginPatchOperation> ParseBeginPatchOperations(string patchText)
    {
        var normalized = patchText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var operations = new List<BeginPatchOperation>();
        var index = 0;

        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
            index++;

        if (index >= lines.Length || !string.Equals(lines[index].Trim(), "*** Begin Patch", StringComparison.Ordinal))
            throw new InvalidOperationException("apply_patch verification failed: missing *** Begin Patch header");

        index++;
        while (index < lines.Length)
        {
            var line = lines[index].TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            if (string.Equals(line, "*** End Patch", StringComparison.Ordinal))
                break;

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                var path = line[14..].Trim();
                index++;
                var addContent = new List<string>();
                while (index < lines.Length && !IsPatchHeader(lines[index]))
                {
                    var contentLine = lines[index].TrimEnd('\r');
                    if (contentLine.StartsWith('+'))
                        addContent.Add(contentLine[1..]);
                    else if (contentLine.Length == 0)
                        addContent.Add(string.Empty);
                    else
                        throw new InvalidOperationException($"apply_patch verification failed: add file content must use '+' lines for {path}");
                    index++;
                }

                operations.Add(new BeginPatchOperation(BeginPatchOperationKind.Add, path, addContent, []));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                operations.Add(new BeginPatchOperation(BeginPatchOperationKind.Delete, line[17..].Trim(), [], []));
                index++;
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                var path = line[17..].Trim();
                index++;
                var chunks = new List<UpdateChunk>();
                while (index < lines.Length && !IsFileOperationHeader(lines[index]) && !string.Equals(lines[index].Trim(), "*** End Patch", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(lines[index]))
                    {
                        index++;
                        continue;
                    }

                    var header = lines[index].TrimEnd();
                    if (!header.StartsWith("@@", StringComparison.Ordinal))
                        throw new InvalidOperationException($"apply_patch verification failed: expected @@ header for update {path}");

                    var anchor = ParseAnchor(header);
                    index++;
                    var chunkLines = new List<PatchLine>();
                    while (index < lines.Length && !lines[index].TrimEnd().StartsWith("@@", StringComparison.Ordinal) && !IsFileOperationHeader(lines[index]) && !string.Equals(lines[index].Trim(), "*** End Patch", StringComparison.Ordinal))
                    {
                        var patchLine = lines[index].TrimEnd('\r');
                        if (patchLine.Length == 0)
                        {
                            chunkLines.Add(new PatchLine(' ', string.Empty));
                        }
                        else if (patchLine[0] is '+' or '-' or ' ')
                        {
                            chunkLines.Add(new PatchLine(patchLine[0], patchLine[1..]));
                        }
                        else
                        {
                            chunkLines.Add(new PatchLine(' ', patchLine));
                        }
                        index++;
                    }

                    chunks.Add(new UpdateChunk(anchor, chunkLines));
                }

                operations.Add(new BeginPatchOperation(BeginPatchOperationKind.Update, path, [], chunks));
                continue;
            }

            throw new InvalidOperationException($"apply_patch verification failed: unsupported patch header '{line}'");
        }

        return operations;
    }

    private static ApplyChunkResult ApplyUpdateChunk(List<string> fileLines, UpdateChunk chunk)
    {
        var oldLines = chunk.Lines.Where(static line => line.Kind is ' ' or '-').Select(static line => line.Text).ToList();
        var newLines = chunk.Lines.Where(static line => line.Kind is ' ' or '+').Select(static line => line.Text).ToList();
        var startIndex = FindChunkStart(fileLines, chunk.Anchor, oldLines);
        if (startIndex < 0)
            return new ApplyChunkResult(false, "could not locate matching lines for update");

        if (oldLines.Count == 0)
        {
            fileLines.InsertRange(startIndex, newLines);
            return new ApplyChunkResult(true, string.Empty);
        }

        fileLines.RemoveRange(startIndex, oldLines.Count);
        fileLines.InsertRange(startIndex, newLines);
        return new ApplyChunkResult(true, string.Empty);
    }

    private static int FindChunkStart(IReadOnlyList<string> fileLines, string? anchor, IReadOnlyList<string> oldLines)
    {
        var searchStart = 0;
        if (!string.IsNullOrWhiteSpace(anchor))
        {
            var anchorIndex = IndexOfLine(fileLines, anchor);
            if (anchorIndex >= 0)
                searchStart = anchorIndex;
        }

        if (oldLines.Count == 0)
            return searchStart;

        for (var index = searchStart; index <= fileLines.Count - oldLines.Count; index++)
        {
            var matched = true;
            for (var offset = 0; offset < oldLines.Count; offset++)
            {
                if (!string.Equals(fileLines[index + offset], oldLines[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return index;
        }

        return -1;
    }

    private static int IndexOfLine(IReadOnlyList<string> lines, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static bool IsPatchHeader(string line)
        => IsFileOperationHeader(line) || string.Equals(line.Trim(), "*** End Patch", StringComparison.Ordinal);

    private static bool IsFileOperationHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("*** Add File: ", StringComparison.Ordinal)
            || trimmed.StartsWith("*** Delete File: ", StringComparison.Ordinal)
            || trimmed.StartsWith("*** Update File: ", StringComparison.Ordinal);
    }

    private static string? ParseAnchor(string header)
    {
        var trimmed = header.Trim();
        if (trimmed == "@@")
            return null;

        var last = trimmed.LastIndexOf("@@", StringComparison.Ordinal);
        if (last < 2)
            return null;

        var candidate = trimmed[2..last].Trim();
        if (candidate.StartsWith("-", StringComparison.Ordinal))
            return null;

        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private static async Task WriteAllLinesAtomicAsync(string path, IReadOnlyList<string> lines, CancellationToken ct)
        => await WriteTextAtomicAsync(path, string.Join(Environment.NewLine, lines), ct);

    private static async Task WriteTextAtomicAsync(string path, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static int ParseHunkStart(string header)
    {
        // Parse @@ -start,count +start,count @@
        var idx = header.IndexOf('-', 3);
        if (idx < 0) return 1;
        var comma = header.IndexOf(',', idx);
        var end = comma > 0 ? comma : header.IndexOf(' ', idx + 1);
        if (end < 0) end = header.Length;
        return int.TryParse(header.AsSpan(idx + 1, end - idx - 1), out var start) ? start : 1;
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";

    private static string? GetString(System.Text.Json.JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : null;
}
