using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Plugins.AiEvaluation.Models;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

public sealed class TraceReadTool(SandboxChatConnection connection) : IToolWithContext
{
    public string Name => "trace_read";

    public string Description =>
        "Read the complete execution trace from the target sandbox, including thinking chains, "
        + "tool calls, and conversation content. Supports filtering by trace type.";

    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "session_id":{"type":"string"},
        "trace_type":{"type":"string","enum":["thinking","tool_calls","conversation","all"],"default":"all"},
        "max_entries":{"type":"integer"},
        "step_from":{"type":"integer"},
        "step_to":{"type":"integer"}
      },
      "required":[]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: trace_read requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;

        var sessionId = GetString(root, "session_id");
        var traceType = GetString(root, "trace_type") ?? "all";
        var maxEntries = GetInt32(root, "max_entries", 200);
        var stepFrom = GetNullableInt32(root, "step_from");
        var stepTo = GetNullableInt32(root, "step_to");

        var prompt = BuildTraceQuery(sessionId, traceType, maxEntries, stepFrom, stepTo);

        try
        {
            var response = await connection.SendMessageAsync(prompt, ct);

            var traceData = ParseTraceData(response);
            if (traceData is not null)
                return JsonSerializer.Serialize(traceData, AiEvaluationJsonContext.Default.TraceData);

            return response;
        }
        catch (Exception ex)
        {
            return $"Error: trace_read failed - {ex.Message}";
        }
    }

    private static TraceData? ParseTraceData(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("trace", out var traceEl))
            {
                return JsonSerializer.Deserialize(
                    traceEl.GetRawText(),
                    AiEvaluationJsonContext.Default.TraceData);
            }

            if (root.TryGetProperty("entries", out _) || root.TryGetProperty("session_id", out _))
            {
                return JsonSerializer.Deserialize(
                    response,
                    AiEvaluationJsonContext.Default.TraceData);
            }
        }
        catch { }

        return null;
    }

    private static string BuildTraceQuery(
        string? sessionId, string traceType, int maxEntries, int? stepFrom, int? stepTo)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(sessionId))
            filters.Add($"session_id={sessionId}");
        if (traceType != "all")
            filters.Add($"type={traceType}");
        if (stepFrom.HasValue)
            filters.Add($"step_from={stepFrom.Value}");
        if (stepTo.HasValue)
            filters.Add($"step_to={stepTo.Value}");

        var filterStr = filters.Count > 0 ? $" with filters: {string.Join(", ", filters)}" : "";
        return $"Read execution trace{filterStr}. Return up to {maxEntries} entries as a JSON object "
            + "with 'trace' key containing session_id, source, total_steps, and entries array. "
            + "Each entry must include step, type, content, tool_name, tool_arguments, and timestamp.";
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int GetInt32(JsonElement root, string propertyName, int defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
            return defaultValue;
        return value;
    }

    private static int? GetNullableInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
            return null;
        return value;
    }
}
