# OpenClaw.Plugins.AiEvaluation

AI 评估插件，基于 Skills + 跨平台脚本架构，为 hirebot（雇佣端应用系统）提供面向 AI 沙箱的自动化评估基础能力。

## 架构模式

遵循 `EmploymentCoachWorkflow` 插件的 Skills 模式：

- **Skills** — `SKILL.md` 定义评估专家角色、流程、规则，LLM 读取后按指令调用脚本
- **Scripts** — PowerShell (`.ps1`) 和 Python (`.py`) 实现实际的 WebSocket 通信与数据处理
- **Schemas** — JSON Schema 验证文件确保数据结构一致性
- **空 C# 注册** — `AiEvaluationPlugin.Register()` 为空方法体，所有功能通过技能目录暴露

## 目录结构

```
OpenClaw.Plugins.AiEvaluation/
├── AiEvaluationPlugin.cs              # 空 Register()，清单标记 skills
├── openclaw.native-plugin.json       # capabilities: ["skills"], skills: ["skills"]
├── README.md
├── skills/
│   └── ai-evaluation/
│       ├── SKILL.md                   # 评估专家技能入口
│       ├── references/                # 协议、标准、格式参考文档
│       ├── schemas/                   # JSON Schema 验证文件
│       └── examples/                  # 测试用例与报告示例
└── scripts/
    ├── Start-SandboxChat.ps1 / .py    # WebSocket 连接与会话管理
    ├── Send-SandboxMessage.ps1 / .py  # 发送消息/测试用例至沙箱
    ├── Read-SandboxTrace.ps1 / .py    # 读取沙箱执行过程跟踪
    ├── Get-ScoringCriteria.ps1 / .py  # 查询评分标准
    ├── New-EvaluationReport.ps1 / .py # 生成评估报告
    └── Invoke-AiEvaluation.ps1 / .py  # 一站式编排完整评估流程
```

## 构建

```powershell
dotnet build src/OpenClaw.Plugins.AiEvaluation/OpenClaw.Plugins.AiEvaluation.csproj
```

插件清单、`skills/` 和 `scripts/` 目录会自动复制到构建输出目录。

## 网关配置

在 `appsettings.json` 中配置：

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
          "Enabled": true
        }
      }
    }
  }
}
```

沙箱端点配置通过独立的 `evaluation-config.json` 文件管理：

```json
{
  "endpoints": {
    "generator": { "wsUrl": "ws://generator:8080/chat" },
    "target":    { "wsUrl": "ws://target-sandbox:9090/chat" },
    "trace":     { "wsUrl": "ws://trace-reader:7070/chat" },
    "ontology":  { "wsUrl": "ws://ontology-kb:6060/chat" },
    "evalReport": { "wsUrl": "ws://report-gen:5050/chat" }
  },
  "evaluation": {
    "maxTestcases": 50,
    "enableDualValidation": false,
    "timeoutSeconds": 120
  }
}
```

## 评估流程

```
评估专家 (LLM)
  │
  ├─ ① 获取测试用例 → Send-SandboxMessage → Generator 沙箱
  ├─ ② 发送至目标   → Send-SandboxMessage → Target 沙箱 (被评估对象)
  ├─ ③ 读取执行跟踪 → Read-SandboxTrace   → Trace 端点
  ├─ ④ 查询评分标准 → Get-ScoringCriteria → Ontology 知识库
  └─ ⑤ 生成评估报告 → New-EvaluationReport → EvalReport 沙箱
```

## hirebot 调用方式

hirebot 通过 Gateway 的 `shell` Tool 调用脚本：

```bash
# 完整评估流程（推荐）
pwsh ./scripts/Invoke-AiEvaluation.ps1 -ConfigPath "./evaluation-config.json" -OutputDir "./reports/"

# 或分步调用
pwsh ./scripts/Start-SandboxChat.ps1 -WsUrl "ws://target:9090/chat"
pwsh ./scripts/Send-SandboxMessage.ps1 -WsUrl "ws://target:9090/chat" -TestcaseFile "./testcases/login.json"
pwsh ./scripts/Read-SandboxTrace.ps1 -WsUrl "ws://trace:7070/chat" -SessionId "abc123"
pwsh ./scripts/Get-ScoringCriteria.ps1 -WsUrl "ws://ontology:6060/chat" -Domain "对话系统"
pwsh ./scripts/New-EvaluationReport.ps1 -WsUrl "ws://report:5050/chat" -Scores '[...]' -OutputPath "./report.json"

# Python 备选方案
python scripts/Invoke-AiEvaluation.py --config-path ./evaluation-config.json --output-dir ./reports/
```

## 依赖

- **OpenClaw.PluginKit** — 动态插件接口
- **PowerShell Core 7+** (`pwsh`) — PowerShell 脚本执行（跨平台）
- **Python 3.9+** + `websockets` 库 — Python 脚本备选执行
  ```bash
  pip install websockets
  ```

该插件为动态原生插件，需要 JIT 运行时模式。插件本身不包含任何运行时工具注册，
所有功能通过技能指令 + 脚本执行实现。
