using OpenClaw.Agent.Observability;
using OpenClaw.Core.Models;
using OpenClaw.TokenHubSink.Observability;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// 验证收编后的链路③：TokenHubSinkTurnTokenUsageObserver 作为观察者链一员，把 record 映射成
/// SessionTokenUsageEvent 推给 sink；no-op sink 短路（零开销），且事件只含白名单 5 字段 + 4 个
/// session 快照，结构上无从携带 record-only 字段（cache_write / is_estimated / 分量估算）。
/// </summary>
public sealed class TokenHubSinkTurnTokenUsageObserverTests
{
    [Fact]
    public void RecordTurn_RealSink_PublishesEventWithWhitelistedFieldsAndSnapshot()
    {
        var sink = new RecordingTokenUsageEventSink();
        var observer = new TokenHubSinkTurnTokenUsageObserver(sink, fixedAgentId: null);

        observer.RecordTurn(NewRecord(senderId: "emp-1"));

        var evt = Assert.Single(sink.Events);
        Assert.Equal("emp-1", evt.AgentId); // SenderId fallback when no fixed id
        Assert.Equal("sess-1", evt.SessionId);
        Assert.Equal("websocket", evt.ChannelId);
        Assert.Equal("deepseek", evt.ProviderId);
        Assert.Equal("deepseek-v4", evt.ModelId);

        // 白名单增量 5 字段。
        Assert.Equal(100, evt.InputTokens);
        Assert.Equal(50, evt.OutputTokens);
        Assert.Equal(20, evt.CacheReadTokens);
        Assert.Equal(150, evt.TotalTokens);

        // 4 个 session 快照。
        Assert.Equal(100, evt.SessionTotalInputTokens);
        Assert.Equal(50, evt.SessionTotalOutputTokens);
        Assert.Equal(20, evt.SessionTotalCacheReadTokens);
        Assert.Equal(150, evt.SessionTotalTokens);

        // record-only 字段（CacheWriteTokens=5 / IsEstimated / 分量估算）在 wire 事件类型上没有对应槽位，
        // 编译期即保证无法泄漏——此处无字段可断言，即为白名单约束的天然满足。
    }

    [Fact]
    public void RecordTurn_FixedAgentId_OverridesSenderId()
    {
        var sink = new RecordingTokenUsageEventSink();
        var observer = new TokenHubSinkTurnTokenUsageObserver(sink, fixedAgentId: "fixed-emp");

        observer.RecordTurn(NewRecord(senderId: "emp-1"));

        Assert.Equal("fixed-emp", Assert.Single(sink.Events).AgentId);
    }

    [Fact]
    public void RecordTurn_NullSink_ShortCircuitsToNoOp()
    {
        var observer = new TokenHubSinkTurnTokenUsageObserver(NullTokenUsageEventSink.Instance, fixedAgentId: null);

        // no-op sink 命中短路分支：不映射、不发布、不抛异常（热路径零开销）。
        var ex = Record.Exception(() => observer.RecordTurn(NewRecord(senderId: "emp-1")));

        Assert.Null(ex);
    }

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

    private sealed class RecordingTokenUsageEventSink : ITokenUsageEventSink
    {
        public List<SessionTokenUsageEvent> Events { get; } = [];

        public void Publish(SessionTokenUsageEvent evt) => Events.Add(evt);
    }
}
