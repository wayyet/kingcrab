using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

public sealed class McpNativeTool(
    McpClient client,
    string localName,
    string remoteName,
    string description,
    string parameterSchema) : ITool
{
    public string Name => localName;
    public string Description => description;
    public string ParameterSchema => parameterSchema;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (argsDoc.RootElement.ValueKind != JsonValueKind.Object)
                return $"Error: Invalid JSON arguments for MCP tool '{localName}': JSON root must be an object.";

            // 直接构建 Dictionary<string, JsonElement>，与 CallToolRequestParams.Arguments 类型匹配
            var argsDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in argsDoc.RootElement.EnumerateObject())
                argsDict[prop.Name] = prop.Value.Clone();

            // 从 AsyncLocal 执行上下文中读取当前用户标识，注入到 MCP 协议的 _meta 字段
            // _meta 是协议级别的元数据，不污染工具的 arguments 参数
            // 优先使用 OIDC 认证得到的稳定用户 ID；无认证时降级为路由级的 SenderId
            JsonObject? meta = null;
            if (MafExecutionContextScope.TryGetCurrent() is { } ctx)
            {
                meta = new JsonObject
                {
                    ["userId"]    = JsonValue.Create(ctx.Session.AuthenticatedUserId ?? ctx.Session.SenderId),
                    ["sessionId"] = JsonValue.Create(ctx.Session.Id),
                };
            }

            var callParams = new CallToolRequestParams
            {
                Name      = remoteName,
                Arguments = argsDict,
                Meta      = meta,
            };

            var response = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                RequestMethods.ToolsCall,
                callParams,
                cancellationToken: ct);

            var text = FormatResponseContent(response);
            var isError = response.IsError ?? false;
            return isError ? $"Error: {text}" : text;
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON arguments for MCP tool '{localName}': {ex.Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{localName}' failed: {ex.Message}";
        }
    }

    private static string FormatResponseContent(CallToolResult response)
    {
        var parts = new List<string>();

        foreach (var item in response.Content ?? [])
        {
            switch (item)
            {
                case TextContentBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                    parts.Add(textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource } when !string.IsNullOrEmpty(resource.Text):
                    parts.Add(resource.Text);
                    break;
                default:
                    parts.Add(JsonSerializer.Serialize(item, McpToolSerializerContext.Default.ContentBlock));
                    break;
            }
        }

        if (response.StructuredContent is { } structuredContent &&
            structuredContent.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            parts.Add(structuredContent.GetRawText());
        }

        return string.Join("\n\n", parts);
    }
}

[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContentBlock))]
[JsonSerializable(typeof(ImageContentBlock))]
[JsonSerializable(typeof(AudioContentBlock))]
[JsonSerializable(typeof(EmbeddedResourceBlock))]
[JsonSerializable(typeof(ResourceLinkBlock))]
[JsonSerializable(typeof(ToolUseContentBlock))]
[JsonSerializable(typeof(ToolResultContentBlock))]
[JsonSerializable(typeof(ResourceContents))]
[JsonSerializable(typeof(TextResourceContents))]
[JsonSerializable(typeof(BlobResourceContents))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal sealed partial class McpToolSerializerContext : JsonSerializerContext;
