using OpenClaw.Agent;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AgentRuntimeFactorySelectorTests
{
    [Fact]
    public void Select_DefaultsToNative()
    {
        var factory = AgentRuntimeFactorySelector.Select([new StubFactory(RuntimeOrchestrator.Maf)], orchestratorId: null);
        Assert.Equal(RuntimeOrchestrator.Maf, factory.OrchestratorId);
    }

    private sealed class StubFactory(string orchestratorId) : IAgentRuntimeFactory
    {
        public string OrchestratorId => orchestratorId;

        public IAgentRuntime Create(AgentRuntimeFactoryContext context)
            => throw new NotSupportedException();
    }
}
