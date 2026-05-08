using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Plugins.AiEvaluation.Models;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

public sealed class EvaluationReportTool(SandboxChatConnection connection) : IToolWithContext
{
    public string Name => "evaluation_report";

    public string Description =>
        "Generate a structured evaluation report for the target sandbox, including multi-dimensional scores, "
        + "strengths, weaknesses, and improvement suggestions.";

    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "scores":{"type":"array"},
        "test_results":{"type":"object"},
        "trace_summary":{"type":"string"},
        "recommendations":{"type":"array"},
        "overall_comment":{"type":"string"}
      },
      "required":[]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: evaluation_report requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;

        var scoresJson = root.TryGetProperty("scores", out var s) ? s.GetRawText() : "[]";
        var testResults = root.TryGetProperty("test_results", out var tr) ? tr.GetRawText() : "{}";
        var traceSummary = GetString(root, "trace_summary") ?? "";
        var recommendations = root.TryGetProperty("recommendations", out var rec) ? rec.GetRawText() : "[]";
        var overallComment = GetString(root, "overall_comment") ?? "";

        var prompt = BuildReportPrompt(scoresJson, testResults, traceSummary, recommendations, overallComment);

        try
        {
            var response = await connection.SendMessageAsync(prompt, ct);

            var report = ParseEvaluationReport(response);
            if (report is not null)
                return JsonSerializer.Serialize(report, AiEvaluationJsonContext.Default.EvaluationReport);

            return response;
        }
        catch (Exception ex)
        {
            return $"Error: evaluation_report failed - {ex.Message}";
        }
    }

    private static EvaluationReport? ParseEvaluationReport(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("report", out var reportEl))
            {
                return JsonSerializer.Deserialize(
                    reportEl.GetRawText(),
                    AiEvaluationJsonContext.Default.EvaluationReport);
            }

            if (root.TryGetProperty("scores", out _) && root.TryGetProperty("report_id", out _))
            {
                return JsonSerializer.Deserialize(
                    response,
                    AiEvaluationJsonContext.Default.EvaluationReport);
            }
        }
        catch { }

        return null;
    }

    private static string BuildReportPrompt(
        string scoresJson, string testResults, string traceSummary,
        string recommendations, string overallComment)
    {
        return $"""
            Generate a structured evaluation report based on the following data.
            Return as a JSON object with 'report' key containing:
            report_id, evaluated_at, target_endpoint, scores array (each with dimension, score, max_score, comment),
            total_score, max_possible_score, overall_rating, strengths array, weaknesses array,
            suggestions array (each with area, suggestion, priority), and summary.

            Scores: {scoresJson}
            Test Results: {testResults}
            Trace Summary: {traceSummary}
            Recommendations: {recommendations}
            Overall Comment: {overallComment}
            """.ReplaceLineEndings(" ");
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
