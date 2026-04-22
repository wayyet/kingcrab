# task-execution projection review

- 当前状态：`READY`
- consumer skill：`software-developer`
- producer skill：`ncrew-ontology`
- 当前主题：`task-execution`
- 当前文件：
	- `task-execution.domain-model.projection.json`
	- `task-execution.prompt-constraint.projection.json`
	- `task-execution.workflow-contract.projection.json`

评审备注：

- 该主题现在同时覆盖 `domain-model`、`prompt-constraint`、`workflow-contract` 三种 target view。
- `domain-model` 适合实现对象和执行 guard。
- `prompt-constraint` 适合提示词、计划与 review 模式下的执行边界输入。
- `workflow-contract` 适合执行步骤、review checkpoint 和转换条件治理。