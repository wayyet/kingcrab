# SkillProjection Contracts 设计说明

本文总结 `SkillProjection Contracts` 从“仅存在于 skill 文档中的约束入口”演进为“接入 skill runtime 的真实运行时路由机制”的完整过程，覆盖背景、设计目标、数据模型、运行时流程、多 producer 支持、producer precedence、测试覆盖和当前限制。

相关文档：

- [docs/ncrew-ontology-slice-to-skill-method.md](docs/ncrew-ontology-slice-to-skill-method.md)：从 `ncrew-ontology` skill 构建业务本体切片，并将本体切片应用到具体业务 Skill 的过程与方法。
- [docs/ncrew-ontology-first-skill-practical-template.md](docs/ncrew-ontology-first-skill-practical-template.md)：从零为一个新业务 Skill 落第一份 slice、第一条 topic 和第一个 projection 的实操模板。

## 1. 背景与问题

`software-developer` skill 目录下已经存在一套 projection contracts 资产：

- `contracts/projections/<producer>/contract-index.json`
- 主题目录下的 `*.projection.json`
- 配套的 `README.md` / `REVIEW.md`

这些资产在设计上已经表达了：

- 如何从用户请求中推断 topic
- 如何从 topic 中继续推断 target view
- 哪些 projection 是 `READY`
- 哪些 projection 因 open questions 或 unresolved item policy 需要阻断

但最初它们只被当作静态文档或人工 prompt 参考，而没有真正进入 skill loader、runtime prompt 生成或 request-time 路由流程。

直接结果是：

- projection contracts 不能被自动发现和绑定
- skill runtime 不能按用户请求动态切 topic / target view
- projection 的 blocking checks 不会影响运行时 skill 可见性
- 多 producer contract 无法并存

## 2. 设计目标

本轮设计的目标分为四步：

1. 把 projection contract 变成 skill runtime 的真实输入，而不是手工阅读材料。
2. 采用最小接线方案，把 `topic selection -> target view selection -> projection load -> blocking checks` 接到现有 runtime。
3. 支持 loader 自动发现 skill 目录下的 projection contract index，而不是在测试里手工构造。
4. 把单 producer 单入口限制升级为多 producer 列表，并在同分场景下支持显式 precedence。

约束条件也很明确：

- topic / target view 的选择依赖当前 user message，因此不能在 reload 时静态固化。
- 改造应尽量复用现有 `SkillDefinition`、`SkillLoader`、`SkillPromptBuilder`、`MafAgentRuntime`。
- 如果无法安全选出 route，必须阻断或隐藏该 skill，而不是伪造 projection。

## 3. 设计演进

### 3.1 第一阶段：确认 projection contracts 未进入 runtime

起点是对 `software-developer/SKILL.md` 和 projection 目录的使用情况做审查，确认当时的实现并没有：

- 在 loader 中绑定 `contract-index.json`
- 在 runtime 中根据 user message 做 route selection
- 在 system prompt 中追加 projection prompt patch
- 在 open questions 场景下阻断 skill

这个阶段的结论是：projection assets 有内容，但没有 runtime 接线。

### 3.2 第二阶段：最小 runtime 接线

最小可用方案是把路由逻辑放到 `MafAgentRuntime.GetSystemPrompt(session, userMessage)` 对应的 per-turn prompt 生成路径，而不是 reload 或 skill load 阶段。

原因是：

- skill reload 只能看到 skill 文件，不知道当前用户请求
- projection route 是 request-dependent 的，而不是 skill-static 的
- topic / target view 的模糊度必须在当前 turn 决定

因此设计选择为：

1. `SkillLoader` 只负责绑定 projection contract 元数据。
2. `SkillProjectionResolver` 在 request time 解析 route。
3. `MafAgentRuntime` 在每个 turn 动态决定：
   - skill 原样保留
   - skill 指令追加 projection patch
   - skill 被隐藏并记录 blocked route

### 3.3 第三阶段：loader 自动发现与诊断

在最小 runtime 接线完成后，继续让 loader 自动从 skill 目录下发现：

`contracts/projections/**/contract-index.json`

并把 discovery 结果记录到 `SkillDefinition` 上，而不是要求测试手工构造 projection model。

同时增加了 discovery diagnostics，便于排查：

