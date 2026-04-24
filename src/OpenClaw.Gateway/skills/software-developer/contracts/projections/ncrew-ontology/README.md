# software-developer projection contracts

这个目录是 `software-developer` 作为 consumer skill 时，消费 `ncrew-ontology` projection contract 的总入口。

选择入口：

- 机器选择：`contract-index.json`
- 人类总览：当前 README

当前采用的结构是：

```text
contracts/projections/ncrew-ontology/<domain-slug>/
```

每个主题目录下至少包含：

- 1 个主 projection 文件
- `README.md`
- `REVIEW.md`

当前多主题骨架：

- `skill-loading/`：面向 `domain-model` 的实现契约
- `task-execution/`：面向 `prompt-constraint` 的提示词与执行边界契约
- `tool-orchestration/`：面向 `workflow-contract` 的执行流契约

## Runtime 权威规则

当前目录下真正影响 runtime 行为的权威入口是 `contract-index.json`，而不是当前 README 的自然语言摘要。

当前可从 runtime 已消费字段中概括出的规则是：

- topic / target view 选择由 runtime 按请求做评分解析。
- `prefer_ready_only = true` 时，只在 `READY` candidates 中继续选择；没有可用候选时阻断。
- target view 会结合显式输出信号、view 信号和 topic 默认 view bonus 做评分，而不是由 README 手工指定固定回退顺序。
- `block_on_open_questions = true` 时，命中的 projection 若仍有 blocking open questions，会被阻断而不是继续消费。
- 如果 route 结果歧义、文件缺失、结构无效或不存在安全匹配，runtime 会阻断，而不是伪造 contract。

如需确认完整规则，以 `contract-index.json`、projection 文档本身，以及 runtime 实现为准。

## 人类阅读提示

当前 README 只承担人工总览职责，用来帮助快速定位应该先看哪类文件，不应被当作 route algorithm 的完整定义。

推荐读取方式：

1. 先读 `contract-index.json` 决定主题和 target view
2. 再按主题进入对应目录
3. 再读取 `<domain-slug>.<projection-type-short>.projection.json`
4. 最后查看本目录的 `REVIEW.md` 决定是否可直接消费
