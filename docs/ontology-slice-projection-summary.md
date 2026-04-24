# Ontology Slice 到 Skill Runtime 的治理化落地总结

## 一、说明

本文重点说明当前体系的建设背景、核心设计、运行机制、治理价值及后续建议。其目标不是重复单篇文档细节，而是在统一视角下说明一条已经逐步成型的工程主线：如何将面向任务的 ontology slice，从知识整理产物演进为可发现、可路由、可阻断、可治理的 skill runtime 正式输入。

## 二、背景与问题定义

在当前体系中，`Ontology` 用于定义领域中的概念、关系、约束及术语边界，能够为复杂业务提供统一的语义基础。但从工程落地角度看，仅有完整 ontology 仍不足以直接支撑 skill runtime 的稳定消费，主要体现在以下几个方面：

1. 完整 ontology 面向的是领域全貌，而实际任务通常只依赖其中有限且明确的一部分语义信息。
2. 下游 skill 需要的不是抽象知识全景，而是与当前任务直接相关、边界清晰、可验证的最小语义输入。
3. 同一份语义信息在不同消费场景下，需要以不同交付视图呈现，例如对象建模、结构校验、prompt 约束或流程编排。
4. 当路由依据不足、视图选择不明确或投影结果仍存在未决问题时，系统需要具备阻断能力，而不是依赖模型自行猜测。

基于以上问题，体系逐步形成了 `Ontology -> Slice -> Projection -> Runtime Consumption` 的分层路径。其中，`Slice` 用于完成任务定向的语义收缩，`Projection` 用于完成面向 consumer skill 的定向交付，运行时机制则负责完成自动发现、请求级路由与阻断治理。

## 三、核心概念与职责边界

### 1. Ontology

`Ontology` 是完整语义骨架，负责定义领域对象、对象关系、约束条件与术语边界。其作用在于提供统一、稳定且可扩展的业务语义基础。

### 2. Slice

`Slice` 是围绕当前任务抽取的最小可验证子图，仅保留完成该任务所必需的：

- `concepts`
- `relations`
- `constraints`
- `sources`

其核心原则不是覆盖更多信息，而是在保证任务判断能力的前提下尽可能收缩边界。因此，slice 具备以下特征：

- 面向任务，而非面向全量知识陈列。
- 可验证，而非停留在说明性文档层面。
- 可追溯，能够明确指出其结论来源。

### 3. Projection

`Projection` 是将 slice 按特定交付视图重写为下游系统可直接消费的契约或视图。其目标是把抽象语义闭包转换为 consumer skill 在具体任务中可调用、可约束、可执行的输入形式。

当前体系中，常见交付视图包括：

- `domain-model`
- `json-schema`
- `prompt-constraint`
- `workflow-contract`

三者关系可以概括为：ontology 提供完整语义母体，slice 负责按任务压缩语义范围，projection 负责将该范围内的语义转化为可消费交付物。

## 四、整体方法路径

从相关文档形成的统一方法看，当前体系已经具备较为清晰的 producer 到 consumer 再到 runtime 的闭环链路。

### 1. Producer 侧：完成语义抽取与投影生成

以 `ncrew-ontology` 为代表的 producer skill，其主要职责不是直接承担业务执行，而是负责：

- 从文档、schema、代码与样例中抽取最小语义闭包。
- 将结果组织为结构化 slice。
- 基于模板、schema、样例与校验脚本，将 slice 固化为稳定产物。
- 按目标交付视图生成 projection 文件。

因此，producer 侧承担的是“语义治理输入生产”职责。

### 2. Consumer 侧：完成任务域承载与路由消费

业务 skill 作为 consumer，不再重新解释 ontology，而是围绕自身任务面组织 contract，并完成以下工作：

- 定义任务域。
- 定义各任务域下可选的交付视图。
- 承载经 producer 生成并通过校验的 projection 文件。
- 在运行时根据用户请求选择 topic 与交付视图，并加载相应 projection。

因此，consumer 侧承担的是“任务执行入口消费”职责。

### 3. Runtime 侧：完成请求级解析与控制

运行时并不负责生成 slice 或 projection，而是负责在当前请求上下文中：

- 发现并绑定 projection contracts。
- 按请求选择 topic。
- 按 topic 选择交付视图。
- 加载对应 projection。
- 在不满足条件时执行阻断。
- 在路由成功时生成 projection prompt patch 并注入 skill instructions。

由此，语义资产由离线整理结果正式转化为在线运行时控制面的一部分。

## 五、从“文档资产”到“运行时资产”的关键转变

本次体系演进中最重要的转变，在于 projection contracts 不再只是 skill 目录下的静态说明文件，而是被正式纳入 skill runtime 的路由与控制流程。

这一转变主要体现为以下几个方面：

### 1. 统一索引入口形成

`contract-index.json` 成为 consumer 侧的统一路由入口，负责承载：

- producer / consumer 元数据
- topic scoring
- 交付视图评分
- topic 与 view 的组织关系
- default selection policy
- 冲突处理规则
- producer priority

这使得 runtime 能够基于显式 contract 进行选择，而非依赖人工阅读文档或隐式约定。

### 2. 路由决策下沉到 request time

相关设计文档已经明确，projection route 是 request-dependent 的，无法在 reload 时静态固化。因此，路由决策必须在每个 turn 中按当前用户请求动态执行。

这一定义使整个机制具备了请求级适配能力，也保证 topic 与交付视图选择能够严格围绕当前任务上下文展开。