- `none`
- `bound`
- `partial`
- `parse-failed`
- `enumeration-failed`

对应 summary 能展示一个 skill 是否绑定了 projection contracts，以及绑定了多少个 index。

### 3.4 第四阶段：多 producer 列表支持

最初的绑定模型是：

- 一个 skill 只能有一个 `ProjectionContracts`
- 如果发现多个 `contract-index.json`，直接跳过绑定

这无法支撑 skill 同时消费多个 producer 的 contract。

后续改造把模型升级为：

- `SkillDefinition.ProjectionContracts: IReadOnlyList<SkillProjectionContractSet>`
- loader 枚举并绑定所有可解析的 index
- resolver 对每个 producer 各自尝试 route resolution
- 最终按 score 选择最佳 route

### 3.5 第五阶段：producer precedence

多 producer 支持后，剩下的关键问题是：

- 如果两个 producer 给出了同分 route，runtime 不能稳定选出结果
- 原始策略只能把它视为 ambiguity 并阻断 skill

为了解决这个问题，引入 producer precedence：

- `contract-index.json` 根节点新增 `producer_priority`
- 同时兼容别名 `producer_precedence`
- loader 把该值绑定到 `SkillProjectionContractSet.ProducerPriority`
- resolver 在同分场景下使用 `ProducerPriority` 做 tie-break

因此现在的选择策略是：

1. 先按 topic/view score 选最高分 route。
2. 如果 top score 相同，则按 `ProducerPriority` 降序选。
3. 只有 score 相同且 priority 也相同，才阻断为跨 producer 歧义。

## 4. 当前数据模型

当前核心模型位于：

- `src/OpenClaw.Core/Skills/SkillModels.cs`

关键字段如下：

### 4.1 SkillDefinition

- `ProjectionContracts`: skill 当前绑定的全部 projection contract sets
- `ProjectionDiscovery`: loader 自动发现与绑定的诊断信息

### 4.2 SkillProjectionContractSet

- `ProducerName`: producer 名称，优先来自 `producer_skill`
- `ProducerPriority`: 当前 producer 的 precedence
- `RootPath`: `contract-index.json` 所在目录
- `Index`: 解析后的 `ProjectionContractIndex`

### 4.3 ProjectionContractIndex

- `ProducerSkill`
- `ProducerPriority`
- `DefaultSelectionPolicy`
- `TopicScoring`
- `TargetViewScoring`
- `Topics`

## 5. Loader 设计

实现入口位于：

- `src/OpenClaw.Core/Skills/SkillLoader.cs`

### 5.1 发现流程

在 `ParseSkillContent(...)` 中，loader 会调用 `TryLoadProjectionContracts(skillDir, logger)`，扫描：

`<skillDir>/contracts/projections/**/contract-index.json`

### 5.2 绑定流程

对每个 index：

1. 解析 JSON 根节点
2. 读取 `producer_skill`
3. 读取 `producer_priority`，若不存在则回退 `producer_precedence`
4. 解析 selection policy / scoring / topics / views
5. 形成 `SkillProjectionContractSet`

### 5.3 诊断语义

loader 不再把“多个 index”视为错误，而是把它当成正常情况。当前诊断状态含义如下：

- `none`: 未发现 projection 目录或未发现 index
- `bound`: 全部发现的 index 都成功绑定
- `partial`: 发现了多个 index，但只有部分解析成功
- `parse-failed`: 全部发现的 index 都解析失败
- `enumeration-failed`: 枚举 index 文件本身失败

## 6. Runtime 设计

运行时入口位于：

- `src/OpenClaw.Agent/MafAgentRuntime.cs`

当前接线点是 `ResolveSkillsForTurn(...)`。

对每个 skill：

1. 如果 `ProjectionContracts.Count == 0`，直接保留 skill。
2. 如果有 projection contracts，调用 `SkillProjectionResolver.ResolveForRequest(skill, userMessage, logger)`。
3. 如果 resolution 被阻断：
   - 把 blocked reason 记录到 `[Blocked Skill Routes]`
   - 将该 skill clone 为 `DisableModelInvocation = true`
4. 如果 resolution 成功：
   - 调用 `BuildPromptPatch(...)`
   - 把 patch 追加到 skill instructions

