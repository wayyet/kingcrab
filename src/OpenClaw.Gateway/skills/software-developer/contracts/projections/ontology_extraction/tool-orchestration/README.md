# tool-orchestration projection contract

这个目录承载 `software-developer` 消费的 `tool-orchestration` 主题 projection。

当前主文件：

- `tool-orchestration.domain-model.projection.json`
- `tool-orchestration.prompt-constraint.projection.json`
- `tool-orchestration.workflow-contract.projection.json`

用途：

- 把 orchestration 配置、参与对象和 source tier 落成领域模型
- 约束 prompt 侧的路由术语、推理路径和 guardrail
- 把 discovery、source precedence 和 eligibility filtering 映射成显式流程步骤
- 作为 workflow 或 planner 侧的可执行契约输入