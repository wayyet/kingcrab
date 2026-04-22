# 从 ncrew-ontology 到具体业务 Skill 的构建方法

本文整理一条在当前仓库内已经成型的实践路径：

1. 从 `ncrew-ontology` skill 出发，为当前业务问题构建一个最小可验证的本体切片。
2. 把这个切片投影成具体 consumer skill 可消费的 projection contracts。
3. 在目标业务 Skill 的 runtime 中，把这些 contracts 真正接到任务域选择、交付视图（target view）选择、projection load 和 blocking checks 上。

这不是一个抽象的 ontology 教程，而是对当前仓库里已经存在的 producer -> consumer -> runtime 闭环的工程化整理。

相关文档：

- [docs/ncrew-ontology-first-skill-practical-template.md](docs/ncrew-ontology-first-skill-practical-template.md)：从零为一个新业务 Skill 落第一份 slice、第一个任务域和第一个 projection 的实操模板。

## 1. 目标

这套方法要解决的不是“如何表达整个业务本体”，而是下面三个更实际的问题：

- 如何从复杂领域材料中抽取当前任务真正需要的最小语义子图。
- 如何把这份子图稳定交付给某个具体业务 Skill，而不是停留在人类文档层。
- 如何让业务 Skill 在运行时按用户请求动态选任务域、选交付视图、加载 projection，并在不安全时阻断而不是猜测。

因此，这里的核心产物不是一份“大而全 ontology”，而是两层工程化交付物：

- `slice`：producer 侧的最小语义闭包。
- `projection contracts`：consumer 侧可直接消费的任务域/交付视图路由契约。

## 2. 角色分工

在当前仓库里，这个过程至少涉及两个角色。

### 2.1 producer skill：ncrew-ontology

producer 侧位于 [src/OpenClaw.Gateway/skills/ncrew-ontology/README.md](src/OpenClaw.Gateway/skills/ncrew-ontology/README.md) 和 [src/OpenClaw.Gateway/skills/ncrew-ontology/SKILL.md](src/OpenClaw.Gateway/skills/ncrew-ontology/SKILL.md)。

它的职责不是直接替业务 Skill 写 prompt，而是：

- 从文档、schema、代码和样例中抽取最小语义闭包。
- 明确 `concepts`、`relations`、`constraints`、`sources`。
- 通过模板、schema 和校验脚本把这份切片变成稳定产物。
- 在需要下游落地时，在 producer 侧按交付视图和映射规范把 slice 显式投影成 projection 文件。

换句话说，`ncrew-ontology` 负责“把业务知识整理成可验证语义输入，并生成可交付的 projection 草案/产物”。

### 2.2 consumer skill：具体业务 Skill

consumer 侧在当前仓库里的实例如下：

- [src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/README.md](src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/README.md)
- [src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/contract-index.json](src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/contract-index.json)

它的职责不是重新理解 ontology，而是：

- 按自己的业务场景拆分任务域。
- 为每个任务域提供多个交付视图。
- 承载由 producer 生成并经校验的 projection 文件。
- 在运行时根据用户请求，选择合适的任务域和交付视图，并消费对应 projection。

换句话说，consumer skill 负责“把语义契约应用到具体业务执行”。

## 3. 端到端流程

从 `ncrew-ontology` 到业务 Skill 的落地过程，可以拆成六步。

### 3.1 先定义 slice request，而不是直接写 projection

第一步不是直接填 `contract-index.json`，而是先回答四个问题：

- 当前业务主题是什么。
- 当前任务要解决哪个子域。
- 这次需要输出给谁消费。
- 最终是用于代码生成、schema 约束、prompt 边界，还是 workflow 编排。

这一步的目标是收缩范围。`ncrew-ontology` 的原则是只保留当前任务所需的最小语义闭包，而不是导出整份领域本体。

在 producer 侧，默认应先落到：

- `templates/TEMPLATE.md`
- 或 `templates/TEMPLATE.json`

对应入口见 [src/OpenClaw.Gateway/skills/ncrew-ontology/README.md](src/OpenClaw.Gateway/skills/ncrew-ontology/README.md)。

### 3.2 抽取最小可验证子图

