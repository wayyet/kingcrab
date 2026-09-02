using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// 锁定 RecordUsage 重构后的记账语义：每回合先累计 Session，再把 record 作为唯一数据源交给
/// 观察者链。record 携带 session 快照（SenderId / session_total_*），TokenHub 成员据此映射上报——
/// 旧的链路③ 旁路已收编进观察者链，不再由 ChatClient 直接 Publish。
/// observer 存在时走链路①（二选一，不再触发 ProviderUsageTracker.RecordTurn）；为 null 时回退到链路②。
/// </summary>
public class MafExecutionServiceChatClientTokenUsageTests
{
    private const long InputTokens = 100;
    private const long OutputTokens = 50;
    private const long CacheReadTokens = 20;

    // 核心回归：observer 收到的 record 增量字段正确，且 session 快照取自 Session 累计之后的最新值。
    [Fact]
    public async Task GetResponseAsync_WithObserver_PassesRecordWithFreshSessionSnapshot()
    {
        var session = new Session { Id = "sess-1", SenderId = "user-1", ChannelId = "chan-1" };
        var observer = new RecordingTurnTokenUsageObserver();
        var providerUsage = new ProviderUsageTracker();

        var client = CreateClient(providerUsage);
        var context = CreateContext(session, observer);

        using (MafExecutionContextScope.Push(context))
        {
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                new ChatOptions(),
                CancellationToken.None);
        }

        var record = Assert.Single(observer.Records);

        // 增量字段（本回合，safe to SUM）。
        Assert.Equal("prov-x", record.ProviderId);
        Assert.Equal("model-y", record.ModelId);
        Assert.Equal(InputTokens, record.InputTokens);
        Assert.Equal(OutputTokens, record.OutputTokens);
        Assert.Equal(CacheReadTokens, record.CacheReadTokens);

        // session 快照新鲜：record 在 AddTokenUsage/AddCacheUsage 之后构造，SenderId 与 session_total_* 为最新值。
        Assert.Equal(session.SenderId, record.SenderId);
        Assert.Equal(InputTokens, record.SessionTotalInputTokens);
        Assert.Equal(OutputTokens, record.SessionTotalOutputTokens);
        Assert.Equal(CacheReadTokens, record.SessionTotalCacheReadTokens);
        Assert.Equal(InputTokens + OutputTokens, record.SessionTotalTokens);

        // 二选一记账：observer 存在时不再走链路② 的 ProviderUsageTracker.RecordTurn。
        Assert.Empty(providerUsage.RecentTurns(session.Id));
    }

    // 对照用例：observer 为 null 时，回退到链路② 的 ProviderUsageTracker.RecordTurn。
    [Fact]
    public async Task GetResponseAsync_WithoutObserver_RecordsProviderUsageTurn()
    {
        var session = new Session { Id = "sess-2", SenderId = "user-2", ChannelId = "chan-2" };
        var providerUsage = new ProviderUsageTracker();

        var client = CreateClient(providerUsage);
        var context = CreateContext(session, observer: null);

        using (MafExecutionContextScope.Push(context))
        {
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                new ChatOptions(),
                CancellationToken.None);
        }

        var turn = Assert.Single(providerUsage.RecentTurns(session.Id));
        Assert.Equal(InputTokens, turn.InputTokens);
        Assert.Equal(OutputTokens, turn.OutputTokens);
        Assert.Equal(CacheReadTokens, turn.CacheReadTokens);
    }

    private static MafExecutionServiceChatClient CreateClient(ProviderUsageTracker providerUsage)
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "hi there")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = InputTokens,
                OutputTokenCount = OutputTokens,
                CachedInputTokenCount = CacheReadTokens
            }
        };
        var llm = new FakeLlmExecutionService(new LlmExecutionResult
        {
            ProviderId = "prov-x",
            ModelId = "model-y",
            Response = response
        });
        return new MafExecutionServiceChatClient(
            llm,
            new RuntimeMetrics(),
            providerUsage,
            new MafTelemetryAdapter());
    }

    private static MafExecutionContext CreateContext(
        Session session,
        ITurnTokenUsageObserver? observer)
        => new()
        {
            Session = session,
            TurnContext = new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId },
            SystemPromptLength = 0,
            SkillPromptLength = 0,
            SessionTokenBudget = 0,
            ToolInvocations = [],
            TurnTokenUsageObserver = observer
        };

    private sealed class FakeLlmExecutionService(LlmExecutionResult result) : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
            => Task.FromResult(result);

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class RecordingTurnTokenUsageObserver : ITurnTokenUsageObserver
    {
        public List<TurnTokenUsageRecord> Records { get; } = [];

        public void RecordTurn(TurnTokenUsageRecord record) => Records.Add(record);
    }
}
