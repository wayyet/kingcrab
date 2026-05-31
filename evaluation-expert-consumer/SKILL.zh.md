# evaluation-expert-consumer（中文导读）

> 本文档是 `SKILL.md` 的中文平行版本，专为人工阅读编写。
> **契约文件（`*.projection.json` / `*.schema.json` / `contract-index.json`）的字段名与文本仍以英文为准**——因为它们是 producer/consumer 双方共同消费的合约语言，翻译 key 会破坏校验。
> 本文与 `SKILL.md` 内容必须保持同义；如发生分歧，以 `SKILL.md` 为权威。

---

## 这个 skill 是做什么的

当用户说"评估这位员工"、"跑一次绩效评估"、"打分"、"作为评估专家"时，会触发本 skill。

它执行**一条与员工角色无关、对所有员工通用的 11 步评估流水线**。每个角色之间的差异不在流程上，而是在**四层热插拔的数据**里：

- `./metrics/` —— 评估指标（一指标一文件）
- `./test-cases/` —— 测试用例（一用例一文件）
- `./runtime-drivers/` —— STEP 3 通信协议适配器（如 `ws_jwt`）
- `./simulators/` —— STEP 3 客户角色档案（评估专家 agent 用自己的 LLM 扮真人客户时使用的 system_prompt + 人设档案，**不是子进程**）

新增一个指标 = 新增一个 `*.metric.json` 文件，**不需要改任何 `*.projection.json`**。
新增一个测试用例 = 新增一个 `*.tc.json` 文件，同理。

---

## 三个 producer 依赖

本 consumer 同时绑定三个 producer skill 的契约：

| Producer | 它发布的契约 | 本 skill 在哪里读 |
|---|---|---|
| `ontology_extraction` | 工作流契约 / 评分判分 prompt 约束 / 指标选取 prompt 约束 | `contracts/projections/ontology_extraction/` |
| `metric-ontology` | `metric-catalog` projection 与 `metric.schema.json` | 契约从 `contracts/projections/metric-ontology/` 读取，数据从 `./metrics/*.metric.json` 读取 |
| `testcase-ontology` | `test-case-catalog` projection 与 `test-case.schema.json` | 契约从 `contracts/projections/testcase-ontology/` 读取，数据从 `./test-cases/*.tc.json` 读取 |

ontology_extraction 的 `contract-index.json` 顶层已经显式声明了 `upstream_producer_dependencies`，运行时会一并加载这两个上游 producer。

---

## 执行铁律（HARD RULES，违反即阻断）

评估专家这个数字员工**直接执行**每一步，**不允许生成中间脚本来代办**：

1. **禁止 ad-hoc 编排脚本（白名单制，不是黑名单）**。本 skill 包内**唯一允许**的可执行文件是 skill 创建时就已经提交的：
   - `./runtime-drivers/<driver_id>/run.py`（以及该 driver 目录下的同级文件）—— STEP 3 协议适配器
   - 未来 skill 自带的 `runtime-*/<id>/` 适配器目录

   除上述白名单外，agent **不得**在 skill 包的**任何位置**新建 `.py` / `.sh` / `.ts` / `.js` / `.mjs` / `.ipynb` / `Makefile` / `*.cmd` / `*.ps1` 文件——不在 `./runs/<eval_id>/`，不在 skill 根目录（如 `./run_scenario.py`），不在 `./scripts/`，不在 `./tools/`，**任何位置都不行**。这包括：
   - 编排 / runner / coordinator 类脚本（如 `run_scenario.py` / `run_step3.py` / `run_evaluation.py` / `runner.py` / `orchestrator.py` / `coordinator.py` / `main.py` / `eval.py`）
   - 渲染 prompt、解析 JSON、跑循环、调 LLM 接口的"辅助"脚本
   - driver 的测试桩（如 `test_driver.py`）
   - 把多个 agent 职责串起来的 shell 脚本

   如果 agent 刚把 `subprocess.Popen(... runtime-drivers/...)` 或 `proc.stdin.write(json.dumps(...))` 写进了一个自己创建的文件，就是契约违反。同样的逻辑必须以**对话中的 agent 工具调用回合**形式存在——一次 terminal 调用启动 driver，然后**每一轮**通过 `read_file` / 内联推理 / shell 写入 driver.stdin 完成一次往返。

   只要 agent 写的文件命中上述任何一类，对应 run 立即标记为 **tainted**：停止评估、不要继续打分、在 `./runs/<eval_id>/` 下（若 run 目录还不存在则在 skill 根）放 `TAINTED.md` 记录违规事实，STEP 9 必须在 `EvaluationReport.open_questions` 中明确告知。

2. **驱动器只调用，不重写**。STEP 3 必须通过 shell 启动 driver 子进程（如 `python -u runtime-drivers/<driver_id>/run.py --evaluation-context <path> --output <trace_path>`），用其 stdin/stdout 行 JSON 协议通信。Agent **不得**把 driver 模块 `import` 进 agent 自写的代码，**不得**在 driver 目录之外复刻 WebSocket / JWT / trace 写盘逻辑。

3. **模拟器就是 agent 自己的 LLM**。`./simulators/<simulator_id>/` 下的角色档案由宿主 agent 的 LLM 直接消费（与 STEP 1.5 / STEP 4 / STEP 8 / STEP 9 同一个大脑）。Agent **不得**把 simulator 当子进程启动，**不得**为它配独立的 LLM key。

4. **Per-run 目录只放数据**。`./runs/<eval_id>/` 只允许 JSON 产物（synthesized-cases / enriched-test-cases / traces / scores / reports / logs / `TAINTED.md`），**不得**包含可执行代码、agent 草稿、任何 STEP 的并行实现。

5. **确定性步骤就是确定性**。PRE / STEP 1 / STEP 2 / STEP 5 / STEP 6 / STEP 7 是纯文件扫描或算术。Agent 直接做（读文件 → 算 → 写 JSON），**不得**调 LLM、**不得**让生成的脚本代劳。

6. **LLM 步骤就在 agent 自己脑子里**。STEP 1.5 / STEP 4 / STEP 8 / STEP 9 直接调用 agent 自身的 LLM，**不得**生成 Python 脚本去 HTTP 调 LLM 接口。