一旦主题收缩完成，就开始构造 slice。本阶段只保留四类信息：

- `concepts`：业务对象、状态、类型、术语边界。
- `relations`：这些对象之间必须保留的关系。
- `constraints`：会改变实现、判断或生成结果的规则。
- `sources`：这些结论来自哪里，可信度如何。

producer 侧的判断标准是：

- 少一个概念会不会让当前业务任务无法判断。
- 少一条关系会不会让目标交付视图失去关键连线。
- 少一条 constraint 会不会让生成结果偏离真实边界。

如果不会，就不应纳入 slice。

### 3.3 用模板、schema 和样例把 slice 固化

切片不是只给人看，因此必须进入模板和校验环节。

当前仓库里，producer 侧已经把这一层标准化到：

- `templates/TEMPLATE.json`
- `templates/TEMPLATE.schema.json`
- `scripts/validate-slice.ps1`
- `examples/ready|warning|invalid`

这一步的目标是两件事：

- 保证结构稳定，不靠口头约定。
- 保证后续 projection 不是拍脑袋生成，而是建立在已验证 slice 之上。

### 3.4 把 slice 投影成 consumer 可消费的交付视图

这是 producer 到 consumer 的关键转换层，而且投影动作本身发生在 producer 侧。

slice 只负责“语义闭包”，并不天然等于业务 Skill 的最终使用形态。真正给业务 Skill 用时，需要由 `ncrew-ontology` 按交付视图和映射规范把它显式改写成 projection。常见交付视图例如：

- `domain-model`
- `json-schema`
- `prompt-constraint`
- `workflow-contract`

这一步的核心问题不是“还能补什么字段”，而是：同一份 slice 在不同业务产物里应该保留什么、不应该保留什么。

例如：

- 当业务 Skill 要生成实现对象时，应该优先投影为 `domain-model`。
- 当业务 Skill 要输出校验契约时，应该优先投影为 `json-schema`。
- 当业务 Skill 要收缩模型自由度时，应该优先投影为 `prompt-constraint`。
- 当业务 Skill 要表达生命周期或步骤编排时，应该优先投影为 `workflow-contract`。

producer 侧这一步的模板与校验入口在：

- `templates/PROJECTION_TEMPLATE.json`
- `templates/PROJECTION_TEMPLATE.schema.json`

完成投影并通过校验后，consumer 侧真实落点才变成：

- `contracts/projections/<producer>/<topic>/<topic>.<view>.projection.json`

## 4. 如何把本体切片应用到具体业务 Skill

真正把 ontology slice 用到业务 Skill，关键不是多加几个 JSON 文件，而是建立一套稳定的 consumer contract 结构。

### 4.1 先定义 consumer 的任务域，而不是直接复制 producer 分类

任务域是 consumer 视角，不一定等于 producer 的内部建模分类。

当前 `software-developer` 的真实 consumer 任务域已经包括：

- `skill-loading`
- `task-execution`
- `tool-orchestration`
- `memory-session`

这些任务域的存在意义，是把“同一个 producer 语义包”拆成业务 Skill 真正需要的几个任务域。也就是说，任务域是 consumer 的任务路由面，而不是 ontology 的全量目录树。

### 4.2 再为每个任务域定义交付视图

任务域确定后，业务 Skill 还需要决定：同一个任务域允许哪些交付视图。

以当前已经落地的 `memory-session` 为例，现在已经扩成完整主题，包含：

- `memory-session.domain-model.projection.json`
- `memory-session.json-schema.projection.json`
- `memory-session.prompt-constraint.projection.json`
- `memory-session.workflow-contract.projection.json`

这意味着同一业务主题可以服务四种不同需求：

- 实现对象建模
- 结构校验契约
- prompt 约束
- 生命周期流程编排

### 4.3 用 contract-index.json 做统一路由入口

consumer 侧真正的总入口不是 README，而是 `contract-index.json`。

当前它至少承担四类职责：

- 声明 producer / consumer 元数据。
- 定义任务域 scoring。
- 定义交付视图 scoring。
- 列出每个任务域下有哪些真实可加载的 projection 文件。

在当前仓库中，这些能力的真实定义都已经集中在：

- [src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/contract-index.json](src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/contract-index.json)

