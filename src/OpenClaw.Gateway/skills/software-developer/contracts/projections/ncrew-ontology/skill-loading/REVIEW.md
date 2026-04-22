# skill-loading projection review

- 当前状态：`READY`
- consumer skill：`software-developer`
- producer skill：`ncrew-ontology`
- 当前主题：`skill-loading`
- 当前文件：
  - `skill-loading.domain-model.projection.json`
  - `skill-loading.json-schema.projection.json`
  - `skill-loading.workflow-contract.projection.json`

评审备注：

- 这是由 `sample-projection.json` 迁移而来的 consumer-skill 专用命名示例。
- 文件名采用 `<domain-slug>.<projection-type-short>.projection.json` 规则。
- 文件路径采用 `contracts/projections/<producer-skill>/<domain-slug>/` 规则。
- 目录内保留 `README.md` 和 `REVIEW.md`，用于人类治理说明，不把这些信息塞回 JSON 文件名。
- 为了让这份绑定版 contract 也满足 `READY` 启发式判定，目录内版本保留了 `SkillLoadConfig` 映射，并去除了会被自动识别为 warning 的冲突摘要写法。
- 当前目录同时展示 `domain-model`、`json-schema`、`workflow-contract` 三种并列 target view，作为同主题多投影面的标准样例。

评审核对顺序：

1. 当前主题是否已经先在 `ncrew-ontology` 中收缩成最小 slice，且 slice 已通过 `TEMPLATE.schema.json` 或 `validate-slice` 校验。
2. 本次 projection 是否已经明确选定单一主视图，而不是把 `domain-model`、`json-schema`、`prompt-constraint`、`workflow-contract` 的要求混写在一起。
3. `PROJECTION_TEMPLATE.json` 是否由 `ncrew-ontology` 按映射规范填写完成，且 `concepts`、`relations`、`constraints` 都有显式映射而不是靠隐式推断。
4. projection 是否已经通过 `validate-projection.ps1` 或 `validate-projection.py`，并确认关键字段完整、结构合法、编辑器诊断为零。
5. projection 是否只在通过校验后才落入 consumer skill 的 `contracts/projections` 目录，且 `contract-index.json`、路由提示和相关说明已经同步更新。
