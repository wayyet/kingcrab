using System.Text.Json.Serialization;
using OpenClaw.Plugins.AiEvaluation.Configs;
using OpenClaw.Plugins.AiEvaluation.Models;

namespace OpenClaw.Plugins.AiEvaluation;

[JsonSerializable(typeof(AiEvaluationConfig))]
[JsonSerializable(typeof(SandboxEndpointConfig))]
[JsonSerializable(typeof(TestcaseEntry))]
[JsonSerializable(typeof(TestcaseEntry[]))]
[JsonSerializable(typeof(TestcaseFetchResult))]
[JsonSerializable(typeof(TestcaseSandboxStatus))]
[JsonSerializable(typeof(TestcaseSandboxStatus[]))]
[JsonSerializable(typeof(TraceData))]
[JsonSerializable(typeof(TraceEntry))]
[JsonSerializable(typeof(TraceEntry[]))]
[JsonSerializable(typeof(ScoringCriteria))]
[JsonSerializable(typeof(ScoreDimension))]
[JsonSerializable(typeof(ScoreDimension[]))]
[JsonSerializable(typeof(EvaluationReport))]
[JsonSerializable(typeof(DimensionScore))]
[JsonSerializable(typeof(DimensionScore[]))]
[JsonSerializable(typeof(ImprovementSuggestion))]
[JsonSerializable(typeof(ImprovementSuggestion[]))]
internal partial class AiEvaluationJsonContext : JsonSerializerContext;