如果 agent 想写 `.py` 文件，那是契约或 prompt 不清晰的信号——把疑点暴露出来，而不是去伪造一个 orchestrator。

---

## 11 步工作流（权威定义在 metric-selection.workflow-contract.projection.json）

| # | 步骤 | 类型 | 关键约束 |
|---|---|---|---|
| PRE | `loadMetricRegistry` | 确定性（deterministic） | 文件系统扫描 `./metrics/*.metric.json`；指标库为空时立即失败 |
| 1 | `resolveEmployeeAndCheckTestCases` | 确定性 | 两个职责：(a) 从 `employee_id` 解析待评测员工（产出 `role` / `scenarios` / `sop_documents`）；(b) **按 role 筛选 `metric_registry`**，产出 `selected_metrics`（下游使用的全集）和 `dropped_metrics`（审计轨迹）。两份列表必须写入 `evaluation_context.json`。然后去 `./test-cases/` 探测以设置 `test_case_status`。详见契约 S1 的 `worked_example` 与约束 **K9**。 |
| 1.5 | `parseTestCases` | LLM，**条件执行**（仅当无匹配用例时） | **用户优先的回退链。** 当 `test_case_status == 'missing'` 时，host agent **必须先询问用户**（约束 **K11**）是否能提供真实业务场景，仅当用户明确拒绝后才能退回 SOP 合成。Tier 1（高可靠）= 用户提供场景；Tier 2（低可靠，必须携 `reliability_caveat`）= SOP 推导；Tier 3 = 阻断。合成用例落在 `./runs/<eval_id>/synthesized-cases/`，**永远不写回 `./test-cases/`**。完整询问提示词 + 用户回复必须落盘到 `evaluation_context.user_consultation_log` |
| 2 | `enrichTestCases` | 确定性，**永远执行** | 给每条用例补齐 `applicable_metrics`，规则：`m 与 tc 匹配 iff role_match(m, role) AND scenario_match(m, tc.scenarios)`，其中 `*` 在 `applicable_roles` 或 `applicable_scenarios` 中是**通配符**（不是字面量字符串）。被筛的指标库必须是 `selected_metrics`（按 **K10**），不是完整的 `metric_registry`。由 `enriched_test_case.schema.json` 与契约 S2 `wildcard_semantics_note` 强制 |
| 3 | `driveEmployeeOnScenario` | **双角色**（I/O 子进程 + 宿主 agent 内嵌客户模拟） | 按回合驱动员工跑完一个用例，记录完整 `ExecutionTrace`（含 `simulator_trail`）。`runtime_driver`（`./runtime-drivers/<driver_id>/`）是**长生命周期 I/O 子进程**，通过 stdin/stdout 行 JSON 协议与员工通信；`runtime_simulator`（`./simulators/<simulator_id>/`）**不是子进程**——评估专家这个数字员工自己（用自己的 LLM 大脑，跟 STEP 1.5 / STEP 4 / STEP 8 / STEP 9 同一个）按 system_prompt 扮客户、每轮决定下一句怎么说、什么时候停。`turn_budget.hard_max_turns`（默认上限 30）是**硬天花板**。两者任一无法解析时 STEP 3 立即失败。**agent 必须把循环跑到底：先写 `end` 再关 stdin，然后等到 `{"event":"trace_written"}`（约束 K14）。仅写一次 `send` 就关 stdin 是协议违反，trace 在 STEP 4 入口会被拒收。** |
| 4 | `scoreScenario` | **LLM fan-out** | 每一对 `(test_case, metric)` 对应**一次** LLM 调用；统一 fan-out 不设例外；输出按 `metric_score.schema.json` 校验 |
|  | LOOP(3, 4) | — | 按测试用例循环，全部跑完才退出循环 |
| 5 | `aggregateAcrossScenarios` | 确定性 | 按每个指标声明的 `aggregation_strategy` 跨场景聚合。**STEP 6 之前必须落盘 `./runs/<eval-id>/aggregated_metric_scores.json`（约束 K12）** |
| 6 | `rollUpToDimensions` | 确定性 | 子指标 → 父维度。**必须落盘 `./runs/<eval-id>/dimension_scores.json`，其 key 集合必须等于 `{ m.parent_dimension for m ∈ selected_metrics }`——给 STEP 1 已剔除指标对应的父维度凭空补分是禁止的（约束 K12、K13）** |
| 7 | `redLineCheck` | 确定性，**禁止 LLM** | 纯代码：根据每个指标的 `red_line` 配置在 `observed_signals[]` 与 trace 数据上判定 pass/fail；**禁止任何 LLM 文笔解释**（约束 **K4**）。**必须落盘 `./runs/<eval-id>/red_line_check.json`（约束 K12）**。详见下文「STEP 7 红线伪代码」 |
| 8 | `buildScenarioReports` | **LLM 合成**（每场景一份，仅写文字） | 每个测试用例出**一份** `ScenarioReport`，可并行。`metric_results.score` 必须**逐字节拷贝**自上游 `MetricScore`；LLM 只写 `summary / what_went_well / what_went_wrong / improvement_points`。按 `scenario_report.schema.json` 校验 |
| 9 | `buildOverallReport` | **LLM 合成**（整次评估**仅一份**，仅写文字） | 等所有 ScenarioReport 落盘后产出 1 份 `EvaluationReport`。`dimension_scores / overall_score / red_line / passed` 必须**逐字节拷贝** STEP 6 / STEP 7 的输出；LLM 只写 `executive_summary / strengths / weaknesses / cross_scenario_patterns / improvement_plan`；**必须以路径引用方式**链接 ScenarioReport，**禁止内联**。按 `evaluation_report.schema.json` 校验 |

### STEP 1 操作手册（按 role 筛选指标）

STEP 1 有两个独立职责，两者都是确定性、inline 完成。

1. **解析员工。** 根据 `employee_id` 查到员工，把 `employee.role`、`employee.scenarios`、`employee.sop_documents` 写入 `evaluation_context.employee`。
2. **按 role 筛选 `metric_registry`**。对 PRE 加载的每条指标 `m`：
   - 若 `employee.role ∈ m.applicable_roles` 或 `"*" ∈ m.applicable_roles` → 放入 `selected_metrics`。
   - 否则 → 放入 `dropped_metrics`，记 `{ metric_code, applicable_roles, drop_reason: "role_mismatch" }`。