这一步的要点是：

- 机器入口统一放在 index。
- 人类说明可以放在 `README.md`、`REVIEW.md` 或 `SKILL.md`。
- 但最终 runtime 行为必须以 index 为准。

### 4.4 把冲突规则和路由提示同步到 SKILL.md

consumer skill 除了机器入口，还需要人类可读提示面。

当前 `software-developer` 已经把任务域路由提示写在：

- [src/OpenClaw.Gateway/skills/software-developer/SKILL.md](src/OpenClaw.Gateway/skills/software-developer/SKILL.md)

这里的关键方法不是“写一份说明就结束”，而是保证 `SKILL.md` 与 `contract-index.json` 不漂移。

一个成熟的 consumer contract 至少要同步三层信息：

- 任务域描述
- 交付视图示例
- multi-topic conflict rules

当前 `memory-session` 相关冲突规则就是一个已落地例子：它已经同时存在于 `contract-index.json` 和 `SKILL.md` 中。

## 5. 运行时如何消费这些 contract

如果只把 projection 放在 skill 目录里，但 runtime 不消费，它仍然只是文档资产。反过来，runtime 也不负责从 slice 生成 projection；它只负责发现、选择、加载和消费已经存在的 projection。

当前仓库已经把这一步真正接到了 runtime 上，整体设计见：

- [docs/skill-projection-contracts-design.md](docs/skill-projection-contracts-design.md)
- [docs/skill-projection-contracts-schema.md](docs/skill-projection-contracts-schema.md)

当前运行时路径可以概括为：

1. `SkillLoader` 自动发现 `contracts/projections/**/contract-index.json`。
2. 运行时在每个 turn 根据用户请求做任务域选择。
3. 再在任务域内做交付视图选择。
4. 加载已由 producer 生成并落盘的 `*.projection.json`。
5. 如果 projection 存在 open questions、视图非 `READY`、或 route 不明确，则阻断而不是猜测。
6. 如果 route 成功，runtime 把 projection 转成 prompt patch，追加到 skill instructions。

这意味着 ontology slice 的价值，不再停留在“帮助理解业务”，而是直接进入 skill runtime 的控制面。

## 6. 推荐方法论

如果要在新的业务 Skill 上复用这套方法，推荐按下面顺序推进。

### 6.1 先做 slice，再做 projection

不要直接从 consumer 侧写 projection。先用 `ncrew-ontology` 把语义闭包切出来，再由 producer 侧按交付视图和映射规范生成 projection，最后再交给 consumer 承载与消费。

否则常见结果是：

- 任务域命名稳定，但语义边界漂移。
- 交付视图看起来完整，但缺少可追溯来源。
- prompt 约束、schema 约束、workflow 约束互相矛盾。

### 6.2 先做最小主题，再扩成完整主题

当前 `memory-session` 的演进路径就是一个推荐模式：

1. 先落最小可运行骨架。
2. 打通 `contract-index.json -> projection file -> runtime` 闭环。
3. 再从单 view 扩到多 view 完整主题。
4. 最后再补人类可读 conflict rules 和 routing hints。

这样可以避免一次性设计过多交付视图，结果 runtime 根本用不起来。

### 6.3 机器规则和人类提示必须双同步

consumer contract 里最容易漂移的是这几类信息：

- 任务域冲突规则
- default 交付视图
- example requests
- view-specific signal hints

因此每次修改 `contract-index.json` 时，都应同步检查：

- `SKILL.md`
- 任务域目录内的 `README.md`
- 任务域目录内的 `REVIEW.md`

### 6.4 先验证结构，再讨论质量

本仓库已经把结构校验与质量判断拆开了。

producer 侧应先确保：

- slice 能通过 `TEMPLATE.schema.json`
- projection 能通过 `PROJECTION_TEMPLATE.schema.json`

consumer 侧应再确保：

- `contract-index.json` 与 `*.projection.json` 的 `$schema` 指向正确
- 编辑器诊断为零
- 任务域 / 交付视图路由条目与磁盘文件一致

结构层通过后，再进入 review 层讨论：

- 任务域切分是否合理
- scoring 信号是否稳定
- conflict rules 是否足够可解释
- projection 是否真的对业务 Skill 有帮助

