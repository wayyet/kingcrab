# OpenClaw.Plugins.AiEvaluation

AI 评估插件，提供面向 AI 沙箱的自动化评估能力。通过 5 个 Tool 组合完成从测试用例获取、目标执行、过程采集、标准判分到报告生成的完整评估流程。

## 提供的 Tool

| Tool | 用途 | 依赖端点 |
|------|------|----------|
| `fetch_testcases` | 从 AI 沙箱获取结构化测试用例 | Generator, Validator (可选) |
| `sandbox_send_message` | 将测试用例/消息发送至被评估目标沙箱 | Target |
| `trace_read` | 读取目标沙箱的完整执行过程（思考链路、工具调用、对话内容） | Trace |
| `ontology_query` | 查询评估评分标准与维度 | Ontology |
| `evaluation_report` | 生成结构化评估报告（评分、改进建议等） | EvalReport |

## 评估流程

```
评估专家 (LLM)
  │
  ├─ ① fetch_testcases    ──→ Generator 沙箱生成测试用例
  │                          ──→ Validator 沙箱审查 (可选)
  │
  ├─ ② sandbox_send_message ──→ Target 沙箱 (被评估对象) 发送测试用例
  │
  ├─ ③ trace_read          ──→ Trace 沙箱读取执行链路
  │
  ├─ ④ ontology_query      ──→ Ontology 知识库获取评分标准
  │
  └─ ⑤ evaluation_report   ──→ EvalReport 沙箱生成评估报告
```

## 目录结构

```
OpenClaw.Plugins.AiEvaluation/
├── AiEvaluationPlugin.cs              # 插件入口，注册 5 个 Tool
├── AiEvaluationJsonContext.cs         # AOT 兼容的 JSON 序列化上下文
├── openclaw.native-plugin.json       # 插件清单
├── Configs/
│   ├── AiEvaluationConfig.cs          # 插件主配置（6 个沙箱端点）
│   └── SandboxEndpointConfig.cs       # 沙箱端点配置模型
├── Models/
│   ├── TestcaseEntry.cs               # 测试用例模型
│   ├── TestcaseFetchResult.cs         # 获取测试用例返回模型
│   ├── TestcaseSandboxStatus.cs       # 沙箱连接状态模型
│   ├── TraceData.cs                   # 执行过程跟踪数据模型
│   ├── ScoringCriteria.cs             # 评分标准模型
│   └── EvaluationReport.cs            # 评估报告模型
└── Tools/
    ├── SandboxChatConnection.cs       # 通用 WebSocket 聊天连接
    ├── TestcaseSandboxConnectionPool.cs # 多角色连接池
    ├── FetchTestcasesTool.cs          # fetch_testcases
    ├── SandboxSendMessageTool.cs      # sandbox_send_message
    ├── TraceReadTool.cs               # trace_read
    ├── OntologyQueryTool.cs           # ontology_query
    └── EvaluationReportTool.cs        # evaluation_report
```

## 构建

```powershell
dotnet build src/OpenClaw.Plugins.AiEvaluation/OpenClaw.Plugins.AiEvaluation.csproj
```

插件清单和输出 DLL 会被复制到构建输出目录。将 `Plugins:DynamicNative:Load:Paths` 指向该目录即可加载。

## 网关配置

在 `appsettings.json` 中配置动态插件加载和沙箱端点：

```json
{
  "Plugins": {
    "DynamicNative": {
      "Enabled": true,
      "Load": {
        "Paths": ["src/OpenClaw.Plugins.AiEvaluation/bin/Debug/net10.0"]
      },
      "Entries": {
        "ai-evaluation": {
          "Enabled": true,
          "Config": {
            "enabled": true,
            "generator": {
              "wsUrl": "ws://generator-sandbox:8080/chat",
              "authToken": "env:SANDBOX_AUTH_TOKEN",
              "systemPrompt": "你是一个测试用例生成器。",
              "connectTimeoutSeconds": 30,
              "requestTimeoutSeconds": 120
            },
            "validator": {
              "wsUrl": "ws://validator-sandbox:8081/chat",
              "authToken": "env:SANDBOX_AUTH_TOKEN"
            },
            "target": {
              "wsUrl": "ws://target-sandbox:9090/chat"
            },
            "trace": {
              "wsUrl": "ws://trace-reader:7070/chat"
            },
            "ontology": {
              "wsUrl": "ws://ontology-kb:6060/chat"
            },
            "evalReport": {
              "wsUrl": "ws://report-gen:5050/chat"
            },
            "timeoutSeconds": 120,
            "maxTestcasesPerFetch": 50,
            "enableDualValidation": false
          }
        }
      }
    }
  }
}
```

