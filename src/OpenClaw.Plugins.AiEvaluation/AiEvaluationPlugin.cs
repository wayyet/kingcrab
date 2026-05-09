using OpenClaw.Plugins.AiEvaluation.Tools;
using OpenClaw.PluginKit;

namespace OpenClaw.Plugins.AiEvaluation;

public sealed class AiEvaluationPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        context.RegisterTool(new EvaluationScoreTool());
        context.RegisterTool(new EvaluationReportTool());
    }
}
