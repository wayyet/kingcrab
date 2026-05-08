using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HandoffToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithoutContext_ReturnsContextError()
    {
        var tool = CreateTool(out _);

        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        Assert.Equal("handoff", tool.Name);
        Assert.DoesNotContain("clear", tool.ParameterSchema, StringComparison.Ordinal);
        Assert.Contains("workflow_id", tool.ParameterSchema, StringComparison.Ordinal);
        Assert.Equal("Error: handoff requires execution context.", result);
    }

    [Fact]
    public async Task ExecuteAsync_UpsertWithSameFingerprint_PreservesIdentityAndMergesPayload()
    {
        var tool = CreateTool(out var metadataStore);
        var context = CreateContext();

        var firstResult = await tool.ExecuteAsync(
            """
            {
              "action":"upsert",
              "title":"Generate return eligibility skill",
              "stage":"skill",
              "target_skill":"skill-generation",
              "intent":"Generate return eligibility skill",
              "payload":{"objective":"original","nested":{"alpha":1}},
              "fingerprint":"skill:return-eligibility"
            }
            """,
            context,
            CancellationToken.None);
        var firstItem = GetItem(firstResult);
        var handoffId = firstItem.GetProperty("handoff_id").GetString();
        var createdAt = firstItem.GetProperty("created_at").GetString();

        var secondResult = await tool.ExecuteAsync(
            """
            {
              "action":"upsert",
              "title":"Generate return eligibility skill v2",
              "payload":{"nested":{"beta":2},"scene_hint":"support"},
              "fingerprint":"skill:return-eligibility"
            }
            """,
            context,
            CancellationToken.None);
        var secondItem = GetItem(secondResult);
        var payload = secondItem.GetProperty("payload");

        Assert.Equal(handoffId, secondItem.GetProperty("handoff_id").GetString());
        Assert.Equal(createdAt, secondItem.GetProperty("created_at").GetString());
        Assert.Equal(2, secondItem.GetProperty("revision").GetInt32());
        Assert.Equal("Generate return eligibility skill v2", secondItem.GetProperty("title").GetString());
        Assert.Equal("original", payload.GetProperty("objective").GetString());
        Assert.Equal(1, payload.GetProperty("nested").GetProperty("alpha").GetInt32());
        Assert.Equal(2, payload.GetProperty("nested").GetProperty("beta").GetInt32());
        Assert.Equal("support", payload.GetProperty("scene_hint").GetString());

        var storedItem = Assert.Single(metadataStore.Get("sess_handoff").HandoffItems);
        Assert.Equal(handoffId, storedItem.HandoffId);
        Assert.Equal(2, storedItem.Revision);
    }

    [Fact]
    public async Task ExecuteAsync_PatchWithStaleRevision_ReturnsMismatchAndKeepsStoredItem()
    {
        var tool = CreateTool(out var metadataStore);
        var context = CreateContext();
        var handoffId = await CreateDraftAsync(tool, context);

        var result = await tool.ExecuteAsync(
            $$"""
            {
              "action":"patch",
              "handoff_id":"{{handoffId}}",
              "expected_revision":99,
              "patch":{"title":"Stale update"}
            }
            """,
            context,
            CancellationToken.None);

        Assert.Contains("expected_revision mismatch", result, StringComparison.Ordinal);
        var storedItem = Assert.Single(metadataStore.Get("sess_handoff").HandoffItems);
        Assert.Equal(1, storedItem.Revision);
        Assert.Equal("Draft skill", storedItem.Title);
    }

    [Fact]
    public async Task ExecuteAsync_TransitionRejectsInvalidStateMove()
    {
        var tool = CreateTool(out _);
        var context = CreateContext();
        var handoffId = await CreateDraftAsync(tool, context);

        var result = await tool.ExecuteAsync(
            $$"""
            {
              "action":"transition",
              "handoff_id":"{{handoffId}}",
              "status":"dispatched",
              "expected_revision":1
            }
            """,
            context,
            CancellationToken.None);

        Assert.Contains("cannot transition from 'drafting' to 'dispatched'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_TransitionWithExpectedRevision_PersistsDispatchState()
    {
        var tool = CreateTool(out var metadataStore);
        var context = CreateContext();
        var handoffId = await CreateDraftAsync(tool, context);

        var readyResult = await tool.ExecuteAsync(
            $$"""
            {
              "action":"transition",
              "handoff_id":"{{handoffId}}",
              "status":"ready_to_dispatch",
              "expected_revision":1
            }
            """,
            context,
            CancellationToken.None);
        var readyItem = GetItem(readyResult);
        Assert.Equal(2, readyItem.GetProperty("revision").GetInt32());

        var dispatchedResult = await tool.ExecuteAsync(
            $$"""
            {
              "action":"transition",
              "handoff_id":"{{handoffId}}",
              "status":"dispatched",
              "expected_revision":2,
              "dispatch_id":"dispatch_123"
            }
            """,
            context,
            CancellationToken.None);
        var dispatchedItem = GetItem(dispatchedResult);

        Assert.Equal("dispatched", dispatchedItem.GetProperty("status").GetString());
        Assert.Equal("dispatch_123", dispatchedItem.GetProperty("dispatch_id").GetString());
        Assert.Equal(3, dispatchedItem.GetProperty("revision").GetInt32());

        var storedItem = Assert.Single(metadataStore.Get("sess_handoff").HandoffItems);
        Assert.Equal("dispatched", storedItem.Status);
        Assert.Equal("dispatch_123", storedItem.DispatchId);
    }

    [Fact]
    public async Task ExecuteAsync_ListReturnsMachineReadableFilteredItems()
    {
        var tool = CreateTool(out _);
        var context = CreateContext();
        await tool.ExecuteAsync(
            """
            {
              "action":"upsert",
              "title":"Material extraction",
              "stage":"material",
              "target_skill":"ontology-extraction",
              "payload":{"objective":"extract rules"},
              "fingerprint":"material:rules"
            }
            """,
            context,
            CancellationToken.None);
        await CreateDraftAsync(tool, context);

        var listResult = await tool.ExecuteAsync("""{"action":"list","stage":"skill"}""", context, CancellationToken.None);

        using var document = JsonDocument.Parse(listResult);
        var root = document.RootElement;
        Assert.Equal("sess_handoff", root.GetProperty("session_id").GetString());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal("employment-coach", item.GetProperty("workflow_id").GetString());
        Assert.Equal("skill", item.GetProperty("stage").GetString());
        Assert.Equal("handoff_todo", item.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_CustomWorkflow_UsesConfiguredShapeAndScopesFingerprint()
    {
        var tool = CreateTool(
            out var metadataStore,
            CreateConfiguredHandoff(AdditionalResearchWorkflowConfig()));
        var context = CreateContext();

        await tool.ExecuteAsync(
            """
            {
              "action":"upsert",
              "title":"Employment skill",
              "stage":"skill",
              "target_skill":"skill-generation",
              "payload":{"objective":"generate"},
              "fingerprint":"shared:intent"
            }
            """,
            context,
            CancellationToken.None);

        var customResult = await tool.ExecuteAsync(
            """
            {
              "action":"upsert",
              "workflow_id":"research-workflow",
              "title":"Collect source notes",
              "kind":"research_handoff",
              "stage":"collect",
              "target_skill":"summarizer",
              "payload":{"topic":"pricing"},
              "fingerprint":"shared:intent"
            }
            """,
            context,
            CancellationToken.None);
        var customItem = GetItem(customResult);
        var customHandoffId = customItem.GetProperty("handoff_id").GetString() ?? throw new InvalidOperationException("handoff_id was missing.");

        Assert.Equal("research-workflow", customItem.GetProperty("workflow_id").GetString());
        Assert.StartsWith("c_", customHandoffId, StringComparison.Ordinal);
        Assert.Equal("research_handoff", customItem.GetProperty("kind").GetString());
        Assert.Equal("collect", customItem.GetProperty("stage").GetString());
        Assert.Equal("summarizer", customItem.GetProperty("target_skill").GetString());
        Assert.Equal("queued", customItem.GetProperty("status").GetString());
        Assert.Equal(2, metadataStore.Get("sess_handoff").HandoffItems.Count);

        var transitionResult = await tool.ExecuteAsync(
            $$"""
            {
              "action":"transition",
              "workflow_id":"research-workflow",
              "handoff_id":"{{customHandoffId}}",
              "status":"sent",
              "expected_revision":1
            }
            """,
            context,
            CancellationToken.None);
        var transitionedItem = GetItem(transitionResult);
        Assert.Equal("sent", transitionedItem.GetProperty("status").GetString());

        var listResult = await tool.ExecuteAsync("""{"action":"list","workflow_id":"research-workflow"}""", context, CancellationToken.None);
        using var listDocument = JsonDocument.Parse(listResult);
        var listedItem = Assert.Single(listDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("research-workflow", listedItem.GetProperty("workflow_id").GetString());

        var invalidResult = await tool.ExecuteAsync(
            """
            {
              "action":"upsert",
              "workflow_id":"research-workflow",
              "title":"Invalid research item",
              "kind":"research_handoff",
              "stage":"skill",
              "target_skill":"summarizer",
              "payload":{},
              "fingerprint":"research:invalid"
            }
            """,
            context,
            CancellationToken.None);
        Assert.Contains("stage 'skill' is not valid for workflow 'research-workflow'", invalidResult, StringComparison.Ordinal);
    }

    private static HandoffTool CreateTool(out SessionMetadataStore metadataStore)
        => CreateTool(out metadataStore, CreateConfiguredHandoff());

    private static HandoffTool CreateTool(out SessionMetadataStore metadataStore, HandoffConfig config)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        metadataStore = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        return new HandoffTool(metadataStore, config);
    }

    private static HandoffConfig CreateConfiguredHandoff(params IEnumerable<KeyValuePair<string, string?>>[] additionalSections)
    {
        var values = EmploymentCoachWorkflowConfig().ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var section in additionalSections)
        {
            foreach (var pair in section)
                values[pair.Key] = pair.Value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return GatewayBootstrapExtensions.LoadGatewayConfig(configuration).Handoff;
    }

    private static IEnumerable<KeyValuePair<string, string?>> EmploymentCoachWorkflowConfig()
    {
        yield return new("OpenClaw:Handoff:DefaultWorkflowId", "employment-coach");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Kind", "handoff_todo");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:DefaultStatus", "drafting");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:NewItemStatuses:0", "drafting");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:NewItemStatuses:1", "ready_to_dispatch");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Stages:0", "material");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Stages:1", "skill");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Stages:2", "external");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Stages:3", "cross_stage");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:TargetSkills:0", "ontology-extraction");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:TargetSkills:1", "skill-generation");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:TargetSkills:2", "external-config");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Statuses:0", "drafting");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Statuses:1", "ready_to_dispatch");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Statuses:2", "dispatched");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Statuses:3", "dirty");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Statuses:4", "confirmed");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Statuses:5", "needs_review");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Statuses:6", "dismissed");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:drafting:0", "ready_to_dispatch");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:drafting:1", "dismissed");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:ready_to_dispatch:0", "drafting");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:ready_to_dispatch:1", "dispatched");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:ready_to_dispatch:2", "dismissed");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:dispatched:0", "dirty");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:dispatched:1", "confirmed");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:dirty:0", "ready_to_dispatch");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:confirmed:0", "needs_review");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:confirmed:1", "dismissed");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:needs_review:0", "confirmed");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:Transitions:needs_review:1", "ready_to_dispatch");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:IdPrefixes:material", "m");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:IdPrefixes:skill", "s");
        yield return new("OpenClaw:Handoff:Workflows:employment-coach:IdPrefixes:external", "e");
    }

    private static IEnumerable<KeyValuePair<string, string?>> AdditionalResearchWorkflowConfig()
    {
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Kind", "research_handoff");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:DefaultStatus", "queued");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:NewItemStatuses:0", "queued");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Stages:0", "collect");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Stages:1", "write");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:TargetSkills:0", "summarizer");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Statuses:0", "queued");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Statuses:1", "sent");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Statuses:2", "done");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Statuses:3", "blocked");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Transitions:queued:0", "sent");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Transitions:queued:1", "blocked");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Transitions:sent:0", "done");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Transitions:sent:1", "blocked");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:Transitions:blocked:0", "queued");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:IdPrefixes:collect", "c");
        yield return new("OpenClaw:Handoff:Workflows:research-workflow:IdPrefixes:write", "w");
    }

    private static ToolExecutionContext CreateContext()
        => new()
        {
            Session = new Session
            {
                Id = "sess_handoff",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess_handoff",
                ChannelId = "websocket"
            }
        };

    private static async Task<string> CreateDraftAsync(HandoffTool tool, ToolExecutionContext context)
    {
        var result = await tool.ExecuteAsync(
            """
            {
              "action":"upsert",
              "title":"Draft skill",
              "stage":"skill",
              "target_skill":"skill-generation",
              "payload":{"skills":[{"skill_name":"Return eligibility"}]},
              "fingerprint":"skill:draft"
            }
            """,
            context,
            CancellationToken.None);
        var item = GetItem(result);
        return item.GetProperty("handoff_id").GetString() ?? throw new InvalidOperationException("handoff_id was missing.");
    }

    private static JsonElement GetItem(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("item").Clone();
    }
}
