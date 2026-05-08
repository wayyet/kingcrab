using Microsoft.Extensions.Configuration;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayBootstrapExtensionsTests
{
    [Fact]
    public void LoadGatewayConfig_ConfiguredToolRootsReplaceWildcardDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:BindAddress"] = "0.0.0.0",
                ["OpenClaw:Tooling:AllowShell"] = "false",
                ["OpenClaw:Tooling:AllowedReadRoots:0"] = "/app/workspace",
                ["OpenClaw:Tooling:AllowedWriteRoots:0"] = "/app/workspace",
                ["OpenClaw:Plugins:Enabled"] = "false"
            })
            .Build();

        var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

        Assert.Equal(["/app/workspace"], config.Tooling.AllowedReadRoots);
        Assert.Equal(["/app/workspace"], config.Tooling.AllowedWriteRoots);
        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void LoadGatewayConfig_BindsHandoffWorkflows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:Handoff:DefaultWorkflowId"] = "review-workflow",
                ["OpenClaw:Handoff:Workflows:review-workflow:Kind"] = "review_handoff",
                ["OpenClaw:Handoff:Workflows:review-workflow:DefaultStatus"] = "queued",
                ["OpenClaw:Handoff:Workflows:review-workflow:NewItemStatuses:0"] = "queued",
                ["OpenClaw:Handoff:Workflows:review-workflow:Stages:0"] = "triage",
                ["OpenClaw:Handoff:Workflows:review-workflow:TargetSkills:0"] = "reviewer",
                ["OpenClaw:Handoff:Workflows:review-workflow:Statuses:0"] = "queued",
                ["OpenClaw:Handoff:Workflows:review-workflow:Statuses:1"] = "done",
                ["OpenClaw:Handoff:Workflows:review-workflow:Transitions:queued:0"] = "done",
                ["OpenClaw:Handoff:Workflows:review-workflow:IdPrefixes:triage"] = "r"
            })
            .Build();

        var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

        Assert.Equal("review-workflow", config.Handoff.DefaultWorkflowId);
        var workflow = Assert.Single(config.Handoff.Workflows);
        Assert.Equal("review-workflow", workflow.Key);
        Assert.Equal("review_handoff", workflow.Value.Kind);
        Assert.Equal(["queued"], workflow.Value.NewItemStatuses);
        Assert.Equal(["triage"], workflow.Value.Stages);
        Assert.Equal(["reviewer"], workflow.Value.TargetSkills);
        Assert.Equal(["queued", "done"], workflow.Value.Statuses);
        Assert.Equal(["done"], workflow.Value.Transitions["queued"]);
        Assert.Equal("r", workflow.Value.IdPrefixes["triage"]);
    }
}
