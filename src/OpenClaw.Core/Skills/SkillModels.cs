using System.Text.Json.Serialization;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Top-level skills configuration. Maps to <c>Skills</c> section in config.
/// </summary>
public sealed class SkillsConfig
{
    /// <summary>Master toggle for the skills system.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Skill loading configuration.</summary>
    public SkillLoadConfig Load { get; set; } = new();

    /// <summary>Per-skill entry overrides (keyed by skill name or skillKey).</summary>
    public Dictionary<string, SkillEntryConfig> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional allowlist for bundled skills only. If set, only listed bundled skills are eligible.</summary>
    public string[] AllowBundled { get; set; } = [];
}

/// <summary>
/// Controls where skills are loaded from.
/// </summary>
public sealed class SkillLoadConfig
{
    /// <summary>Additional skill directories (lowest precedence).</summary>
    public string[] ExtraDirs { get; set; } = [];

    /// <summary>Load bundled skills shipped with the gateway.</summary>
    public bool IncludeBundled { get; set; } = true;

    /// <summary>Load managed/local skills from ~/.openclaw/skills.</summary>
    public bool IncludeManaged { get; set; } = true;

    /// <summary>Load workspace skills from $OPENCLAW_WORKSPACE/skills.</summary>
    public bool IncludeWorkspace { get; set; } = true;

    /// <summary>Enable file-system watching for hot reload.</summary>
    public bool Watch { get; set; } = false;

    /// <summary>Debounce interval for the watcher (ms).</summary>
    public int WatchDebounceMs { get; set; } = 250;
}

/// <summary>
/// Per-skill config override.
/// </summary>
public sealed class SkillEntryConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>API key shorthand — injected as the env var named by <c>primaryEnv</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Environment variables injected for this skill's agent run.</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom per-skill config bag.</summary>
    public Dictionary<string, string> Config { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A parsed skill definition, loaded from a <c>SKILL.md</c> file.
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>Skill name from frontmatter.</summary>
    public required string Name { get; init; }

    /// <summary>Short description from frontmatter.</summary>
    public required string Description { get; init; }

    /// <summary>The full instructions body (markdown below the frontmatter).</summary>
    public required string Instructions { get; init; }

    /// <summary>Filesystem path of the skill directory.</summary>
    public required string Location { get; init; }

    /// <summary>Where the skill came from.</summary>
    public SkillSource Source { get; init; }

    /// <summary>Parsed metadata from the frontmatter.</summary>
    public SkillMetadata Metadata { get; init; } = new();

    /// <summary>Whether the skill is user-invocable as a slash command.</summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>Whether the skill is excluded from the model prompt.</summary>
    public bool DisableModelInvocation { get; init; } = false;

    /// <summary>Optional tool dispatch settings.</summary>
    public string? CommandDispatch { get; init; }
    public string? CommandTool { get; init; }
    public string? CommandArgMode { get; init; }

    /// <summary>Bound projection contracts that can refine this skill per request.</summary>
    public IReadOnlyList<SkillProjectionContractSet> ProjectionContracts { get; init; } = [];

    /// <summary>Optional machine-readable artifact/stage contract loaded from contracts/artifacts.json.</summary>
    public SkillArtifactContract? ArtifactContract { get; init; }

    /// <summary>Optional projection contract discovery diagnostics for loader summaries.</summary>
    public SkillProjectionDiscovery? ProjectionDiscovery { get; init; }
}

/// <summary>
/// Machine-readable contract describing artifacts a skill may emit and the stage gates they drive.
/// Loaded from <c>contracts/artifacts.json</c> inside the skill directory.
/// </summary>
public sealed class SkillArtifactContract
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SkillArtifactStageContract> Stages { get; init; } = [];
}

public sealed class SkillArtifactStageContract
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public SkillArtifactStageGateContract? Gate { get; init; }
    public IReadOnlyList<SkillArtifactTypeContract> Artifacts { get; init; } = [];
}

public sealed class SkillArtifactStageGateContract
{
    public string? RequiresStage { get; init; }
}

public sealed class SkillArtifactTypeContract
{
    public required string Type { get; init; }
    public string? Label { get; init; }
    public string? Display { get; init; }
    public bool? Terminal { get; init; }
}

public sealed class SkillProjectionDiscovery
{
    public required string Status { get; init; }
    public int IndexCount { get; init; }
    public int BoundCount { get; init; }
    public IReadOnlyList<string> IndexPaths { get; init; } = [];
    public string? Message { get; init; }
}

/// <summary>
/// Bound projection contracts attached to a skill.
/// </summary>
public sealed class SkillProjectionContractSet
{
    public string? ProducerName { get; init; }
    public int ProducerPriority { get; init; }
    public required string RootPath { get; init; }
    public required ProjectionContractIndex Index { get; init; }
}

public sealed class ProjectionContractIndex
{
    public string? ProducerSkill { get; init; }
    public int ProducerPriority { get; init; }
    public ProjectionSelectionPolicy DefaultSelectionPolicy { get; init; } = new();
    public ProjectionTopicScoring? TopicScoring { get; init; }
    public ProjectionTargetViewScoring? TargetViewScoring { get; init; }
    public IReadOnlyList<ProjectionTopicRecord> Topics { get; init; } = [];
}

public sealed class ProjectionSelectionPolicy
{
    public bool PreferReadyOnly { get; init; }
    public bool BlockOnOpenQuestions { get; init; }
    public IReadOnlyList<string> FallbackOrderByTargetView { get; init; } = [];
}

