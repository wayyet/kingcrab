# tool-orchestration projection review

- 当前状态：`READY`
- consumer skill：`software-developer`
- producer skill：`ncrew-ontology`
- 当前主题：`tool-orchestration`
- 当前文件：
	- `tool-orchestration.domain-model.projection.json`
	- `tool-orchestration.prompt-constraint.projection.json`
	- `tool-orchestration.workflow-contract.projection.json`

评审备注：

- 该主题现在同时覆盖 `domain-model`、`prompt-constraint`、`workflow-contract` 三种 target view。
- `domain-model` 适合路由对象、source tier 和 orchestration policy 的实现建模。
- `prompt-constraint` 适合 prompt 侧的编排 guardrail 和术语边界。
- `workflow-contract` 适合 planner、编排步骤和执行前置条件治理。