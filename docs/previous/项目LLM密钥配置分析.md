# 项目 LLM API Key 配置分析

> 分析对象：kingcrab（OpenClaw.Gateway，.NET 10 LLM 网关）
> 生成日期：2026-06-26

## 一、结论速览

主 LLM 的 API Key 配置入口是 **`src/OpenClaw.Gateway/appsettings.json`** 的 `OpenClaw:Llm:ApiKey` 节点，运行时再由 **`GatewayBootstrapExtensions.ApplyEnvironmentOverrides`** 用环境变量 **`MODEL_PROVIDER_KEY`** 覆盖。最终由 **`LlmClientFactory`** 把 key 交给各 provider 客户端使用。

配置优先级（从高到低）：

1. `appsettings.json` 中 `OpenClaw:Llm:ApiKey` 写的**明文**值 → 直接使用
2. 该值若写成 `env:XXX` / `raw:XXX` 引用 → 经 `SecretResolver` 解析
3. 上述为空时 → 回退到环境变量 `MODEL_PROVIDER_KEY`

> ⚠️ 标准 ASP.NET Core 机制下，环境变量 `OpenClaw__Llm__ApiKey` 也能在第 1 步之前覆盖 JSON 值。

## 二、配置链路（按调用顺序）

### 1. 主配置文件（开发默认值）

