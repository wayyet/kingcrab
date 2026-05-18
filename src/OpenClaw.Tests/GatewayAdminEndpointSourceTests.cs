using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayAdminEndpointSourceTests
{
    [Fact]
    public async Task AdminSessionDeleteEndpoint_IsMappedForWebchatCleanup()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "OpenClaw.Gateway", "Endpoints", "AdminEndpoints.cs"));
        var source = await File.ReadAllTextAsync(sourcePath, CancellationToken.None);

        Assert.Contains("MapDelete(\"/admin/sessions/{id}\"", source, StringComparison.Ordinal);
    }
}
