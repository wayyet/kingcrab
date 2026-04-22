using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public class MafAgentRuntimeTests
{
    private readonly IChatClient _chatClient;
    private readonly IMemoryStore _memory;
    private readonly List<ITool> _tools;
    private readonly MafAgentRuntime _agent;
    private readonly LlmProviderConfig _config;

    public MafAgentRuntimeTests()
    {
        _chatClient = Substitute.For<IChatClient>();
        _memory = Substitute.For<IMemoryStore>();
        _tools = new List<ITool>();
        _config = new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "Hello from AI") })));

        _agent = MafTestRuntimeFactory.CreateRuntime(
            _chatClient,
            _memory,
            _tools,
            _config,
            maxHistoryTurns: 5);
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_SingleTurn_ReturnsResponse()
    {
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };
        var result = await _agent.RunAsync(session, "Hello", CancellationToken.None);

        Assert.Equal("Hello from AI", result);
        Assert.Contains(session.History, t => t.Role == "user" && t.Content == "Hello");
        Assert.Contains(session.History, t => t.Role == "assistant" && t.Content == "Hello from AI");
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_TrimsHistory()
    {
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };
        for (int i = 0; i < 10; i++)
            session.History.Add(new ChatTurn { Role = "user", Content = $"msg {i}" });

        await _agent.RunAsync(session, "New message", CancellationToken.None);

        Assert.True(session.History.Count <= 6, $"Expected history <= 6 but was {session.History.Count}");
    }

    [Fact]
    public async Task MafAgentRuntime_ReloadSkillsAsync_UpdatesLoadedSkillNames()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"openclaw-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(workspaceDir, "skills", "reloadable");
        Directory.CreateDirectory(skillDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: reloadable-skill
                description: Hot reloaded during tests
                ---
                Use this skill after reload.
                """, TestContext.Current.CancellationToken);

            var agent = MafTestRuntimeFactory.CreateRuntime(
                _chatClient,
                _memory,
                _tools,
                _config,
                maxHistoryTurns: 5,
                skillsConfig: new SkillsConfig
                {
                    Load = new SkillLoadConfig
                    {
                        IncludeBundled = false,
                        IncludeManaged = false,
                        IncludeWorkspace = true
                    }
                },
                skillWorkspacePath: workspaceDir);

            Assert.Empty(agent.LoadedSkillNames);

            var loaded = await agent.ReloadSkillsAsync(TestContext.Current.CancellationToken);

            Assert.Single(loaded);
            Assert.Contains("reloadable-skill", loaded);
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public void GetSystemPrompt_WithProjectionRoute_AppendsProjectionPatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-runtime-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var relativePath = Path.Combine("task-execution", "task-execution.prompt-constraint.projection.json");
            var projectionDir = Path.Combine(tempDir, "task-execution");
            Directory.CreateDirectory(projectionDir);

            File.WriteAllText(
                Path.Combine(tempDir, relativePath),
                """
                {
                  "mapping_policy": {
                    "unresolved_item_policy": "block_or_escalate"
                  },
                  "prompt_projection": {
                    "allowed_terms": ["skills_config"],
                    "forbidden_assumptions": ["Do not invert source precedence."],
                    "required_clarifications": ["Clarify the managed path first."],
                    "reasoning_paths": ["skills_config -> source_precedence"],
                    "source_digest": ["Primary source: SkillLoader.cs"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var runtime = MafTestRuntimeFactory.CreateRuntime(
                _chatClient,
                _memory,
                _tools,
                _config,
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "software-developer",
                        Description = "Developer skill",
                        Instructions = "Base skill instructions.",
                        Location = "/skills/software-developer",
                        ProjectionContracts = [CreateProjectionContracts(tempDir, relativePath.Replace('\\', '/'))]
                    }
                ]);

            var prompt = MafTestRuntimeFactory.GetSystemPrompt(runtime, new Session { Id = "s1", SenderId = "u1", ChannelId = "c1" }, "Please add prompt policy and review guidance.");

            Assert.Contains("## Skill: software-developer", prompt);
            Assert.Contains("[Projection Route]", prompt);
            Assert.Contains("Selected topic: task-execution", prompt);
            Assert.Contains("Do not invert source precedence.", prompt);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetSystemPrompt_WithBlockedProjection_HidesSkillAndAddsBlockedRoute()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-runtime-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var relativePath = Path.Combine("task-execution", "task-execution.prompt-constraint.projection.json");
            var projectionDir = Path.Combine(tempDir, "task-execution");
            Directory.CreateDirectory(projectionDir);

            File.WriteAllText(
                Path.Combine(tempDir, relativePath),
                """
                {
                  "mapping_policy": {
                    "unresolved_item_policy": "block_or_escalate"
                  },
                  "prompt_projection": {
                    "allowed_terms": ["skills_config"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": ["Needs clarification."]
                }
                """);

            var runtime = MafTestRuntimeFactory.CreateRuntime(
                _chatClient,
                _memory,
                _tools,
                _config,
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "software-developer",
                        Description = "Developer skill",
                        Instructions = "Base skill instructions.",
                        Location = "/skills/software-developer",
                        ProjectionContracts = [CreateProjectionContracts(tempDir, relativePath.Replace('\\', '/'))]
                    }
                ]);

            var prompt = MafTestRuntimeFactory.GetSystemPrompt(runtime, new Session { Id = "s1", SenderId = "u1", ChannelId = "c1" }, "task execution");

            Assert.Contains("[Blocked Skill Routes]", prompt);
            Assert.Contains("software-developer: Projection 'task-execution/prompt-constraint' has blocking open questions.", prompt);
            Assert.DoesNotContain("## Skill: software-developer", prompt);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetSystemPrompt_WithMultipleProjectionContracts_UsesHigherPriorityProducerOnTie()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-runtime-projection-{Guid.NewGuid():N}");
        var producerOneRoot = Path.Combine(tempDir, "producer-one");
        var producerTwoRoot = Path.Combine(tempDir, "producer-two");
        Directory.CreateDirectory(Path.Combine(producerOneRoot, "task-execution"));
        Directory.CreateDirectory(Path.Combine(producerTwoRoot, "task-execution"));

        try
        {
            var relativePath = Path.Combine("task-execution", "task-execution.prompt-constraint.projection.json").Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(producerOneRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["generic_review"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            File.WriteAllText(
                Path.Combine(producerTwoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["producer_two_term"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var runtime = MafTestRuntimeFactory.CreateRuntime(
                _chatClient,
                _memory,
                _tools,
                _config,
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "software-developer",
                        Description = "Developer skill",
                        Instructions = "Base skill instructions.",
                        Location = "/skills/software-developer",
                        ProjectionContracts =
                        [
                            CreateProjectionContracts(producerOneRoot, relativePath, explicitArtifactSignals: ["prompt policy"], allowedTermsSignals: ["review guidance", "prompt policy"], producerPriority: 10),
                            CreateProjectionContracts(producerTwoRoot, relativePath, explicitArtifactSignals: ["prompt policy"], allowedTermsSignals: ["review guidance", "prompt policy"], producerPriority: 50)
                        ]
                    }
                ]);

            var prompt = MafTestRuntimeFactory.GetSystemPrompt(runtime, new Session { Id = "s1", SenderId = "u1", ChannelId = "c1" }, "Please add review guidance and prompt policy.");

            Assert.Contains("producer_two_term", prompt);
            Assert.DoesNotContain("generic_review", prompt);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static SkillProjectionContractSet CreateProjectionContracts(
        string rootPath,
        string relativePath,
        string[]? explicitArtifactSignals = null,
        string[]? allowedTermsSignals = null,
        int producerPriority = 0)
        => new()
        {
            ProducerPriority = producerPriority,
            RootPath = rootPath,
            Index = new ProjectionContractIndex
            {
                ProducerPriority = producerPriority,
                DefaultSelectionPolicy = new ProjectionSelectionPolicy
                {
                    PreferReadyOnly = true,
                    BlockOnOpenQuestions = true
                },
                TopicScoring = new ProjectionTopicScoring
                {
                    ClarifyWhenScoreGapBelow = 2,
                    ScoreDimensions =
                    [
                        new ProjectionScoreDimension { Dimension = "explicit_artifact_bonus", Score = 4 },
                        new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                        new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                        new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 },
                        new ProjectionScoreDimension { Dimension = "cross_topic_conflict_penalty", Score = -2 }
                    ],
                    Topics =
                    [
                        new ProjectionTopicSignals
                        {
                            DomainSlug = "task-execution",
                            PrimaryIntentSignals = allowedTermsSignals ?? ["review guidance", "prompt policy", "task execution"],
                            SupportingSignals = ["guidance", "review"],
                            ExplicitArtifactSignals = explicitArtifactSignals ?? ["prompt policy"],
                            DemoteWhenCompetingTopicSignals = []
                        }
                    ]
                },
                TargetViewScoring = new ProjectionTargetViewScoring
                {
                    ClarifyWhenScoreGapBelow = 2,
                    ScoreDimensions =
                    [
                        new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                        new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                        new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                        new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                        new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 }
                    ],
                    Views =
                    [
                        new ProjectionViewSignals
                        {
                            TargetView = "prompt-constraint",
                            ExplicitOutputSignals = explicitArtifactSignals ?? ["prompt policy"],
                            StrongSignals = ["review guidance", "allowed terms"],
                            SupportingSignals = ["guidance", "task execution"],
                            DemoteWhenCompetingViewSignals = []
                        }
                    ]
                },
                Topics =
                [
                    new ProjectionTopicRecord
                    {
                        DomainSlug = "task-execution",
                        DefaultTargetView = "prompt-constraint",
                        Views =
                        [
                            new ProjectionViewRecord
                            {
                                TargetView = "prompt-constraint",
                                Status = "READY",
                                Path = relativePath
                            }
                        ]
                    }
                ]
            }
        };
}
