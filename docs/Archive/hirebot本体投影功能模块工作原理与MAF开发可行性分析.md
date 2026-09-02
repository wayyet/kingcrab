# hirebot 本体投影功能模块工作原理与 MAF 开发可行性分析

> 分析日期：2026-07-04
> 分析对象：
> - `hirebot/back-end/HireBot.ApiService/Assets/DigitalEmployeeTemplates/employment-coach-conversation/ontology/ontology-slice.md`（本体约定文档）
> - 同模板包内 `skills/ontology-slice-extraction`（本体切片抽取技能）
> - 同模板包内 `skills/ontology-projection`（本体投影技能）
> - 对比基准：`kingcrab/src/OpenClaw.Agent` 的 MAF 运行时（参见《kingcrab项目MAF编排器与Planner任务规划功能分析.md》《hirebot模板包skill编排与kingcrab-MAF编排优缺点分析.md》）
>
> 结论先行：
> 1. `ontology/` 目录本身只有一份 20 行的约定文档；真正的功能主体是 **ontology-slice-extraction（资料→切片）** 和 **ontology-projection（切片→按技能投影）** 两个下游技能，它们构成一条"两段式语义提炼流水线"，产物是 skill-generation 的数据契约。
> 2. 这条流水线的编排、校验、容错逻辑**全部写在提示词（SKILL.md）里**，靠模型自觉执行；确定性校验下沉到随包 Python/PowerShell 脚本。
> 3. **可以用 kingcrab 的 MAF 开发**，且这个模块是整个模板包里最适合代码化的部分——推荐"外层代码工作流 + 内层 LLM 语义节点"的混合架构；前提是给 kingcrab 补上进程内 `Microsoft.Agents.AI.Workflows` 图编排（当前未引用），或走已有的远程 Durable 工作流通道。

---

## 一、模块组成：三层结构

| 层 | 位置 | 角色 |
|----|------|------|
| 约定层 | `ontology/ontology-slice.md` | 声明工作区语义边界：参考模板只读、工作模板唯一可写、slice 落 `ontology/`、上传文件在 `uploads/` |
| 抽取层 | `skills/ontology-slice-extraction/` | 从上传资料中抽取"最小语义闭包"，产出 `ontology/*.slice.json` + `*.slice.md` |
| 投影层 | `skills/ontology-projection/` | 把 slice 按每个业务技能的能力域裁剪成 per-skill 投影文件，落 `ontology/projections/<skill_slug>/` |

每个技能目录都是一个自包含规范包：`SKILL.md`（给模型的执行手册）、`templates/`（JSON 模板 + JSON Schema）、`references/`（字段口径、消费指南、迁移说明）、`examples/`（ready / warning / invalid 三态样例）、`scripts/`（Python/PowerShell 确定性校验器）。

## 二、工作原理：两段式语义提炼流水线

整条链路由主技能 employment-coach-conversation 通过内部 downstream trigger 派发，阶段推进靠 `emit_artifact` 事件驱动：

```
资料阶段收口（material_handoff_summary, isTerminal: true）
  │  payload: workspace_root + items[]（含 source_path）
  ▼
① ontology-slice-extraction
  │  读取上传资料 → 构造最小语义闭包
  │  产出 <topic>.slice.json + <topic>.slice.md → ontology/
  │  发 ontology_slice_extraction_done（completed / blocked + diagnostic）
  ▼
技能定义阶段收口（skill_workorder_summary，含 skills[] + business_rules）
  ▼
② ontology-projection
  │  扫描 ontology/*.slice.json → 逐 skill 语义匹配（宁投不弃）
  │  slice × skill → 最小投影闭包 + business_rules 合并进 constraint_mappings
  │  产出 ontology/projections/<skill_slug>/<domain>.<type>.projection.json
  │  发 ontology_projection_done（含 projection_paths / skipped / diagnostic）
  ▼
③ skill-generation 消费 projection 作为数据契约，物化生成技能包
```

### 2.1 抽取层（slice-extraction）的核心机制

- **最小语义闭包**：不导出全量本体，只保留当前任务依赖的 `concepts`（概念）、`relations`（关系）、`constraints`（约束）、`sources`（可追溯依据），未决项显式写入 `ambiguities`。
- **双格式产物**：`.md` 给人评审、`.json` 给工程消费，两者必须描述同一切片；JSON 受 `TEMPLATE.schema.json` 严格校验。
- **入口门禁**：只接受来自上游的内部 payload（须含 `workspace_root`、`items` 等字段）；用户在聊天里只是提到"本体/切片"不会触发，防止绕过阶段门。
- **反造假规约**：资料只有文件名没有正文时必须 blocked，禁止写"占位 slice"；发 done 前必须逐一确认文件真实落盘（防"只在对话里描述了内容就当写入了"）。
- **有界自愈**：`source_path` 暂不可读时按 500ms 间隔重试、最长 5 秒；仅允许在 `uploads/` 内做一次窄范围文件名恢复，恢复失败即阻断，不做宽泛猜测。
- **失败也收口**：无论成功失败都发 terminal artifact，失败时 `diagnostic` 只能取 `insufficient_material` / `source_unreadable` / `scan_error` 三个枚举值，保证上游能机器化分流。

