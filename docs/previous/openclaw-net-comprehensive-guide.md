# OpenClaw.NET 详解：自托管 AI Agent 运行时与网关的完整指南

## 引言

在人工智能应用蓬勃发展的今天，如何构建一个既安全可靠又灵活高效的 AI Agent 运行时，成为许多开发团队面临的核心挑战。OpenClaw.NET 正是为解决这一问题而生的开源项目——它是一个完全基于 .NET 构建的自托管 AI Agent 运行时与网关，能够让开发者运行能够自主思考、调用工具并跨渠道通信的 Agent，且所有功能都集成在一个兼容 NativeAOT 的单一二进制文件中。

无论你需要的是一个个人编程助手、多渠道聊天机器人，还是生产级的 Agent 基础设施，OpenClaw.NET 都提供了完整的运行时、工具以及将一切粘合在一起的连接层。本文将基于项目的三篇核心文档，带你全面了解 OpenClaw.NET 的定位、架构与运行时模式。

## 系统概览：核心功能与定位

OpenClaw.NET 本质上一个以网关为核心的 AI Agent 平台，它在单一的首选 WebSocket 服务器之后，编排多渠道对话、可扩展的工具执行以及可插拔的 LLM 提供商。从底层来看，系统接收来自 9 种渠道适配器的消息，将其交由 Agent 运行时处理，在 LLM 调用与原生工具之间循环执行，最后将结果返回给用户。

### 九大渠道适配器

OpenClaw.NET 原生支持 9 种消息渠道，每个渠道都支持 DM 策略（open、pairing、closed）、发送者白名单以及消息去重，且 Webhook 渠道会强制执行加密签名验证：

| 渠道 | 传输方式 | 签名验证 |
|------|---------|----------|
| Telegram | Webhook | Telegram 提供 |
| WhatsApp | Webhook / Bridge | X-Hub-Signature-256 |
| Slack | Events API | HMAC-SHA256 |
| Discord | Gateway WebSocket | Ed25519 交互 Webhook |
| Teams | Bot Framework | JWT 验证 |
| Signal | signald / signal-cli | Unix Socket 认证 |
| Twilio SMS | Webhook | Twilio 签名 |
| Email | IMAP/SMTP | TLS |
| Webhooks | HTTP POST | HMAC 验证 |

### 四十八个原生工具

系统内置了 48 个原生工具，按领域分组并通过预设进行控制。这些工具涵盖文件与代码操作（shell、read_file、write_file、edit_file、git、code_exec、browser）、会话管理（sessions、sessions_spawn、sessions_search）、记忆系统（memory、memory_search、project_memory）、Web 与数据操作（web_search、web_fetch、database）、通信（message、email、calendar）、家庭与物联网（home_assistant、mqtt）、生产力（todo、automation、cron）以及媒体与 AI（image_gen、vision_analyze、text_to_speech）等多个领域。

工具预设机制允许通过组合可复用的工具组来定义不同界面的可用工具：full 预设提供所有工具无限制访问，coding 预设包含文件 I/O、Shell、Git、代码执行、浏览器和会话管理，messaging 预设包含消息、会话、记忆、配置和待办事项，minimal 预设仅提供 session_status，而 web、telegram、automation 和 readonly 则分别为特定渠道设置默认值。

### LLM 提供商支持

OpenClaw.NET 在启动时原生注册多家 LLM 提供商，开发者只需选择提供商并设置 API 密钥即可开始使用。目前支持的提供商包括 openai（默认选项，使用 OpenAI API 密钥）、anthropic/claude（通过 Anthropic API 使用 Claude 模型）、gemini/google（通过 Google AI 使用 Gemini 模型）、azure-openai（Azure 托管的 OpenAI 端点）、ollama（通过 Ollama 使用本地模型）以及 openai-compatible（兼容 OpenAI 的端点，如 Groq、Together、LM Studio、vLLM 等）。

提供商还可以通过命名模型配置来选择，这些配置在提供商层之上抽象了路由和功能，从而实现无缝回退和感知配置的路由。

### 入口点与交互界面

网关启动后（默认地址：http://127.0.0.1:18789），开发者可以通过多种界面进行交互：

- **Web UI**：在浏览器中打开 http://127.0.0.1:18789/chat
- **CLI 聊天**：dotnet run --project src/OpenClaw.Cli -- chat
- **单次运行**：dotnet run --project src/OpenClaw.Cli -- run "your prompt" --file ./somefile.md
- **终端 UI**：dotnet run --project src/OpenClaw.Cli -- tui
- **桌面伴侣**：dotnet run --project src/OpenClaw.Companion（Avalonia UI）
- **WebSocket**：ws://127.0.0.1:18789/ws 或 ws://127.0.0.1:18789/ws/live
- **REST API**：http://127.0.0.1:18789/api/integration/status
- **MCP 端点**：http://127.0.0.1:18789/mcp（JSON-RPC）
- **OpenAI 兼容**：http://127.0.0.1:18789/v1/responses

