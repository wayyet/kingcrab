namespace OpenClaw.Plugins.AiEvaluation.Models;

public sealed class TraceData
{
    public string SessionId { get; set; } = "";
    public string Source { get; set; } = "";
    public TraceEntry[] Entries { get; set; } = [];
    public int TotalSteps { get; set; }
}

public sealed class TraceEntry
{
    public int Step { get; set; }
    public string Type { get; set; } = "";   // thinking, tool_call, message, response
    public string? Content { get; set; }
    public string? ToolName { get; set; }
    public string? ToolArguments { get; set; }
    public string? Timestamp { get; set; }
}
