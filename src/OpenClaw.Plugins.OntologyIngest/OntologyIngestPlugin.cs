using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.PluginKit;

namespace OpenClaw.Plugins.OntologyIngest;

public sealed class OntologyIngestPlugin : INativeDynamicPlugin
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public void Register(INativeDynamicPluginContext context)
    {
        var config = ReadToolingConfig(context.Config);
        context.RegisterTool(new OntologyIngestTool(config));
    }

    private static ToolingConfig ReadToolingConfig(JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } configObject)
            return new ToolingConfig();

        var toolingElement = configObject.TryGetProperty("tooling", out var nestedTooling) && nestedTooling.ValueKind == JsonValueKind.Object
            ? nestedTooling
            : configObject;

        return JsonSerializer.Deserialize<ToolingConfig>(toolingElement.GetRawText(), ConfigJsonOptions) ?? new ToolingConfig();
    }
}
