using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;

namespace OpenClaw.Gateway.Mcp;

internal static class McpSdkExtensions
{
    /// <summary>
    /// Registers the official ModelContextProtocol.AspNetCore MCP server plus the
    /// DI infrastructure needed to bridge <see cref="GatewayAppRuntime"/> into
    /// the tool / resource / prompt classes.
    /// Call this from the service registration phase (before <c>builder.Build()</c>).
    /// Then call <see cref="InitializeRuntime"/> after the runtime is created.
    /// </summary>
    public static IServiceCollection AddOpenClawMcpSdkServices(
        this IServiceCollection services,
        GatewayStartupContext startup)
    {
        // GatewayAppRuntime is built after the DI container; the holder bridges this gap.
        services.AddSingleton<GatewayRuntimeHolder>();

        // IntegrationApiFacade wraps all gate-level operations. Registered as singleton
        // because every dependency (GatewayRuntimeHolder, IMemoryStore) is also singleton.
        services.AddSingleton<IntegrationApiFacade>(sp =>
        {
            var holder = sp.GetRequiredService<GatewayRuntimeHolder>();
            var sessionAdminStore = (ISessionAdminStore)sp.GetRequiredService<IMemoryStore>();
            return new IntegrationApiFacade(startup, holder.Runtime, sessionAdminStore);
        });

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "OpenClaw Gateway MCP",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport()
            .WithTools<OpenClawMcpTools>()
            .WithResources<OpenClawMcpResources>()
            .WithPrompts<OpenClawMcpPrompts>();

        return services;
    }

    /// <summary>
    /// Populates <see cref="GatewayRuntimeHolder.Runtime"/> after the runtime is created.
    /// Must be called before any MCP requests are served.
    /// </summary>
    public static void InitializeMcpRuntime(this WebApplication app, GatewayAppRuntime runtime)
    {
        app.Services.GetRequiredService<GatewayRuntimeHolder>().Runtime = runtime;
    }

    /// <summary>
    /// Adds a lightweight middleware that enforces the same token-based authorization
    /// used by all other OpenClaw endpoints on requests to <c>/mcp</c>.
    /// When <c>OpenClaw:Security:OidcAuthority</c> is configured, standard OIDC JWT Bearer
    /// validation is used for <c>/mcp</c> and <c>/api/</c> paths regardless of loopback bind;
    /// browser sessions remain a valid alternative for <c>/api/</c> admin endpoints.
    /// </summary>
    public static void UseOpenClawMcpAuth(this WebApplication app, GatewayStartupContext startup)
    {
        var runtimeHolder = app.Services.GetRequiredService<GatewayRuntimeHolder>();
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var useOidc = !string.IsNullOrEmpty(startup.Config.Security.OidcAuthority);

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var isMcp = path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
            var isApi = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
            var isWs  = path.StartsWithSegments("/ws",  StringComparison.OrdinalIgnoreCase);

            if (isMcp || isWs)
            {
                // OIDC mode: UseAuthentication() has already validated the JWT and populated ctx.User.
                // Static token mode: fall back to the existing bearer-token comparison.
                var authorized = useOidc
                    ? ctx.User.Identity?.IsAuthenticated == true
                    : EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind);

                if (!authorized)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
                    return;
                }

                if (!runtimeHolder.Runtime.Operations.ActorRateLimits.TryConsume(
                        "ip",
                        EndpointHelpers.GetRemoteIpKey(ctx),
                        "mcp_http",
                        out var blockedByPolicyId))
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync($"{{\"error\":\"Rate limit exceeded by policy '{blockedByPolicyId}'.\"}}");
                    return;
                }
            }
            else if (useOidc && isApi)
            {
                // When OIDC is configured, enforce auth on /api/ paths regardless of loopback bind.
                // Accept either a valid OIDC JWT (ctx.User populated by UseAuthentication()) or an
                // active browser session (admin Web UI) so that neither consumer is broken.
                var jwtOk = ctx.User.Identity?.IsAuthenticated == true;
                var sessionOk = browserSessions.TryAuthorize(ctx, requireCsrf: false, out _);

                if (!jwtOk && !sessionOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
                    return;
                }
            }

            await next(ctx);
        });
    }
}