[appsettings.json](../src/OpenClaw.Gateway/appsettings.json#L22-L26)

```json
"Llm": {
  "Provider": "deepseek",
  "Model": "gpt-5.2",
  "ApiKey": "sk-zd4vgCanFy62ZJoQpiJCdbKXE80JcRmFQWD9vdxJib2r5g44",
  "Endpoint": "https://new-api.ai4c.cn/v1"
}
```

- 当前为**明文硬编码**的 deepseek key（安全隐患，见第四节）。

### 2. 配置模型（C# 强类型）

[GatewayConfig.cs:89-95](../src/OpenClaw.Core/Models/GatewayConfig.cs#L89-L95) — `LlmProviderConfig.ApiKey` 属性绑定 `OpenClaw:Llm` 节点。

### 3. 环境变量覆盖

[GatewayBootstrapExtensions.cs:222-228](../src/OpenClaw.Gateway/Bootstrap/GatewayBootstrapExtensions.cs#L222-L228)

```csharp
private static void ApplyEnvironmentOverrides(GatewayConfig config)
{
    config.Llm.ApiKey   = ResolveSecretRefOrNull(config.Llm.ApiKey) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
    config.Llm.Model    = Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL") ?? config.Llm.Model;
    config.Llm.Endpoint = ResolveSecretRefOrNull(config.Llm.Endpoint) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT");
    config.AuthToken  ??= Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
}
```

[ResolveSecretRefOrNull:397-409](../src/OpenClaw.Gateway/Bootstrap/GatewayBootstrapExtensions.cs#L397-L409)：明文值原样返回；`env:`/`raw:` 前缀交给 `SecretResolver`；空值返回 `null`（从而回退到 `MODEL_PROVIDER_KEY`）。

### 4. 密钥引用解析器

[SecretResolver.cs](../src/OpenClaw.Core/Security/SecretResolver.cs) 支持三种写法：

| 写法 | 含义 |
|------|------|
| `env:VAR_NAME` | 读取环境变量 |
| `raw:literal` | 字面量（生产不推荐） |
| 裸字符串 | 当作环境变量名，取不到则回退为字面量 |

### 5. 实际消费点（各 provider 客户端工厂）

[LlmClientFactory.cs](../src/OpenClaw.Gateway/Extensions/LlmClientFactory.cs) 按 `Provider` 分发，从 `config.ApiKey` 取 key：

- `deepseek`、`openai` / `azure-openai`、`anthropic`、`anthropic-vertex`、`amazon-bedrock`、`gemini` / `google`、`ollama`、OpenAI 兼容（含 `aperture`）。
- key 缺失时抛 `MODEL_PROVIDER_KEY must be set for the X provider.`
- 特例：`aperture` + `tailnet-identity` 鉴权模式允许无 key。

### 6. 多模型 Profile 的密钥

[ConfiguredModelProfileRegistry.cs:233-296](../src/OpenClaw.Gateway/Models/ConfiguredModelProfileRegistry.cs#L233-L296)

- `OpenClaw:Models:Profiles[]` 中每个 profile 可有独立 `ApiKey`（经 `SecretResolver.Resolve` 解析）。
- 未配置时**继承** `OpenClaw:Llm:ApiKey`（`profile.ApiKey ?? config.Llm.ApiKey`）。
- 远程 provider 的 profile 若两处都没有 key 会报校验错误。

### 7. CLI / 初始化生成的配置

- [InitCommand.cs:113,126](../src/OpenClaw.Cli/InitCommand.cs#L113) 与 [SetupCommand.cs:15](../src/OpenClaw.Cli/SetupCommand.cs#L15)：生成配置时默认写 `apiKey: "env:MODEL_PROVIDER_KEY"`（即推荐用环境变量而非明文）。

### 8. 生产配置与容器部署

- [appsettings.Production.json](../src/OpenClaw.Gateway/appsettings.Production.json#L6-L9)：`Llm` 节点**故意不含 ApiKey**，依赖环境变量注入。
- [docker-compose.yml:15](../docker-compose.yml#L15)：`MODEL_PROVIDER_KEY=${MODEL_PROVIDER_KEY:?Set MODEL_PROVIDER_KEY}`（未设置则启动失败）。
- README 安全清单明确要求：「Set `MODEL_PROVIDER_KEY` via environment variable (never in config files)」。

## 三、其它 LLM 相关密钥（非主对话模型）

均位于 [appsettings.json](../src/OpenClaw.Gateway/appsettings.json)：

| 用途 | 配置节点 | 当前状态 |
|------|----------|----------|
| 图像理解 | `OpenClaw:Plugins:Native:ImageAnalyze:ApiKey` | ⚠️ **明文硬编码** Azure OpenAI key（约 600 行） |
| 图像生成 | `OpenClaw:Plugins:Native:ImageGen:ApiKey` | null（默认关闭） |
| Web 搜索 | `OpenClaw:Plugins:Native:WebSearch:ApiKey` | null（searxng） |
| 多模态语音 | `OpenClaw:Multimodal:ElevenLabs:ApiKey` | null |
| 流式工具 MCP | `OpenClaw:Plugins:Mcp:Servers:streaming-tools:Headers:Authorization` | ⚠️ 明文 Bearer token |
| 沙箱 | `OpenClaw:Sandbox:ApiKey` | `dev-sandbox-key` |
| 各 IM 渠道令牌 | `OpenClaw:Channels:*` | 多数用 `env:XXX` 引用 |

## 四、安全提示

1. **`appsettings.json` 中存在明文真实密钥**：主 LLM key（deepseek）与 ImageAnalyze 的 Azure OpenAI key、MCP Bearer token 均为明文。建议改为 `env:MODEL_PROVIDER_KEY` 等引用，并将真实值移到环境变量 / 密钥管理。
2. 该文件若已纳入 git 跟踪，相关密钥可能已进入历史记录，建议**轮换泄露的 key** 并清理历史。
3. 生产环境遵循 `appsettings.Production.json` + 环境变量的既有约定即可。

## 五、最快定位方法

| 目的 | 文件 |
|------|------|
| 改默认 key / provider / endpoint | `src/OpenClaw.Gateway/appsettings.json` → `OpenClaw:Llm` |
| 用环境变量注入 | 设 `MODEL_PROVIDER_KEY`（或 `OpenClaw__Llm__ApiKey`） |
| 理解覆盖与回退逻辑 | `GatewayBootstrapExtensions.ApplyEnvironmentOverrides` |
| 理解 `env:`/`raw:` 引用 | `SecretResolver.Resolve` |
| 查 key 如何到达各 provider | `LlmClientFactory` |
