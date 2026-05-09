using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

/// <summary>
/// Report generation tool. The agent calls this to produce a structured evaluation report
/// conforming to the evaluation-report.schema.json contract.
/// </summary>
public sealed class EvaluationReportTool : ITool
{
    public string Name => "evaluation_generate_report";

    public string Description =>
        "Generate a structured evaluation report. " +
        "Provide dimension_scores (from evaluation_score tool output), a summary string, " +
        "and optional strengths, weaknesses, and improvement suggestions. " +
        "Returns a complete report JSON conforming to the evaluation report schema.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "dimension_scores": {
              "type": "array",
              "description": "Scored dimensions with evidence references",
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
            "overall_score": {
              "type": "number",
              "description": "Weighted overall score (0-100)"
            },
            "verdict": {
              "type": "string",
              "description": "PASS or FAIL"
            },
            "summary": {
              "type": "string",
              "description": "Overall evaluation summary in the target language"
            },
            "strengths": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Observed strengths"
            },
            "weaknesses": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Observed weaknesses"
            },
            "suggestions": {
              "type": "array",
              "description": "Improvement suggestions",
              "items": {
                "type": "object",
                "properties": {
                  "area": { "type": "string" },
                  "suggestion": { "type": "string" },
                  "priority": { "type": "string", "enum": ["high", "medium", "low"] }
                },
                "required": ["area", "suggestion", "priority"]
              }
            }
          },
          "required": ["dimension_scores", "overall_score", "verdict", "summary"]
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

        if (!root.TryGetProperty("overall_score", out var overallElement) ||
            !overallElement.TryGetDecimal(out var overallScore))
        {
            return new ValueTask<string>(ErrorJson("Missing or invalid 'overall_score'."));
        }

        if (!root.TryGetProperty("verdict", out var verdictElement) ||
            verdictElement.ValueKind != JsonValueKind.String)
        {
            return new ValueTask<string>(ErrorJson("Missing or invalid 'verdict'."));
        }

        if (!root.TryGetProperty("summary", out var summaryElement) ||
            summaryElement.ValueKind != JsonValueKind.String)
        {
            return new ValueTask<string>(ErrorJson("Missing or invalid 'summary'."));
        }

        var reportId = $"eval-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..24];
        var evaluatedAt = DateTimeOffset.UtcNow.ToString("o");

        // Build dimension scores array
        var dimScores = new List<object>();
        decimal maxPossible = 0;
        foreach (var item in scoresElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var dim = item.TryGetProperty("dimension", out var d) ? d.GetString() ?? "" : "";
            var sc = item.TryGetProperty("score", out var s) && s.TryGetDecimal(out var sv) ? sv : 0;
            var mx = item.TryGetProperty("max_score", out var m) && m.TryGetDecimal(out var mv) ? mv : 100;
            var comment = item.TryGetProperty("comment", out var c) ? c.GetString() ?? "" : "";
            maxPossible += mx;
            dimScores.Add(new { dimension = dim, score = sc, max_score = mx, comment });
        }

        // Strengths
        var strengths = new List<string>();
        if (root.TryGetProperty("strengths", out var strElement) &&
            strElement.ValueKind == JsonValueKind.Array)
            foreach (var s in strElement.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String) strengths.Add(s.GetString()!);

        // Weaknesses
        var weaknesses = new List<string>();
        if (root.TryGetProperty("weaknesses", out var weakElement) &&
            weakElement.ValueKind == JsonValueKind.Array)
            foreach (var w in weakElement.EnumerateArray())
                if (w.ValueKind == JsonValueKind.String) weaknesses.Add(w.GetString()!);

        // Suggestions
        var suggestions = new List<object>();
        if (root.TryGetProperty("suggestions", out var sugElement) &&
            sugElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var sg in sugElement.EnumerateArray())
            {
                if (sg.ValueKind != JsonValueKind.Object) continue;
                var area = sg.TryGetProperty("area", out var a) ? a.GetString() ?? "" : "";
                var suggestion = sg.TryGetProperty("suggestion", out var su) ? su.GetString() ?? "" : "";
                var priority = sg.TryGetProperty("priority", out var p) ? p.GetString() ?? "medium" : "medium";
                suggestions.Add(new { area, suggestion, priority });
            }
        }

        // Rating label
        var rating = overallScore switch
        {
            >= 90 => "A (Excellent)",
            >= 80 => "B (Good)",
            >= 70 => "C (Adequate)",
            >= 60 => "D (Needs Improvement)",
            _ => "F (Failing)"
        };

        var report = new
        {
            report_id = reportId,
            evaluated_at = evaluatedAt,
            scores = dimScores,
            total_score = overallScore,
            max_possible_score = maxPossible,
            overall_rating = rating,
            strengths,
            weaknesses,
            suggestions,
            summary = summaryElement.GetString() ?? ""
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        return new ValueTask<string>(json);
    }

    private static string ErrorJson(string message)
        => $"{{\"error\":\"{message.Replace("\"", "\\\"")}\"}}";
}
