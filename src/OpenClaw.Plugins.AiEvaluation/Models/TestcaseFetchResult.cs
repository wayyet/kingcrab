namespace OpenClaw.Plugins.AiEvaluation.Models;

public sealed class TestcaseFetchResult
{
    public int TotalCount { get; set; }
    public string Source { get; set; } = "";
    public TestcaseEntry[] Testcases { get; set; } = [];
    public string? RawResponse { get; set; }
    public string? ValidationNotes { get; set; }
}
