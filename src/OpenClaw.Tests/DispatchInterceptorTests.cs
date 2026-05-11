using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Dispatch;
using Xunit;

namespace OpenClaw.Tests;

public sealed class DispatchInterceptorTests
{
    [Fact]
    public void StreamingFilter_StripsDispatchSplitAcrossChunks()
    {
        var filter = new ControlBlockExtractor.StreamingControlBlockFilter();
        var visible = new List<string>();

        visible.AddRange(filter.Append("我让"));
        visible.AddRange(filter.Append("<dis"));
        visible.AddRange(filter.Append("patch>{\"target\":\"ontology-extraction\",\"handoff_ids\":[\"m_1\"]}</dispatch>"));
        visible.AddRange(filter.Append(" 去处理了"));
        visible.AddRange(filter.Complete());

        Assert.Equal("我让 去处理了", string.Concat(visible));
        var block = Assert.Single(filter.Blocks);
        Assert.Equal(ControlBlockKind.Dispatch, block.Kind);
        Assert.True(DispatchSignalParser.TryParseDispatch(block.Json, out var signal, out var error), error);
        Assert.Equal("ontology-extraction", signal.Target);
        Assert.Equal(["m_1"], signal.HandoffIds);
    }

    [Fact]
    public void Coordinator_AcceptsReadyMaterialDispatch_TransitionsAndRecordsDispatch()
    {
        var store = CreateStore();
        var session = new Session { Id = "session_dispatch", ChannelId = "websocket", SenderId = "user" };
        store.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = [ReadyMaterialItem(session.Id, "m_ready")]
        });
        var coordinator = CreateCoordinator(store);

        var result = coordinator.AcceptDispatch(
            session,
            new DispatchSignal("ontology-extraction", ["m_ready"], "incremental", "用户表示先这些", null));

        Assert.True(result.Accepted, result.Error);
        var metadata = store.Get(session.Id);
        var item = Assert.Single(metadata.HandoffItems);
        Assert.Equal("dispatched", item.Status);
        Assert.Equal(result.DispatchId, item.DispatchId);
        Assert.Equal(2, item.Revision);
        var dispatch = Assert.Single(metadata.DispatchItems);
        Assert.Equal(result.DispatchId, dispatch.DispatchId);
        Assert.Equal("ontology-extraction", dispatch.Target);
        Assert.Equal(["m_ready"], dispatch.HandoffIds);
        Assert.Equal("accepted", dispatch.Status);
    }

    [Fact]
    public void Coordinator_RejectsDraftingMaterialDispatch()
    {
        var store = CreateStore();
        var session = new Session { Id = "session_draft", ChannelId = "websocket", SenderId = "user" };
        store.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = [ReadyMaterialItem(session.Id, "m_draft", status: "drafting")]
        });
        var coordinator = CreateCoordinator(store);

        var result = coordinator.AcceptDispatch(
            session,
            new DispatchSignal("ontology-extraction", ["m_draft"], "incremental", null, null));

        Assert.False(result.Accepted);
        var item = Assert.Single(store.Get(session.Id).HandoffItems);
        Assert.Equal("drafting", item.Status);
        Assert.Empty(store.Get(session.Id).DispatchItems);
    }

    [Fact]
    public async Task Runtime_RunAsync_StripsDispatchAndSanitizesHistory()
    {
        var store = CreateStore();
        var session = new Session { Id = "session_run", ChannelId = "websocket", SenderId = "user" };
        store.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = [ReadyMaterialItem(session.Id, "m_run")]
        });
        var raw = "我让本体整理去处理了。<dispatch>{\"target\":\"ontology-extraction\",\"handoff_ids\":[\"m_run\"],\"mode\":\"incremental\"}</dispatch>";
        var extraction = ControlBlockExtractor.Extract(raw);
        Assert.Equal("{\"target\":\"ontology-extraction\",\"handoff_ids\":[\"m_run\"],\"mode\":\"incremental\"}", Assert.Single(extraction.Blocks).Json);
        var runtime = new DispatchInterceptingAgentRuntime(
            new FakeRuntime(raw),
            CreateCoordinator(store));

        var visible = await runtime.RunAsync(session, "先这些", CancellationToken.None);

        Assert.Equal("我让本体整理去处理了。", visible);
        Assert.Equal("我让本体整理去处理了。", Assert.Single(session.History).Content);
        Assert.DoesNotContain("<dispatch>", session.History[0].Content);
        Assert.Equal("dispatched", Assert.Single(store.Get(session.Id).HandoffItems).Status);
    }

    [Fact]
    public void Coordinator_RejectsCallbackWhenTodoResultsDoNotCoverHandoffs()
    {
        var store = CreateStore();
        var session = new Session { Id = "session_callback_missing", ChannelId = "websocket", SenderId = "user" };
        store.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = [ReadyMaterialItem(session.Id, "m_callback_missing")]
        });
        var coordinator = CreateCoordinator(store);
        var dispatch = coordinator.AcceptDispatch(
            session,
            new DispatchSignal("ontology-extraction", ["m_callback_missing"], null, null, null));
        Assert.True(dispatch.Accepted, dispatch.Error);

        var callback = coordinator.AcceptCallback(
            session,
            new DispatchCallbackSignal(
                "ontology-extraction",
                ["m_callback_missing"],
                "已经抽取并生成切片。",
                [],
                "success",
                []));

        Assert.False(callback.Accepted);
        var metadata = store.Get(session.Id);
        Assert.Equal("dispatched", Assert.Single(metadata.HandoffItems).Status);
        Assert.Equal("accepted", Assert.Single(metadata.DispatchItems).Status);
    }

    [Fact]
    public void Coordinator_AcceptsCallbackRecordsSummaryWithoutConfirmingHandoff()
    {
        var store = CreateStore();
        var session = new Session { Id = "session_callback", ChannelId = "websocket", SenderId = "user" };
        store.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = [ReadyMaterialItem(session.Id, "m_callback")]
        });
        var coordinator = CreateCoordinator(store);
        var dispatch = coordinator.AcceptDispatch(
            session,
            new DispatchSignal("ontology-extraction", ["m_callback"], null, null, null));
        Assert.True(dispatch.Accepted, dispatch.Error);

        var callback = coordinator.AcceptCallback(
            session,
            new DispatchCallbackSignal(
                "ontology-extraction",
                ["m_callback"],
                "已生成本体切片 artifact://ontology/m_callback.json。",
                [new DispatchTodoResult("m_callback", "success", ["artifact://ontology/m_callback.json"], [])],
                "success",
                []));

        Assert.True(callback.Accepted, callback.Error);
        var metadata = store.Get(session.Id);
        var item = Assert.Single(metadata.HandoffItems);
        Assert.Equal("dispatched", item.Status);
        Assert.Equal("已生成本体切片 artifact://ontology/m_callback.json。", item.CallbackSummary);
        var dispatchItem = Assert.Single(metadata.DispatchItems);
        Assert.Equal("success", dispatchItem.Status);
        Assert.Equal("已生成本体切片 artifact://ontology/m_callback.json。", dispatchItem.CallbackSummary);
        Assert.NotNull(dispatchItem.CompletedAtUtc);
    }

        [Fact]
        public async Task Runtime_SystemEventCallbackProcessesInboundBlockBeforeInnerRun()
        {
                var store = CreateStore();
                var session = new Session { Id = "session_inbound_callback", ChannelId = "websocket", SenderId = "user" };
                store.Set(session.Id, new SessionMetadataUpdateRequest
                {
                        HandoffItems = [ReadyMaterialItem(session.Id, "m_inbound_callback")]
                });
                var coordinator = CreateCoordinator(store);
                var dispatch = coordinator.AcceptDispatch(
                        session,
                        new DispatchSignal("ontology-extraction", ["m_inbound_callback"], null, null, null));
                Assert.True(dispatch.Accepted, dispatch.Error);
                var fake = new FakeRuntime("我看到了下游结果，等用户确认。");
                var runtime = new DispatchInterceptingAgentRuntime(fake, coordinator);
                var callbackBlock = """
                <dispatch_callback>{
                    "source_dispatch_target": "ontology-extraction",
                    "handoff_ids": ["m_inbound_callback"],
                    "user_summary": "已生成入库流程本体切片。",
                    "todo_results": [
                        {
                            "handoff_id": "m_inbound_callback",
                            "status": "success",
                            "artifacts": ["artifact://ontology/inbound.json"],
                            "errors": []
                        }
                    ],
                    "status": "success",
                    "errors": []
                }</dispatch_callback>
                Dispatch dispatch_test returned from ontology-extraction.
                """;

                var visible = await runtime.RunAsync(session, callbackBlock, CancellationToken.None, isSystemEvent: true);

                Assert.Equal("我看到了下游结果，等用户确认。", visible);
                Assert.DoesNotContain("<dispatch_callback>", fake.LastUserMessage);
                Assert.Contains("Dispatch dispatch_test returned from ontology-extraction.", fake.LastUserMessage);
                Assert.Contains("A downstream dispatch callback was received", fake.LastUserMessage);
                Assert.Contains("已生成入库流程本体切片。", fake.LastUserMessage);
                var metadata = store.Get(session.Id);
                Assert.Equal("dispatched", Assert.Single(metadata.HandoffItems).Status);
                Assert.Equal("已生成入库流程本体切片。", Assert.Single(metadata.HandoffItems).CallbackSummary);
                Assert.Equal("success", Assert.Single(metadata.DispatchItems).Status);
        }

    [Fact]
    public async Task Runtime_RunStreamingAsync_StripsDispatchSplitAcrossChunksAndSanitizesHistory()
    {
        var store = CreateStore();
        var session = new Session { Id = "session_stream", ChannelId = "websocket", SenderId = "user" };
        store.Set(session.Id, new SessionMetadataUpdateRequest
        {
            HandoffItems = [ReadyMaterialItem(session.Id, "m_stream")]
        });
        var chunks = new[]
        {
            "我让本体整理",
            "去处理了。<dis",
            "patch>{\"target\":\"ontology-extraction\",\"handoff_ids\":[\"m_stream\"]}</dispatch>"
        };
        var runtime = new DispatchInterceptingAgentRuntime(
            new FakeRuntime(chunks),
            CreateCoordinator(store));
        var visibleEvents = new List<string>();

        await foreach (var evt in runtime.RunStreamingAsync(session, "先这些", CancellationToken.None))
        {
            if (evt.Type == AgentStreamEventType.TextDelta)
                visibleEvents.Add(evt.Content);
        }

        Assert.Equal("我让本体整理去处理了。", string.Concat(visibleEvents));
        Assert.Equal("我让本体整理去处理了。", Assert.Single(session.History).Content);
        Assert.Equal("dispatched", Assert.Single(store.Get(session.Id).HandoffItems).Status);
    }

    private static WorkflowDispatchCoordinator CreateCoordinator(SessionMetadataStore store)
        => new(store, NullLogger<WorkflowDispatchCoordinator>.Instance);

    private static SessionMetadataStore CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-dispatch-tests", Guid.NewGuid().ToString("N"));
        return new SessionMetadataStore(root, NullLogger<SessionMetadataStore>.Instance);
    }

    private static SessionHandoffItem ReadyMaterialItem(string sessionId, string handoffId, string status = "ready_to_dispatch")
        => new()
        {
            SessionId = sessionId,
            WorkflowId = "employment-coach",
            HandoffId = handoffId,
            Title = "资料：入库流程",
            Kind = "handoff_todo",
            Stage = "material",
            TargetSkill = "ontology-extraction",
            Intent = "抽取资产入库本体",
            Category = "流程 SOP",
            Payload = Json("""
            {
              "objective": "抽取资产入库流程节点、字段和边界约束",
              "source_files": ["入库流程.txt"],
              "scene_hint": "内勤",
              "mode": "incremental"
            }
            """),
            Source = "用户上传入库流程.txt",
            Acceptance = "ontology-extraction 回传切片覆盖入库流程",
            Status = status,
            Fingerprint = "material:first-batch",
            RelatedFiles = ["入库流程.txt"]
        };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeRuntime : IAgentRuntime
    {
        private readonly string? _response;
        private readonly string[]? _chunks;

        public FakeRuntime(string response) => _response = response;

        public FakeRuntime(string[] chunks) => _chunks = chunks;

        public string? LastUserMessage { get; private set; }

        public CircuitState CircuitBreakerState => CircuitState.Closed;

        public IReadOnlyList<string> LoadedSkillNames => [];

        public IReadOnlyList<AITool> LoadedTools => [];

        public event Action<IReadOnlyList<SkillDefinition>>? SkillsReloaded
        {
            add { }
            remove { }
        }

        public Task<string> RunAsync(
            Session session,
            string userMessage,
            CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            JsonElement? responseSchema = null,
            bool isSystemEvent = false)
        {
            LastUserMessage = userMessage;
            _ = ct;
            _ = approvalCallback;
            _ = responseSchema;
            _ = isSystemEvent;
            session.History.Add(new ChatTurn { Role = "assistant", Content = _response ?? "" });
            return Task.FromResult(_response ?? "");
        }

        public Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task ApplyMcpToolChangesAsync(
            IReadOnlyList<ITool> toAdd,
            IReadOnlyList<string> toRemove,
            CancellationToken ct = default)
        {
            _ = toAdd;
            _ = toRemove;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
            Session session,
            string userMessage,
            [EnumeratorCancellation] CancellationToken ct,
            ToolApprovalCallback? approvalCallback = null,
            bool isSystemEvent = false)
        {
            LastUserMessage = userMessage;
            _ = approvalCallback;
            _ = isSystemEvent;
            var raw = string.Concat(_chunks ?? []);
            foreach (var chunk in _chunks ?? [])
            {
                ct.ThrowIfCancellationRequested();
                yield return AgentStreamEvent.TextDelta(chunk);
                await Task.Yield();
            }

            session.History.Add(new ChatTurn { Role = "assistant", Content = raw });
            yield return AgentStreamEvent.Complete();
        }
    }

}
