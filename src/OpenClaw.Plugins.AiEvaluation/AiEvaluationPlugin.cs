using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Plugins.AiEvaluation.Configs;
using OpenClaw.Plugins.AiEvaluation.Tools;
using OpenClaw.PluginKit;

namespace OpenClaw.Plugins.AiEvaluation;

public sealed class AiEvaluationPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        var config = ParseConfig(context.Config);
        if (!config.Enabled)
        {
            context.Logger.LogInformation("AI Evaluation plugin disabled.");
            return;
        }

        var pool = new TestcaseSandboxConnectionPool(config, context.Logger);
        context.RegisterTool(new FetchTestcasesTool(config, pool));

        context.RegisterTool(new SandboxSendMessageTool(
            new SandboxChatConnection(config.Target, context.Logger)));

        context.RegisterTool(new TraceReadTool(
            new SandboxChatConnection(config.Trace, context.Logger)));

        context.RegisterTool(new OntologyQueryTool(
            new SandboxChatConnection(config.Ontology, context.Logger)));

        context.RegisterTool(new EvaluationReportTool(
            new SandboxChatConnection(config.EvalReport, context.Logger)));

        context.Logger.LogInformation(
            "AI Evaluation plugin registered fetch_testcases, sandbox_send_message, trace_read, ontology_query, evaluation_report tools.");
    }

    private static AiEvaluationConfig ParseConfig(JsonElement? configElement)
    {
        if (configElement is not { } element)
            return new AiEvaluationConfig();

        try
        {
            var config = JsonSerializer.Deserialize(
                element.GetRawText(),
                AiEvaluationJsonContext.Default.AiEvaluationConfig);
            return config ?? new AiEvaluationConfig();
        }
        catch
        {
            return new AiEvaluationConfig();
        }
    }
}
