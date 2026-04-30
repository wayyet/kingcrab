# task-execution projection review

- 当前状态：`READY`
- consumer skill：`software-developer`
- producer skill：`ontology_extraction`
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

评审核对顺序：

1. 当前主题是否已经先在 `ontology_extraction` 中收缩成最小 slice，且 slice 已按所在层级通过校验：在 `ontology_extraction` 技能根目录使用 `validate-slice`，在仓库根目录使用 `validate-ontology-slice`。
2. 本次 projection 是否已经明确选定单一主视图，而不是把 `domain-model`、`json-schema`、`prompt-constraint`、`workflow-contract` 的要求混写在一起。
3. `PROJECTION_TEMPLATE.json` 是否由 `ontology_extraction` 按映射规范填写完成，且 `concepts`、`relations`、`constraints` 都有显式映射而不是靠隐式推断。
4. projection 是否已经按所在层级通过校验：在 `ontology_extraction` 技能根目录使用 `validate-projection.py`，在仓库根目录使用 `validate-ontology-projection.py`；并确认关键字段完整、结构合法、编辑器诊断为零。
5. projection 是否只在通过校验后才落入 consumer skill 的 `contracts/projections` 目录，且 `contract-index.json`、路由提示和相关说明已经同步更新。
