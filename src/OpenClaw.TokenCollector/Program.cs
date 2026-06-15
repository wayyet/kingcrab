using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;
using OpenClaw.TokenCollector;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Collector").Get<CollectorOptions>() ?? new CollectorOptions();

builder.WebHost.UseUrls(options.BindUrl);

builder.Services.AddSingleton(options.Kafka);
builder.Services.AddSingleton<KafkaTokenUsagePublisher>();
builder.Services.AddSingleton<ITokenUsageEventSink>(sp => sp.GetRequiredService<KafkaTokenUsagePublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<KafkaTokenUsagePublisher>());

var app = builder.Build();

// Resolved once at startup; the gateway sends Authorization: Bearer <this>.
var authToken = SecretResolver.Resolve(options.AuthTokenRef, app.Logger);
if (string.IsNullOrEmpty(authToken))
    app.Logger.LogWarning(
        "Collector auth token is not set ({Ref}); the ingest endpoint will accept unauthenticated requests.",
        options.AuthTokenRef);

app.MapGet("/health", (HttpContext ctx) => ctx.Response.WriteAsync("ok"));

app.MapPost("/ingest/token-usage", async (HttpContext ctx, ITokenUsageEventSink sink) =>
{
    if (!string.IsNullOrEmpty(authToken) && !IsAuthorized(ctx, authToken))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (ctx.Request.ContentLength is { } len && len > options.MaxRequestBytes)
    {
        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    string body;
    using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        body = await reader.ReadToEndAsync(ctx.RequestAborted);

    SessionTokenUsageEvent[]? events;
    try
    {
        events = JsonSerializer.Deserialize(body, TokenUsageJsonContext.Default.SessionTokenUsageEventArray);
    }
    catch (JsonException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Invalid JSON.", ctx.RequestAborted);
        return;
    }

    if (events is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Expected a JSON array of token usage events.", ctx.RequestAborted);
        return;
    }

    foreach (var evt in events)
        sink.Publish(evt);

    ctx.Response.StatusCode = StatusCodes.Status202Accepted;
});

app.Run();

static bool IsAuthorized(HttpContext ctx, string expected)
{
    const string prefix = "Bearer ";
    var header = ctx.Request.Headers.Authorization.ToString();
    var provided = header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..] : string.Empty;
    var providedBytes = Encoding.UTF8.GetBytes(provided);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return providedBytes.Length == expectedBytes.Length
        && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}
