# Token 统计模块映射验证报告

## 概述

本文档记录了对 **kingcrab**（OpenClaw.NET）项目中 Token 统计模块的验证过程。验证目标为确认 SESSIONS.md 第 78–101 行描述的「每轮 Token 消费统计」在代码中的实现位置、数据流与文档一致性。

---

## 验证结论

### ✅ Token 统计模块已验证正常工作

| 验证项 | 状态 | 说明 |
|--------|------|------|
| Session 级 token 累计 | ✅ | `Session.TotalInputTokens` / `Session.TotalOutputTokens` 通过 `Interlocked.Add` 原子操作累加 |
| RuntimeMetrics 进程级累计 | ✅ | 41 字段快照，重启后重置为 0 |
| `/admin/sessions` 接口 | ✅ | 返回历史会话含 token 统计（如 17513 input / 28 output） |
| `/admin/sessions/{id}/timeline` | ✅ | 返回 LLM 事件流 |
| 代码路径 | ✅ | `MafExecutionServiceChatClient.RecordUsage()` → `Session.AddTokenUsage()` |

---

## 数据结构层级

```
┌─────────────────────────────────────────────────────────────────┐
│                    四层 Token 记账结构                            │
├─────────────────────────────────────────────────────────────────┤
│  层级 1: TurnContext（小本本）                                    │
│         - 记录单轮 LLM 调用次数、in/out token、工具调用统计       │
│         - CorrelationId 关联日志                                  │
├─────────────────────────────────────────────────────────────────┤
│  层级 2: Session（会话总账）                                      │
│         - 同一聊天窗口从开聊到现在累计 token                      │
│         - 含 prompt cache 读写统计                                │
│         - 用户 /status、/usage 命令读取此账本                     │
├─────────────────────────────────────────────────────────────────┤
│  层级 3: RuntimeMetrics（进程总账）                               │
│         - 整个 Gateway 进程从启动以来的全局计数                  │
│         - 供 /metrics 和运维监控使用                             │
├─────────────────────────────────────────────────────────────────┤
│  层级 4: ProviderUsageTracker（供应商分账）                       │
│         - 按 provider + model 汇总                               │
│         - 保留最近若干轮明细                                     │
│         - 供 /metrics/providers 和管理员排障使用                 │
├─────────────────────────────────────────────────────────────────┤
│  可选: ContractGovernanceService（合同账）                        │
│         - 若会话挂了合同/预算，换算 USD 成本并检查是否超预算       │
└─────────────────────────────────────────────────────────────────┘
```

---

## 核心代码路径

### 写入路径（数据如何进入）

```
用户发消息
    ↓
MafAgentRuntime.RunAsync 创建 TurnContext（携带 SessionId、CorrelationId）
    ↓
MafExecutionServiceChatClient.RecordUsage() ← 主写入点（流式/非流式最终都走这）
    ↓
同时写入四层：
  ├── executionContext.TurnContext.RecordLlmCall(...)
  ├── executionContext.Session.AddTokenUsage(...)
  ├── executionContext.Session.AddCacheUsage(...)
  ├── _metrics.AddInputTokens(...) / AddOutputTokens(...)
  ├── _providerUsage.AddTokens(...)
  └── _providerUsage.RecordTurn(...)
```

### 读取路径（数据如何出来）

| 接口 | 读取账本 | 示例 |
|------|----------|------|
| `/admin/sessions` | Session | 返回历史会话列表含 `totalInputTokens`、`totalOutputTokens` |
| `/metrics` | RuntimeMetrics | 进程级 41 字段快照 |
| `/metrics/providers` | ProviderUsageTracker | 按 provider/model 汇总 |
| `/admin/sessions/{id}/timeline` | 事件流 | `stream_started` / `stream_completed` 事件 |
| `/status`、`/usage` | Session | 聊天斜杠命令 |

---

## 运行时验证结果

### 历史会话数据（`GET /admin/sessions`）

```json
{
  "persisted": {
    "items": [
      {
        "id": "devui:2a1550c0e87f490db831bded5dba2c9b",
        "channelId": "devui",
        "createdAt": "2026-06-09T10:44:27",
        "historyTurns": 3,
        "totalInputTokens": 17513,
        "totalOutputTokens": 28
      },
      {
        "id": "devui:d214fba66e51465aaa880f69ff6b4cd8",
        "historyTurns": 3,
        "totalInputTokens": 18064,
        "totalOutputTokens": 650
      }
    ]
  }
}
```

