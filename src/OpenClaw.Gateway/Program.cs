using OpenClaw.Agent;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Mcp;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.Gateway.Profiles;
using System.Text;
#if OPENCLAW_ENABLE_OPENSANDBOX
using OpenClawNet.Sandbox.OpenSandbox;
#endif

var builder = WebApplication.CreateSlimBuilder(args);

// 设置控制台输出编码为UTF-8
Console.OutputEncoding = Encoding.UTF8;

var bootstrap = await builder.AddOpenClawBootstrapAsync(args);
if (bootstrap.ShouldExit)
{
    Environment.ExitCode = bootstrap.ExitCode;
    return;
}

var startup = bootstrap.Startup
    ?? throw new InvalidOperationException("Bootstrap completed without a startup context.");
builder.Services.AddOpenApi("openclaw-integration");
builder.AddOpenClawObservability();
builder.Services.AddOpenClawCoreServices(startup);
builder.Services.AddOpenClawChannelServices(startup);
builder.Services.AddOpenClawToolServices(startup);
builder.Services.AddOpenClawSecurityServices(startup);
builder.Services.AddOpenClawMcpServices(startup);
builder.Services.ApplyOpenClawRuntimeProfile(startup);
builder.Services.AddMicrosoftAgentFramework(builder.Configuration);
if (builder.Environment.IsDevelopment())
    builder.Services.AddOpenClawDevUI(startup.Config);
#if OPENCLAW_ENABLE_OPENSANDBOX
builder.Services.AddOpenSandboxIntegration(builder.Configuration);
#endif

var app = builder.Build();
var runtime = await app.InitializeOpenClawRuntimeAsync(startup);

// Populate the GatewayRuntimeHolder so MCP tools can access the runtime via DI.
app.InitializeMcpRuntime(runtime);

// Browser WebSocket API cannot set custom Authorization headers.
// Bridge /ws?token=... into Authorization: Bearer ... so standard auth can validate it.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase)
        && !ctx.Request.Headers.ContainsKey("Authorization"))
    {
        var queryToken = ctx.Request.Query["token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(queryToken))
            ctx.Request.Headers.Authorization = $"Bearer {queryToken}";
    }

    await next(ctx);
});

// Enable ASP.NET Core authentication middleware when OIDC is configured.
if (!string.IsNullOrEmpty(startup.Config.Security.OidcAuthority))
    app.UseAuthentication();
app.UseOpenClawMcpAuth(startup, runtime);

app.UseOpenClawPipeline(startup, runtime);
app.MapOpenApi("/openapi/{documentName}.json");
app.MapOpenClawEndpoints(startup, runtime);
app.MapMcp("/mcp");

if (app.Environment.IsDevelopment())
    app.MapOpenClawDevUI();

app.Run($"http://{startup.Config.BindAddress}:{startup.Config.Port}");
