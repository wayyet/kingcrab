# hirebot 模板包 skill 编排与 kingcrab MAF 编排优缺点分析

> 分析日期：2026-07-04
> 分析对象：
> - `hirebot/back-end/HireBot.ApiService/Assets/DigitalEmployeeTemplates/employment-coach-conversation`（雇佣教练对话包 / 生产链）
> - `hirebot/back-end/HireBot.ApiService/Assets/DigitalEmployeeTemplates/evaluation-expert`（评估专家包 / 消费·评估链）
> - `kingcrab/src/OpenClaw.Agent`（MAF 运行时，参见《kingcrab项目MAF编排器与Planner任务规划功能分析.md》）
>
> 结论先行：
> 1. 两个模板包**并不是"绕开了 kingcrab"**——它们本身就跑在 kingcrab（OpenClaw 沙箱）的 MAF 单智能体循环之上；区别在于**编排逻辑写在提示词（SKILL.md / playbook）里，而不是写在 C# 代码里**。
> 2. kingcrab 目前的 MAF 用法是**单智能体 ReAct 循环引擎**，没有引入进程内图编排（`Microsoft.Agents.AI.Workflows`）；所以"改用 kingcrab MAF 编排"首先要补上这块能力（或走远程 Durable 工作流）。
> 3. `evaluation-expert` 是**接近纯固定的流水线**，非常适合改造成 MAF 工作流；`employment-coach-conversation` 是**"固定骨架 + 开放对话内腔"的半结构化流程**，适合"外层硬状态机（代码）+ 内层软对话（skill）"的混合架构，不适合整体代码化。

---

## 一、两个模板包现在是怎么"编排"的

### 1.1 employment-coach-conversation（雇佣教练对话包 / 生产链）

**流程形态**：四阶段串行流水线，阶段之间有强制确认门（gate）：

```
资料（material）
  └─ material_handoff_ready → material_handoff_summary（terminal artifact）
  └─ R1 触发 ontology-slice-extraction，等 ontology_slice_extraction_done（completed 且 completed_slices>0）
技能（skill）
  └─ skill_definition_ready → skill_workorder_summary（terminal）
  └─ ontology_projection_ready → R2 触发 ontology-projection → ontology_projection_done（须含 projection_paths[]）
  └─ skill_generation_ready → R3 触发 skill-generation（projection_binding_confirmed: true）
外部（external）
  └─ external_system_entry_ready → external_workorder_summary（terminal）→ external_config_committed（系统层写入）
打包（ready_for_packaging）
  └─ 可选 R4 packaging-test-cases → 可选 R5 完整性审查 → 打包 zip → template_package artifact → 前端 auto-importPackage
```

**编排载体**：
- `manifest.json` 的 `stage_rules[]`：用自然语言 + `required_fields` 声明每个阶段的进入/收口条件；
- 主 skill `employment-coach-conversation/SKILL.md`（**105.8 KB**）+ 9 个 references（`stage-data-schema.md` 33.6 KB、`downstream-handoff-registry.md` 30.6 KB、`flow-constraints.md` 等）：把状态机、确认门、R1–R5 派发规则、防跑偏规则全部写成给模型看的"纸面协议"；
- `emit_artifact` 协议：阶段推进信号全部通过 artifact 事件（`*_ready` 确认门、`*_summary` terminal 收口、`*_done` 子技能完成）驱动前端胶囊和系统层派发；
- 子技能（ontology-slice-extraction / ontology-projection / skill-generation / packaging-test-cases / 完整性审查）通过 `load_skill` 渐进披露、按需加载；确定性校验下沉到随包附带的 Python/PowerShell 脚本（`validate-slice.py`、`validate_digital_employee_package.py` 48.5 KB 等）。

**关键观察**：`flow-constraints.md` 末尾有 **20 多条"质量自检"清单**，其中包括"发 `skill_workorder_progress` 之前 `ontology_slice_extraction_done` 是否已到达""不要抢跑到外部阶段"这类条目——这些本质上都是**状态机不变量**，因为没有代码强制，只能靠提示词反复叮嘱 + 模型自觉。甚至还有"上传文件 `source_path` 未出现时给 5 秒有界等待"这种**用提示词手写竞态处理**的条目。

### 1.2 evaluation-expert（评估专家包 / 消费·评估链）

**流程形态**：三段式评估流水线，`playbooks/orchestrator.md` 明确写成**状态机**：