3. **两份列表都要写入** `evaluation_context.json`。`selected_metrics` 是 STEP 1.5 / STEP 2 / STEP 4 使用的全集，**不是** 完整的 `metric_registry`。
4. **继续之前自查**：
   - `len(selected_metrics) + len(dropped_metrics) == len(metric_registry)` ✅
   - `selected_metrics` 中每条都确实命中了 role（或通配 `*`） ✅
   - 若 `selected_metrics == []` 而 `metric_registry != []` → **block_or_escalate**（没有任何指标适用于该员工角色，不要继续往下走）✅
5. **探测 `./test-cases/`** 来设置 `test_case_status`（`ready` / `missing`）。这一步只决定 STEP 1.5 是否运行，不会改 `selected_metrics`。

**举例。** 设 `employee.role = "customer-service-ecommerce"`，指标库共有 8 条覆盖 `customer-service-ecommerce`、`after-sales-agent`、`hr-attendance`、`bid-writer`、`legal-expert`、`software-engineer`。STEP 1 正确输出应保留 **3** 条（`tool_call_correctness`（通过 `*` 通配命中）、`interaction_empathy`、`order_refund_policy_accuracy`），剔除 **5** 条（`attendance_rule_compliance`、`bid_clause_completeness`、`legal_citation_accuracy`、`code_change_risk_disclosure`、`confidentiality_boundary_compliance`）。把 8 条全部塞进 `selected_metrics` 是 `runs/eval-001/` 中观察到的 bug —— 触发 **K9**，该 run 标 tainted。

**跨步骤不变量（K10）。** STEP 2 还会按 `applicable_scenarios ∩ tc.scenarios` 进一步收窄。因此对每个 enriched 测试用例 `tc`：`tc.applicable_metrics ⊆ selected_metrics`。STEP 3 / STEP 4 必须以 `./runs/<eval_id>/enriched-cases/<tc_id>.json` 文件为权威源，**而不是** `evaluation_context.enriched_test_cases[]` 里内嵌的那份拷贝。两者必须字节相同，任何不一致都会让该 run 标 tainted。

### STEP 1.5 操作手册（先问用户，SOP 仅作为回退）

用户提供的真实业务场景是评估的**最高保真度凭据**。SOP 只告诉我们员工**应该**怎么做，并不告诉我们员工**实际**遇到什么。因此 STEP 1.5 触发时：

1. **在任何 LLM 合成之前先询问用户。** 发送一条询问消息，**不要**默默开始从 SOP 合成。模板：
   > 我即将为员工 `<employee_id>`（role=`<role>`）生成测试用例。为了让评估贴近真实业务，请提供该员工在生产环境中实际处理的代表性场景（1–7 个）。每个场景请说明：(a) 场景名称与频率；(b) 客户典型开场话术与诉求；(c) 需要员工调用的关键工具 / 查询 / 决策；(d) 隐含红线。若你明确表示「没有」「你自己合成即可」，我才会退回 SOP 合成并标 caveat。
2. **按三条分支处理用户回复：**
   - **（A）用户提供了场景** → Tier 1。以用户原文作为每个用例的种子。LLM 只负责把用户文本渲染为 `test-case.schema.json` v2.0 结构，**不允许**凭空创造用户未提及的场景类型。每个用例的 `provenance = { source: "user_provided_scenarios", reliability: "high" }`。
   - **（B）用户明确拒绝**（如「你自己合成」「没有」「skip」） → Tier 2 SOP 回退。每个用例 `provenance = { source: "synthesized_from_sop", reliability: "low", reliability_caveat: "synthesized_from_sop_only_no_user_grounding" }`。STEP 9 必须在 `open_questions` 中明示该 caveat，并软化结论语气（用「初步」「参考性」而不是「确凿」）。
   - **（C）用户部分提供**（只给 1–2 个种子，请你填剩下的） → 混合模式。用户提供部分走 Tier 1 / `reliability=high`；SOP 扩展部分走 Tier 2 / `reliability=low`。逐用例独立归因。
3. **询问记录落盘** 到 `evaluation_context.user_consultation_log = [{ asked_at, prompt, user_response, decision: "tier1" | "tier2" | "tier3" }]`，作为评估可审计证据。
4. **Tier 3（阻断）。** 若用户拒绝且 `employee.sop_documents` 为空 → block_or_escalate。**不允许**凭空造场景。
5. **每个合成用例的 `provenance` 为必填。** Schema 层强制：`{ source, reliability, reliability_caveat? }`。缺 `provenance` 的用例在写入 `./runs/<eval_id>/synthesized-cases/` 之前必须检验失败。

**反模式（会触发 K11）：**
- 检测到 `test_case_status == "missing"` 后不问用户，直接调 LLM 从 SOP 合成。
- 问了用户但在其回复之前就开始 SOP 合成。
- 把 SOP 推导用例标为 `reliability="high"` 或省略 `reliability_caveat`。
- 当 run 中存在 Tier 2 用例时，STEP 9 `open_questions` 中未出现 `synthesized_from_sop_only_no_user_grounding` 警示。

#### stop_conditions ↔ expected_tool_calls 一致性校验（K15）

在 STEP 3 开始前，**每一个**合成/enriched 用例都必须通过以下自检：

1. **若 `expected_tool_calls` 包含 `criticality="must"` 条目**，问自己：「如果那些工具从未被调用，`stop_conditions.success` 能为真吗？」若能 → 用例存在内部矛盾。重写 `stop_conditions.success` 使其要求一个透明意味着 must 工具已被触发的结果（例：`"退款申请已提交并确认订单符合退款条件"` 而非 `"获得退货指引"`）。
2. **若 `context` 含有员工必需的信息**（如 `order_reference`）**但 `opening_message` 故意未包含**，问自己：「`stop_conditions.success` 是否要求客户在对话中提供了该信息？」若否 → simulator 可能在提供信息前就判定 `goal_achieved`，产生死胡同 trace。重写 success 条件以包含信息交接步骤。
3. **可操作性闭环测试**：`stop_conditions.success` 必须描述一个客户问题**已在解决轨道上**的结果（已采取或正在执行动作），**不能**只是被动接收流程解释。模板：`"已<动词: 提交/确认/发起> + <对象: 退款申请/催派工单/订单查询结果>"`，而不是 `"获得流程说明"`。