这样 projection contract 不会替换 skill，而是细化 skill 的当次有效指令。

## 7. Resolver 设计

核心实现位于：

- `src/OpenClaw.Core/Skills/SkillProjectionResolver.cs`

### 7.1 单 producer 路径

对单个 producer，resolver 的流程是：

1. `SelectTopic(index, requestText)`
2. `SelectView(index, topic, requestText)`
3. 根据 `RootPath + view.Path` 加载 projection 文件
4. 执行 blocking checks
5. 生成 `SkillProjectionResolution`

### 7.2 Topic 选择

topic 评分来自 `TopicScoring`，主要维度包括：

- `primary_intent_match`
- `strong_keyword_match`
- `supporting_keyword_match`
- `explicit_artifact_bonus`
- `cross_topic_conflict_penalty`

如果前两名 score gap 小于 `ClarifyWhenScoreGapBelow`，则视为 topic ambiguity。

### 7.3 Target view 选择

view 评分来自 `TargetViewScoring`，主要维度包括：

- `explicit_output_match`
- `strong_signal_match`
- `supporting_signal_match`
- `cross_view_conflict_penalty`
- `topic_default_view_bonus`
- `within_topic_overrides`

如果前两名 view 的 score gap 太小，也会直接阻断而不猜测。

### 7.4 Blocking checks

当前会阻断的情况包括：

- topic 选择不明确
- target view 选择不明确
- projection file 不存在
- projection file 解析失败
- projection route 缺少必需字段
- `PreferReadyOnly == true` 但 view 不是 `READY`
- `BlockOnOpenQuestions == true` 且 `OpenQuestions.Count > 0`
- `mapping_policy.unresolved_item_policy == block_or_escalate` 且存在 open questions

### 7.5 多 producer 路径

当前多 producer 路径如下：

1. 遍历 `skill.ProjectionContracts`
2. 对每个 producer 调用 `TryResolveContract(...)`
3. 收集所有成功 route
4. 按 `(Score DESC, ProducerPriority DESC)` 排序
5. 如果 top1 和 top2 在 score 与 priority 都相同，则阻断为 ambiguity
6. 否则选 top1

这个模型的关键点是：

- score 仍然是主排序键
- precedence 只用于 tie-break
- precedence 不会替代 topic / view 自身的 scoring 逻辑

## 8. Prompt Patch 设计

当前 projection route 成功后，runtime 会在 skill instructions 末尾追加如下结构：

- `[Projection Route]`
- `Selected topic`
- `Selected target view`
- `Projection source`
- `Allowed terms`
- `Forbidden assumptions`
- `Required clarifications`
- `Reasoning paths`
- `Source digest`
- `Dropped items`

这样 LLM 最终看到的不是原始 `contract-index.json`，而是已经完成选择和裁剪后的精简 prompt patch。

## 9. 当前真实 contract 形态

当前仓库里真实的 checked-in projection producer 入口为：

- `src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/contract-index.json`

它目前已经具备：

- `producer_skill: "ncrew-ontology"`
- `producer_priority: 100`

也就是说，当前真实 contract 已经能参与多 producer precedence 选择，而不只是测试里的模型样例。

### 9.1 如何验证 `$schema`

这套 contract 的 `$schema` 维护，建议按下面三个层次做检查。

第一层是接线正确性：

- `contract-index.json` 应指向 `docs/skill-projection-contract-index.schema.json`
- 真实 `*.projection.json` 应指向 `docs/skill-projection-document.schema.json`
- 使用相对路径时，应从文件自身目录出发计算，不要复用 producer template 目录里的旧 schema 路径

第二层是编辑器诊断：

- 修改 `$schema` 后，先确认 JSON 文件本身没有语法错误
- 如果编辑器能解析到目标 schema，通常会直接给出缺失字段、类型不匹配或无效枚举值
- 当前仓库中，这一步至少应覆盖真实 consumer contract 文件，而不只是 `templates/` 下的样例

第三层是显式 schema 校验：

- 如果本机具备 JSON Schema 校验工具，应同时校验 `contract-index.json` 与至少一个真实 `*.projection.json`
- 推荐优先校验 checked-in 的真实 consumer contract，而不是只校验模板样例
- 如果环境缺少校验器，也至少要保留编辑器诊断为零，并确认 `$schema` 路径可以实际解析到 `docs/` 下的 schema 文件