此外，对于 .NET 自动化集成，还可以使用 OpenClaw.Client 类库以强类型的方式访问集成 API 和 MCP 外观层。

## 架构设计：中枢辐射型拓扑

OpenClaw.NET 的系统架构遵循经典的中枢辐射型（Hub and Spoke）拓扑结构，其中 OpenClaw.Gateway 充当中央进程。它在启动期间组合 Agent 运行时、消息管道、渠道适配器、插件宿主以及丰富的内部服务集，然后通过统一的中间件和工作器基础设施路由所有流量。

### 项目结构与职责划分

整个解决方案被划分为 16 个项目，各司其职。网关项目是重量级的编排核心，所有其他项目均为类库或可选适配器：

**OpenClaw.Gateway** 是中央服务器进程，负责引导、DI 组合、运行时初始化、HTTP/WS 端点、工作器、管道和插件宿主。**OpenClaw.Core** 提供共享抽象与模型，包含 23+ 个接口定义。**OpenClaw.Agent** 实现 Agent 循环引擎，运行 AgentRuntime、工具执行器、断路器、上下文压缩和钩子。**OpenClaw.Channels** 包含所有 9 种渠道适配器实现。

此外还有 **OpenClaw.Client**（HTTP/WS 客户端 SDK）、**OpenClaw.Cli**（命令行界面）、**OpenClaw.Companion**（Avalonia 桌面 UI）、**OpenClaw.MicrosoftAgentFrameworkAdapter**（MAF 集成）、**OpenClaw.SemanticKernelAdapter**（SK 互操作）、**OpenClaw.PluginKit**（插件 API 契约）和 **OpenClawNet.Sandbox.OpenSandbox**（沙盒执行后端）等项目。

### 核心抽象层设计

OpenClaw.Core 定义了其他所有项目都依赖的契约词汇表，23 个以上的接口构成了系统可扩展性的骨干。主要接口包括：

- **IChannelAdapter**：渠道启动/停止监听，发送出站消息，接收入站事件
- **ITool**：工具声明名称、描述、JSON 模式，带参数执行
- **IToolHook**：工具执行前/后拦截（审计、自治、契约范围）
- **IToolSandbox**：沙盒将工具执行路由到隔离的后端
- **IMemoryStore**：内存读写笔记，支持可选的向量搜索
- **ISessionAdminStore**：会话的管理员 CRUD 操作
- **IUserProfileStore**：每用户偏好与事实存储
- **IModelProfileRegistry**：LLM 模型配置的选择与路由
- **IToolPresetResolver**：工具解析给定会话中哪些工具处于激活状态
- **IExecutionBackend**：后端外部代码后端进程管理
- **IAutomationStore**：自动化定时任务与自动化规则持久化

**设计原则——AOT 优先接口**：OpenClaw.Core 中的每个接口均专为 NativeAOT 兼容性而设计。在契约层中，不存在任何基于反射的模式、没有 Activator.CreateInstance，也没有无类型字典。JSON 序列化通过 CoreJsonContext 使用源生成的 JsonTypeInfo。这正是使得网关能够通过激进的链接裁剪编译为单一原生二进制文件的原因。

### 网关启动序列

网关遵循严格的四阶段启动流程，确保在接受任何流量之前所有依赖项均已就绪。理解此序列对于调试启动故障和扩展系统至关重要：

**阶段 1——引导**：从 appsettings.json、环境变量以及可选的配置文件覆盖中加载 GatewayConfig。验证安���要��（非回环绑定的身份验证令牌）、解析运行时模式（AOT 与 JIT），并可选地运行 --doctor 健康检查或 --health-check 存活探针。

**阶段 2——DI 组合**：将所有单例服务注册到 DI 容器中——内存存储、会话管理、提供程序注册表、模型配置、工具服务、渠道工厂、安全服务、MCP 工具注册表以及条件性的功能门控服务（MAF 实验、OpenSandbox）。

**阶段 3——运行时初始化**：最复杂的阶段。从 DI 中解析所有服务，构建渠道适配器，创建内置工具（约 25+ 个），加载 TS/JS 桥接插件和原生动态插件，注册 MCP 工具，连接 LLM 提供程序注册表，合并工具偏好解析，加载技能，创建工具钩子（审计、自治、契约范围），最后通过工厂选择器模式构建 AgentRuntime。