public sealed class ProjectionTopicScoring
{
    public int ClarifyWhenScoreGapBelow { get; init; } = 2;
    public IReadOnlyList<ProjectionScoreDimension> ScoreDimensions { get; init; } = [];
    public IReadOnlyList<ProjectionTopicSignals> Topics { get; init; } = [];
}

public sealed class ProjectionTargetViewScoring
{
    public int ClarifyWhenScoreGapBelow { get; init; } = 2;
    public bool PreferExplicitUserArtifactRequests { get; init; }
    public IReadOnlyList<ProjectionScoreDimension> ScoreDimensions { get; init; } = [];
    public IReadOnlyList<ProjectionViewSignals> Views { get; init; } = [];
    public IReadOnlyList<ProjectionTopicViewOverride> WithinTopicOverrides { get; init; } = [];
}

public sealed class ProjectionScoreDimension
{
    public required string Dimension { get; init; }
    public int Score { get; init; }
}

public sealed class ProjectionTopicSignals
{
    public required string DomainSlug { get; init; }
    public IReadOnlyList<string> PrimaryIntentSignals { get; init; } = [];
    public IReadOnlyList<string> SupportingSignals { get; init; } = [];
    public IReadOnlyList<string> ExplicitArtifactSignals { get; init; } = [];
    public IReadOnlyList<string> DemoteWhenCompetingTopicSignals { get; init; } = [];
}

public sealed class ProjectionViewSignals
{
    public required string TargetView { get; init; }
    public IReadOnlyList<string> ExplicitOutputSignals { get; init; } = [];
    public IReadOnlyList<string> StrongSignals { get; init; } = [];
    public IReadOnlyList<string> SupportingSignals { get; init; } = [];
    public IReadOnlyList<string> DemoteWhenCompetingViewSignals { get; init; } = [];
}

public sealed class ProjectionTopicViewOverride
{
    public required string DomainSlug { get; init; }
    public IReadOnlyList<ProjectionTopicViewBonus> Bonuses { get; init; } = [];
}

public sealed class ProjectionTopicViewBonus
{
    public required string TargetView { get; init; }
    public IReadOnlyList<string> WhenRequestSignals { get; init; } = [];
    public int Score { get; init; }
}

public sealed class ProjectionTopicRecord
{
    public required string DomainSlug { get; init; }
    public required string DefaultTargetView { get; init; }
    public IReadOnlyList<ProjectionViewRecord> Views { get; init; } = [];
}

public sealed class ProjectionViewRecord
{
    public required string TargetView { get; init; }
    public required string Status { get; init; }
    public required string Path { get; init; }
}

public sealed class ProjectionDocument
{
    public ProjectionMappingPolicy MappingPolicy { get; init; } = new();
    public ProjectionPromptPayload PromptProjection { get; init; } = new();
    public IReadOnlyList<ProjectionDeliveryArtifact> DeliveryArtifacts { get; init; } = [];
    public IReadOnlyList<string> DroppedItems { get; init; } = [];
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
}

public sealed class ProjectionMappingPolicy
{
    public string? UnresolvedItemPolicy { get; init; }
    public string? PromptAssumptionPolicy { get; init; }
}

public sealed class ProjectionPromptPayload
{
    public IReadOnlyList<string> AllowedTerms { get; init; } = [];
    public IReadOnlyList<string> ForbiddenAssumptions { get; init; } = [];
    public IReadOnlyList<string> RequiredClarifications { get; init; } = [];
    public IReadOnlyList<string> ReasoningPaths { get; init; } = [];
    public IReadOnlyList<string> SourceDigest { get; init; } = [];
}

public sealed class ProjectionDeliveryArtifact
{
    public required string ArtifactName { get; init; }
    public required string ArtifactType { get; init; }
    public required string Path { get; init; }
    public string? Status { get; init; }
}

public sealed class SkillProjectionResolution
{
    public required string SkillName { get; init; }
    public bool HasContracts { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockReason { get; init; }
    public string? SelectedTopic { get; init; }
    public string? SelectedTargetView { get; init; }
    public string? ProjectionFilePath { get; init; }
    public ProjectionDocument? Projection { get; init; }
}

/// <summary>
/// Where a skill was loaded from.
/// </summary>
public enum SkillSource : byte
{
    Bundled,
    Managed,
    Workspace,
    Extra,
    Plugin
}

/// <summary>
/// Metadata block parsed from the <c>metadata</c> frontmatter line.
/// </summary>
public sealed class SkillMetadata
{
    /// <summary>If true, skill is always eligible regardless of other gates.</summary>
    public bool Always { get; set; }

    /// <summary>Optional emoji for UI display.</summary>
    public string? Emoji { get; set; }

    /// <summary>Optional homepage URL.</summary>
    public string? Homepage { get; set; }

    /// <summary>OS filter (darwin, linux, win32). Empty = any OS.</summary>
    public string[] Os { get; set; } = [];

    /// <summary>Required binary names on PATH.</summary>
    public string[] RequireBins { get; set; } = [];

    /// <summary>At least one of these binaries must exist on PATH.</summary>
    public string[] RequireAnyBins { get; set; } = [];

    /// <summary>Required environment variables.</summary>
    public string[] RequireEnv { get; set; } = [];

    /// <summary>Required config paths that must be truthy.</summary>
    public string[] RequireConfig { get; set; } = [];

    /// <summary>Primary env var associated with <c>skills.entries.*.apiKey</c>.</summary>
    public string? PrimaryEnv { get; set; }

    /// <summary>Alternative config key used by <c>skills.entries.*</c>.</summary>
    public string? SkillKey { get; set; }
}
