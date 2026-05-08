namespace OpenClaw.Plugins.AiEvaluation.Configs;

public sealed class SandboxEndpointConfig
{
    public string? WsUrl { get; set; }
    public string? AuthToken { get; set; }
    public string SystemPrompt { get; set; } = "";
    public int ConnectTimeoutSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 120;
}
