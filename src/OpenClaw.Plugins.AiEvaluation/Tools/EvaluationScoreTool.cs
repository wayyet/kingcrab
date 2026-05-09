using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

/// <summary>
/// Multi-dimension scoring tool. The agent calls this to produce a structured verdict
/// from dimension scores, weights, and execution evidence.
/// </summary>
public sealed class EvaluationScoreTool : ITool
{
    public string Name => "evaluation_score";

    public string Description =>
        "Score evaluation results across multiple dimensions. " +
        "Provide dimension_scores (each with dimension, score, max_score, comment, evidence_refs), " +
        "weights (optional, keyed by dimension name), and pass_threshold (default 75). " +
        "Returns overall_score, verdict (PASS/FAIL), and normalized dimension scores.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "dimension_scores": {
              "type": "array",
              "description": "Per-dimension scores with evidence references",
              "items": {
                "type": "object",
                "properties": {
                  "dimension": { "type": "string" },
                  "score": { "type": "number" },
                  "max_score": { "type": "number" },
                  "comment": { "type": "string" },
                  "evidence_refs": {
                    "type": "array",
                    "items": { "type": "string" }
                  }
                },
                "required": ["dimension", "score", "max_score", "comment"]
              }
            },
            "weights": {
              "type": "object",
              "description": "Optional dimension weights. Keys match dimension names. Defaults to equal weighting.",
              "additionalProperties": { "type": "number" }
            },
            "pass_threshold": {
              "type": "number",
              "description": "Minimum overall_score to pass. Default 75."
            }
          },
          "required": ["dimension_scores"]
        }
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("dimension_scores", out var scoresElement) ||
            scoresElement.ValueKind != JsonValueKind.Array)
        {
            return new ValueTask<string>(ErrorJson("Missing or invalid 'dimension_scores' array."));
        }

        var scores = new List<DimensionScoreInput>();
        foreach (var item in scoresElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var dim = item.TryGetProperty("dimension", out var d) ? d.GetString() : null;
            var sc = item.TryGetProperty("score", out var s) && s.TryGetDecimal(out var sv) ? sv : -1;
            var mx = item.TryGetProperty("max_score", out var m) && m.TryGetDecimal(out var mv) ? mv : 100;
            var comment = item.TryGetProperty("comment", out var c) ? c.GetString() ?? "" : "";
            var refs = new List<string>();
            if (item.TryGetProperty("evidence_refs", out var er) && er.ValueKind == JsonValueKind.Array)
                foreach (var r in er.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String) refs.Add(r.GetString()!);

            if (string.IsNullOrWhiteSpace(dim) || sc < 0)
                return new ValueTask<string>(ErrorJson($"Invalid dimension entry: dimension='{dim}', score={sc}"));

            scores.Add(new DimensionScoreInput(dim, sc, mx, comment, refs));
        }

        if (scores.Count == 0)
            return new ValueTask<string>(ErrorJson("At least one dimension_score is required."));

        // Resolve weights
        var weights = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("weights", out var weightsElement) &&
            weightsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in weightsElement.EnumerateObject())
            {
                if (prop.Value.TryGetDecimal(out var w) && w >= 0)
                    weights[prop.Name] = w;
            }
        }

        var passThreshold = 75m;
        if (root.TryGetProperty("pass_threshold", out var pt) && pt.TryGetDecimal(out var ptv) && ptv > 0 && ptv <= 100)
            passThreshold = ptv;

        // Compute weighted overall score
        var totalWeight = 0m;
        var weightedSum = 0m;
        foreach (var score in scores)
        {
            var normalized = score.MaxScore > 0
                ? score.Score / score.MaxScore * 100m
                : 0m;
            var weight = weights.TryGetValue(score.Dimension, out var w) && w > 0
                ? w
                : 1m; // default equal weight
            weightedSum += normalized * weight;
            totalWeight += weight;
        }

        var overallScore = totalWeight > 0
            ? Math.Round(weightedSum / totalWeight, 1)
            : 0m;
        var passed = overallScore >= passThreshold;

        var resultObj = new
        {
            verdict = passed ? "PASS" : "FAIL",
            overall_score = overallScore,
            pass_threshold = passThreshold,
            dimension_count = scores.Count,
            weighted = weights.Count > 0,
            dimension_scores = scores.Select(s => new
            {
                dimension = s.Dimension,
                score = s.Score,
                max_score = s.MaxScore,
                normalized_pct = s.MaxScore > 0
                    ? Math.Round(s.Score / s.MaxScore * 100m, 1)
                    : 0m,
                comment = s.Comment,
                evidence_refs = s.EvidenceRefs
            })
        };

        var json = JsonSerializer.Serialize(resultObj, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        return new ValueTask<string>(json);
    }

    private static string ErrorJson(string message)
        => $"{{\"error\":\"{message.Replace("\"", "\\\"")}\"}}";

    private sealed record DimensionScoreInput(
        string Dimension,
        decimal Score,
        decimal MaxScore,
        string Comment,
        IReadOnlyList<string> EvidenceRefs);
}
