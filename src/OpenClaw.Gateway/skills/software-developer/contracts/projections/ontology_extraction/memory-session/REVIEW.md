# memory-session projection review

- 当前状态：`READY`
- consumer skill：`software-developer`
- producer skill：`ontology_extraction`
- 当前主题：`memory-session`
- 当前文件：
  - `memory-session.domain-model.projection.json`
  - `memory-session.json-schema.projection.json`
  - `memory-session.prompt-constraint.projection.json`
  - `memory-session.workflow-contract.projection.json`

评审备注：

- 这已经是新增第 4 个 topic 的完整主题，而不是仅供路由打通的最小骨架。
- 当前主题同时提供 `domain-model`、`json-schema`、`prompt-constraint`、`workflow-contract` 四种 target view。
- 默认视图选择 `domain-model`，便于在未显式指定产物形态时优先落到实现对象和运行时边界。
- 文件命名仍采用 `<domain-slug>.<projection-type-short>.projection.json` 规则。
- 文件路径仍采用 `contracts/projections/<producer-skill>/<domain-slug>/` 规则。
- 各 target view 的评分信号与 topic-specific bonuses 已同步接入 `contract-index.json`。

评审核对顺序：

1. 当前主题是否已经先在 `ontology_extraction` 中收缩成最小 slice，且 slice 已按所在层级通过校验：在 `ontology_extraction` 技能根目录使用 `validate-slice`，在仓库根目录使用 `validate-ontology-slice`。
2. 本次 projection 是否已经明确选定单一主视图，而不是把 `domain-model`、`json-schema`、`prompt-constraint`、`workflow-contract` 的要求混写在一起。
3. `PROJECTION_TEMPLATE.json` 是否由 `ontology_extraction` 按映射规范填写完成，且 `concepts`、`relations`、`constraints` 都有显式映射而不是靠隐式推断。
4. projection 是否已经按所在层级通过校验：在 `ontology_extraction` 技能根目录使用 `validate-projection.py`，在仓库根目录使用 `validate-ontology-projection.py`；并确认关键字段完整、结构合法、编辑器诊断为零。
5. projection 是否只在通过校验后才落入 consumer skill 的 `contracts/projections` 目录，且 `contract-index.json`、路由提示和相关说明已经同步更新。
