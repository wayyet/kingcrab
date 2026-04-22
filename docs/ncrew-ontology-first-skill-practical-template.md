# 从零落第一个 Slice、任务域 和 Projection 的实操模板

本文不是讲原理，而是给一条可以直接照着走的最短路径：

目标是从零开始，为一个新的业务 Skill 完成三件事：

1. 落第一份 `ncrew-ontology` slice。
2. 在 consumer skill 下落第一个任务域。
3. 为这个任务域落第一个可被 runtime 消费的 projection。

如果你只想先把第一条 producer -> consumer -> runtime 的链路打通，这篇文档应该优先于更偏方法论的 [docs/ncrew-ontology-slice-to-skill-method.md](docs/ncrew-ontology-slice-to-skill-method.md)。

## 1. 适用范围

这份模板适用于下面这种场景：

- 你已经有一个新的业务 Skill，或者准备新增一个业务 Skill。
- 你已经知道这个 Skill 需要消费某个领域语义，而不想把规则散落在 `SKILL.md` 纯文本里。
- 你希望先打通最小闭环，再逐步扩成完整任务域和多交付视图。

这份模板不适合下面这种场景：

- 你还不知道当前任务的主题是什么。
- 你手上没有任何可追溯的领域来源。
- 你想一次性做完整 ontology 导出，而不是先做最小切片。

## 2. 最短目标

先把目标收窄到最小可运行闭环，不要一开始就做完整主题。

最小闭环是：

1. 一份合法 slice。
2. 一个 consumer 任务域。
3. 一个 `contract-index.json` 路由条目。
4. 一个真实 `*.projection.json`。
5. runtime 能按用户请求选中它。

只要这 5 件事成立，你就已经把“语义建模”变成了“runtime 可消费 contract”。

## 3. 第一步：先做第一份 Slice

### 3.1 先回答四个问题

在动手前，先写清楚：

- 这次业务主题是什么。
- 当前任务最小子域是什么。
- 这个 slice 未来要给哪个 consumer skill 用。
- 这次最主要的交付视图是什么。

一个简单写法可以是：

```text
业务主题：订单审核
当前子域：审核规则与人工复核边界
目标 consumer skill：order-reviewer
首个交付视图：prompt-constraint
```

这里最重要的不是表述多漂亮，而是强行收缩范围。第一份 slice 不要覆盖整个业务域。

### 3.2 只保留最小语义闭包

第一份 slice 只保留四类内容：

- `concepts`
- `relations`
- `constraints`
- `sources`

判断标准很简单：

- 少了它，这次业务判断会不会失真。
- 少了它，第一个 projection 会不会失去边界。

如果不会，就先不要放进去。

### 3.3 从 producer 模板开始

producer 侧入口在：

- [src/OpenClaw.Gateway/skills/ncrew-ontology/SKILL.md](src/OpenClaw.Gateway/skills/ncrew-ontology/SKILL.md)
- [src/OpenClaw.Gateway/skills/ncrew-ontology/README.md](src/OpenClaw.Gateway/skills/ncrew-ontology/README.md)

第一份 slice 推荐从这两个模板之一开始：

- `templates/TEMPLATE.md`：先人读梳理
- `templates/TEMPLATE.json`：直接工程化落地

如果你是第一次做，建议先在 `TEMPLATE.md` 里把主题、范围、来源和关键约束写清楚，再落到 JSON。

### 3.4 第一份 slice 的完成标准

第一份 slice 不要求完整，但至少要满足：

- 有明确主题
- 有纳入范围和排除范围
- 有至少一个可追溯来源
- 有至少一个核心概念
- 有至少一个关键约束
- 引用不悬空

通过结构校验的最低标准见：

- [src/OpenClaw.Gateway/skills/ncrew-ontology/README.md](src/OpenClaw.Gateway/skills/ncrew-ontology/README.md)

## 4. 第二步：把 Slice 投影成第一个 Projection

### 4.1 第一份 projection 只选一个交付视图

第一份 projection 不要同时做四种 view。先选一个最贴近当前任务的输出形态。

推荐的选择规则：

- 你要约束模型用词、澄清规则、禁止假设：选 `prompt-constraint`
- 你要落实现对象、状态类型、运行时 guard：选 `domain-model`
- 你要落结构校验和 payload shape：选 `json-schema`
- 你要落执行步骤、生命周期、审批流：选 `workflow-contract`

如果你不确定，第一次通常优先从 `prompt-constraint` 开始，因为它最容易快速体现“语义边界已经进入 runtime”。

### 4.2 第一份 projection 的最小字段

按当前 runtime，第一份 `*.projection.json` 至少要有：

- `$schema`
- `mapping_policy`
- `prompt_projection`
- `open_questions`

最小骨架可以直接照抄下面这份：

```json
{
  "$schema": "../../../../../../../../docs/skill-projection-document.schema.json",
  "mapping_policy": {
    "unresolved_item_policy": "block_or_escalate",
    "prompt_assumption_policy": "disallow_unmapped_terms"
  },
  "prompt_projection": {
    "allowed_terms": [
      "new topic"
    ],
    "forbidden_assumptions": [
      "Do not invent rules outside the mapped projection."
    ],
    "required_clarifications": [],
    "reasoning_paths": [
      "new-topic -> prompt-constraint"
    ],
    "source_digest": [
      "Primary source: new-topic projection contract"
    ]
  },
  "delivery_artifacts": [],
  "dropped_items": [],
  "open_questions": []
}
```

如果你要从更完整的 producer 模板出发，参考：

- `templates/PROJECTION_TEMPLATE.json`
- `templates/PROJECTION_TEMPLATE.schema.json`

