namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// Singleton holder that bridges the DI container to <see cref="McpWorkspaceWatcherService"/>,
/// which is constructed after the container is built.  Same pattern as <see cref="GatewayRuntimeHolder"/>.
/// </summary>
internal sealed class McpWatcherHolder
{
    public McpWorkspaceWatcherService? Watcher { get; set; }
}
