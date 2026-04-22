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

当前默认选择规则：

- 优先选 `READY` contract
- 优先精确匹配目标 target view
- 找不到精确 target view 时，才考虑主题默认 view
- 如果没有安全匹配，不伪造 contract

推荐读取方式：

1. 先读 `contract-index.json` 决定主题和 target view
2. 再按主题进入对应目录
3. 再读取 `<domain-slug>.<projection-type-short>.projection.json`
4. 最后查看本目录的 `REVIEW.md` 决定是否可直接消费