当前环境中的已知限制也需要记录：

- PowerShell 环境未提供可直接使用的 `Test-Json` 方案
- Python 环境默认未安装 `jsonschema` 模块

因此当前维护基线是：

- 先用编辑器诊断兜底
- 再检查 `$schema` 相对路径是否全部切到 `docs/` 下的新 schema
- 若后续把校验器纳入 CI，再把“显式 schema 校验”升级为必跑步骤

## 10. 测试覆盖

测试主要分布在：

- `src/OpenClaw.Tests/SkillTests.cs`
- `src/OpenClaw.Tests/MafAgentRuntimeTests.cs`
- `src/OpenClaw.Tests/LlmClientFactoryTests.cs`（用于清理旧编译阻塞，确保测试项目可运行）

### 10.1 Loader 测试

已覆盖：

- 单个 `contract-index.json` 成功绑定
- 多个 `contract-index.json` 全部绑定
- discovery 状态、bound count、index paths 正确输出
- `producer_skill` 与 `producer_priority` 能被读取

### 10.2 Resolver 测试

已覆盖：

- 正常 topic/view 选择
- projection patch 构建
- open questions 阻断
- 多 producer 中按更高 score 选 route
- 同分时按更高 `ProducerPriority` 选 route

### 10.3 Runtime 测试

已覆盖：

- 成功 route 时把 patch 追加进 system prompt
- blocked route 时隐藏 skill 并写入 blocked reason
- 多 producer 同分时沿用 precedence 结果生成最终 prompt

## 11. 关键设计取舍

### 11.1 为什么不是 reload-time 路由

因为 topic/view 选择依赖当前 user request。reload-time 只能绑定静态 contract，不能决定当前 turn 应该消费哪个 projection。

### 11.2 为什么不是直接把所有 contracts 全塞进 prompt

原因有三点：

- token 成本不可控
- 多 topic / 多 view / 多 producer 会制造 prompt 噪音
- unresolved route 应该阻断，而不是交给模型自己猜

### 11.3 为什么 precedence 只用于 tie-break

因为 precedence 代表“同等匹配条件下的 producer 偏好”，而不是“无视请求语义的全局强制覆盖”。

如果 precedence 直接覆盖 score，会让更弱匹配的 producer 抢走 route，这会破坏 topic/view scoring 的语义完整性。

## 12. 当前限制

当前实现仍有一些明确限制：

1. precedence 目前只接受整数优先级，没有更复杂的 producer policy。
2. resolver 的 topic/view 匹配仍以 deterministic phrase matching 为主，没有更复杂的 ranking 解释信息输出。
3. 当前 runtime 只把最终 route 结果写入 prompt，不会把完整候选比较过程暴露给上层诊断接口。
4. `contract-index.json` 的 schema 目前由代码解析隐式定义，尚未单独抽出正式 schema 文档。

## 13. 推荐后续工作

后续如果继续扩展，可按以下顺序推进：

### 13.1 诊断可观测性

增加 runtime 级 diagnostics，输出：

- 各 producer 的 topic/view score
- tie-break 发生原因
- 哪些 route 被 block、为什么 block

### 13.2 Schema 明文化

为 `contract-index.json` 与 `*.projection.json` 单独输出 schema 或 contract spec，避免字段语义只存在于代码解析逻辑中。

### 13.3 Producer policy 扩展

在简单整数 precedence 之外，引入更细粒度策略，例如：

- consumer-specific precedence
- topic-specific precedence
- environment-specific precedence

### 13.4 管理端点或调试界面

把 loader discovery summary 和 last-route diagnostics 暴露到管理端点，便于线上排查“为什么没有选中某个 producer”。

## 14. 当前结论

到当前状态为止，`SkillProjection Contracts` 已完成从“文档化约束资产”到“真实 runtime 路由层”的关键闭环：

- loader 能自动发现并绑定 projection contracts
- runtime 能按请求动态选择 topic / target view
- blocking checks 能影响 skill 可见性
- 多 producer 能并存
- 同分时可以通过显式 precedence 做稳定决策

这意味着 projection contracts 已不再只是辅助说明材料，而是 skill runtime 的正式控制面之一。