## 5. 第三步：在 Consumer Skill 下落第一个任务域

### 5.1 先决定任务域名，而不是先想 view 名

任务域是 consumer 侧任务域，不是 producer 侧 ontology 树的直接镜像。

任务域名建议满足三个条件：

- 面向业务任务，而不是面向内部字段
- 能对应一类稳定用户请求
- 未来允许并列多个交付视图

一个合格的任务域名示例：

- `order-review`
- `payment-reconciliation`
- `inventory-sync`

不太好的任务域名示例：

- `config`
- `rules`
- `data`

这些名字太泛，后面做 scoring 时很难稳定。

### 5.2 先只做一个任务域 + 一个 projection 文件

consumer 侧的第一轮目录建议就长这样：

```text
<skill>/contracts/projections/<producer>/
  contract-index.json
  order-review/
    order-review.prompt-constraint.projection.json
    README.md
    REVIEW.md
```

在当前仓库里，真实参考路径是：

- [src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology](src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology)

### 5.3 contract-index.json 第一轮只补三段

按当前 runtime，第一轮只需要补三类内容：

1. `topic_scoring.topics[]`
2. `target_view_scoring.views[]`
3. 顶层 `topics[]`

最小模板如下。

第一段：topic scoring

```json
{
  "domain_slug": "order-review",
  "primary_intent_signals": [
    "order review",
    "review policy",
    "approval rule"
  ],
  "supporting_signals": [
    "review guidance",
    "manual check"
  ],
  "explicit_artifact_signals": [
    "review policy",
    "review contract"
  ],
  "demote_when_competing_topic_signals": [
    "workflow graph",
    "json schema",
    "planner flow"
  ]
}
```

第二段：交付视图 scoring

```json
{
  "target_view": "prompt-constraint",
  "explicit_output_signals": [
    "review policy",
    "review guidance"
  ],
  "strong_signals": [
    "constraint",
    "guardrail"
  ],
  "supporting_signals": [
    "guidance"
  ],
  "demote_when_competing_view_signals": [
    "domain model",
    "json schema",
    "workflow contract"
  ]
}
```

第三段：路由表

```json
{
  "domain_slug": "order-review",
  "default_target_view": "prompt-constraint",
  "views": [
    {
      "target_view": "prompt-constraint",
      "status": "READY",
      "path": "order-review/order-review.prompt-constraint.projection.json"
    }
  ]
}
```

## 6. 第四步：把任务域写进 SKILL.md

如果你只改 `contract-index.json`，机器路由能工作，但人类提示会漂移。

所以第一轮至少还要同步修改 consumer `SKILL.md` 的三块内容：

- 任务域描述
- 一个 request mapping example
- 一条最重要的 multi-topic conflict rule

最小补法可以是：

```md
- `order-review`: requests about review policy, approval boundaries, and manual check rules.
```

再补一个例子：

```md
| "给订单审核补一份 review policy / reviewer guidance" | `order-review` | `prompt-constraint` | The request is about review boundaries, guardrails, and clarification policy. |
```

如果当前只有一个新任务域，那冲突规则至少要补与最近邻任务域的一条 pairwise 说明。

## 7. 第五步：验证最小闭环

第一轮不要追求“完整主题”，先验证以下 6 点：

1. `contract-index.json` 结构合法
2. `*.projection.json` 结构合法
3. `$schema` 指向正确
4. `views[].path` 与磁盘文件一致
5. `SKILL.md` 与任务域/交付视图说明同步
6. 编辑器诊断为零

如果环境里没有现成 JSON Schema 校验器，当前仓库至少要做到：

- 编辑器诊断为零
- `$schema` 相对路径正确
- `contract-index.json` 与新 projection 文件都能被检索到并对应起来

## 8. 第六步：再从最小任务域扩成完整主题

只有最小闭环跑通后，才建议扩成完整主题。

扩展顺序建议如下：

1. 先补第二个交付视图
2. 再补第三个交付视图
3. 补 `within_topic_overrides`
4. 补 `example_requests`
5. 补 `topic_conflict_resolution.pairwise_rules`
6. 再同步到 `SKILL.md`、`README.md`、`REVIEW.md`

当前仓库里，`memory-session` 就是一个已经走完这条路径的例子，可参考：

- [src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/memory-session/README.md](src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/memory-session/README.md)

## 9. 一份可直接照抄的执行顺序

如果你现在就要为一个新业务 Skill 落第一个 slice + 第一个任务域 + 第一个 projection，可以按这 10 步直接执行：

1. 先写一句话 slice request
2. 确定首个业务任务域名
3. 选首个交付视图
4. 在 `ncrew-ontology` 里落最小 slice
5. 从 slice 落第一个 projection 文件
6. 在 consumer 下创建任务域目录
7. 在 `contract-index.json` 中补三段最小路由定义
8. 在 consumer `SKILL.md` 中补任务域描述和一个映射示例
9. 检查 `$schema`、路径和编辑器诊断
10. 只有闭环跑通后，再扩成完整主题

## 10. 当前结论

从零开始做第一条 producer -> consumer -> runtime 链路，最容易失败的地方不是 schema，而是范围失控和同步缺失：

- slice 做太大，导致 projection 失去边界
- 一开始就做多 view，导致 runtime 没有稳定入口
- `contract-index.json` 改了，但 `SKILL.md` 没同步
- 任务域名太泛，后续 scoring 和冲突规则很难收敛

因此最稳的做法不是“一次设计完整体系”，而是：

- 先做第一份最小 slice
- 先落第一个任务域
- 先跑通第一个 projection
- 然后再迭代扩成完整主题

这也是当前仓库里已经验证过的最小落地路径。