### 3. 阻断机制成为正式治理手段

在当前设计中，如果出现以下情况，系统应阻断而不是猜测：

- topic 选择不明确。
- 交付视图选择不明确。
- projection 文件缺失或解析失败。
- 视图状态不是 `READY`。
- 存在 `open questions` 且策略要求阻断。
- 多 producer 之间出现无法打破的同分歧义。

这一点具有明显的治理意义。它意味着 runtime 不再以“尽量继续执行”为默认目标，而是以“在边界明确前不扩大错误”为优先原则。

## 六、SkillLoader 在体系中的基础作用

`SkillLoader.LoadAll` 的价值，不仅在于扫描技能目录、完成优先级覆盖和条件筛选，更在于为 projection contracts 的自动发现和正式接线提供基础入口。

从整体链路看，SkillLoader 的作用可以分为两层：

1. 将 skill 自身纳入技能系统，并完成来源聚合、同名覆盖和基础筛选。
2. 发现 skill 目录下的 `contracts/projections/**/contract-index.json`，并将其绑定到 `SkillDefinition`，供后续 runtime 解析使用。

这意味着，如果缺少 loader 侧的自动发现能力，即使 projection 结构设计已经完成，它仍然只能作为静态资产存在，无法进入正式运行链路。

同时，discovery diagnostics 的引入也为后续排查提供了必要支撑。`none`、`bound`、`partial`、`parse-failed`、`enumeration-failed` 等状态，使系统能够区分是“没有发现 contract”、还是“发现后解析失败”、或是“部分绑定成功”。这一点对于工程维护具有现实价值。

## 七、治理能力的形成与成熟度提升

从 `SESSION_SUMMARY.md` 反映的演进轨迹来看，当前体系的建设重点已经从“能否产出 slice / projection”转向“如何保障其可治理、可评审、可迁移、可运行”。

目前已形成的治理能力，至少包括以下几个层面：

### 1. 结构治理

通过模板、schema、样例和校验脚本，确保 slice 与 projection 的结构具备稳定性和一致性。

### 2. 状态治理

通过 `READY`、`WARNING`、`FAIL` 等状态，明确区分“结构合法”与“可直接进入消费”的边界，避免把形式正确误判为可直接使用。

### 3. 路由治理

通过 topic scoring、交付视图评分、冲突惩罚项、gap 阈值和 producer precedence 等规则，将原本依赖经验的选择过程显式化、规则化。

### 4. 风险治理

通过 blocking checks，将未决问题、歧义路由和不满足条件的 projection 排除在运行时消费之外，降低错误传播风险。

### 5. 演进治理

围绕模板、schema、样例、校验器和说明文档的同步迁移，体系已具备从“当前可用”向“后续可持续演进”过渡的条件。

## 八、多 Producer 支持的意义

随着体系能力扩展，单一 consumer skill 对应单一 producer 的假设已经不再充分。当前设计已支持同一 consumer skill 绑定多个 producer 的 projection contracts，并通过如下机制控制选择过程：

- loader 侧绑定多个 contract set。
- resolver 对各 producer 分别尝试 route resolution。
- 最终按 `Score DESC, ProducerPriority DESC` 进行排序。
- 仅当 score 与 priority 同时相同，才判定为不可消解歧义并阻断。

这一能力的价值在于：它允许 skill 在统一框架下消费多来源语义契约，同时保留显式优先级与阻断机制，防止多源接入演变为无控制叠加。

## 九、阶段性结论

综合以上分析，可以将本阶段建设成果概括为以下几点：

1. 已形成从 ontology 到 slice、从 slice 到 projection、从 projection 到 runtime consumption 的完整方法链路。
2. `ncrew-ontology` 已从单一 skill 说明入口，演进为 producer 侧的语义治理入口。
3. projection contracts 已从静态文档资产演进为 skill runtime 的正式控制面之一。
4. loader 自动发现、request-time 路由、blocking checks、多 producer precedence 等关键机制已经构成一套可运行、可治理的工程框架。
5. 当前体系的核心价值已不再是“生成文档”或“补充 JSON 文件”，而是在于将语义建模、契约交付、运行时路由与风险控制纳入同一条闭环治理链路。

从工程视角看，这一体系所解决的核心问题并非“如何描述知识本身”，而是“如何让知识以受控方式进入技能系统，并在真实请求中稳定、可解释地工作”。

## 十、后续建议

为进一步提升该体系的可用性与可观测性，建议后续优先推进以下方向：

1. 完善运行时诊断能力，输出 producer/topic/view 的评分、阻断原因及 tie-break 依据。
2. 将 runtime contract 基线固定为：`contract-index.json -> docs/skill-projection-contract-index.schema.json`、`*.projection.json -> docs/skill-projection-document.schema.json`，并统一通过仓库内校验入口执行显式验证。
3. 在更多业务 skill 中按“最小闭环优先”的方式推广该机制，优先验证 `contract-index -> projection -> runtime patch` 的实际稳定性。
4. 持续保持模板、schema、样例、脚本和说明文档之间的同步演进，避免运行时行为与文档口径漂移。

总体而言，当前工作已经完成从概念说明到工程接线、从结构产出到运行时治理的关键跨越，具备进一步向标准化、可复用能力沉淀的基础。

团队如果需要把 `producer 模板 schema` 与 `runtime contract schema` 的使用边界固化成固定流程，可直接参考 `docs/skill-projection-schema-migration-checklist.md`。