```
INIT → PREP（Prep Agent：STEP 0~2.5，产出 run_plan.json）
     → RUN（Run Agent × N：每个测试用例一个实例，可串行/并行，产出 traces/、scores/）
     → REPORT（Report Agent：STEP 5~10，确定性汇总 + 报告 + 上传）
     → DONE / ERROR（TAINTED.md 污染生命周期）
```

**编排载体**：
- `playbooks/`：orchestrator 状态机、三个子 Agent 的职责边界（`agent-boundaries.md`）、step-00 到 step-10 的编号步骤手册、K1–K22 硬规则、超时配置（PREP 10 分钟 / 单 Run Agent 15 分钟 / REPORT 10 分钟）——**全部用 markdown 写给模型执行**；
- 子 Agent 之间**只通过文件系统传递数据**（`run_plan.json`、`traces/<tc_id>/`、`scores/<tc_id>__*.json`），配套 15 个 `runtime-schemas/*.schema.json` 做结构约束；
- 确定性环节大量下沉到 Python：`runtime-drivers/ws_jwt/run.py`（62 KB）、`report_assembler.py`、各 uploader；"deterministic rollup"（STEP 5~7）明确要求不走 LLM。

**关键观察**：这个包比雇佣教练包**结构化程度高得多**——状态迁移条件、超时、并行开关、错误分支都已经写成了准形式化规格。换句话说，`orchestrator.md` 就是一份"还没翻译成代码的工作流定义"。

### 1.3 kingcrab 侧的 MAF 现状（对比基准）

据 `docs/kingcrab项目MAF编排器与Planner任务规划功能分析.md` 及本次代码核对：

- kingcrab 引用 `Microsoft.Agents.AI` 1.11.1 + Hosting/A2A/DevUI（1.8.0-preview），**没有引用 `Microsoft.Agents.AI.Workflows`**；
- MAF 在 kingcrab 中的角色是**单智能体对话轮次编排**（`MafAgentRuntime` → `ChatClientAgent`：LLM ↔ 工具循环），外骨骼（记忆、预算、审批、治理、遥测）全部自研挂接；
- 多步骤持久化工作流只有**远程客户端**（`AgentWorkflowRegistry` + `MafDurableHttpWorkflowRunner`，HTTP 调外部 MAF Durable 服务），进程内没有图编排器；
- 没有自主任务规划器（Planner）；`Handoff:Workflows:employment-coach` 配置只是待办流转跟踪。

所以准确的对比不是"skill vs MAF 工作流"，而是：**"提示词状态机（跑在 MAF 单智能体循环上）" vs "假如把编排下沉为代码级工作流（需给 kingcrab 补进程内 MAF Workflows 或走远程 Durable）"**。

---

## 二、问题 1：用 skill 技能而非 MAF 任务编排的优缺点

### 2.1 优点

| # | 优点 | 具体体现 |
|---|------|----------|
| 1 | **零编译热更新、发布敏捷** | 流程改动 = 改 markdown/JSON，模板包独立版本化（manifest `v1.0.0`），不需要重编译、重部署 kingcrab 网关。业务流程迭代节奏与平台发布节奏解耦。 |
| 2 | **业务可读、可审** | 引导话术、决策启发式（"技能拆得太细怎么合并""强弱同事差在哪"）直接用中文写在 skill 里，领域专家能看懂、能改。代码图编排做不到这一点。 |
| 3 | **天然适配开放式对话** | 雇佣教练的核心是多轮引导：追问模糊描述、承接跑偏话题再拉回、story-driven 提问。这种"阶段内不可枚举"的交互只能靠 LLM 的语言能力，写成代码状态机反而僵硬。 |
| 4 | **打包分发模型简洁** | 包 = 纯数据（zip），沙箱按 `entry_skill` 加载即可运行；每个数字员工实例可独立定制，不产生代码分支。评估包甚至自带 runtime driver、simulator、metric 库，开箱即用。 |
| 5 | **与运行时松耦合** | 包不依赖 kingcrab 的 C# API 版本。MAF Workflows 目前仍在 preview（kingcrab 的 Hosting 包停在 1.8.0-preview），提示词协议反而是更稳定的接口。 |
| 6 | **上下文成本可控（渐进披露）** | 子技能通过 `load_skill` 按需加载，R1–R5 派发把重活拆给子技能/子 Agent；确定性校验下沉到随包脚本，不烧 token。 |
| 7 | **LLM 弥合模糊性和小故障** | 用户输入不规范、资料不齐、轻微异常时，模型能自我修复、重新引导，不需要为每个边角情况写代码分支。 |

### 2.2 缺点