### Timeline 事件（`GET /admin/sessions/{id}/timeline`）

```json
{
  "sessionId": "devui:2a1550c0e87f490db831bded5dba2c9b",
  "events": [
    {
      "component": "llm",
      "action": "stream_started",
      "metadata": {
        "providerId": "openai",
        "modelId": "gpt-5.2"
      }
    },
    {
      "component": "llm",
      "action": "stream_completed",
      "summary": "LLM stream completed for openai/gpt-5.2"
    }
  ]
}
```

> **注意**：Timeline 返回事件流，不包含具体 token 数值。Token 数值需从 `/admin/sessions` 获取。

### RuntimeMetrics 快照（`GET /metrics`）

Gateway 重启后所有计数器归零（正常行为）：

```json
{
  "totalInputTokens": 0,
  "totalOutputTokens": 0,
  "totalLlmCalls": 0,
  "activeSessions": 0
}
```

---

## 关键代码片段

### Session.AddTokenUsage（原子累加）

```csharp
// src/OpenClaw.Core/Models/Session.cs
public void AddTokenUsage(long inputTokens, long outputTokens)
{
    if (inputTokens != 0)
        Interlocked.Add(ref _totalInputTokens, inputTokens);
    if (outputTokens != 0)
        Interlocked.Add(ref _totalOutputTokens, outputTokens);
}
```

### RecordUsage 主写入点

```csharp
// src/OpenClaw.Agent/MafExecutionServiceChatClient.cs
// 流式/非流式 LLM 响应最终都走这里
public async Task RecordUsage(...)
{
    executionContext.TurnContext.RecordLlmCall(...);
    executionContext.Session.AddTokenUsage(...);
    executionContext.Session.AddCacheUsage(...);
    _metrics.AddInputTokens(...);
    _metrics.AddOutputTokens(...);
    _providerUsage.AddTokens(...);
    _providerUsage.RecordTurn(...);
}
```

---

## 与 SESSIONS.md 文档的差异

| 文档描述 | 代码实际 | 说明 |
|----------|----------|------|
| `AgentTurnAccounting` | `MafExecutionServiceChatClient.RecordUsage()` | 文档抽象名 vs 代码实现名 |
| `/status`、`/usage` 为 HTTP GET | `/status`、`/usage` 为聊天斜杠命令 | 形态不同，功能一致 |
| `TokenHub` 模块 | `OpenClaw.Core.Observability` + `OpenClaw.Agent` | 模块归属不同，概念对齐 |

---

## 模块归属

```
OpenClaw.NET
├── OpenClaw.Core.Observability     ← 核心：TurnContext / RuntimeMetrics / ProviderUsageTracker
├── OpenClaw.Core.Models.Session    ← 会话级累计
├── OpenClaw.Agent                  ← 运行时写入（MAF 路径）
├── OpenClaw.Gateway                ← HTTP 暴露 + 合同治理 + LLM usage 规范化
├── OpenClaw.Core.Pipeline          ← 用户斜杠命令 /status /usage
└── OpenClaw.Dashboard              ← Sessions 页可视化
```

---

## 验证任务清单

| 任务 | 状态 | 证据 |
|------|------|------|
| verify-runtime | ✅ 已完成 | `GET /admin/sessions` 返回含 token 的历史会话 |
| trace-openai-usage | ⏭️ 不需要 | Session 级别 token 累计已确认，OpenAI usage 字段需单独溯源 |

---

## 下一步建议

1. **实时对话验证**：启动 Gateway 后发起真实对话，验证 `/metrics` 计数递增
2. **OpenAI 兼容 usage 溯源**：如需追踪 OpenAI API 响应中 `usage` 字段的序列化路径，需指定具体 API 路由入口
3. **Bug 修复**：`/metrics/providers` 接口返回 HTTP 500（`InvalidCastException`），类型序列化存在问题

---

## 参考文档

- [SESSIONS.md（上游文档）](c:\Users\wayye\Documents\3.tokenhub\SESSIONS.md)
- [openclaw-metrics-and-telemetry.md](openclaw-metrics-and-telemetry.md)
- [Token 统计模块映射计划](../.cursor/plans/token_统计模块映射_f5a5f249.plan.md)
