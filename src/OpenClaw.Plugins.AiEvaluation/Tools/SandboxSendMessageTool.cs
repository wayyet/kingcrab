using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Plugins.AiEvaluation.Models;

namespace OpenClaw.Plugins.AiEvaluation.Tools;

public sealed class SandboxSendMessageTool(SandboxChatConnection connection) : IToolWithContext
{
    public string Name => "sandbox_send_message";

    public string Description =>
        "Send messages (including test cases) to the target sandbox being evaluated. "
        + "Accepts a message or a structured test case, sends it to the configured target sandbox via WebSocket, "
        + "and returns the response.";

    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "message":{"type":"string"},
        "testcase":{"type":"object"},
        "testcases":{"type":"array"}
      },
      "required":[]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: sandbox_send_message requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;

        var message = GetString(root, "message");
        var hasTestcase = root.TryGetProperty("testcase", out var singleTc);
        var hasTestcases = root.TryGetProperty("testcases", out var tcArray);

        if (string.IsNullOrWhiteSpace(message) && !hasTestcase && !hasTestcases)
            return "Error: at least one of message, testcase, or testcases is required.";

        try
        {
            var formattedMessage = BuildMessage(message, singleTc, hasTestcase, tcArray, hasTestcases);
            var response = await connection.SendMessageAsync(formattedMessage, ct);
            return response;
        }
        catch (Exception ex)
        {
            return $"Error: send message failed - {ex.Message}";
        }
    }

    private static string BuildMessage(
        string? message,
        JsonElement singleTc,
        bool hasTestcase,
        JsonElement tcArray,
        bool hasTestcases)
    {
        if (!string.IsNullOrWhiteSpace(message) && !hasTestcase && !hasTestcases)
            return message;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            if (!string.IsNullOrWhiteSpace(message))
                writer.WriteString("message", message);

            if (hasTestcases)
            {
                writer.WritePropertyName("testcases");
                tcArray.WriteTo(writer);
            }

            if (hasTestcase)
            {
                writer.WritePropertyName("testcase");
                singleTc.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
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