**反例（eval-xiaofu-001 tc-004-refund-request 的 bug）。** 原始写法：
```
stop_conditions.success = "获得明确的退换货指引和流程说明"
expected_tool_calls = [query_order_status(must), query_refund_policy(must)]
context.order_reference = "ORD20240528003"  (未包含在 opening_message 中)
```
→ Simulator 看到员工列出步骤 → 第 2 轮即判 `goal_achieved` → 员工始终未拿到订单号 → 工具未调用 → 红线触发 → **评估失真、对员工不公平**。

修正后：
```
stop_conditions.success = "员工已查询订单并确认符合退款条件，或已为客户发起退货退款申请"
```
→ Simulator 必须继续对话直到员工实际查询了订单并确认资格。

#### 边界覆盖：正反例成对生成

合成用例（Tier 1 或 Tier 2）时，应用**等价类划分**来最大化决策路径覆盖：

1. **识别场景种子或 SOP 中的决策边界**：
   - 金额阈值（如「>500 元转人工」「>1000 元需经理审批」）
   - 时间限制（如「7 天内无理由退货」）
   - 类目限制（如「电子产品需质检」）
   - 客户等级门控（如「VIP 走优先通道」）

2. **对每个边界生成配对用例**：

   | 极性 polarity | 含义 | 示例（阈值=500） |
   |---|---|---|
   | `positive` | 正常/允许路径 | order_amount=350，直接批准退款 |
   | `negative` | 超出边界/受限路径 | order_amount=899，必须转接人工 |
   | `boundary` | 恰好在阈值上（可选） | order_amount=500，边界行为 |

3. **用 `paired_case_id` 互指**：正例指向反例，反例指向正例，方便审计覆盖度。

4. **不同极性 → 不同 `expected_tool_calls`**：正例可能期望 `process_refund(must)`，反例期望 `create_handoff_ticket(must)`。不同路径 → 不同必调工具 → 不同红线触发条件。

5. **标记 `polarity`**：在每个用例上设置 `polarity = "positive" | "negative" | "boundary"`。这是 schema 层可选字段，不是阻断要求。

**这是最佳实践，不是阻断约束。** 如果用户场景种子中无可识别的决策边界（如纯信息查询），生成单独不配对的用例是允许的。

### STEP 3 操作手册（按字面执行，不要写脚本）

对每个 enriched test case `tc`，agent **逐条**执行下面动作：

1. 从 `evaluation_context` 解析 `runtime_driver.driver_id` 与 `runtime_simulator.simulator_id`，任一缺失立即失败。
2. **shell 启动 driver 子进程**：
   ```
   python -u runtime-drivers/<driver_id>/run.py \
     --evaluation-context <eval_ctx_path> \
     --test-case <enriched_tc_path> \
     --output ./runs/<eval_id>/traces/<tc_id>.trace.json
   ```
   每个场景一个 driver 进程；**不要**为整次评估常驻一个 daemon。
3. **读 driver stdout 第一行**——必须是 `{"event":"ready", ...}`，否则中断该场景。
4. **Turn 0**：往 driver stdin 写第一条 `send` action。`text` 必须是 `tc.input.opening_message` 原文；`decision` 是确定性 turn-0 SimulatorDecision（**不调 LLM**）。
5. **循环直到 termination**，每一轮：
   1. 读下一行 stdout，应是 `{"event":"evaluatee_turn", ...}`（其它事件按错误处理）；
   2. 用占位符 {`customer_persona` / `goal` / `stop_conditions` / `context` / `current_emotion` / `dialog_so_far` / `effective_max_turns`} 渲染 `simulators/<simulator_id>/system_prompt.md`，**用 agent 自己的 LLM** 出 `SimulatorDecision` JSON，按 `runtime-schemas/simulator_decision.schema.json` 校验；
   3. 计算 `effective_max_turns = min(tc.turn_budget.hard_max_turns, evaluation_context.global_turn_cap or 30)`。若 `turn_index + 1 >= effective_max_turns`，直接写 `end` action（`termination.reason = "max_turns_reached"`），无视 `decision.should_continue`；
   4. 否则若 `decision.should_continue == false`，写 `end` action，`termination.reason` 按 `decision.stop_reason` 映射（`goal_achieved` → `completed_normally`；`bottom_line_violated` → `bottom_line_violated`；`deadlock_detected` / `customer_gave_up` → `deadlock_detected`）；
   5. 否则写 `send` action，把 `decision.next_utterance` 作为 `text`，并完整带上 `decision`。
6. **等到 `{"event":"trace_written", ...}`**，driver 进程退出。`./runs/<eval_id>/traces/<tc_id>.trace.json` 是 STEP 3 唯一权威产物，进入 STEP 4。
7. 收到任何 `{"event":"error", ...}` → 暴露 `detail`、中断当前场景；driver 退出前已写好 partial trace。

整个循环 agent **在对话里直接交互执行**，driver 是唯一的子进程，agent 自己的 LLM 每轮产出 `SimulatorDecision`。**STEP 3 全过程不创建任何 `.py` 文件。**

#### STEP 3 反模式（命中其一立即停手并标 tainted）

如果你正打算做下面任何一件事，**STOP**，回去重读 HARD RULE 1：

- 在 skill 任何位置创建 `run_scenario.py` / `run_step3.py` / `run_evaluation.py` / `run_full_evaluation.py` / `runner.py` / `orchestrator.py` / `coordinator.py` / `main.py` / `eval.py` / `test_driver.py` / `driver_client.py` 或任何同类命名的文件
- 在 agent 自写代码里塞入 `subprocess.Popen([...,'runtime-drivers/...'])` 后跟 `proc.stdin.write(...)` / `proc.stdout.readline()` 的函数
- 写一个 `while True:` 循环，把多轮 driver I/O 打包进一次执行
- 写一个脚本读 system_prompt 模板，再"调 LLM"的 HTTP client —— LLM 就是宿主 agent 自己，不是 HTTP endpoint
- 写一个 `.sh` / `Makefile` 把 spawn 命令和后续步骤串起来

