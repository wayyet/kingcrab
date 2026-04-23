using OpenClaw.Gateway.Endpoints;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HireBotIntegrationEndpointsTests
{
    [Fact]
    public void MergeMaterials_SkipsDuplicateContentHashes()
    {
        var existing = new List<HireBotIntegrationEndpoints.ConversationMaterial>
        {
            new(
                Type: "text",
                Name: "existing",
                Content: "hello",
                ContentHash: "hash-1",
                Size: 5,
                MimeType: "text/plain",
                Metadata: null)
        };

        var incoming = new[]
        {
            new HireBotIntegrationEndpoints.ConversationMaterial(
                Type: "text",
                Name: "duplicate",
                Content: "hello again",
                ContentHash: "hash-1",
                Size: 11,
                MimeType: "text/plain",
                Metadata: null),
            new HireBotIntegrationEndpoints.ConversationMaterial(
                Type: "file",
                Name: "brief.txt",
                Content: "world",
                ContentHash: "hash-2",
                Size: 5,
                MimeType: "text/plain",
                Metadata: new Dictionary<string, string>
                {
                    ["source"] = "upload"
                })
        };

        var added = HireBotIntegrationEndpoints.MergeMaterials(existing, incoming);

        Assert.Equal(1, added);
        Assert.Collection(
            existing,
            material => Assert.Equal("hash-1", material.ContentHash),
            material => Assert.Equal("hash-2", material.ContentHash));
    }
}