### 6.5 一条推荐的最佳实践顺序

如果目标不是只做一份“可读的 ontology 文档”，而是要把 slice 真正变成 consumer skill 可加载、runtime 可消费的 contract，推荐严格按下面顺序推进，而不是在 consumer 侧直接补 projection 文件。

#### 第一步：先用 `ncrew-ontology` 把 slice 收缩并校验通过

起点永远是 producer 侧的最小语义闭包，而不是 consumer 侧的投影产物。

这一步的目标是先回答三件事：

- 当前主题到底是什么。
- 当前任务真正依赖哪些 concepts、relations 和 constraints。
- 哪些来源足以支撑这份切片进入下游交付。

只有当 slice 先通过结构校验，后面的 projection 才有稳定语义输入。否则常见结果是 projection 结构看起来合法，但它映射的语义边界其实并不稳定。

producer 侧至少应做到：

- 用 `templates/TEMPLATE.json` 或对应 Markdown 模板固化 slice。
- 用 `TEMPLATE.schema.json` 或 `validate-slice` 脚本完成结构校验。
- 把 conflicts、ambiguities、uncertainties 显式写出来，而不是留给 projection 阶段临时猜测。

这一步通过后，才能说“当前主题已经具备投影前提”。

#### 第二步：明确这次要投影成哪一种交付视图

slice 只是语义闭包，不等于最终交付形态。因此第二步不是立刻复制 projection 模板，而是先决定本次到底面向哪一个 view。

当前仓库中最常见的四种视图是：

- `domain-model`
- `json-schema`
- `prompt-constraint`
- `workflow-contract`

这里的关键判断标准不是“哪个名字看起来最全”，而是“这次要交付给 consumer skill 的到底是什么能力”。

一般可以按下面方式判断：

- 如果目标是实现对象、领域结构或运行时对象模型，优先选 `domain-model`。
- 如果目标是结构校验、字段约束或机器可检验契约，优先选 `json-schema`。
- 如果目标是收缩模型用词、推理路径和禁止假设，优先选 `prompt-constraint`。
- 如果目标是表达步骤关系、生命周期流转或执行编排，优先选 `workflow-contract`。

这一步的意义，是先把“交付意图”固定下来，再决定 slice 中哪些信息需要保留、哪些信息应该裁剪、哪些信息必须阻断。

#### 第三步：让 `ncrew-ontology` 按映射规范填充 projection 模板

视图一旦确定，producer 侧再开始做 projection。这里不是简单复制 `PROJECTION_TEMPLATE.json` 后机械改字段，而是要按 view 的消费目标，把 slice 显式映射到 projection contract。

这一步至少要完成四类映射：

- 把 `concepts` 映射到 `concept_mappings`。
- 把 `relations` 映射到 `relation_mappings`。
- 把 `constraints` 映射到 `constraint_mappings`。
- 把术语边界、禁止假设、澄清要求和推理路径映射到 `prompt_projection`。

同时要补齐 projection 自身的交付信息：

- `projection.projection_type`
- `projection.target_format`
- `projection.target_runtime`
- `projection.source_slice`
- `delivery_artifacts`
- `dropped_items`
- `open_questions`

这一步的原则是“显式映射，不做隐式继承”。也就是说：

- slice 中保留下来的关键概念，不应在 projection 中无痕消失。
- 不能消费的关系或约束，要记录为 `dropped_items`、`open_questions`，或者直接阻断。
- prompt、schema、workflow 三类约束不要混写成一份模糊 projection，而要围绕当前选定的 view 做定向交付。

只有这样，projection 才是 producer 侧交付给 consumer 的稳定契约，而不是一份看起来像模板实例的半成品。

#### 第四步：用 projection 校验脚本验证通过

projection 写完后，不应直接落到 consumer skill 目录中。先过结构校验，再讨论质量。

这一步至少要确保：

- projection 能通过 `PROJECTION_TEMPLATE.schema.json`。
- `$schema`、`projection_version`、`projection_type` 等关键字段与模板要求一致。
- 必填映射区块没有缺项。
- 编辑器诊断和本地校验脚本结果一致。