正确形态是：**每个 agent 回合一次 shell 调用**。一次 tool call 启动 driver；之后每一个 agent 回合恰好做**一次** driver 往返（读一行 stdout、决策、写一行 stdin）。**对话本身就是 orchestrator**，不允许把它外化成脚本。

#### STEP 3 LOOP 完整性约束（K14）

driver 期望严格交替：`send → 读 evaluatee_turn → send | end`。**在写 `end` 之前关 stdin 是协议违反，不是优雅退出**。

禁止形态（每一种都会让 trace 在 STEP 4 入口被拒收，见 K14）：

- 写一次 `send` 就关 stdin → driver 输出 `termination.detail = "stdin closed before 'end' action received"`、`turns_used = 1`、`actual_tool_calls = []`。STEP 4 **绝不**对此 trace 评分。
- 写完最后一次 `end` 后忘记读 `{"event":"trace_written"}` → trace 文件可能不完整或缺失。
- 因为 LLM 「觉得对话差不多结束」就直接跳出循环、没写 `end` → 同一个 bug。

如果宿主 agent 已经写了 `send` 但因为 LLM 渲染失败没法继续往下写，**正确恢复方式**是：先写 `{"action":"end","termination":{"reason":"deadlock_detected","detail":"<原因>"}}` 然后再关 stdin，**绝不**先关 stdin。

**Trace 拒收规则**（STEP 4 与 STEP 9 都会执行）：满足下列任一即拒收：

```
termination.reason == "evaluatee_error"
AND termination.detail 包含 "stdin closed before 'end' action received"
OR  (termination.reason == "evaluatee_error" AND turns_used == 1 AND actual_tool_calls == [])
OR  (termination.reason == "max_turns_reached"
     AND turns_used < effective_max_turns
     AND simulator_trail[-1].should_continue == true)
OR  (simulator_trail 非空
     AND simulator_trail[-1].next_utterance 是非空字符串
     AND 该字符串不是 dialog_turns 中
         actor=="evaluator" 的最后一条 content)
```

**第三条**捕捉「演示模式快捷方式」的 bug：agent 自行把轮次压到低于 `effective_max_turns`（如 `detail = "Reached max turns for demonstration"`），而 simulator 仍然想继续。这是 K14 违规——agent 不得发明自己的提前终止理由。

**第四条**捕捉 **eval-soul-001 「simulator 决策了但 agent 没发出去」** 的 bug：simulator_trail 记录 `next_utterance = "订单号是 ORD…"` 且 `should_continue = false`、`stop_reason = "goal_achieved"`，但 `dialog_turns` 里客户从未真正说过这句话——因为 agent 在发最后一条 `send` 之前就关了 stdin。**只要 simulator decision 给出非空 `next_utterance`，agent 必须先写一条 `send` 携带该原文，再写 `end`——哪怕 `should_continue == false` 也一样。** 客户的末句（报订单号、说「谢谢再见」等）是对话的一部分，必须出现在 `dialog_turns` 中。

**Simulator 侧对称规则（K15 可操作性闭环）。** 在待评估员工向客户索取必要信息（如 order_number、refund_id）后的第一个决策里，simulator **不得**将 `goal_progress` 设为 `"goal_achieved"` 或将 `stop_reason` 设为 `"goal_achieved"`，除非包含该信息的客户回复已在之前某一轮被送达。在信息还锁在 `next_utterance` 里就自行宣布 goal_achieved，会被上述第四条拒收。

被拒收的 trace 让整个 run 标 tainted；受影响的 `tc_id` 必须出现在 `EvaluationReport.open_questions`。

**禁止快捷方式（K14）。** agent **不得**以「演示」「预览」「抽样」「测试」「简洁」或任何其它自发明的理由提前终止 STEP 3 循环，这些理由不能覆盖计算出来的 `effective_max_turns = min(tc.turn_budget.hard_max_turns, evaluation_context.global_turn_cap or 30)`。循环内写 `end` 的**唯三合法理由**：

1. `decision.should_continue == false`（simulator 决定停止）
2. `turn_index + 1 >= effective_max_turns`（硬预算耗尽）
3. Driver 发出 `{"event":"error"}`（不可恢复的 driver 故障）

任何其它理由都是 K14 违规，产生的 trace 会被拒收。

#### STEP 4 fan-out：没有演示模式（K16）

STEP 4 是后半段流程里**唯一**的 LLM 有边界调用步骤（参 K4）。`./runs/<eval-id>/scores/<tc_id>__<metric_code>.json` 里每一个得分文件都必须是评分 LLM 一次实际调用的原始输出——`enriched_test_cases[tc].applicable_metrics` 里每一个 `(test_case_id, metric_code)` 求应一次独立调用。

**硬红线（K16）：**

1. **禁止批量伪造。** agent **不得**从自己对 trace 和指标定义的理解里「推出」得分，也不得一次性以统一时间戳生成所有得分文件。每个 prompt 都从（i）那条 trace +（ii）那个指标定义 +（iii）rubric/red-line 配置 +（iv）单条用例的 `stop_conditions` 拼出，独立提交给评分 LLM。
2. **`scored_at` 必须是真时间戳。** `MetricScore.scored_at` 必须是 LLM 响应拿到那一刻抓取的 ISO8601 时间戳，至少精确到秒，且**不同调用之间不可能完全相同**（微秒级漂移是必然的）。
3. **重复时间戳即 tainted。** 同一 run 下多个得分文件的 `scored_at` 字段不可以完全相同。只要出现两份以上字串相等的 `scored_at`，run 标 tainted，STEP 9 必须在 `open_questions` 里列出所有重复对，严重性 `critical`。这正是 **eval-soul-001** 的 指纹：10 个 score 文件的 `scored_at` 全是 `"2026-05-29T14:30:00Z"`。
4. **得分理由必须引证。** `MetricScore.scoring_reasoning` 必须至少引用一段所评 trace 的 `dialog_turns` 或 `actual_tool_calls` 里的实际文本。只有「基于评估标准生成」「合理的演示结果」「作为典型用例」这种没有任何可观测证据的套话会被判定为伪造，该得分文件必须重新生成。
5. **禁止捷径（对称 K14）。** agent **不得**以「演示」「预览」「抽样」「示例」「时间紧」或任何其它理由跳过逐（case, metric）LLM 调用。**评分没有演示模式**——每条用例的每个指标都需要一次真实调用评分 LLM。

