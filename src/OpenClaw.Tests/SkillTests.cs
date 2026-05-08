using OpenClaw.Core.Skills;
using Xunit;
using System.Text.Json;

namespace OpenClaw.Tests;

public class SkillLoaderTests
{
    [Fact]
    public void ParseSkillContent_ValidFrontmatter_ReturnsSkill()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill for unit testing
            ---
            Use the test tool to run tests.
            Always validate output before returning.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/test-skill", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("test-skill", skill!.Name);
        Assert.Equal("A test skill for unit testing", skill.Description);
        Assert.Contains("test tool", skill.Instructions);
        Assert.Equal("/skills/test-skill", skill.Location);
        Assert.Equal(SkillSource.Workspace, skill.Source);
    }

    [Fact]
    public void ParseSkillContent_WithArtifactContract_LoadsContract()
    {
        var skillDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(skillDir, "contracts"));
        File.WriteAllText(Path.Combine(skillDir, "contracts", "artifacts.json"), """
            {
              "schemaVersion": 1,
              "stages": [
                {
                  "name": "analysis",
                  "label": "Analysis",
                  "artifacts": [
                    { "type": "query_plan", "label": "Query plan", "display": "tree", "terminal": true }
                  ]
                }
              ]
            }
            """);

        try
        {
            var content = """
                ---
                name: sql-expert
                description: SQL expert
                ---
                Analyze SQL queries.
                """;

            var skill = SkillLoader.ParseSkillContent(content, skillDir, SkillSource.Workspace);

            Assert.NotNull(skill);
            Assert.NotNull(skill!.ArtifactContract);
            Assert.Equal(1, skill.ArtifactContract!.SchemaVersion);
            var stage = Assert.Single(skill.ArtifactContract.Stages);
            Assert.Equal("analysis", stage.Name);
            var artifact = Assert.Single(stage.Artifacts);
            Assert.Equal("query_plan", artifact.Type);
            Assert.Equal("tree", artifact.Display);
            Assert.True(artifact.Terminal);
        }
        finally
        {
            Directory.Delete(skillDir, recursive: true);
        }
    }

    [Fact]
    public void ParseSkillContent_MissingFrontmatter_ReturnsNull()
    {
        var content = "Just some markdown without frontmatter.";

        var skill = SkillLoader.ParseSkillContent(content, "/skills/bad", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MissingName_ReturnsNull()
    {
        var content = """
            ---
            description: No name here
            ---
            Instructions body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/noname", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_WithMetadata_ParsesRequirements()
    {
        var content = """
            ---
            name: gemini-skill
            description: Use Gemini for coding
            metadata: {"openclaw": {"requires": {"bins": ["gemini"], "env": ["GEMINI_API_KEY"]}, "primaryEnv": "GEMINI_API_KEY", "emoji": "♊️"}}
            ---
            Use the gemini CLI tool.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/gemini", SkillSource.Managed);

        Assert.NotNull(skill);
        Assert.Equal("gemini-skill", skill!.Name);
        Assert.Single(skill.Metadata.RequireBins);
        Assert.Equal("gemini", skill.Metadata.RequireBins[0]);
        Assert.Single(skill.Metadata.RequireEnv);
        Assert.Equal("GEMINI_API_KEY", skill.Metadata.RequireEnv[0]);
        Assert.Equal("GEMINI_API_KEY", skill.Metadata.PrimaryEnv);
        Assert.Equal("♊️", skill.Metadata.Emoji);
    }

    [Fact]
    public void ParseSkillContent_UserInvocableFalse_SetsProperly()
    {
        var content = """
            ---
            name: internal-skill
            description: Not user-invocable
            user-invocable: false
            ---
            Internal instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/internal", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.False(skill!.UserInvocable);
    }

    [Fact]
    public void ParseSkillContent_DisableModelInvocation_SetsProperly()
    {
        var content = """
            ---
            name: slash-only
            description: Slash command only
            disable-model-invocation: true
            ---
            Only via slash command.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/slash", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.True(skill!.DisableModelInvocation);
    }

    [Fact]
    public void ParseSkillContent_CommandDispatch_SetsProperly()
    {
        var content = """
            ---
            name: summarize
            description: Summarize content
            command-dispatch: tool
            command-tool: summarize_tool
            command-arg-mode: raw
            ---
            Summarization instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/summarize", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("tool", skill!.CommandDispatch);
        Assert.Equal("summarize_tool", skill.CommandTool);
        Assert.Equal("raw", skill.CommandArgMode);
    }

    [Fact]
    public void ParseSkillContent_ReplacesBaseDir()
    {
        var content = """
            ---
            name: my-skill
            description: Uses baseDir
            ---
            Run the script at {baseDir}/run.sh
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/home/user/skills/my-skill", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Contains("/home/user/skills/my-skill/run.sh", skill!.Instructions);
        Assert.DoesNotContain("{baseDir}", skill.Instructions);
    }

    [Fact]
    public void ParseSkillContent_WithOsGate_ParsesOsList()
    {
        var content = """
            ---
            name: mac-only
            description: macOS only skill
            metadata: {"openclaw": {"os": ["darwin"]}}
            ---
            macOS instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/mac", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.Single(skill!.Metadata.Os);
        Assert.Equal("darwin", skill.Metadata.Os[0]);
    }

    [Fact]
    public void ParseSkillContent_AlwaysTrue_SetsFlag()
    {
        var content = """
            ---
            name: core-skill
            description: Always loaded
            metadata: {"openclaw": {"always": true}}
            ---
            Core instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/core", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.True(skill!.Metadata.Always);
    }

        [Fact]
        public void ParseSkillContent_WithProjectionContractIndex_BindsProjectionContracts()
        {
                var skillDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-projection-{Guid.NewGuid():N}");
                var contractRoot = Path.Combine(skillDir, "contracts", "projections", "ontology_extraction");
                Directory.CreateDirectory(contractRoot);

                try
                {
                        File.WriteAllText(Path.Combine(contractRoot, "contract-index.json"),
                            $$"""
                                {
                                    "producer_skill": "ontology_extraction",
                                    "producer_priority": 42,
                                    "default_selection_policy": {
                                        "prefer_ready_only": true,
                                        "block_on_open_questions": true
                                    },
                                    "topic_scoring": {
                                        "clarify_when_score_gap_below": 2,
                                        "score_dimensions": [
                                            { "dimension": "primary_intent_match", "score": 5 }
                                        ],
                                        "topics": [
                                            {
                                                "domain_slug": "task-execution",
                                                "primary_intent_signals": ["task execution"],
                                                "supporting_signals": ["review"],
                                                "explicit_artifact_signals": ["prompt policy"],
                                                "demote_when_competing_topic_signals": []
                                            }
                                        ]
                                    },
                                    "target_view_scoring": {
                                        "clarify_when_score_gap_below": 2,
                                        "score_dimensions": [
                                            { "dimension": "explicit_output_match", "score": 5 }
                                        ],
                                        "views": [
                                            {
                                                "target_view": "{{SkillProjectionViewKeys.PromptConstraint}}",
                                                "explicit_output_signals": ["prompt policy"],
                                                "strong_signals": ["review guidance"],
                                                "supporting_signals": ["review"],
                                                "demote_when_competing_view_signals": []
                                            }
                                        ],
                                        "within_topic_overrides": []
                                    },
                                    "topics": [
                                        {
                                            "domain_slug": "task-execution",
                                            "default_target_view": "{{SkillProjectionViewKeys.PromptConstraint}}",
                                            "views": [
                                                {
                                                    "target_view": "{{SkillProjectionViewKeys.PromptConstraint}}",
                                                    "status": "READY",
                                                    "path": "task-execution/task-execution.{{SkillProjectionViewKeys.PromptConstraint}}.projection.json"
                                                }
                                            ]
                                        }
                                    ]
                                }
                                """);

                        var content = """
                                ---
                                name: projected-skill
                                description: Uses projection contracts
                                ---
                                Instructions body.
                                """;

                        var skill = SkillLoader.ParseSkillContent(content, skillDir, SkillSource.Workspace, new TestLogger());

                        Assert.NotNull(skill);
                        Assert.Single(skill!.ProjectionContracts);
                        Assert.NotNull(skill.ProjectionDiscovery);
                        Assert.Equal(contractRoot, skill.ProjectionContracts[0].RootPath);
                        Assert.Equal("ontology_extraction", skill.ProjectionContracts[0].ProducerName);
                        Assert.Equal(42, skill.ProjectionContracts[0].ProducerPriority);
                        Assert.Equal("bound", skill.ProjectionDiscovery!.Status);
                        Assert.Equal(1, skill.ProjectionDiscovery.BoundCount);
                        Assert.True(skill.ProjectionContracts[0].Index.DefaultSelectionPolicy.PreferReadyOnly);
                        Assert.True(skill.ProjectionContracts[0].Index.DefaultSelectionPolicy.BlockOnOpenQuestions);
                        Assert.Single(skill.ProjectionContracts[0].Index.Topics);
                        Assert.Equal("task-execution", skill.ProjectionContracts[0].Index.Topics[0].DomainSlug);
                        Assert.Single(skill.ProjectionContracts[0].Index.Topics[0].Views);
                        Assert.Equal("prompt-constraint", skill.ProjectionContracts[0].Index.Topics[0].Views[0].TargetView);
                }
                finally
                {
                        Directory.Delete(skillDir, true);
                }
        }

    [Fact]
    public void ParseMetadata_Null_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata(null);
        Assert.False(meta.Always);
        Assert.Empty(meta.Os);
        Assert.Empty(meta.RequireBins);
        Assert.Empty(meta.RequireEnv);
    }

    [Fact]
    public void ParseMetadata_InvalidJson_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata("not json at all");
        Assert.False(meta.Always);
    }

    [Fact]
    public void ParseMetadata_NoOpenclawKey_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata("""{"other": true}""");
        Assert.False(meta.Always);
    }

    [Fact]
    public void LoadAll_Disabled_ReturnsEmpty()
    {
        var config = new SkillsConfig { Enabled = false };
        var logger = new TestLogger();

        var skills = SkillLoader.LoadAll(config, null, logger);

        Assert.Empty(skills);
    }

    [Fact]
    public void LoadAll_NoDirectories_ReturnsEmpty()
    {
        var config = new SkillsConfig
        {
            Enabled = true,
            Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
        };
        var logger = new TestLogger();

        var skills = SkillLoader.LoadAll(config, "/nonexistent/workspace", logger);

        Assert.Empty(skills);
    }

    [Fact]
    public void LoadAll_WithSkillFiles_LoadsAndFilters()
    {
        // Create temp skill structure: <workspace>/skills/<skill-name>/SKILL.md
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempDir, "skills", "test-skill");
        Directory.CreateDirectory(skillDir);

        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: test-skill
                description: A test skill
                ---
                Test instructions here.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
            };
            var logger = new TestLogger();

            // Use tempDir as workspace skills
            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Single(skills);
            Assert.Equal("test-skill", skills[0].Name);
            Assert.Equal(SkillSource.Workspace, skills[0].Source);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_DisabledByEntry_Excluded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempDir, "skills", "disabled-skill");
        Directory.CreateDirectory(skillDir);

        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: disabled-skill
                description: Should be filtered out
                ---
                Instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false },
                Entries = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["disabled-skill"] = new SkillEntryConfig { Enabled = false }
                }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Empty(skills);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_WorkspaceOverridesManaged_HigherPrecedenceWins()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var extraDir = Path.Combine(tempDir, "extra");
        var wsDir = Path.Combine(tempDir, "workspace");

        var extraSkillDir = Path.Combine(extraDir, "my-skill");
        var wsSkillDir = Path.Combine(wsDir, "skills", "my-skill");
        Directory.CreateDirectory(extraSkillDir);
        Directory.CreateDirectory(wsSkillDir);

        try
        {
            File.WriteAllText(Path.Combine(extraSkillDir, "SKILL.md"), """
                ---
                name: my-skill
                description: Extra version
                ---
                Extra instructions.
                """);

            File.WriteAllText(Path.Combine(wsSkillDir, "SKILL.md"), """
                ---
                name: my-skill
                description: Workspace version
                ---
                Workspace instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { ExtraDirs = [extraDir], IncludeBundled = false, IncludeManaged = false }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, wsDir, logger);

            Assert.Single(skills);
            Assert.Equal("my-skill", skills[0].Name);
            Assert.Equal("Workspace version", skills[0].Description);
            Assert.Equal(SkillSource.Workspace, skills[0].Source);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_ManagedSkill_IsDiscoveredFromDotOpenclaw()
    {
        var managedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw",
            "skills",
            $"managed-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(managedRoot);

        try
        {
            File.WriteAllText(Path.Combine(managedRoot, "SKILL.md"), """
                ---
                name: managed-skill
                description: Managed skill
                ---
                Managed instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeWorkspace = false }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, null, logger);

            var skill = Assert.Single(skills, s => s.Name == "managed-skill");
            Assert.Equal(SkillSource.Managed, skill.Source);
        }
        finally
        {
            Directory.Delete(managedRoot, true);
        }
    }

    [Fact]
    public void ParseSkillContent_WithMultipleProjectionIndexes_BindsAllDiscoveredContracts()
    {
        var skillDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-projection-{Guid.NewGuid():N}");
        var producerOneRoot = Path.Combine(skillDir, "contracts", "projections", "producer-one");
        var producerTwoRoot = Path.Combine(skillDir, "contracts", "projections", "producer-two");
        Directory.CreateDirectory(producerOneRoot);
        Directory.CreateDirectory(producerTwoRoot);

        try
        {
            File.WriteAllText(Path.Combine(producerOneRoot, "contract-index.json"), "{ \"topics\": [] }");
            File.WriteAllText(Path.Combine(producerTwoRoot, "contract-index.json"), "{ \"topics\": [] }");

            var content = """
                ---
                name: projected-skill
                description: Uses projection contracts
                ---
                Instructions body.
                """;

            var skill = SkillLoader.ParseSkillContent(content, skillDir, SkillSource.Workspace, new TestLogger());

            Assert.NotNull(skill);
            Assert.Equal(2, skill!.ProjectionContracts.Count);
            Assert.NotNull(skill.ProjectionDiscovery);
            Assert.Equal("bound", skill.ProjectionDiscovery!.Status);
            Assert.Equal(2, skill.ProjectionDiscovery.IndexCount);
            Assert.Equal(2, skill.ProjectionDiscovery.BoundCount);
            Assert.Equal(2, skill.ProjectionDiscovery.IndexPaths.Count);
        }
        finally
        {
            Directory.Delete(skillDir, true);
        }
    }
}

