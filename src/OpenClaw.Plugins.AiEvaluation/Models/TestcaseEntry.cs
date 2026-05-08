using System.Text.Json;

namespace OpenClaw.Plugins.AiEvaluation.Models;

public sealed class TestcaseEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Steps { get; set; } = [];
    public string ExpectedResult { get; set; } = "";
    public string? Priority { get; set; }
    public string[] Tags { get; set; } = [];
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
