using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal static class SecurityServicesExtensions
{
    public static IServiceCollection AddOpenClawSecurityServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        // Register standard OIDC/JWT Bearer authentication when OidcAuthority is configured.
        if (!string.IsNullOrEmpty(startup.Config.Security.OidcAuthority))
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = startup.Config.Security.OidcAuthority;
                    options.Audience = startup.Config.Security.OidcAudience;
                    options.RequireHttpsMetadata = startup.Config.Security.OidcRequireHttpsMetadata;
                });
            services.AddAuthorization();
        }

        services.AddSingleton<ToolApprovalService>();
        services.AddSingleton(sp =>
            new PairingManager(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PairingManager>>()));
        services.AddSingleton(sp => new BrowserSessionAuthService(startup.Config));
        services.AddSingleton(sp =>
            new AdminSettingsService(
                startup.Config,
                AdminSettingsService.CreateSnapshot(startup.Config),
                AdminSettingsService.GetSettingsPath(startup.Config),
                sp.GetRequiredService<ILogger<AdminSettingsService>>()));
        services.AddSingleton(sp =>
            new PluginAdminSettingsService(
                startup.Config,
                sp.GetRequiredService<ILogger<PluginAdminSettingsService>>()));
        services.AddSingleton(sp =>
            new ApprovalAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ApprovalAuditStore>>()));
        services.AddSingleton(sp =>
            new RuntimeEventStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<RuntimeEventStore>>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.RuntimeMetrics>()));
        services.AddSingleton(sp =>
            new OperatorAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OperatorAuditStore>>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.RuntimeMetrics>()));
        services.AddSingleton(sp =>
            new ToolApprovalGrantStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ToolApprovalGrantStore>>()));
        services.AddSingleton(sp =>
            new WebhookDeliveryStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<WebhookDeliveryStore>>()));
        services.AddSingleton(sp =>
            new PluginHealthService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PluginHealthService>>()));
        services.AddSingleton(sp =>
            new ContractStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ContractStore>>()));
        services.AddSingleton(sp =>
            new ContractGovernanceService(
                startup,
                sp.GetRequiredService<ContractStore>(),
                sp.GetRequiredService<RuntimeEventStore>(),
                sp.GetRequiredService<OpenClaw.Core.Observability.ProviderUsageTracker>(),
                sp.GetRequiredService<ILogger<ContractGovernanceService>>()));

        return services;
    }
}
