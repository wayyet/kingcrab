# skill-loading projection contract

这个目录演示 `software-developer` 作为 consumer skill 时，如何按约定消费 `ncrew-ontology` 生成的 projection 文件。

当前主文件：

- `skill-loading.domain-model.projection.json`
- `skill-loading.json-schema.projection.json`
- `skill-loading.workflow-contract.projection.json`

这份文件由 `ncrew-ontology` 生成并迁移而来，对应上游基线是：

- `../../../../../ncrew-ontology/examples/ready/sample-projection.json`

当前目录的作用不是存示例，而是演示一个 consumer skill 在自身目录中保存绑定版 projection contract 的落法。

当前这一主题已经展示了“同一主题下并列多投影面”的完整样式：

- `domain-model`：面向实现对象和运行时 guard
- `json-schema`：面向配置校验与结构约束
- `workflow-contract`：面向执行步骤、边和前置条件

推荐读取顺序：

1. 先按目标选读对应 projection 文件
2. 再读 `REVIEW.md` 看当前治理结论
3. 如需回溯上游语义，再回到 `source_slice.path` 指向的 slice