| # | 缺点 | 具体体现 |
|---|------|----------|
| 1 | **状态机是"纸面约束"，无硬保证** | 阶段顺序、确认门全靠提示词"禁止/必须"+ 模型自觉。`flow-constraints.md` 20 多条自检清单、"不得抢跑外部阶段"的专门条目，本身就是对"模型可能不守规矩"的补偿。模型升级/更换后行为可能漂移。 |
| 2 | **Token 成本与上下文压力大** | 主 SKILL.md 105.8 KB + 大型 references，长会话叠加后有上下文裁剪风险（清单里专门有"裁剪后重新 load_skill"条目）。每一轮对话都在为"背诵流程规则"付费。 |
| 3 | **状态一致性弱** | 状态散落在会话历史、artifact 事件、工作区文件三处，没有类型化状态对象、没有事务性。"source_path 5 秒有界等待"就是用提示词硬扛竞态；代码编排里这是一个普通的重试策略。 |
| 4 | **可测试性差** | 能测 schema 和脚本（`test_skill_stage_contract.py` 等），但**无法单元测试"模型会遵守确认门"**。行为回归只能靠端到端评估，不确定性高。 |
| 5 | **超时/重试/并发原语靠模型自觉** | `orchestrator.md` 用 markdown 定义超时和并行开关，指望 LLM 读懂后"扮演"一个状态机。代码工作流引擎里这些是免费拿到的基础设施（checkpoint、重试、fan-out/fan-in）。 |
| 6 | **可观测与调试困难** | 出错表现为"模型没按文档做"，只能翻会话记录定位；不像类型化工作流每个节点有独立遥测、可断点重放。 |
| 7 | **演进成本随规模上升** | R1–R5 触发规则在 registry、清单、stage_rules 三处重复陈述，改一处漏一处没有编译器兜底；105 KB 的提示词文件重构风险高（"提示词面条化"）。 |
| 8 | **确定性步骤也可能走 LLM** | 虽然两个包已经把不少确定性工作下沉到脚本，但"由谁在什么时机调脚本"仍由模型决定，存在漏调/错调的可能。 |

### 2.3 小结

skill 编排的本质权衡是：**用"可靠性与工程可控性"换"迭代速度、业务可读性和对话灵活性"**。两个包的作者显然清楚这一点——大量自检清单、K 规则、terminal status 契约测试、随包校验脚本，都是在提示词体系内部尽力补可靠性短板。但补丁越多，恰恰说明这些不变量本该由代码强制。

---

## 三、问题 2：它们是固定工作流吗？能否用 kingcrab 的 MAF 开发？

### 3.1 是不是固定工作流

| 模板包 | 判定 | 依据 |
|--------|------|------|
| employment-coach-conversation | **半结构化：骨架固定，内腔开放** | 四阶段顺序、确认门、R1–R5 派发链完全固定（stage_rules 写死）；但每个阶段内部是不可枚举的多轮引导对话（追问、拉回、启发式判断），步骤数和走向由用户交互决定。 |
| evaluation-expert | **基本是固定工作流** | INIT→PREP→RUN×N→REPORT→DONE 状态机、迁移条件、超时、并行开关、错误分支全部预先定义；step-00~step-10 编号步骤手册；rollup 明确要求确定性执行。只有节点内部（驱动被评对象对话、按 metric 打分）需要 LLM 判断。 |

### 3.2 能否用 kingcrab 的 MAF 开发

**前提修正**：kingcrab 当前没有进程内图编排能力，要"用 MAF 编排"有两条路：
- ① 给 kingcrab 引入 `Microsoft.Agents.AI.Workflows`（Executor 图 + 类型化消息 + checkpoint + fan-out/fan-in + RequestInfoExecutor 人机交互端口），在 `AgentWorkflowRegistry` 里新增进程内 backend kind（该文档 §2.5 已指出这是自然挂点）；
- ② 复用现有 `MafDurableHttpWorkflowRunner`，把工作流放到外部 MAF Durable 服务跑。

在此前提下：

**evaluation-expert → 强烈适合改造为 MAF 工作流**

```
PrepExecutor（可内嵌 AIAgent 做 metric 甄选/测试用例增强）
   │ run_plan（类型化消息，不再是"写文件即视为完成"）
   ├─ fan-out ─► RunExecutor × N（每 TC 一个，Workflows 原生并发 + 单节点超时/重试）
   │                │ trace + scores（类型化）
   └─ fan-in ──► ReportExecutor（确定性 rollup 纯代码实现，零 LLM 成本）
                    └─ UploadExecutor
```