### 2.2 投影层（projection）的核心机制

- **per-skill 数据契约**：对每个已确认技能，从最匹配的 slice 中裁出 `concept_mappings` / `relation_mappings` / `constraint_mappings`，无关项进 `dropped_items`（附剔除原因），解决不了的进 `open_questions`。
- **business_rules 合并**：技能定义阶段收集的业务规则（交期口径、拆单偏好、CIP 矩阵等）直接映射为约束项，禁止对已有规则重复提问；缺口必须以"哪个 skill 缺哪条规则 + 2~5 个选项"的形式精确提问，禁止开放式追问。
- **宁投不弃**：只有 1 个 slice 时默认对所有 skill 适用（除非业务域完全无交集）；部分覆盖也投影，不因不完整而跳过；同一 slice 可被多个 skill 复用。
- **slug 不可变**：`skill_slug` 是流程确认后的业务主键，投影目录和 `intended_consumers` 必须逐字使用，发现冲突要阻断上报而不是自行改写。
- **落盘验证 + 超时降级**：发 done 前逐路径确认文件存在、JSON 完整（含 `projection_type` / `source_slice` / `concept_mappings`）；未就绪按 500ms 轮询最长 5 秒，超时则该 skill 降级进 `skipped_skills` 重新计数；零投影必须给 `diagnostic` 枚举。
- **禁止 stub**：投影文件必须是自包含完整 JSON，不允许只写 `note` + 引用路径的占位文件——那会让 skill-generation 拿到空契约。

### 2.3 一句话概括

这套模块的本质是：**用提示词实现了一个带阶段门、事件驱动、有落盘验证和降级策略的两段式 ETL 流水线，把非结构化业务资料逐步收敛成可被代码生成消费的强结构数据契约（RAG 的"提炼-固化"变体）**。

## 三、优点与缺点

### 3.1 优点

| # | 优点 | 体现 |
|---|------|------|
| 1 | 契约先行，产物强结构化 | slice 和 projection 都有 JSON Schema + 三态样例 + 校验脚本，下游消费稳定 |
| 2 | 最小闭包，控噪音控成本 | 只保留任务相关子图，避免全量本体转储污染下游上下文 |
| 3 | 双格式对齐人机两侧 | `.md` 可人工评审，`.json` 可机器消费，评审与工程不脱节 |
| 4 | 可追溯 | 每条概念/关系/约束都有 `source_ids` 回链到 `sources`，冲突显式记录不静默合并 |
| 5 | 防御性规约密集 | 入口门禁、落盘验证、禁占位/禁 stub、失败也收口、diagnostic 枚举——针对 LLM 常见"假成功"模式逐一设防 |
| 6 | 声明式、热迭代 | 改编排逻辑=改 markdown，不用改 C# 代码、不用发版；模板包可移植到任何 OpenClaw 宿主 |
| 7 | 确定性环节下沉脚本 | 结构校验交给 Python/PowerShell，不浪费 LLM token 也不依赖模型算对 |

### 3.2 缺点

| # | 缺点 | 体现 |
|---|------|------|
| 1 | 不变量无代码强制 | "发 done 前必须落盘验证""禁止改写 slug"全靠模型自觉，提示词写得再狠也只是概率保障 |
| 2 | 用提示词手写系统级容错 | 500ms 轮询 / 5 秒超时 / 窄范围文件恢复这类竞态处理本应是代码的活，让 LLM 执行既贵又不可靠 |
| 3 | token 成本高 | 两份 SKILL.md 合计约 700 行强约束文本，每次触发都要进上下文 |
| 4 | 校验是事后的 | Schema 校验发生在产物写完之后，而不是像类型系统那样在构造时就阻止非法状态 |
| 5 | 难测试、难观测 | 编排路径无法单元测试，只能靠三态样例 + 事后审计；状态散落在文件系统和 artifact 事件里，没有统一状态机可查询 |
| 6 | 规约膨胀螺旋 | 每发现一种模型跑偏就加一条"⛔ 严禁"，SKILL.md 持续变长，进一步推高成本、稀释注意力 |

## 四、能否用 kingcrab 的 MAF 开发？

**可以，而且这是模板包里最值得下沉为代码的部分。** 但要先厘清两个事实：

1. **它现在就跑在 kingcrab 的 MAF 上。** 模板包在 OpenClaw 沙箱运行，`MafAgentRuntime` 的单智能体 ReAct 循环负责执行这些 SKILL.md——所以问题不是"能不能用 MAF"，而是"要不要把编排从提示词下沉为 MAF 代码级工作流"。
2. **kingcrab 当前缺进程内图编排。** 项目引用了 `Microsoft.Agents.AI` 但未引用 `Microsoft.Agents.AI.Workflows`；多步工作流只有远程 Durable 客户端（`AgentWorkflowRegistry` + `MafDurableHttpWorkflowRunner`）。要做本方案需补依赖，或把工作流放到外部 Durable 服务。

