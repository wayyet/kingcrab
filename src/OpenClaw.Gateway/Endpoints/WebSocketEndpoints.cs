using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class WebSocketEndpoints
{
    public static void MapOpenClawWebSocketEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.Map("/ws", async (HttpContext ctx) =>
        {
            var logger = ctx.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("OpenClaw.Gateway.WebSocket");
            var originHeader = ctx.Request.Headers.Origin.ToString();
            var forwardedHost = ctx.Request.Headers["X-Forwarded-Host"].ToString();
            var forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
            var forwardedFor = ctx.Request.Headers["X-Forwarded-For"].ToString();

            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                logger.LogWarning(
                    "Rejected /ws request because it was not a WebSocket upgrade. Path={Path}, Scheme={Scheme}, Host={Host}, Origin={Origin}, XForwardedHost={XForwardedHost}, XForwardedProto={XForwardedProto}, XForwardedFor={XForwardedFor}",
                    ctx.Request.Path,
                    ctx.Request.Scheme,
                    ctx.Request.Host.Value,
                    originHeader,
                    forwardedHost,
                    forwardedProto,
                    forwardedFor);
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (ctx.Request.Headers.TryGetValue("Origin", out var origin))
            {
                var originStr = origin.ToString();
                if (!string.IsNullOrWhiteSpace(originStr))
                {
                    if (runtime.AllowedOriginsSet is not null)
                    {
                        if (!runtime.AllowedOriginsSet.Contains(originStr))
                        {
                            logger.LogWarning(
                                "Rejected /ws request because Origin was not allowed. Origin={Origin}, AllowedOrigins={AllowedOrigins}, Scheme={Scheme}, Host={Host}, XForwardedHost={XForwardedHost}, XForwardedProto={XForwardedProto}, XForwardedFor={XForwardedFor}",
                                originStr,
                                string.Join(",", runtime.AllowedOriginsSet),
                                ctx.Request.Scheme,
                                ctx.Request.Host.Value,
                                forwardedHost,
                                forwardedProto,
                                forwardedFor);
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }
                    }
                    else
                    {
                        if (!Uri.TryCreate(originStr, UriKind.Absolute, out var originUri))
                        {
                            logger.LogWarning(
                                "Rejected /ws request because Origin could not be parsed. Origin={Origin}, Scheme={Scheme}, Host={Host}, XForwardedHost={XForwardedHost}, XForwardedProto={XForwardedProto}, XForwardedFor={XForwardedFor}",
                                originStr,
                                ctx.Request.Scheme,
                                ctx.Request.Host.Value,
                                forwardedHost,
                                forwardedProto,
                                forwardedFor);
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        var host = ctx.Request.Host;
                        if (!host.HasValue)
                        {
                            logger.LogWarning(
                                "Rejected /ws request because Host was empty during same-origin validation. Origin={Origin}, Scheme={Scheme}, XForwardedHost={XForwardedHost}, XForwardedProto={XForwardedProto}, XForwardedFor={XForwardedFor}",
                                originStr,
                                ctx.Request.Scheme,
                                forwardedHost,
                                forwardedProto,
                                forwardedFor);
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        var expectedScheme = ctx.Request.Scheme;
                        var expectedHost = host.Host;
                        var expectedPort = host.Port ?? (string.Equals(expectedScheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
                        var originPort = originUri.IsDefaultPort
                            ? (string.Equals(originUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                            : originUri.Port;

                        var sameOrigin =
                            string.Equals(originUri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(originUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) &&
                            originPort == expectedPort;

                        if (!sameOrigin)
                        {
                            logger.LogWarning(
                                "Rejected /ws request because same-origin validation failed. Origin={Origin}, ExpectedScheme={ExpectedScheme}, ExpectedHost={ExpectedHost}, ExpectedPort={ExpectedPort}, OriginScheme={OriginScheme}, OriginHost={OriginHost}, OriginPort={OriginPort}, XForwardedHost={XForwardedHost}, XForwardedProto={XForwardedProto}, XForwardedFor={XForwardedFor}",
                                originStr,
                                expectedScheme,
                                expectedHost,
                                expectedPort,
                                originUri.Scheme,
                                originUri.Host,
                                originPort,
                                forwardedHost,
                                forwardedProto,
                                forwardedFor);
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }
                    }
                }
            }

            // Auth is enforced by UseOpenClawMcpAuth middleware for /ws;
            // this call handles the non-OIDC static-token path and respects AlwaysRequireAuth.
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
            {
                logger.LogWarning(
                    "Rejected /ws request because authorization failed. Scheme={Scheme}, Host={Host}, Origin={Origin}, HasAuthorizationHeader={HasAuthorizationHeader}, HasQueryToken={HasQueryToken}, XForwardedHost={XForwardedHost}, XForwardedProto={XForwardedProto}, XForwardedFor={XForwardedFor}",
                    ctx.Request.Scheme,
                    ctx.Request.Host.Value,
                    originHeader,
                    ctx.Request.Headers.ContainsKey("Authorization"),
                    !string.IsNullOrWhiteSpace(ctx.Request.Query["token"].FirstOrDefault()),
                    forwardedHost,
                    forwardedProto,
                    forwardedFor);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!runtime.Operations.ActorRateLimits.TryConsume("ip", EndpointHelpers.GetRemoteIpKey(ctx), "websocket", out _))
            {
                logger.LogWarning(
                    "Rejected /ws request because rate limit was exceeded. RemoteIp={RemoteIp}, Scheme={Scheme}, Host={Host}, Origin={Origin}, XForwardedHost={XForwardedHost}, XForwardedProto={XForwardedProto}, XForwardedFor={XForwardedFor}",
                    ctx.Connection.RemoteIpAddress,
                    ctx.Request.Scheme,
                    ctx.Request.Host.Value,
                    originHeader,
                    forwardedHost,
                    forwardedProto,
                    forwardedFor);
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var clientId = ctx.Connection.Id;
            await runtime.WebSocketChannel.HandleConnectionAsync(ws, clientId, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
        });
    }
}
