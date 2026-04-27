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

评审核对顺序：

1. 当前主题是否已经先在 `ncrew-ontology` 中收缩成最小 slice，且 slice 已按所在层级通过校验：在 `ncrew-ontology` 技能根目录使用 `validate-slice`，在仓库根目录使用 `validate-ontology-slice`。
2. 本次 projection 是否已经明确选定单一主视图，而不是把 `domain-model`、`json-schema`、`prompt-constraint`、`workflow-contract` 的要求混写在一起。
3. `PROJECTION_TEMPLATE.json` 是否由 `ncrew-ontology` 按映射规范填写完成，且 `concepts`、`relations`、`constraints` 都有显式映射而不是靠隐式推断。
4. projection 是否已经按所在层级通过校验：在 `ncrew-ontology` 技能根目录使用 `validate-projection.ps1` / `validate-projection.py`，在仓库根目录使用 `validate-ontology-projection.ps1` / `validate-ontology-projection.py`；并确认关键字段完整、结构合法、编辑器诊断为零。
5. projection 是否只在通过校验后才落入 consumer skill 的 `contracts/projections` 目录，且 `contract-index.json`、路由提示和相关说明已经同步更新。