public class SkillPromptBuilderTests
{
    [Fact]
    public void Build_NoSkills_ReturnsEmpty()
    {
        var result = SkillPromptBuilder.Build([]);
        Assert.Equal("", result);
    }

    [Fact]
    public void Build_WithSkills_GeneratesXml()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "web-search",
                Description = "Search the web",
                Instructions = "Use the web_search tool to find information.",
                Location = "/skills/web-search"
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("<available-skills>", result);
        Assert.Contains("<name>web-search</name>", result);
        Assert.Contains("<description>Search the web</description>", result);
        Assert.Contains("<location>/skills/web-search</location>", result);
        Assert.Contains("</available-skills>", result);
        Assert.Contains("<skill-instructions>", result);
        Assert.Contains("## Skill: web-search", result);
        Assert.Contains("Use the web_search tool", result);
    }

    [Fact]
    public void Build_DisableModelInvocation_ExcludesSkill()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "visible",
                Description = "Visible skill",
                Instructions = "Visible instructions.",
                Location = "/skills/visible"
            },
            new()
            {
                Name = "hidden",
                Description = "Hidden skill",
                Instructions = "Hidden instructions.",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("visible", result);
        Assert.DoesNotContain("<name>hidden</name>", result);
    }

    [Fact]
    public void Build_EscapesXmlChars()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "test & <demo>",
                Description = "A \"test\" skill",
                Instructions = "Instructions here.",
                Location = "/skills/test"
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("test &amp; &lt;demo&gt;", result);
        Assert.Contains("A &quot;test&quot; skill", result);
    }

    [Fact]
    public void BuildSummary_NoSkills_ReturnsMessage()
    {
        var result = SkillPromptBuilder.BuildSummary([]);
        Assert.Equal("No skills loaded.", result);
    }

    [Fact]
    public void BuildSummary_WithSkills_ListsThem()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "search",
                Description = "Web search",
                Instructions = "...",
                Location = "/skills/search",
                Source = SkillSource.Workspace,
                ProjectionDiscovery = new SkillProjectionDiscovery
                {
                    Status = "bound",
                    IndexCount = 1,
                    BoundCount = 1,
                    IndexPaths = ["/skills/search/contracts/projections/ontology_extraction/contract-index.json"]
                }
            },
            new()
            {
                Name = "internal",
                Description = "Internal only",
                Instructions = "...",
                Location = "/skills/internal",
                Source = SkillSource.Bundled,
                DisableModelInvocation = true
            }
        };

        var result = SkillPromptBuilder.BuildSummary(skills);

        Assert.Contains("Loaded skills (2)", result);
        Assert.Contains("search: Web search", result);
        Assert.Contains("(Workspace)", result);
        Assert.Contains("projection:bound(1)", result);
        Assert.Contains("internal: Internal only", result);
        Assert.Contains("[no-model]", result);
        Assert.Contains("(Bundled)", result);
    }

    [Fact]
    public void EstimateCharacterCost_NoSkills_ReturnsZero()
    {
        Assert.Equal(0, SkillPromptBuilder.EstimateCharacterCost([]));
    }

    [Fact]
    public void EstimateCharacterCost_WithSkills_ReturnsPositive()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "test",
                Description = "Test skill",
                Instructions = "Do the thing.",
                Location = "/skills/test"
            }
        };

        var cost = SkillPromptBuilder.EstimateCharacterCost(skills);
        Assert.True(cost > 195); // base + per-skill
    }

    [Fact]
    public void EstimateCharacterCost_ExcludesDisabledModelSkills()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "hidden",
                Description = "Hidden",
                Instructions = "...",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        Assert.Equal(0, SkillPromptBuilder.EstimateCharacterCost(skills));
    }
}

