using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Plugins.AiEvaluation.Models;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

public sealed class OntologyQueryTool(SandboxChatConnection connection) : IToolWithContext
{
    public string Name => "ontology_query";

    public string Description =>
        "Query scoring criteria and evaluation rubrics from the ontology knowledge base. "
        + "Returns multi-dimensional scoring standards for evaluating target sandbox performance.";

    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "domain":{"type":"string"},
        "category":{"type":"string"},
        "dimensions":{"type":"array","items":{"type":"string"}}
      },
      "required":[]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: ontology_query requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;

        var domain = GetString(root, "domain");
        var category = GetString(root, "category");
        var dimensions = GetStringArray(root, "dimensions");

        var prompt = BuildQuery(domain, category, dimensions);

        try
        {
            var response = await connection.SendMessageAsync(prompt, ct);

            var criteria = ParseScoringCriteria(response);
            if (criteria is not null)
                return JsonSerializer.Serialize(criteria, AiEvaluationJsonContext.Default.ScoringCriteria);

            return response;
        }
        catch (Exception ex)
        {
            return $"Error: ontology_query failed - {ex.Message}";
        }
    }

    private static ScoringCriteria? ParseScoringCriteria(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("criteria", out var criteriaEl))
            {
                return JsonSerializer.Deserialize(
                    criteriaEl.GetRawText(),
                    AiEvaluationJsonContext.Default.ScoringCriteria);
            }

            if (root.TryGetProperty("dimensions", out _))
            {
                return JsonSerializer.Deserialize(
                    response,
                    AiEvaluationJsonContext.Default.ScoringCriteria);
            }
        }
        catch { }

        return null;
    }

    private static string BuildQuery(string? domain, string? category, string[] dimensions)
    {
        var sb = new System.Text.StringBuilder("Query evaluation scoring criteria");
        if (!string.IsNullOrWhiteSpace(domain))
            sb.Append($" for domain '{domain}'");
        if (!string.IsNullOrWhiteSpace(category))
            sb.Append($" in category '{category}'");
        if (dimensions.Length > 0)
            sb.Append($" covering dimensions: {string.Join(", ", dimensions)}");
        sb.Append(". Return as a JSON object with 'criteria' key containing domain, version, and dimensions array. "
            + "Each dimension must include name, description, max_score, indicators array, and levels array "
            + "(each level with label, range_min, range_max, description).");
        return sb.ToString();
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string[] GetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array)
            return [];

        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToArray();
    }
}