### 配置项说明

| 配置路径 | 类型 | 默认值 | 说明 |
|----------|------|--------|------|
| `enabled` | bool | `false` | 启用/禁用插件 |
| `generator` | object | - | 测试用例生成器沙箱端点，fetch 操作必需 |
| `validator` | object | - | 测试用例审查器沙箱端点，dual validation 必需 |
| `target` | object | - | 被评估目标沙箱端点，sandbox_send_message 必需 |
| `trace` | object | - | 执行过程跟踪沙箱端点，trace_read 必需 |
| `ontology` | object | - | 评分标准知识库沙箱端点，ontology_query 必需 |
| `evalReport` | object | - | 评估报告生成沙箱端点，evaluation_report 必需 |
| `timeoutSeconds` | int | `120` | 全局请求超时 |
| `maxTestcasesPerFetch` | int | `50` | 单次 fetch 最大获取数 |
| `enableDualValidation` | bool | `false` | fetch 后是否自动发送至 validator 审查 |

### 沙箱端点配置

每个端点 (`SandboxEndpointConfig`)：

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `wsUrl` | string | null | WebSocket 地址 |
| `authToken` | string | null | 认证令牌，支持 `env:VAR` / `raw:VALUE` 引用 |
| `systemPrompt` | string | "" | 发送给沙箱的系统提示词 |
| `connectTimeoutSeconds` | int | 30 | 连接超时（秒） |
| `requestTimeoutSeconds` | int | 120 | 请求超时（秒） |

## WebSocket 协议

各沙箱端点需要实现简单的 JSON 请求-响应协议：

### 认证握手（可选）

连接后沙箱发送 `{"type": "auth_required"}` → 插件自动发送 `{"type": "auth", "access_token": "<token>"}` → 沙箱回复 `{"type": "auth_ok"}`。

无需认证的沙箱可跳过握手，直接接收 chat 请求。

### Chat 请求（插件 → 沙箱）

```json
{"id": 12345, "type": "chat", "prompt": "生成登录页的测试用例..."}
```

### Chat 响应（沙箱 → 插件）

```json
{
  "id": 12345,
  "type": "result",
  "success": true,
  "result": {
    "text": "...",
    "testcases": [...]
  }
}
```

## Tool 调用示例

### fetch_testcases

```json
// 获取测试用例
{"action": "fetch", "prompt": "为用户注册流程生成测试用例", "max_count": 10}

// 审查已有测试用例
{"action": "validate", "testcases": [{"id": "TC001", "title": "...", "steps": ["..."], "expected_result": "..."}]}

// 直接与生成器对话
{"action": "chat", "target": "generator", "prompt": "解释覆盖率策略"}

// 查看连接池状态
{"action": "status"}
```

### sandbox_send_message

```json
// 发送纯文本消息
{"message": "请执行以下测试用例并返回结果..."}

// 发送单个测试用例
{"testcase": {"id": "TC001", "title": "登录测试", "steps": ["打开页面", "输入凭据"], "expected_result": "成功登录"}}

// 发送测试用例集
{"testcases": [{"id": "TC001", ...}, {"id": "TC002", ...}]}

// 组合发送
{"message": "执行以下回归测试", "testcases": [...]}
```

### trace_read

```json
// 获取所有执行过程
{}

// 按会话 ID 过滤
{"session_id": "abc123"}

// 仅获取工具调用
{"trace_type": "tool_calls", "max_entries": 50}

// 按步骤范围
{"step_from": 1, "step_to": 20}
```

### ontology_query

```json
// 获取所有评分标准
{}

// 按领域和类别过滤
{"domain": "对话系统", "category": "功能性测试"}

// 指定评分维度
{"dimensions": ["功能完整性", "交互质量", "响应准确性"]}
```

### evaluation_report

```json
// 提交评分数据生成报告
{
  "scores": [
    {"dimension": "功能完整性", "score": 85, "max_score": 100, "comment": "..."},
    {"dimension": "交互质量", "score": 78, "max_score": 100, "comment": "..."}
  ],
  "trace_summary": "目标沙箱完成了 15 个步骤，包含 3 次工具调用...",
  "overall_comment": "建议改善错误处理逻辑"
}
```

## 依赖

- `OpenClaw.PluginKit` — 动态插件接口
- `System.Net.WebSockets.ClientWebSocket` — WebSocket 通信（.NET 内置，无外部依赖）

该插件为动态原生插件，需要 JIT 运行时模式。
