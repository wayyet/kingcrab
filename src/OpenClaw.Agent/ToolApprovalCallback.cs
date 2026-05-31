namespace OpenClaw.Agent;

/// <summary>
/// Callback invoked before an agent runtime executes a tool.
/// Returns <c>true</c> to approve, <c>false</c> to deny.
/// </summary>
/// <remarks>
/// Upstream defines this delegate in <c>AgentRuntime.cs</c>, which is excluded from kingcrab
/// (Native runtime). The delegate itself is reused by the inlined MAF runtime, so kingcrab
/// keeps a standalone declaration here.
/// </remarks>
public delegate ValueTask<bool> ToolApprovalCallback(string toolName, string arguments, CancellationToken ct);