**校验伪代码（STEP 5 入口执行）：**

```
scored_at_set = { read(f).scored_at for f in scores/*.json }
assert len(scored_at_set) == count(scores/*.json), \
    f"K16 违规：跨 score 文件出现重复 scored_at——评分 LLM 未逐 (case, metric) 调用"
```

---

### 为什么是 fan-out 而不是一次性大 prompt

把"全部指标 + 全部规则 + 完整 trace + 输出 schema"打包进一个 prompt 会爆 token 并稀释注意力。STEP 4 改成**每对 `(test_case, metric)` 一次精简 LLM 调用**，每次只拼：

- `scoring-judgement.prompt-constraint.projection.json` 中 `applies_to_layer = per_metric_fanout_prompt` 的那一片约束
- 当前指标的 `scoring_rubric` 与 `runtime_slice_selector`
- 经过 selector 过滤的运行时数据（通常是这条用例的 expected_output + 这一场景 trace 的相关切片）
- 严格响应 schema `metric_score.schema.json`

### 为什么红线判定必须由确定性代码做

LLM 在共情/社交压力下会软化红线判定。架构里强制：
- STEP 4 LLM **只能上报观测信号**（如 `missing_required_tool_call`）；
- 最终 pass/fail 由 STEP 7 的确定性代码根据每个指标的 `red_line` 配置统一计算；
- LLM 的响应 schema 中**故意**没有 `red_line_passed` / `pass_fail` 字段，模型甚至无法返回这个判断。

### STEP 5/6/7 中间产物落盘约束（K12、K13）

三个确定性步骤每个都必须在下一步开始前把产物以 typed JSON 写入 `./runs/<eval-id>/`。STEP 9 按 K7 逐字节拷贝这三份文件，任一缺失就不准开始。

| 步骤 | 产物路径 | key 约束 |
|---|---|---|
| 5 | `./runs/<eval-id>/aggregated_metric_scores.json` | key ⊇ `{ m.metric_code for m ∈ selected_metrics }` |
| 6 | `./runs/<eval-id>/dimension_scores.json` | key **==** `{ m.parent_dimension for m ∈ selected_metrics }`（K13） |
| 7 | `./runs/<eval-id>/red_line_check.json` | 每条 `red_line` 非空的指标对应一条记录 |

**K13 —— 什么是错的。** 若 `selected_metrics` 只包含 `{interaction_empathy, order_refund_policy_accuracy, tool_call_correctness}`（即 customer-service-ecommerce 经过 STEP 1 筛选），那 `dimension_scores.json` 必须**正好**包含这三条指标 roll up 到的父维度。在没有任何选中指标 roll up 到的父维度上凭空塞 `process_compliance=87`、`problem_resolution=82` 是 **eval-xiaofu-001 编造分数 bug** —— STEP 9 的 LLM 给没有上游证据的维度造数。K13 硬阻断这种行为；STEP 9 必须拒收 key 集合是严格超集的 `dimension_scores.json`。

#### STEP 7 红线伪代码（K4）

STEP 7 是纯代码，**无 LLM、无文笔解释**。权威算法：

```
red_line_check = {}
for m in selected_metrics:
    cfg = m.red_line                     # 可能为空 → 跳过
    if cfg is None: continue
    triggered = False
    evidence = []
    if cfg.trigger_kind == "missing_required_signal":
        for tc_id, score in per_metric_scores[m.metric_code].items():
            tc    = enriched_cases[tc_id]
            trace = traces[tc_id]
            must_tools = [t for t in tc.expected_tool_calls if t.criticality == "must"]
            absent     = [t for t in must_tools if t.tool_name not in trace.actual_tool_calls]
            if absent:
                triggered = True
                evidence.append({"tc_id": tc_id, "missing": [t.tool_name for t in absent]})
    elif cfg.trigger_kind == "score_below_threshold":
        for tc_id, score in per_metric_scores[m.metric_code].items():
            if score.overall_score < cfg.threshold:
                triggered = True
                evidence.append({"tc_id": tc_id, "score": score.overall_score, "threshold": cfg.threshold})
    # ... 其它 trigger_kind 见 metric_score.schema.json
    red_line_check[m.metric_code] = {
        "trigger_kind": cfg.trigger_kind,
        "triggered": triggered,
        "evidence": evidence,
    }
```

**LLM 不允许覆盖 `triggered`。** 类似「tool_call_correctness 拿了 10/100 但红线没触发，因为 agent 有合理的替代行为」这种文笔解释属于 **K4 违规** —— eval-xiaofu-001 的 bug。STEP 9 的 LLM 可以在 `executive_summary` 文字部分把已触发的红线说出来，但 `red_line.triggered` 字段是从 `red_line_check.json` 逐字节拷贝（按 K7）。

### STEP 9 双格式输出（JSON + HTML）

STEP 9 **必须**产出两份报告文件：

| 文件 | 路径 | 用途 |
|---|---|---|
| JSON | `./runs/<eval-id>/reports/evaluation_report.json` | 机器可读，按 `evaluation_report.schema.json` 校验 |
| HTML | `./runs/<eval-id>/reports/evaluation_report.html` | 人类可读，自包含单文件报告 |

**HTML 生成流程：**

1. 加载模板文件 `./runtime-schemas/report-template.html`。
2. 收集场景数据：对每个用例，组装 `{ report: <scenario .report.json>, trace: <.trace.json>, enriched: <enriched-case .json> }`。
3. 用 `evaluation_report.json` 完整内容（JSON 字符串）替换 `{{REPORT_DATA}}`。
4. 用场景对象数组（JSON 字符串）替换 `{{SCENARIOS_DATA}}`。
5. 将 `<title>` 标签中的 `{{EMPLOYEE_NAME}}` 替换为员工显示名。
6. 将最终 HTML 写入 `./runs/<eval-id>/reports/evaluation_report.html`。