public class SkillProjectionResolverTests
{
    [Fact]
    public void ResolveForRequest_SelectsTopicAndViewAndBuildsPatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var relativePath = ProjectionRelativePath(SkillProjectionViewKeys.PromptConstraint);
            var projectionDir = Path.Combine(tempDir, "task-execution");
            Directory.CreateDirectory(projectionDir);

            File.WriteAllText(
                Path.Combine(tempDir, relativePath),
                """
                {
                  "mapping_policy": {
                    "unresolved_item_policy": "block_or_escalate",
                    "prompt_assumption_policy": "disallow_unmapped_terms"
                  },
                  "prompt_projection": {
                    "allowed_terms": ["skills_config", "skill_definition"],
                    "forbidden_assumptions": ["Do not invent new routing rules."],
                    "required_clarifications": ["Clarify the managed path before changing precedence."],
                    "reasoning_paths": ["skills_config -> skill_definition"],
                    "source_digest": ["Primary source: SkillLoader.cs"]
                  },
                                    "delivery_artifacts": [
                                        {
                                            "artifact_name": "TaskExecutionPromptPolicy.md",
                                            "artifact_type": "prompt_fragment",
                                            "path": "artifacts/TaskExecutionPromptPolicy.md",
                                            "status": "planned"
                                        }
                                    ],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
                            DefaultSelectionPolicy = new ProjectionSelectionPolicy
                            {
                                PreferReadyOnly = true,
                                BlockOnOpenQuestions = true,
                                FallbackOrderByTargetView = ["prompt-constraint"]
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
                                        PrimaryIntentSignals = ["review guidance", "prompt policy", "execution policy"],
                                        SupportingSignals = ["guidance", "review"],
                                        ExplicitArtifactSignals = ["prompt policy"],
                                        DemoteWhenCompetingTopicSignals = []
                                    },
                                    new ProjectionTopicSignals
                                    {
                                        DomainSlug = "tool-orchestration",
                                        PrimaryIntentSignals = ["workflow", "planner"],
                                        SupportingSignals = ["steps"],
                                        ExplicitArtifactSignals = ["workflow contract"],
                                        DemoteWhenCompetingTopicSignals = ["review guidance"]
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
                                        ExplicitOutputSignals = ["prompt policy"],
                                        StrongSignals = ["review guidance", "allowed terms"],
                                        SupportingSignals = ["guidance"],
                                        DemoteWhenCompetingViewSignals = []
                                    },
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "workflow-contract",
                                        ExplicitOutputSignals = ["workflow contract"],
                                        StrongSignals = ["workflow", "step graph"],
                                        SupportingSignals = ["steps"],
                                        DemoteWhenCompetingViewSignals = ["prompt policy"]
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
                                            Path = relativePath.Replace('\\', '/')
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "Please add review guidance and a prompt policy for this task.", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("task-execution", resolution.SelectedTopic);
            Assert.Equal("prompt-constraint", resolution.SelectedTargetView);

            var patch = SkillProjectionResolver.BuildPromptPatch(resolution);
            Assert.Contains("[Projection Route]", patch);
            Assert.Contains("Selected topic: task-execution", patch);
            Assert.Contains("Allowed terms:", patch);
            Assert.Contains("skills_config", patch);
            Assert.Contains("Do not invent new routing rules.", patch);
            Assert.Contains("Prompt constraint: Do not use unmapped terms or invent terminology beyond this projection.", patch);
            Assert.Contains("Delivery artifacts:", patch);
            Assert.Contains("TaskExecutionPromptPolicy.md (prompt_fragment) -> artifacts/TaskExecutionPromptPolicy.md [planned]", patch);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_BlockOnOpenQuestions_ReturnsBlockedResolution()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
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
                  "open_questions": ["Need clarification before use."]
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
                            DefaultSelectionPolicy = new ProjectionSelectionPolicy
                            {
                                PreferReadyOnly = true,
                                BlockOnOpenQuestions = true,
                                FallbackOrderByTargetView = ["prompt-constraint"]
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
                                            Path = relativePath.Replace('\\', '/')
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "task execution", new TestLogger());

            Assert.True(resolution.IsBlocked);
            Assert.Contains("blocking open questions", resolution.BlockReason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithStructuredProjectionMetadata_BlocksAndBuildsReadablePatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
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
                    "unresolved_item_policy": "record_only"
                  },
                  "prompt_projection": {
                    "allowed_terms": ["skills_config"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [
                    {
                      "item_type": "concept",
                      "item_id": "C2",
                      "reason": "Projection keeps only the aggregate root in the runtime patch."
                    }
                  ],
                  "open_questions": [
                    {
                      "question": "Should the managed path be treated as config or runtime state?",
                      "impact": "This changes the chosen validation boundary.",
                      "required_input": "Need a maintainer decision."
                    }
                  ]
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
                            DefaultSelectionPolicy = new ProjectionSelectionPolicy
                            {
                                PreferReadyOnly = true,
                                BlockOnOpenQuestions = true,
                                FallbackOrderByTargetView = ["prompt-constraint"]
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
                                            Path = relativePath.Replace('\\', '/')
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var blockedResolution = SkillProjectionResolver.ResolveForRequest(skill, "task execution", new TestLogger());

            Assert.True(blockedResolution.IsBlocked);
            Assert.Contains("blocking open questions", blockedResolution.BlockReason);

            var unblockedSkill = new SkillDefinition
            {
                Name = skill.Name,
                Description = skill.Description,
                Instructions = skill.Instructions,
                Location = skill.Location,
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
                            DefaultSelectionPolicy = new ProjectionSelectionPolicy
                            {
                                PreferReadyOnly = true,
                                BlockOnOpenQuestions = false,
                                FallbackOrderByTargetView = ["prompt-constraint"]
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
                                            Path = relativePath.Replace('\\', '/')
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var unblockedResolution = SkillProjectionResolver.ResolveForRequest(unblockedSkill, "task execution", new TestLogger());

            Assert.False(unblockedResolution.IsBlocked);

            var patch = SkillProjectionResolver.BuildPromptPatch(unblockedResolution);
            Assert.Contains("Dropped items:", patch);
            Assert.Contains("concept C2: Projection keeps only the aggregate root in the runtime patch.", patch);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithMultipleContracts_SelectsHigherScoringProducer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
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
                    "allowed_terms": ["producer_one_term"]
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

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    CreateProjectionContractSet(producerOneRoot, relativePath, ["review"], ["review"]),
                    CreateProjectionContractSet(producerTwoRoot, relativePath, ["prompt policy"], ["review guidance", "prompt policy"])
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "Please add review guidance and prompt policy for this task.", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.NotNull(resolution.ProjectionFilePath);
            Assert.Contains("producer-two", resolution.ProjectionFilePath);

            var patch = SkillProjectionResolver.BuildPromptPatch(resolution);
            Assert.Contains("producer_two_term", patch);
            Assert.DoesNotContain("producer_one_term", patch);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithTiedScore_UsesHigherProducerPriority()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
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
                    "allowed_terms": ["producer_one_term"]
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

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    CreateProjectionContractSet(producerOneRoot, relativePath, ["prompt policy"], ["review guidance", "prompt policy"], producerPriority: 10),
                    CreateProjectionContractSet(producerTwoRoot, relativePath, ["prompt policy"], ["review guidance", "prompt policy"], producerPriority: 50)
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "Please add review guidance and prompt policy for this task.", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.NotNull(resolution.ProjectionFilePath);
            Assert.Contains("producer-two", resolution.ProjectionFilePath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithNoMatchingSignals_UsesConfiguredFallbackRoute()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "task-execution"));

        try
        {
            var relativePath = Path.Combine("task-execution", "task-execution.prompt-constraint.projection.json").Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar)),
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

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    CreateProjectionContractSet(
                        tempDir,
                        relativePath,
                        ["prompt policy"],
                        ["review guidance"])
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "hello", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("task-execution", resolution.SelectedTopic);
            Assert.Equal("prompt-constraint", resolution.SelectedTargetView);
            Assert.NotNull(resolution.ProjectionFilePath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithExplicitArtifactRequest_PrefersMatchingTargetView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "task-execution"));

        try
        {
            var promptConstraintPath = ProjectionRelativePath(SkillProjectionViewKeys.PromptConstraint).Replace('\\', '/');
            var jsonSchemaPath = ProjectionRelativePath(SkillProjectionViewKeys.JsonSchema).Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(tempDir, promptConstraintPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["prompt_constraint"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            File.WriteAllText(
                Path.Combine(tempDir, jsonSchemaPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["json_schema"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
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
                                    new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 }
                                ],
                                Topics =
                                [
                                    new ProjectionTopicSignals
                                    {
                                        DomainSlug = "task-execution",
                                        PrimaryIntentSignals = ["review guidance"],
                                        SupportingSignals = ["guidance"],
                                        ExplicitArtifactSignals = [],
                                        DemoteWhenCompetingTopicSignals = []
                                    }
                                ]
                            },
                            TargetViewScoring = new ProjectionTargetViewScoring
                            {
                                ClarifyWhenScoreGapBelow = 1,
                                PreferExplicitUserArtifactRequests = true,
                                ScoreDimensions =
                                [
                                    new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                                    new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "explicit_user_artifact_request_bonus", Score = 4 }
                                ],
                                Views =
                                [
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "prompt-constraint",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    },
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "json-schema",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
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
                                            Path = promptConstraintPath
                                        },
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "json-schema",
                                            Status = "READY",
                                            Path = jsonSchemaPath
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "Please provide the json schema for this review guidance.", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("json-schema", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("json_schema", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithChineseArtifactRequest_PrefersMatchingTargetView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "task-execution"));

        try
        {
            var promptConstraintPath = ProjectionRelativePath(SkillProjectionViewKeys.PromptConstraint).Replace('\\', '/');
            var jsonSchemaPath = ProjectionRelativePath(SkillProjectionViewKeys.JsonSchema).Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(tempDir, promptConstraintPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["prompt_constraint"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            File.WriteAllText(
                Path.Combine(tempDir, jsonSchemaPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["json_schema"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
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
                                    new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 }
                                ],
                                Topics =
                                [
                                    new ProjectionTopicSignals
                                    {
                                        DomainSlug = "task-execution",
                                        PrimaryIntentSignals = ["review guidance"],
                                        SupportingSignals = ["guidance"],
                                        ExplicitArtifactSignals = [],
                                        DemoteWhenCompetingTopicSignals = []
                                    }
                                ]
                            },
                            TargetViewScoring = new ProjectionTargetViewScoring
                            {
                                ClarifyWhenScoreGapBelow = 1,
                                PreferExplicitUserArtifactRequests = true,
                                ScoreDimensions =
                                [
                                    new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                                    new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "explicit_user_artifact_request_bonus", Score = 4 }
                                ],
                                Views =
                                [
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "prompt-constraint",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    },
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "json-schema",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
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
                                            Path = promptConstraintPath
                                        },
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "json-schema",
                                            Status = "READY",
                                            Path = jsonSchemaPath
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请提供这份 review guidance 的 JSON Schema 文件。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("json-schema", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("json_schema", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithStableChinesePromptConstraintTerm_PrefersPromptConstraintView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "task-execution"));

        try
        {
            var domainModelPath = ProjectionRelativePath(SkillProjectionViewKeys.DomainModel).Replace('\\', '/');
            var promptConstraintPath = ProjectionRelativePath(SkillProjectionViewKeys.PromptConstraint).Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(tempDir, domainModelPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["domain_model"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            File.WriteAllText(
                Path.Combine(tempDir, promptConstraintPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["prompt_constraint"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
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
                                    new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 }
                                ],
                                Topics =
                                [
                                    new ProjectionTopicSignals
                                    {
                                        DomainSlug = "task-execution",
                                        PrimaryIntentSignals = ["review guidance"],
                                        SupportingSignals = ["guidance"],
                                        ExplicitArtifactSignals = [],
                                        DemoteWhenCompetingTopicSignals = []
                                    }
                                ]
                            },
                            TargetViewScoring = new ProjectionTargetViewScoring
                            {
                                ClarifyWhenScoreGapBelow = 1,
                                PreferExplicitUserArtifactRequests = true,
                                ScoreDimensions =
                                [
                                    new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                                    new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "explicit_user_artifact_request_bonus", Score = 4 }
                                ],
                                Views =
                                [
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "domain-model",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    },
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "prompt-constraint",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    }
                                ]
                            },
                            Topics =
                            [
                                new ProjectionTopicRecord
                                {
                                    DomainSlug = "task-execution",
                                    DefaultTargetView = "domain-model",
                                    Views =
                                    [
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "domain-model",
                                            Status = "READY",
                                            Path = domainModelPath
                                        },
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "prompt-constraint",
                                            Status = "READY",
                                            Path = promptConstraintPath
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请提供这份 review guidance 的提示词约束。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("prompt-constraint", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("prompt_constraint", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithGenericSchemaWord_DoesNotTriggerJsonSchemaPreference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "task-execution"));

        try
        {
            var promptConstraintPath = ProjectionRelativePath(SkillProjectionViewKeys.PromptConstraint).Replace('\\', '/');
            var jsonSchemaPath = ProjectionRelativePath(SkillProjectionViewKeys.JsonSchema).Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(tempDir, promptConstraintPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["prompt_constraint"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            File.WriteAllText(
                Path.Combine(tempDir, jsonSchemaPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["json_schema"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
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
                                    new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 }
                                ],
                                Topics =
                                [
                                    new ProjectionTopicSignals
                                    {
                                        DomainSlug = "task-execution",
                                        PrimaryIntentSignals = ["review guidance"],
                                        SupportingSignals = ["guidance"],
                                        ExplicitArtifactSignals = [],
                                        DemoteWhenCompetingTopicSignals = []
                                    }
                                ]
                            },
                            TargetViewScoring = new ProjectionTargetViewScoring
                            {
                                ClarifyWhenScoreGapBelow = 1,
                                PreferExplicitUserArtifactRequests = true,
                                ScoreDimensions =
                                [
                                    new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                                    new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "explicit_user_artifact_request_bonus", Score = 4 }
                                ],
                                Views =
                                [
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "prompt-constraint",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    },
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "json-schema",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
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
                                            Path = promptConstraintPath
                                        },
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "json-schema",
                                            Status = "READY",
                                            Path = jsonSchemaPath
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "Please provide the schema for this review guidance.", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("prompt-constraint", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("prompt_constraint", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithNonCanonicalChineseArtifactWord_DoesNotTriggerJsonSchemaPreference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "task-execution"));

        try
        {
            var promptConstraintPath = ProjectionRelativePath(SkillProjectionViewKeys.PromptConstraint).Replace('\\', '/');
            var jsonSchemaPath = ProjectionRelativePath(SkillProjectionViewKeys.JsonSchema).Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(tempDir, promptConstraintPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["prompt_constraint"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            File.WriteAllText(
                Path.Combine(tempDir, jsonSchemaPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["json_schema"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
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
                                    new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 }
                                ],
                                Topics =
                                [
                                    new ProjectionTopicSignals
                                    {
                                        DomainSlug = "task-execution",
                                        PrimaryIntentSignals = ["review guidance"],
                                        SupportingSignals = ["guidance"],
                                        ExplicitArtifactSignals = [],
                                        DemoteWhenCompetingTopicSignals = []
                                    }
                                ]
                            },
                            TargetViewScoring = new ProjectionTargetViewScoring
                            {
                                ClarifyWhenScoreGapBelow = 1,
                                PreferExplicitUserArtifactRequests = true,
                                ScoreDimensions =
                                [
                                    new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                                    new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "explicit_user_artifact_request_bonus", Score = 4 }
                                ],
                                Views =
                                [
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "prompt-constraint",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    },
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "json-schema",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
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
                                            Path = promptConstraintPath
                                        },
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "json-schema",
                                            Status = "READY",
                                            Path = jsonSchemaPath
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请给我这份 review guidance 的 json 模式。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("prompt-constraint", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("prompt_constraint", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithBroadChineseConstraintPhrase_DoesNotTriggerPromptConstraintPreference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "task-execution"));

        try
        {
            var domainModelPath = ProjectionRelativePath(SkillProjectionViewKeys.DomainModel).Replace('\\', '/');
            var promptConstraintPath = ProjectionRelativePath(SkillProjectionViewKeys.PromptConstraint).Replace('\\', '/');

            File.WriteAllText(
                Path.Combine(tempDir, domainModelPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["domain_model"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            File.WriteAllText(
                Path.Combine(tempDir, promptConstraintPath.Replace('/', Path.DirectorySeparatorChar)),
                """
                {
                  "prompt_projection": {
                    "allowed_terms": ["prompt_constraint"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Developer skill",
                Instructions = "Base skill instructions.",
                Location = "/skills/software-developer",
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        RootPath = tempDir,
                        Index = new ProjectionContractIndex
                        {
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
                                    new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 }
                                ],
                                Topics =
                                [
                                    new ProjectionTopicSignals
                                    {
                                        DomainSlug = "task-execution",
                                        PrimaryIntentSignals = ["review guidance"],
                                        SupportingSignals = ["guidance"],
                                        ExplicitArtifactSignals = [],
                                        DemoteWhenCompetingTopicSignals = []
                                    }
                                ],
                            },
                            TargetViewScoring = new ProjectionTargetViewScoring
                            {
                                ClarifyWhenScoreGapBelow = 1,
                                PreferExplicitUserArtifactRequests = true,
                                ScoreDimensions =
                                [
                                    new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                                    new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                                    new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                                    new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 },
                                    new ProjectionScoreDimension { Dimension = "explicit_user_artifact_request_bonus", Score = 4 }
                                ],
                                Views =
                                [
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "domain-model",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    },
                                    new ProjectionViewSignals
                                    {
                                        TargetView = "prompt-constraint",
                                        ExplicitOutputSignals = [],
                                        StrongSignals = [],
                                        SupportingSignals = [],
                                        DemoteWhenCompetingViewSignals = []
                                    }
                                ]
                            },
                            Topics =
                            [
                                new ProjectionTopicRecord
                                {
                                    DomainSlug = "task-execution",
                                    DefaultTargetView = "domain-model",
                                    Views =
                                    [
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "domain-model",
                                            Status = "READY",
                                            Path = domainModelPath
                                        },
                                        new ProjectionViewRecord
                                        {
                                            TargetView = "prompt-constraint",
                                            Status = "READY",
                                            Path = promptConstraintPath
                                        }
                                    ]
                                }
                            ]
                        }
                    }
                ]
            };

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请提供这份 review guidance 的提示约束。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("domain-model", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("domain_model", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithStableChineseWorkflowContractTerm_PrefersWorkflowContractView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");

        try
        {
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.DomainModel), "domain_model");
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.WorkflowContract), "workflow_contract");

            var skill = CreateExplicitArtifactPreferenceSkill(
                tempDir,
                "domain-model",
                ("domain-model", "domain_model", "task-execution/task-execution.domain-model.projection.json"),
                ("workflow-contract", "workflow_contract", "task-execution/task-execution.workflow-contract.projection.json"));

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请提供这份 review guidance 的工作流契约。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("workflow-contract", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("workflow_contract", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithBroadChineseWorkflowPhrase_DoesNotTriggerWorkflowContractPreference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");

        try
        {
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.DomainModel), "domain_model");
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.WorkflowContract), "workflow_contract");

            var skill = CreateExplicitArtifactPreferenceSkill(
                tempDir,
                "domain-model",
                ("domain-model", "domain_model", "task-execution/task-execution.domain-model.projection.json"),
                ("workflow-contract", "workflow_contract", "task-execution/task-execution.workflow-contract.projection.json"));

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请提供这份 review guidance 的工作流。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("domain-model", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("domain_model", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithStableChineseDomainModelTerm_PrefersDomainModelView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");

        try
        {
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.DomainModel), "domain_model");
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.WorkflowContract), "workflow_contract");

            var skill = CreateExplicitArtifactPreferenceSkill(
                tempDir,
                "workflow-contract",
                ("workflow-contract", "workflow_contract", "task-execution/task-execution.workflow-contract.projection.json"),
                ("domain-model", "domain_model", "task-execution/task-execution.domain-model.projection.json"));

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请提供这份 review guidance 的领域模型。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal("domain-model", resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("domain_model", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveForRequest_WithBroadChineseModelPhrase_DoesNotTriggerDomainModelPreference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-projection-tests-{Guid.NewGuid():N}");

        try
        {
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.DomainModel), "domain_model");
            WriteProjectionDocument(tempDir, ProjectionRelativePath(SkillProjectionViewKeys.WorkflowContract), "workflow_contract");

            var skill = CreateExplicitArtifactPreferenceSkill(
                tempDir,
                "workflow-contract",
                ("workflow-contract", "workflow_contract", "task-execution/task-execution.workflow-contract.projection.json"),
                ("domain-model", "domain_model", "task-execution/task-execution.domain-model.projection.json"));

            var resolution = SkillProjectionResolver.ResolveForRequest(skill, "请提供这份 review guidance 的模型。", new TestLogger());

            Assert.False(resolution.IsBlocked);
            Assert.Equal(SkillProjectionViewKeys.WorkflowContract, resolution.SelectedTargetView);
            Assert.NotNull(resolution.Projection);
            Assert.Contains("workflow_contract", resolution.Projection.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static SkillDefinition CreateExplicitArtifactPreferenceSkill(
        string rootPath,
        string defaultTargetView,
        params (string TargetView, string AllowedTerm, string RelativePath)[] views)
        => new()
        {
            Name = "software-developer",
            Description = "Developer skill",
            Instructions = "Base skill instructions.",
            Location = "/skills/software-developer",
            ProjectionContracts =
            [
                new SkillProjectionContractSet
                {
                    RootPath = rootPath,
                    Index = new ProjectionContractIndex
                    {
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
                                new ProjectionScoreDimension { Dimension = "primary_intent_match", Score = 5 },
                                new ProjectionScoreDimension { Dimension = "strong_keyword_match", Score = 3 },
                                new ProjectionScoreDimension { Dimension = "supporting_keyword_match", Score = 1 }
                            ],
                            Topics =
                            [
                                new ProjectionTopicSignals
                                {
                                    DomainSlug = "task-execution",
                                    PrimaryIntentSignals = ["review guidance"],
                                    SupportingSignals = ["guidance"],
                                    ExplicitArtifactSignals = [],
                                    DemoteWhenCompetingTopicSignals = []
                                }
                            ]
                        },
                        TargetViewScoring = new ProjectionTargetViewScoring
                        {
                            ClarifyWhenScoreGapBelow = 1,
                            PreferExplicitUserArtifactRequests = true,
                            ScoreDimensions =
                            [
                                new ProjectionScoreDimension { Dimension = "explicit_output_match", Score = 5 },
                                new ProjectionScoreDimension { Dimension = "strong_signal_match", Score = 3 },
                                new ProjectionScoreDimension { Dimension = "supporting_signal_match", Score = 1 },
                                new ProjectionScoreDimension { Dimension = "cross_view_conflict_penalty", Score = -2 },
                                new ProjectionScoreDimension { Dimension = "topic_default_view_bonus", Score = 1 },
                                new ProjectionScoreDimension { Dimension = "explicit_user_artifact_request_bonus", Score = 4 }
                            ],
                            Views =
                            [
                                ..views.Select(view => new ProjectionViewSignals
                                {
                                    TargetView = view.TargetView,
                                    ExplicitOutputSignals = [],
                                    StrongSignals = [],
                                    SupportingSignals = [],
                                    DemoteWhenCompetingViewSignals = []
                                })
                            ]
                        },
                        Topics =
                        [
                            new ProjectionTopicRecord
                            {
                                DomainSlug = "task-execution",
                                DefaultTargetView = defaultTargetView,
                                Views =
                                [
                                    ..views.Select(view => new ProjectionViewRecord
                                    {
                                        TargetView = view.TargetView,
                                        Status = "READY",
                                        Path = view.RelativePath
                                    })
                                ]
                            }
                        ]
                    }
                }
            ]
        };

    private static void WriteProjectionDocument(string rootPath, string relativePath, string allowedTerm)
    {
        var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            $$"""
            {
              "prompt_projection": {
                "allowed_terms": ["{{allowedTerm}}"]
              },
              "delivery_artifacts": [],
              "dropped_items": [],
              "open_questions": []
            }
            """);
    }

    private static SkillProjectionContractSet CreateProjectionContractSet(
        string rootPath,
        string relativePath,
        string[] explicitOutputSignals,
        string[] primaryIntentSignals,
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
                    BlockOnOpenQuestions = true,
                    FallbackOrderByTargetView = [SkillProjectionViewKeys.PromptConstraint]
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
                            PrimaryIntentSignals = primaryIntentSignals,
                            SupportingSignals = ["guidance", "review"],
                            ExplicitArtifactSignals = explicitOutputSignals,
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
                            TargetView = SkillProjectionViewKeys.PromptConstraint,
                            ExplicitOutputSignals = explicitOutputSignals,
                            StrongSignals = primaryIntentSignals,
                            SupportingSignals = ["guidance"],
                            DemoteWhenCompetingViewSignals = []
                        }
                    ]
                },
                Topics =
                [
                    new ProjectionTopicRecord
                    {
                        DomainSlug = "task-execution",
                        DefaultTargetView = SkillProjectionViewKeys.PromptConstraint,
                        Views =
                        [
                            new ProjectionViewRecord
                            {
                                TargetView = SkillProjectionViewKeys.PromptConstraint,
                                Status = "READY",
                                Path = relativePath
                            }
                        ]
                    }
                ]
            }
        };

    private static string ProjectionRelativePath(string targetView)
        => Path.Combine("task-execution", $"task-execution.{targetView}.projection.json");
}

/// <summary>Minimal ILogger for tests.</summary>
file sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) { }
}
