using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Channels;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>
/// Generic channel config admin endpoints.
/// Routes:
///   GET  /admin/channels/{channel}         — read current effective config
///   POST /admin/channels/{channel}/update  — apply in-memory override and reconnect
///
/// To add a new channel, add one case to each switch block below.
/// Auth and rate-limit logic are shared across all channels.
/// </summary>
internal static class AdminChannelEndpoints
{
    public static void MapOpenClawAdminChannelEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var operations = runtime.Operations;

        // Resolve channel adapters once at startup (only channels that support runtime updates).
        var feishu = app.Services.GetRequiredService<FeishuChannel>();
        var channelStore = app.Services.GetRequiredService<ChannelConfigStore>();

        // ── GET /admin/channels/{channel} ─────────────────────────────────────
        // Returns the currently effective config for the named channel.
        app.MapGet("/admin/channels/{channel}", (HttpContext ctx, string channel) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            return channel switch
            {
                "feishu" => Results.Json(feishu.GetEffectiveConfigForAdmin(), CoreJsonContext.Default.FeishuChannelConfig),

                // Add new channels here:
                // "slack"   => Results.Json(slack.GetEffectiveConfig(), CoreJsonContext.Default.SlackChannelConfig),
                // "discord" => Results.Json(discord.GetEffectiveConfig(), CoreJsonContext.Default.DiscordChannelConfig),

                _ => Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Unknown channel '{channel}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound)
            };
        });

        // ── POST /admin/channels/{channel}/update ─────────────────────────────
        // Applies a full in-memory config replacement and reconnects the channel.
        app.MapPost("/admin/channels/{channel}/update", async (HttpContext ctx, string channel) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.channels", out var blockedByPolicyId))
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);

            return channel switch
            {
                "feishu" => await HandleFeishuUpdateAsync(ctx, feishu, channelStore),

                // Add new channels here:
                // "slack"   => await HandleSlackUpdateAsync(ctx, slack, channelStore),
                // "discord" => await HandleDiscordUpdateAsync(ctx, discord, channelStore),

                _ => Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Unknown channel '{channel}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound)
            };
        });

        // ── DELETE /admin/channels/{channel}/override ─────────────────────────
        // Clears the in-memory + persisted override so the channel falls back to appsettings.
        app.MapDelete("/admin/channels/{channel}/override", async (HttpContext ctx, string channel) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.channels", out var blockedByPolicyId))
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);

            switch (channel)
            {
                case "feishu":
                    channelStore.Delete("feishu");
                    // Clear override so IOptionsMonitor / appsettings takes over again.
                    feishu.SetRuntimeConfig(null);
                    await feishu.RestartAsync(ctx.RequestAborted);
                    break;

                // Add new channels here

                default:
                    return Results.Json(
                        new OperationStatusResponse { Success = false, Error = $"Unknown channel '{channel}'." },
                        CoreJsonContext.Default.OperationStatusResponse,
                        statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(
                new OperationStatusResponse { Success = true, Message = $"Channel '{channel}' override cleared; reverted to appsettings." },
                CoreJsonContext.Default.OperationStatusResponse);
        });
    }

    // ── Per-channel update handlers ───────────────────────────────────────────
    // Each handler is responsible for deserializing its own typed config (AOT-safe)
    // and calling the channel's UpdateConfigAsync().

    private static async Task<IResult> HandleFeishuUpdateAsync(HttpContext ctx, FeishuChannel feishu, ChannelConfigStore channelStore)
    {
        FeishuChannelConfig? patch;
        try
        {
            patch = await ctx.Request.ReadFromJsonAsync(CoreJsonContext.Default.FeishuChannelConfig, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = $"Invalid JSON: {ex.Message}" },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (patch is null)
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = "Request body is required." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);

        // Persist to volume first so the config survives a container restart.
        channelStore.Save("feishu", patch, CoreJsonContext.Default.FeishuChannelConfig);

        // Apply in-memory and reconnect.
        await feishu.UpdateConfigAsync(patch, ctx.RequestAborted);

        return Results.Json(
            new OperationStatusResponse { Success = true, Message = "Feishu config persisted and channel reconnected." },
            CoreJsonContext.Default.OperationStatusResponse);
    }
}