**HTML 报告特性：**
- **能力雷达图**：5 维度能力覆盖范围，同心圆参考线（0/20/40/60/80/100），灰色虚线目标值（85分），维度标签外置并注明权重
- **场景 Tab 切换**：每个用例一个 Tab，展示会话聊天历史、模拟器决策过程、工具调用（工具名 + 参数 + 结果）、指标得分、叙述分析
- **自包含**：单个 HTML 文件，仅依赖 Chart.js CDN，可直接用浏览器打开
- **可与员工绑定**：HTML 文件名可加员工 ID 后缀，作为能力评估产物归档

---

## 目录与路径

```
evaluation-expert-consumer/
├── SKILL.md                                  ← 英文权威版
├── SKILL.zh.md                               ← 本文件
├── metrics/                                  ← 数据层 ① 一指标一文件
├── test-cases/                               ← 数据层 ② 一用例一文件
├── runtime-drivers/                          ← 数据层 ③ STEP 3 通信协议适配器，热插拔
│   ├── README.md
│   └── ws_jwt/                               ← 内置：WebSocket + JWT（迁移自旧 live_evaluator）
├── simulators/                               ← 数据层 ④ STEP 3 客户角色档案（评估专家自己扮）
│   ├── README.md
│   └── customer_realistic/                   ← 内置：真实客户人设（默认；只含 simulator.json + system_prompt.md，无可执行入口）
├── runtime-schemas/                          ← 运行时数据 schema + 报告模板
│   ├── evaluation_context.schema.json
│   ├── enriched_test_case.schema.json
│   ├── execution_trace.schema.json
│   ├── metric_score.schema.json
│   ├── scenario_score.schema.json
│   ├── scenario_report.schema.json           ← 每轮报告（STEP 8 产出）
│   ├── evaluation_report.schema.json         ← 综合报告（STEP 9 产出）
│   ├── report-template.html                  ← HTML 报告模板（STEP 9 填充数据后生成最终文件）
│   ├── runtime_driver.schema.json            ← driver.json 元数据 schema
│   ├── simulator.schema.json                 ← simulator.json 元数据 schema
│   └── simulator_decision.schema.json        ← 每轮 simulator 决策 schema
└── contracts/projections/
    ├── ontology_extraction/                  ← 流程 + 选指标 + 打分 三类契约
    ├── metric-ontology/                      ← 指标库 producer 契约
    └── testcase-ontology/                    ← 用例库 producer 契约
```

### 默认路径与环境变量覆盖

| 层 | 默认路径（相对 skill 根） | 覆盖环境变量 |
|---|---|---|
| 指标数据 | `./metrics/` | `EVALUATION_METRICS_DIR` |
| 用例数据 | `./test-cases/` | `EVALUATION_TEST_CASES_DIR` |
| 单次评估产物 | `./runs/<eval_id>/` | `EVALUATION_RUN_DIR` |
| 合成用例（STEP 1.5 输出） | `./runs/<eval_id>/synthesized-cases/` | 由运行目录派生 |
| 运行时驱动器（STEP 3 协议适配器） | `./runtime-drivers/` | `EVALUATION_DRIVERS_DIR` |
| 选用的驱动器 id | （无默认；必须在 `evaluation_context.runtime_driver.driver_id` 中显式指定） | `EVALUATION_DRIVER_ID` |
| 用户模拟器（STEP 3 客户角色档案，宿主 agent 用自己的 LLM 扮，不是子进程） | `./simulators/` | `EVALUATION_SIMULATORS_DIR` |
| 选用的模拟器 id | （无默认；必须在 `evaluation_context.runtime_simulator.simulator_id` 中显式指定） | `EVALUATION_SIMULATOR_ID` |
| 单场景对话硬上限 | 每个 `*.tc.json` 的 `turn_budget.hard_max_turns`；缺失则回退到 `evaluation_context.global_turn_cap`（默认 30） | — |

---

## 10 份运行时 schema（runtime-schemas/）

这一层**不是契约**，只是运行时数据形状，用于让每一步都能校验自己的输入输出。**永远不允许把这里的数据写回 `contracts/projections/**`**。

| Schema | 产出步骤 | 消费步骤 | 落盘位置 |
|---|---|---|---|
| `evaluation_context.schema.json` | STEP 6 `materializeEvaluationContext`（确定性） | STEP 4 fan-out / STEP 5–9 | `./runs/<eval_id>/evaluation_context.json` |
| `enriched_test_case.schema.json` | STEP 2 `enrichTestCases`（确定性，永远执行） | STEP 3 / STEP 4 | `./runs/<eval_id>/enriched-cases/<test_case_id>.json` |
| `execution_trace.schema.json` | STEP 3 `driveEmployeeOnScenario`（含 `simulator_trail`） | STEP 4 fan-out / STEP 8 | `./runs/<eval_id>/traces/<test_case_id>.trace.json` |
| `metric_score.schema.json` | STEP 4 单次 fan-out LLM 调用 | STEP 5 / STEP 7 / STEP 8 | `./runs/<eval_id>/scores/<test_case_id>__<metric_code>.json` |
| `scenario_score.schema.json` | STEP 4 后置聚合（确定性） | STEP 5 / STEP 7 / STEP 8 | `./runs/<eval_id>/scenarios/<test_case_id>.json` |
| `scenario_report.schema.json` | STEP 8 `buildScenarioReports`（LLM 合成，仅写文字） | STEP 9 | `./runs/<eval_id>/reports/scenarios/<test_case_id>.report.json` |
| `evaluation_report.schema.json` | STEP 9 `buildOverallReport`（LLM 合成，仅写文字） | 评估流程外部消费者 | `./runs/<eval_id>/reports/evaluation_report.json` |
| `runtime_driver.schema.json` | 驱动器作者（写 `driver.json`） | STEP 3 驱动器加载器 | `./runtime-drivers/<driver_id>/driver.json`（**不在** `./runs/` 下） |
| `simulator.schema.json` | 模拟器作者（写 `simulator.json`） | STEP 3 模拟器加载器 | `./simulators/<simulator_id>/simulator.json`（**不在** `./runs/` 下） |
| `simulator_decision.schema.json` | 模拟器入口（每轮一次） | STEP 3（直接消费 + 落盘到 `execution_trace.simulator_trail`） | 不单独落盘，嵌入 trace |