在当前仓库中，这一步应优先使用 `validate-projection.ps1` 或 `validate-projection.py`。如有需要，也可以配合 review mode 判断它现在更接近 `READY` 还是 `WARNING`。

这一步的目的不是证明 projection “已经完美”，而是确保它至少已经从“草稿猜想”变成“结构合法、可继续 review、可进入 consumer contract”的产物。

#### 第五步：再把产物落到 consumer skill 的 `contracts/projections` 目录中

只有在 producer 侧的 projection 已经完成并通过校验后，才进入 consumer 侧承载阶段。

此时的落点不应再是 `ncrew-ontology` 自己的模板目录，而应进入 consumer skill 的正式 contract 目录，例如：

- `contracts/projections/<producer>/contract-index.json`
- `contracts/projections/<producer>/<topic>/<topic>.<view>.projection.json`

consumer 侧此时要做的不是重写 projection，而是：

- 把它纳入 `contract-index.json` 的任务域和交付视图路由。
- 确认任务域 scoring 与 view scoring 能正确命中这份 projection。
- 把相关 routing hints、conflict rules 和例子同步到 consumer `SKILL.md` 或配套说明。

换句话说，producer 负责生成并校验 projection，consumer 负责承载、路由和运行时消费。两边职责不能混。

#### 为什么这个顺序不能反过来

这五步真正约束的是职责边界，而不是文档排版顺序。

如果跳过第一步，直接从 consumer 侧写 projection，通常会得到“结构看起来完整，但没有可靠来源支撑”的伪契约。

如果跳过第二步，不先定 view，就很容易把 `domain-model`、`json-schema`、`prompt-constraint`、`workflow-contract` 的要求混在一起，最后谁都不好用。

如果跳过第三步的显式映射，只是复制模板改名字，那么 projection 大概率无法解释“为什么保留这个概念、为什么丢掉那条关系、为什么某条约束变成 runtime guard”。

如果跳过第四步，consumer 目录里会积累大量“看起来像 contract，实际上 runtime 读不稳”的文件。

如果跳过第五步，projection 即使已经合法，也仍然只是 producer 侧资产，没有真正进入业务 Skill 的执行路径。

因此，更稳妥的最佳实践不是“先把文件放进去再慢慢补”，而是：先完成 producer 侧的语义收缩和投影交付，再让 consumer 侧正式接管这份 contract。

## 7. 一份可复用的最小清单

如果你要把这套方法迁移到一个新的业务 Skill，最小清单如下。

1. 在 `ncrew-ontology` 中先产出一份通过校验的 JSON slice；如需评审或人读说明，可再补一份配套 Markdown。
2. 复制 `PROJECTION_TEMPLATE.json`，按交付视图从 slice 显式映射并产出至少一个通过校验的 `*.projection.json`。
3. 在 consumer skill 下创建 `contracts/projections/<producer>/contract-index.json`。
4. 定义任务域。
5. 为任务域定义交付视图。
6. 为任务域和交付视图补 scoring 信号。
7. 为任务域补至少一个真实 `*.projection.json` 文件。
8. 把人类可读 routing hints 写进 consumer `SKILL.md`。
9. 把 loader / runtime 真正接到这些 contracts 上。
10. 补 `$schema`、诊断检查和必要的 review 说明。

## 8. 当前结论

从 `ncrew-ontology` 构建业务本体切片，再把本体切片应用到具体业务 Skill，本质上是一条三层转换链：

- 第一层：从复杂领域材料中抽出最小语义闭包。
- 第二层：把语义闭包投影成任务域/交付视图维度的 consumer contracts。
- 第三层：把 consumer contracts 接进业务 Skill 的真实 runtime。

这条方法的关键不在于 ontology 本身多完整，而在于每一层都能回答一个明确问题：

- 这次任务到底需要哪些概念、关系和约束。
- 这些语义应该以什么交付视图交付给业务 Skill。
- 业务 Skill 在收到用户请求时，如何稳定、安全、可解释地选择并消费它们。

到这个阶段，`ncrew-ontology` 不再只是一个“建模说明 skill”，而是 producer 侧的语义治理入口；而具体业务 Skill 也不再只是“读文档猜规则”，而是通过 projection contracts 在运行时正式消费这些语义约束。
