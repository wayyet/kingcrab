using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Mcp;

namespace OpenClaw.Gateway.Composition;

/// <summary>
/// Registers Microsoft Agent Framework DevUI for development environments.
/// DevUI provides a web-based interface at /devui for testing and inspecting
/// the registered agent (tools list, instructions, model metadata) and
/// sending test messages via the OpenAI-compatible Responses API.
///
/// The DevUI chat is backed by <see cref="DevUIPipelineChatClient"/> which
/// routes every message through the full OpenClaw IAgentRuntime pipeline
/// (tools, memory, approval hooks, circuit-breaker, telemetry).
/// </summary>
internal static class DevUIExtensions
{
    private const string DevUIInstructions =
        "You are OpenClaw, a self-hosted AI assistant. " +
        "You run locally on the user's machine and can execute tools to interact with the operating system, " +
        "files, and external services. Be concise, helpful, and security-conscious.";

    /// <summary>
    /// Adds the DevUI services and registers the OpenClaw agent in the DI container
    /// so the DevUI entity-discovery endpoint can enumerate it.
    /// Must be called after the startup config is available (post-bootstrap).
    /// </summary>
    public static IServiceCollection AddOpenClawDevUI(
        this IServiceCollection services,
        GatewayConfig config)
    {
        // The pipeline bridge — routes DevUI chat messages through IAgentRuntime.
        // GatewayRuntimeHolder is resolved lazily at request time (not at build
        // time), so it is safe to register in DI before app.Build() is called.
        services.AddSingleton<DevUIPipelineChatClient>();

        // Register a ChatClientAgent backed by the pipeline bridge.
        // The DevUI entity-discovery endpoint resolves AIAgent out of the DI
        // container (both keyed and default), so we expose it as a keyed service
        // with the agent name as the key AND as a plain default singleton.
        services.AddKeyedSingleton<AIAgent>(
            ServiceKeys.DevUIAgent,
            (sp, _) => CreateDevUIAgent(sp));

        services.AddSingleton<AIAgent>(sp =>
            sp.GetRequiredKeyedService<AIAgent>(ServiceKeys.DevUIAgent));

        // AddDevUI registers the keyed-factory fallback that lets DevUI resolve
        // agents/workflows by key from the DI container.
        services.AddDevUI();

        // OpenAI-compatible Responses + Conversations hosting —
        // required so the DevUI SPA chat panel can send test messages.
        services.AddOpenAIResponses();
        services.AddOpenAIConversations();

        return services;
    }

    /// <summary>
    /// Maps the /devui SPA, the /v1/entities discovery API, and the
    /// OpenAI-compatible endpoints that the DevUI frontend calls.
    /// </summary>
    public static WebApplication MapOpenClawDevUI(this WebApplication app)
    {
        // OpenAI-compatible API endpoints consumed by the DevUI SPA.
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();

        // DevUI SPA at /devui  +  /v1/entities  +  /meta
        app.MapDevUI();

        app.Logger.LogInformation("DevUI enabled — open http://{Host}:{Port}/devui",
            app.Configuration["OpenClaw:BindAddress"] ?? "localhost",
            app.Configuration["OpenClaw:Port"] ?? "18789");

        return app;
    }

    // -----------------------------------------------------------------------

    private static ChatClientAgent CreateDevUIAgent(IServiceProvider sp)
    {
        // The pipeline bridge is a singleton registered above; every ChatClientAgent
        // call is intercepted and routed through IAgentRuntime.
        var pipelineClient = sp.GetRequiredService<DevUIPipelineChatClient>();
        var factory = sp.GetRequiredService<MafAgentFactory>();
        var holder = sp.GetRequiredService<GatewayRuntimeHolder>();

        // This factory delegate runs on the FIRST DI resolution of the keyed service,
        // which happens at the first incoming HTTP request — always AFTER
        // InitializeOpenClawRuntimeAsync() has completed.  LoadedTools therefore
        // contains the full set of plugin / skill tools with name, description and
        // JSON schema, so DevUI's entity panel shows them correctly.
        //
        // The tools are passed for METADATA only.  DevUIPipelineChatClient.GetService
        // returns a non-null value for FunctionInvokingChatClient which signals to
        // ChatClientAgent that tool invocation is handled by the bridge internally,
        // preventing any re-invocation loop.
        var tools = holder.Runtime.AgentRuntime.LoadedTools;
        return factory.Create(pipelineClient, DevUIInstructions, [.. tools]);
    }

    // -----------------------------------------------------------------------

    private static class ServiceKeys
    {
        public const string DevUIAgent = "openclaw-devui";
    }
}
