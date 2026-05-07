using System.Collections.Concurrent;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Gateway.Tools;

internal sealed class SkillArtifactRuntime
{
    private readonly ConcurrentDictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StageState> _stageStates = new(StringComparer.OrdinalIgnoreCase);

    public void ReplaceSkills(IEnumerable<SkillDefinition> skills)
    {
        _skills.Clear();
        foreach (var skill in skills)
            _skills[skill.Name] = skill;
    }

    public SkillArtifactResult NormalizeAndRecord(string sessionId, SkillArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.SkillName))
            return SkillArtifactResult.Success(artifact, null);

        if (!_skills.TryGetValue(artifact.SkillName, out var skill))
            return SkillArtifactResult.Failure($"Unknown skill '{artifact.SkillName}'.");

        var contract = skill.ArtifactContract;
        if (contract is null || contract.Stages.Count == 0)
            return SkillArtifactResult.Success(artifact, null);

        if (!TryResolveArtifactContract(contract, artifact, out var stage, out var artifactType, out var error))
            return SkillArtifactResult.Failure(error ?? "Artifact does not match the skill contract.");

        var normalized = artifact with
        {
            SkillName = skill.Name,
            Stage = stage.Name,
            ArtifactType = artifactType.Type,
            Label = artifact.Label ?? artifactType.Label,
            DisplayHint = artifact.DisplayHint ?? artifactType.Display,
            IsTerminal = artifactType.Terminal ?? artifact.IsTerminal
        };

        SkillStageGateEvent? gate = null;
        if (normalized.IsTerminal)
        {
            MarkStageTerminal(sessionId, skill.Name, stage.Name);
            gate = BuildGateEvent(sessionId, skill.Name, contract, stage.Name);
        }

        return SkillArtifactResult.Success(normalized, gate);
    }

    private static bool TryResolveArtifactContract(
        SkillArtifactContract contract,
        SkillArtifact artifact,
        out SkillArtifactStageContract stage,
        out SkillArtifactTypeContract artifactType,
        out string? error)
    {
        stage = null!;
        artifactType = null!;
        error = null;

        if (!string.IsNullOrWhiteSpace(artifact.Stage))
        {
            stage = contract.Stages.FirstOrDefault(s => string.Equals(s.Name, artifact.Stage, StringComparison.OrdinalIgnoreCase))!;
            if (stage is null)
            {
                error = $"Stage '{artifact.Stage}' is not declared in contracts/artifacts.json.";
                return false;
            }

            artifactType = stage.Artifacts.FirstOrDefault(a => string.Equals(a.Type, artifact.ArtifactType, StringComparison.OrdinalIgnoreCase))!;
            if (artifactType is null)
            {
                error = $"Artifact type '{artifact.ArtifactType}' is not declared for stage '{stage.Name}'.";
                return false;
            }

            return true;
        }

        var matches = contract.Stages
            .SelectMany(s => s.Artifacts.Select(a => (Stage: s, Artifact: a)))
            .Where(x => string.Equals(x.Artifact.Type, artifact.ArtifactType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            stage = matches[0].Stage;
            artifactType = matches[0].Artifact;
            return true;
        }

        error = matches.Count == 0
            ? $"Artifact type '{artifact.ArtifactType}' is not declared in contracts/artifacts.json."
            : $"Artifact type '{artifact.ArtifactType}' appears in multiple stages; pass the stage explicitly.";
        return false;
    }

    private void MarkStageTerminal(string sessionId, string skillName, string stageName)
    {
        var key = StageKey(sessionId, skillName, stageName);
        _stageStates.AddOrUpdate(
            key,
            static _ => new StageState(true, DateTimeOffset.UtcNow),
            static (_, existing) => existing with { IsTerminal = true, UpdatedAtUtc = DateTimeOffset.UtcNow });
    }

    private SkillStageGateEvent? BuildGateEvent(string sessionId, string skillName, SkillArtifactContract contract, string completedStage)
    {
        var currentIndex = -1;
        for (var i = 0; i < contract.Stages.Count; i++)
        {
            if (string.Equals(contract.Stages[i].Name, completedStage, StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0 || currentIndex >= contract.Stages.Count - 1)
            return null;

        var nextStage = contract.Stages[currentIndex + 1];
        if (!string.IsNullOrWhiteSpace(nextStage.Gate?.RequiresStage))
        {
            var requiredStage = nextStage.Gate.RequiresStage;
            var satisfied = _stageStates.TryGetValue(StageKey(sessionId, skillName, requiredStage), out var state)
                && state.IsTerminal;
            return new SkillStageGateEvent
            {
                SkillName = skillName,
                CompletedStage = completedStage,
                NextStage = nextStage.Name,
                CanProceed = satisfied,
                BlockedReason = satisfied ? null : $"Stage '{requiredStage}' is not complete."
            };
        }

        return new SkillStageGateEvent
        {
            SkillName = skillName,
            CompletedStage = completedStage,
            NextStage = nextStage.Name,
            CanProceed = true
        };
    }

    private static string StageKey(string sessionId, string skillName, string stageName)
        => $"{sessionId}:{skillName}:{stageName}";

    private sealed record StageState(bool IsTerminal, DateTimeOffset UpdatedAtUtc);
}

internal sealed record SkillArtifactResult(SkillArtifact? Artifact, SkillStageGateEvent? StageGate, string? Error)
{
    public bool Succeeded => Error is null;

    public static SkillArtifactResult Success(SkillArtifact artifact, SkillStageGateEvent? stageGate)
        => new(artifact, stageGate, null);

    public static SkillArtifactResult Failure(string error)
        => new(null, null, error);
}