**阶段 4——管道激活**：配置转发头、CORS、静态文件、WebSocket 支持，启动工作器循环（最多 4 个并发工作器），启动所有渠道适配器，注册优雅关闭处理程序，并记录启动横幅。

### 消息管道与工作器

渠道适配器接收入站消息后，会将其写入 System.Threading.Channels.ChannelWriter——即 MessagePipeline。一个网关工作器池（1 到 4 个，上限为 CPU 核心数）从此通道读取数据，获取每个会话的 SemaphoreSlim 锁以防止同一会话内出现并发轮次，运行中间件管道，并调度至 Agent 运行时。

**中间件管道**在消息到达 Agent 之前，通过有序的中间件对其进行处理。始终存在两个内置中间件：

- **RateLimitMiddleware**：当 SessionRateLimitPerMinute > 0 时，拒绝超过单会话速率限制的消息
- **TokenBudgetMiddleware**：当预估或实际 Token 用量超过会话预算时拒绝该轮次

### Agent 运行时引擎

OpenClaw.Agent 中的 AgentRuntime 是系统的大脑。它实现了经典的 ReAct（推理 + 行动）Agent 循环：接收消息、构建上下文、调用 LLM、执行工具调用、将结果回传，并重复此过程，直到 LLM 生成最终的文本响应或达到迭代上限。

运行时支持两种执行模式：通过 RunAsync 进行请求/响应，以及通过 RunStreamingAsync 进行实时 Token 流式传输。两者共享相同的韧性基础设施——针对 429/5xx 错误的指数退避重试、单次调用超时、具有可配置阈值和冷却时间的断路器，以及后备模型级联。

**并行工具执行**：当启用并行工具执行且 LLM 在单轮中返回多个工具调用时，它们会通过 Task.WhenAll 并发执行。如果任何工具崩溃，关联的 CancellationTokenSource 将取消同级任务以避免资源浪费。

### 插件架构

插件系统支持三种截然不同的插件类型，各自具备不同的能力与运行时要求：

| 插件类型 | 传输方式 | 运行时模式 | 能力 |
|---------|---------|-----------|------|
| 桥接插件 | 通过 Node.js 子进程运行 TS/JS | 仅 JIT | 工具、渠道、命令、LLM 提供程序、事件订阅 |
| 原生动态插件 | 通过 AssemblyLoadContext 加载 C# DLL | AOT 和 JIT | 工具、渠道、命令、LLM 提供程序 |
| MCP 工具插件 | 模型上下文协议（基于 stdio/SSE 的 JSON-RPC） | AOT 和 JIT | 仅工具 |

桥接插件通过 plugin-bridge.mjs 脚本使用结构化的 JSON-RPC 消息与网关通信。原生动态插件实现 OpenClaw.PluginKit 中的 INativeDynamicPlugin 接口。MCP 插件通过 McpServerToolRegistry 进行注册。这三者都将工具贡献到同一个统一的工具列表中，并由偏好解析决定当名称冲突时哪个工具胜出。

## 运行时模式：AOT 与 JIT 的选择

OpenClaw.NET 能够以两种根本不同的运行时模式运行：Native AOT（预先编译）和 JIT（即时编译）。在这两种模式之间进行选择不仅是构建时需要考虑的问题——它还决定了运行时有哪些子系统、插件接口和执行后端可用。

### 模式解析机制

运行时模式在引导序列早期（即注册任何服务之前）就已确定。RuntimeModels.cs 中的 RuntimeModeResolver 实现了由 OpenClaw:Runtime:Mode 配置值驱动的三重解析策略：

- **auto**（默认值）：探测 RuntimeFeature.IsDynamicCodeSupported。如果二进制文件是通过 NativeAOT 发布的，此操作将返回 false，模式解析为 Aot。否则，解析为 JIT。
- **aot**：无条件强制使用 AOT 模式。如果运行中的二进制文件实际上支持动态代码，此操作会有意限制可用接口。
- **jit**：强制使用 JIT 模式。如果二进制文件是通过 NativeAOT 编译的，解析器将抛出 InvalidOperationException。

解析器会生成一个 GatewayRuntimeState 记录，其中包含 RequestedMode（原始配置字符串）、EffectiveMode（解析后的 GatewayRuntimeMode 枚举）和 DynamicCodeSupported（底层运行时能力标志）三个字段，并贯穿整个启动管道。

关键的一点是，对于官方 Docker 镜像和发布产物，自动检测始终是准确的。Dockerfile 中的项目文件声明了 PublishAot=true，NativeAOT 编译路径会从二进制文件中完全剥离 JIT 引擎，因此 RuntimeFeature.IsDynamicCodeSupported 会可靠地返回 false。

### 能力差异解析

