using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenClaw.TokenHubSink.Models;
using OpenClaw.TokenHubSink.Observability;

namespace OpenClaw.TokenHubSink;

/// <summary>
/// Wires the TokenHub thin-client sink into DI. "http" registers the batching <see cref="HttpTokenUsageSink"/>
/// (also as its hosted background service) and exposes it as the single <see cref="ITokenUsageEventSink"/>;
/// anything else binds the no-op sink so the LLM hot path pays nothing. The bound <see cref="TokenUsageConfig"/>
/// is always registered so callers can read <see cref="TokenUsageConfig.AgentId"/>.
/// </summary>
public static class TokenHubSinkServiceCollectionExtensions
{
    public static IServiceCollection AddTokenHubSink(this IServiceCollection services, TokenUsageConfig config)
    {
        services.AddSingleton(config);

        if (config.IsHttpSinkEnabled)
        {
            services.AddSingleton<HttpTokenUsageSink>(sp =>
                new HttpTokenUsageSink(config, sp.GetRequiredService<ILogger<HttpTokenUsageSink>>()));
            services.AddSingleton<ITokenUsageEventSink>(sp => sp.GetRequiredService<HttpTokenUsageSink>());
            services.AddHostedService(sp => sp.GetRequiredService<HttpTokenUsageSink>());
        }
        else
        {
            services.AddSingleton<ITokenUsageEventSink>(NullTokenUsageEventSink.Instance);
        }

        return services;
    }
}
