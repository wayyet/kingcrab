namespace OpenClaw.Plugins.AiEvaluation.Configs;

public sealed class AiEvaluationConfig
{
    public bool Enabled { get; set; } = false;
    public SandboxEndpointConfig Generator { get; set; } = new();
    public SandboxEndpointConfig Validator { get; set; } = new();
    public SandboxEndpointConfig Target { get; set; } = new();
    public SandboxEndpointConfig Trace { get; set; } = new();
    public SandboxEndpointConfig Ontology { get; set; } = new();
    public SandboxEndpointConfig EvalReport { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxTestcasesPerFetch { get; set; } = 50;
    public bool EnableDualValidation { get; set; } = false;
}
