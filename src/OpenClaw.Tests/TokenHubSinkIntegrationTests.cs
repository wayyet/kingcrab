using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.TokenHubSink;
using OpenClaw.TokenHubSink.Models;
using OpenClaw.TokenHubSink.Observability;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Covers the kingcrab-side TokenHub thin-client wiring: DI selection between the HTTP sink and the
/// no-op sink, and the per-call event mapping (incremental counts + session snapshot + AgentId fallback).
/// The JSON wire contract itself is guarded by TokenHub's own golden-field tests.
/// </summary>
public sealed class TokenHubSinkIntegrationTests
{
    // ── DI selection ──────────────────────────────────────────────────────

    [Fact]
    public void AddTokenHubSink_Http_RegistersHttpSinkAsSingletonAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenHubSink(new TokenUsageConfig { Sink = "http" });

        using var provider = services.BuildServiceProvider();

        var sink = provider.GetRequiredService<ITokenUsageEventSink>();
        Assert.IsType<HttpTokenUsageSink>(sink);

        // Same instance is exposed via the concrete type, the interface, and the hosted service.
        Assert.Same(sink, provider.GetRequiredService<HttpTokenUsageSink>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => ReferenceEquals(s, sink));

        // The bound config is registered so callers can read AgentId.
        Assert.Equal("http", provider.GetRequiredService<TokenUsageConfig>().Sink);
    }

    [Fact]
    public void AddTokenHubSink_Default_RegistersNoOpSinkAndNoHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTokenHubSink(new TokenUsageConfig()); // Sink defaults to "none"

        using var provider = services.BuildServiceProvider();

        Assert.Same(NullTokenUsageEventSink.Instance, provider.GetRequiredService<ITokenUsageEventSink>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is HttpTokenUsageSink);
    }

    [Theory]
    [InlineData("http", true)]
    [InlineData("HTTP", true)]
    [InlineData("Http", true)]
    [InlineData("none", false)]
    [InlineData("", false)]
    public void IsHttpSinkEnabled_IsCaseInsensitive(string sink, bool expected)
        => Assert.Equal(expected, new TokenUsageConfig { Sink = sink }.IsHttpSinkEnabled);

    // ── Event mapping (record -> wire event) ──────────────────────────────

    [Fact]
    public void Create_MapsIncrementalCounts_AndSessionSnapshot()
    {
        var record = NewRecord(senderId: "emp-1");

        var evt = TokenUsageEventMapper.Create(record, fixedAgentId: null);

        Assert.Equal("emp-1", evt.AgentId); // falls back to record.SenderId
        Assert.Equal("sess-1", evt.SessionId);
        Assert.Equal("websocket", evt.ChannelId);
        Assert.Equal("deepseek", evt.ProviderId);
        Assert.Equal("deepseek-v4", evt.ModelId);

        // Incremental (this call): safe to SUM downstream.
        Assert.Equal(100, evt.InputTokens);
        Assert.Equal(50, evt.OutputTokens);
        Assert.Equal(20, evt.CacheReadTokens);
        Assert.Equal(150, evt.TotalTokens); // input + output, cache write excluded

        // Snapshot (running session totals): reconciliation only.
        Assert.Equal(100, evt.SessionTotalInputTokens);
        Assert.Equal(50, evt.SessionTotalOutputTokens);
        Assert.Equal(20, evt.SessionTotalCacheReadTokens);
        Assert.Equal(150, evt.SessionTotalTokens);
    }

    [Fact]
    public void Create_PrefersConfiguredFixedAgentId_OverSenderId()
    {
        var evt = TokenUsageEventMapper.Create(NewRecord(senderId: "emp-1"), fixedAgentId: "fixed-emp");

        Assert.Equal("fixed-emp", evt.AgentId);
    }

    // record-only fields (CacheWriteTokens=5, IsEstimated, component estimate) are deliberately populated
    // here; the wire event type has no slot for them, so the mapping cannot leak them past the boundary.
    private static TurnTokenUsageRecord NewRecord(string senderId)
        => new()
        {
            SessionId = "sess-1",
            ChannelId = "websocket",
            ProviderId = "deepseek",
            ModelId = "deepseek-v4",
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadTokens = 20,
            CacheWriteTokens = 5,
            EstimatedInputTokensByComponent = new InputTokenComponentEstimate(),
            IsEstimated = true,
            SenderId = senderId,
            SessionTotalInputTokens = 100,
            SessionTotalOutputTokens = 50,
            SessionTotalCacheReadTokens = 20,
            SessionTotalTokens = 150
        };
}