特别注意：
- `metric_score.schema.json` **故意不含** `red_line_passed`，红线判定一律走 STEP 7。
- `enriched_test_case.schema.json` 强制 `applicable_metrics` 非空，是 STEP 2 永远执行这条规则的物理强制。
- 合成用例必须落在 `./runs/<eval_id>/synthesized-cases/`，**不能污染 `./test-cases/`**（正式目录）。
- **报告分两层**：STEP 8 每个测试用例出 1 份 `ScenarioReport`；STEP 9 整次评估只出 1 份 `EvaluationReport`。STEP 9 必须以路径**引用** ScenarioReport，**禁止内联**。
- **报告中的数字字段必须是拷贝、不允许重算**：`ScenarioReport.metric_results[].score` 与 `EvaluationReport` 的 `per_metric_final_scores / dimension_scores / overall_score / red_line / passed` 必须与上游 `MetricScore` / STEP 5 / STEP 6 / STEP 7 的输出**逐字节相同**。STEP 8 / STEP 9 的 LLM 只能写散文。
- **STEP 3 通信协议是热插拔层，不是契约**：与被评测员工通信的代码（WebSocket / HTTP / stdio / mock）只能放在 `./runtime-drivers/<driver_id>/`。每个驱动器要发布一份 `driver.json`（按 `runtime_driver.schema.json` 校验），输出必须是合法的 `ExecutionTrace`。驱动器**禁止**含评估逻辑、**禁止**被任何 `*.projection.json` 引用、**禁止**作为缺省回退（`runtime_driver.driver_id` 缺失时 STEP 3 直接失败）。
- **STEP 3 是双角色（异构执行）**：驱动器**是**长生命周期子进程（I/O 通道，stdin/stdout 行 JSON 协议）；模拟器**不是**子进程，而是评估专家这个数字员工**用自己的 LLM 大脑**（跟 STEP 1.5 / STEP 4 / STEP 8 / STEP 9 是同一个）扮的客户。LLM 扮客户的 system_prompt + 人设档案只能放在 `./simulators/<simulator_id>/`。每个模拟器要发布一份 `simulator.json`（按 `simulator.schema.json` 校验），每轮宿主 agent 产出的决策必须是合法的 `SimulatorDecision`（按 `simulator_decision.schema.json` 校验）。模拟器**禁止**给员工打分、**禁止**提及指标、**禁止**判红线；目录中**禁止**放任何可执行入口（没有 `decide.py` / `entry`）。`runtime_simulator.simulator_id` 缺失时 STEP 3 直接失败。
- **会话轮次硬上限**：每个测试用例可在自己的 `turn_budget.hard_max_turns` 中声明（不超过 200）；缺省回退到 `evaluation_context.global_turn_cap`（默认 30）。宿主 agent 即便想 `should_continue=true` 也**不能突破**该上限——撞顶后必须发 `{"action":"end","termination":{"reason":"max_turns_reached",...}}` 给驱动器。

---

## 5 个固定父维度（不可改）

这 5 个名字被**冻结**，让红线阈值在子指标演化时仍然稳定。新加子指标通过 `metric.parent_dimension` 字段挂上来。

1. `functional_completeness`（默认权重 0.25）
2. `interaction_quality`（默认权重 0.20）
3. `process_compliance`（默认权重 0.20）
4. `problem_resolution`（默认权重 0.15）
5. `tool_call_correctness`（默认权重 0.20）

### 内置红线阈值

任意一条命中即整体不通过：

- `tool_call_correctness = 0`（必须调用的工具在 trace 中没有匹配记录）
- `process_compliance ≤ 30`
- `interaction_quality ≤ 30`
- `functional_completeness ≤ 40`

新指标可以在自己的 `*.metric.json` 里声明 `red_line` 块，STEP 7 会与上述默认值取并集。

### 内置通过线

- 加权总分 ≥ 70
- 5 个父维度每一项都 ≥ 60
- 没有任何红线触发

---

## 路由表（内置 customer-service-ecommerce 模板）

| 员工模板 | 主题 | 默认视图 | 触发信号 |
|---|---|---|---|
| customer-service-ecommerce | customer-service-ecommerce | workflow-contract | "客服" / "售后" / "退货" / "投诉" / "电商" |
| customer-service-ecommerce | metric-selection | **workflow-contract** | "测试用例" / "用例匹配" / "指标库" / "评估流程" / "fan-out" |
| customer-service-ecommerce | metric-selection | prompt-constraint | "指标" / "评分维度" / "评估标准" |
| customer-service-ecommerce | scoring-judgement | prompt-constraint | "打分" / "评分" / "严格评估" |

`metric-selection` 主题下同时挂有两个 view：纯指标选取走 `prompt-constraint`，端到端流水线走 `workflow-contract`。

---

## 阅读顺序建议

如果想从零理解这个 skill，建议按下列顺序读：

1. 本文件（中文导读）
2. `contracts/projections/ontology_extraction/contract-index.json` —— 看路由表与 producer 依赖
3. `contracts/projections/ontology_extraction/metric-selection/metric-selection.workflow-contract.projection.json` —— 看 10 步工作流的权威定义
4. `runtime-schemas/README.md` —— 看一次评估运行中数据的流向
5. `metrics/README.md` 与 `test-cases/README.md` —— 看数据层怎么写
6. `contracts/projections/metric-ontology/metric-library/metric-library.metric-catalog.projection.json` —— 看指标库契约
7. `contracts/projections/testcase-ontology/testcase-library/testcase-library.test-case-catalog.projection.json` —— 看用例库契约
8. `contracts/projections/ontology_extraction/scoring-judgement/scoring-judgement.prompt-constraint.projection.json` —— 看分层消费的打分规则
9. `contracts/projections/ontology_extraction/metric-selection/metric-selection.prompt-constraint.projection.json` —— 看选指标的硬约束 K1–K4
10. `SKILL.md` —— 与本文对齐，权威英文版

---

## 一句话总结

> 契约层只读、数据层热插拔、流程是固定的 11 步、LLM 只在 STEP 1.5 / STEP 4 / STEP 8 / STEP 9 出现，
> 红线判定永远由确定性代码做，token 通过 fan-out + `runtime_slice_selector` 控制，
> 报告分**每轮（STEP 8）+ 综合（STEP 9）**两层，数字字段一律拷贝不重算。