### 4.1 推荐架构：外层代码工作流 + 内层 LLM 语义节点

流水线中真正需要 LLM 的只有两个环节（语义抽取、语义匹配映射），其余全是确定性逻辑，映射如下：

| 现状（提示词实现） | 下沉后（MAF Workflow 实现） |
|--------------------|------------------------------|
| SKILL.md 里的阶段流程描述 | Workflow 图：`ExtractionExecutor → 校验节点 → ProjectionExecutor（按 skill fan-out 并行）→ 汇总节点` |
| 入口门禁（检查 payload 字段） | 强类型输入模型 + 代码条件边，非法输入直接编译期/运行期拒绝 |
| 落盘验证、500ms 轮询、5 秒超时降级 | C# 重试策略（如 Polly），确定性执行，不再消耗 token |
| `diagnostic` 枚举、skip_reasons | C# enum，非法值写不出来 |
| Python 校验脚本 | 进程内 C# 校验器（JsonSchema.Net 或直接反序列化为强类型模型） |
| `emit_artifact` 事件 | Workflow 事件 → 复用现有 artifact 通道发给前端 |
| slug 不可变、禁 stub 等"⛔ 严禁"条款 | 代码写死，从"提示词恳求"变成"结构上不可能违反" |
| slice→concepts/relations/constraints 抽取 | 仍是 LLM Agent 节点（`ChatClientAgent` + 结构化输出，schema 约束响应） |
| skill×slice 语义匹配、business_rules 缺口提问 | 仍是 LLM Agent 节点；缺口提问通过 workflow 的人工输入节点（对应现有确认门） |

kingcrab 现有的外骨骼（`MafExecutionServiceChatClient` 的多 Provider 路由/熔断/记账、`MafToolAdapter` 的审批/沙箱/治理）可直接复用给这些 Agent 节点。

### 4.2 收益与代价

**收益**：不变量从概率保障变成代码强制（防假成功、防 stub 由类型系统兜底）；每技能投影可真正并行 fan-out；编排路径可单元测试、可断点调试；每轮省掉数百行规约文本的 token；工作流状态可查询、可观测。

**代价**：迭代从"改 markdown 即生效"变成"改 C# 发版"；模板包丧失跨宿主可移植性（现在的 skill 包扔进任何 OpenClaw 实例都能跑）；需要新增 `Microsoft.Agents.AI.Workflows` 依赖并搭进程内工作流宿主（或依赖外部 Durable 服务）；语义匹配质量本身不会因代码化而提高——LLM 节点该有的不确定性还在。

### 4.3 落地建议（渐进式，不推倒重来）

1. **第一步（低风险）**：把两个 skill 的"落盘验证 + 就绪等待 + 超时降级"抽成 kingcrab 沙箱工具（如 `verify_artifact_files`），提示词只需调用一个工具，先把最脆的容错逻辑代码化。
2. **第二步**：把 Python 校验脚本移植为网关内 C# 校验服务，slice/projection 写入即校验，非法产物当场拒绝。
3. **第三步**：引入 `Microsoft.Agents.AI.Workflows`，将 extraction→projection→generation 建成进程内图工作流，在 `AgentWorkflowRegistry` 新增一种本地 backend kind；SKILL.md 缩减为纯语义指令（只描述"怎么抽得准"，不再描述"流程怎么走"）。
4. **保留混合形态**：主对话（employment-coach 的开放式引导）继续用 skill 提示词编排——那部分需要灵活性；只有这条本体流水线值得硬化，因为它是**结构最确定、防御规约最密集、失败代价最高**的一段。

---

## 附：关键文件索引

| 文件 | 职责 |
|------|------|
| `ontology/ontology-slice.md` | 工作区语义边界约定（20 行） |
| `skills/ontology-slice-extraction/SKILL.md` | 抽取技能执行手册（约 370 行） |
| `skills/ontology-slice-extraction/templates/TEMPLATE.schema.json` | slice 结构校验 Schema |
| `skills/ontology-slice-extraction/scripts/validate-slice.py` | slice 确定性校验器 |
| `skills/ontology-projection/SKILL.md` | 投影技能执行手册（约 330 行） |
| `skills/ontology-projection/templates/PROJECTION_TEMPLATE.json` | 投影输出模板（含 mapping_policy / prompt_projection） |
| `skills/ontology-projection/scripts/validate-projection.py` | projection 确定性校验器 |
| `skills/skill-generation/scripts/materialize-consumer-projection-contract.py` | 下游消费：投影物化为 consumer contract |
| `kingcrab/src/OpenClaw.Agent/MafAgentRuntime.cs` | 当前承载这些 skill 的 MAF 单智能体循环 |
| `kingcrab/src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs` | 若下沉代码编排，本地 backend 的自然挂点 |