一旦确定了有效模式，系统就会实例化一个运行时配置文件对象，用于声明哪些高级接口可用。GatewayRuntimeCapabilities 记录公开了两个布尔标志：

| 能力 | AOT 模式 | JIT 模式 | 控制内容 |
|------|---------|---------|----------|
| SupportsExpandedBridgeSurfaces | false | true | 扩展的插件桥接传输选项 |
| SupportsNativeDynamicPlugins | false | true | 通过 AssemblyLoadContext 进行的进程内 .NET 插件加载 |

AotRuntimeProfile 将这两项能力硬编码为 false，而 JitRuntimeProfile 则将它们全部启用。该配置文件通过扩展方法注册为单例，根据有效模式进行切换，并将其能力存储在 DI 容器中。

**桥接插件始终可用**，无论运行时模式如何。插件桥接会生成隔离的 Node.js 子进程，并专门通�� stdio 或套接字上的 JSON-RPC 进行通信。这种架构边界意味着，对于 TypeScript/JavaScript 插件而言，Gateway 进程内部的 AOT/JIT 决策是完全不可见的。

只有需要进程内程序集加载的原生 .NET 插件才会受到 AOT 限制的影响。当有效模式为 AOT 时，NativeDynamicPluginHost 声明的所有能力都会被返回为已阻止状态。

### 对部署的实际影响

对于使用官方 Docker 镜像的生产环境部署，运行时始终为 AOT。Dockerfile 通过 NativeAOT 发布到经过精简的 runtime-deps 基础镜像中，该镜像不包含 .NET SDK 或 JIT 运行时。Dockerfile 中的默认环境变量禁用了插件和 Shell 访问，这与 AOT 的安全姿态保持一致。

在 AOT 模式下运行时，以下子系统仍能完全正常工作：

- 所有内置工具（Shell、文件 I/O、Web 获取、Web 搜索等）
- 基于 Bridge 的插件加载（通过 Node.js 子进程加载的 TypeScript/JavaScript 插件）
- MCP 服务器工具注册（进程外协议）
- 所有通道适配器
- LLM 提供程序注册表
- 内存存储、会话管理、定时调度以及完整的 Agent 循环

唯一仅限 JIT 的接口是 NativeDynamicPluginHost——即通过 openclaw.native-plugin.json 清单加载的进程内 .NET 插件。

## 技术栈与工程实践

OpenClaw.NET 在技术选型上展现了现代 .NET 开发的最佳实践：

- **运行时**：.NET 10.0、C# 14，采用 InvariantGlobalization 和激进的 AOT 裁剪
- **Web 服务器**：ASP.NET Core Minimal API，使用 CreateSlimBuilder 和 WebApplication
- **LLM 抽象**：Microsoft.Extensions.AI，通过 IChatClient、ITool 和源生成 JSON
- **实时通信**：原生 ASP.NET WebSocket，30 秒保活、按会话的通道缓冲
- **配置**：appsettings.json + 环境变量，支持密钥引用（env:、raw:）
- **持久化**：基于文件或 SQLite，通过 IMemoryStore 提供程序实现可插拔
- **可观测性**：System.Diagnostics Activity 自定义 ActivitySource 和 RuntimeMetrics 计数器
- **容器**：Docker 单阶段构建，NativeAOT 发布以实现最小镜像体积

## 快速开始

最快启动的方式只需要三个环境变量和一条命令：

```bash
export OpenClaw__Llm__Provider="openai" # 或: anthropic / gemini / ollama
export OpenClaw__Llm__Model="gpt-4.1"
export MODEL_PROVIDER_KEY="sk-..."
 
dotnet run --project src/OpenClaw.Gateway -c Release
```

然后在浏览器中打开 http://127.0.0.1:18789/chat 即可开始聊天。

对于基于 Docker 的部署：

```bash
export MODEL_PROVIDER_KEY="sk-..."
export OPENCLAW_AUTH_TOKEN="$(openssl rand -hex 32)"
docker compose up -d openclaw
```

## 结语

OpenClaw.NET 代表了自托管 AI Agent 运行时的一种务实设计思路：它以 .NET 10.0 为基础，借助 NativeAOT 技术实现单文件部署；通过 AOT/JIT 双模式支持，平衡了安全性与扩展性；借助九大渠道适配器和四十八个原生工具，覆盖了主流的通信场景；而其 ReAct 循环实现的 Agent 运行时，则为 AI 能力的动态执行提供了可靠框架。

无论是希望构建个人编程助手，还是需要生产级的 Agent 基础设施，OpenClaw.NET 都提供了一套完整的技术方案。通过理解其架构设计和运行时模式的选择，开发者可以根据实际需求，在安全性与灵活性之间找到最佳平衡点。