# task-execution projection contract

这个目录承载 `software-developer` 消费的 `task-execution` 主题 projection。

当前主文件：

- `task-execution.domain-model.projection.json`
- `task-execution.prompt-constraint.projection.json`
- `task-execution.workflow-contract.projection.json`

用途：

- 让实现类任务可以落成领域对象与运行时 guard
- 约束实现类任务中的术语边界
- 约束解释和计划中的推理路径
- 为 prompt 或 review 模式保留上游 ontology 的 guardrail
- 把执行计划、review 检查点和转换步骤映射成 workflow contract