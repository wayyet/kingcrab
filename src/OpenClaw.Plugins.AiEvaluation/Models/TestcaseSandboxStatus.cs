namespace OpenClaw.Plugins.AiEvaluation.Models;

public sealed class TestcaseSandboxStatus
{
    public string Role { get; set; } = "";
    public bool Connected { get; set; }
    public string? WsUrl { get; set; }
    public string? LastError { get; set; }
}