改造收益直接对应 2.2 节的每条缺点：
- K1–K22 规则从"模型自觉"变成**代码断言**；
- 并行、超时、失败重试、TAINTED 恢复由工作流引擎接管（checkpoint 可断点续跑，不再靠 `TAINTED.md` 人工恢复）；
- STEP 5~7 确定性汇总不再消耗任何 token；
- 每个节点有独立遥测，接入 kingcrab 现有 metrics/tracing 体系。
成本：评估逻辑进入 C# 代码，metric/simulator 的热更新能力需要保留在数据文件层（这部分包里本来就是 JSON/markdown，可以继续外置）。

**employment-coach-conversation → 适合"混合架构"，不适合整体代码化**

- **外层硬状态机下沉为代码**：四阶段迁移、R1–R5 派发、artifact 确认门（`*_ready`/`*_summary`/`*_done`）改由工作流引擎强制。MAF Workflows 的 `RequestInfoExecutor`/InputPort 正好对应"确认门等用户点头"这种 human-in-the-loop 语义。收益：`flow-constraints.md` 里 20 多条自检清单中约 2/3（顺序类、门禁类、触发类）直接变成代码保证，主 SKILL.md 可以大幅瘦身，只保留对话引导内容。
- **内层对话保持 skill**：阶段内的引导话术、启发式、跑偏处理继续用提示词承载（每个阶段一个 AIAgent 节点 + 对应 skill），保住热更新和业务可读性。
- **整体代码化不可取**：对话内腔不可枚举，硬编码会把产品最有价值的"教练感"写死。

### 3.3 一条值得认真考虑的中间路线：声明式工作流合约

注意到两个包里已经存在 `workflow-contract-projection.json`（本体投影产出的工作流合约），而且 `manifest.json` 的 `stage_rules[]` 事实上已经是"半声明式状态机"。与其把每个模板的编排写成 C# 类，不如在 kingcrab 里实现**一个通用的工作流合约解释器**：

- 模板包继续以纯数据分发，新增/强化一份机器可读的工作流定义（阶段、门、触发、超时、required_fields）；
- kingcrab 运行时读取该定义，**在代码层强制**阶段迁移与门禁（拒绝模型抢跑发出的越阶 artifact），LLM 只负责阶段内对话；
- 这样同时保住"包=数据、热更新、业务可读"（skill 路线优点 1/2/4/5）和"状态机硬保证"（消除缺点 1/3/5），且所有模板共享同一个解释器，不随模板数量增加代码量。

### 3.4 建议优先级

| 优先级 | 动作 | 理由 |
|--------|------|------|
| P0 | evaluation-expert 的 orchestrator + 确定性步骤（rollup/上传/超时/并行）下沉为代码工作流（进程内 Workflows 或远程 Durable） | 它已经是固定流水线，纸面状态机在这里纯粹是成本和风险，没有换来任何灵活性收益 |
| P1 | kingcrab 实现通用"工作流合约解释器"，强制 stage_rules 门禁 | 一次投入，所有模板受益；雇佣教练包不动内容即可获得硬保证 |
| P2 | 雇佣教练包按混合架构瘦身 SKILL.md（外层规则移出，保留对话引导） | 依赖 P1；降低 token 成本与提示词维护负担 |

---

## 四、关键文件索引

| 文件 | 说明 |
|------|------|
| `hirebot/.../employment-coach-conversation/manifest.json` | 四阶段 stage_rules（半声明式状态机） |
| `hirebot/.../employment-coach-conversation/skills/employment-coach-conversation/SKILL.md` | 主编排提示词（105.8 KB） |
| `hirebot/.../references/flow-constraints.md` | 防偏差规则 + 20 余条状态机自检清单 |
| `hirebot/.../references/downstream-handoff-registry.md` | R1–R5 系统层派发注册表 |
| `hirebot/.../evaluation-expert/skills/evaluation-expert-consumer/playbooks/orchestrator.md` | 三段式评估状态机（markdown 版工作流定义） |
| `hirebot/.../evaluation-expert/skills/evaluation-expert-consumer/playbooks/agent-boundaries.md` | Prep/Run/Report 三 Agent 职责边界 |
| `hirebot/.../evaluation-expert/skills/evaluation-expert-consumer/runtime-drivers/ws_jwt/run.py` | 确定性驱动脚本（62 KB） |
| `kingcrab/src/OpenClaw.Agent/MafAgentRuntime.cs` | MAF 单智能体轮次编排运行时 |
| `kingcrab/src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs` | 远程 Durable 工作流客户端（现有唯一多步编排通道） |
| `kingcrab/docs/kingcrab项目MAF编排器与Planner任务规划功能分析.md` | kingcrab MAF 现状详析（本文对比基准） |
