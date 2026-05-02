namespace OpenClaw.Core.Security;

public static class OntologyIngestPathResolver
{
    public static string? ResolveInputPath(string path)
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
}
