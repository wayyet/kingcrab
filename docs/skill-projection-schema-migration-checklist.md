# Producer Template Schema 与 Runtime Contract Schema 最小迁移清单

本文档面向团队协作，专门回答一个高频问题：当我们在新增 topic、补 projection、迁移业务 Skill 或调整校验链路时，`producer 模板 schema` 和 `runtime contract schema` 分别该在什么阶段使用，哪些文件必须跟着改，哪些文件不应该混用。

它不是字段手册，也不是 schema 设计文档，而是一份固定流程清单。

相关文档：

- `docs/skill-projection-contracts-schema.md`
- `docs/ncrew-ontology-slice-to-skill-method.md`
- `src/OpenClaw.Gateway/skills/ncrew-ontology/references/SCHEMA_MIGRATION.md`

---

## 一、先固定边界，不要混用

团队在迁移或新增 contract 前，先把两类 schema 的职责固定下来：

- `producer 模板 schema`
  - `src/OpenClaw.Gateway/skills/ncrew-ontology/templates/TEMPLATE.schema.json`
  - `src/OpenClaw.Gateway/skills/ncrew-ontology/templates/PROJECTION_TEMPLATE.schema.json`
  - 用途：校验 producer 侧产出的 slice 与 projection 模板文件
  - 场景：ontology 收缩、投影生成、ready/warning/invalid 样例维护、producer 模板升级

- `runtime contract schema`
  - `docs/skill-projection-contract-index.schema.json`
  - `docs/skill-projection-document.schema.json`
  - 用途：校验 consumer skill 下真正进入 `SkillLoader` / `SkillProjectionResolver` 的 contract 文件
  - 场景：`contract-index.json` 接线、`*.projection.json` 路由消费、runtime 阻断与 patch 行为、自定义业务 Skill 接入

固定规则只有一条：

1. 在 producer 侧生成与整理时，使用模板 schema。
2. 在 consumer 侧落盘并进入 runtime 前，使用 runtime contract schema。
3. 不要用 `PROJECTION_TEMPLATE.schema.json` 替代 runtime `*.projection.json` 的主校验入口。
4. 不要把 `docs/skill-projection-document.schema.json` 当成 producer 模板设计的唯一约束来源。

---

## 二、固定流程

下面这 8 步是团队协作时建议固定执行的最小流程。

### 第 1 步：先确认当前变更属于哪一层

先回答这一个问题：当前要改的是 producer 产物生成方式，还是 runtime 消费方式。

- 如果你改的是 ontology slice 结构、projection 模板结构、样例字段、生成映射方式，归为 producer 层。
- 如果你改的是 `contract-index.json`、consumer `*.projection.json`、路由字段、阻断字段、runtime patch 可见字段，归为 runtime 层。

如果这个问题答不清，不要开始改文件。因为最常见的错误就是还没分层，先把 schema 路径改了。

### 第 2 步：producer 层先完成模板校验

只要变更涉及 producer 产出，就先确保 producer 层通过模板校验：

```powershell
# slice
.\scripts\validate-ontology-slice.ps1

# projection template output
.\scripts\validate-ontology-projection.ps1
```

此阶段的目标不是让 runtime 直接消费，而是确保：

1. slice 结构合法。
2. projection 模板映射完整。
3. ready / warning / invalid 样例仍然保持各自定位。

### 第 3 步：再决定哪些 producer 字段需要进入 runtime contract

producer projection 合法后，不要直接整份复制到 consumer 目录。先明确：

1. 哪些字段是 runtime 真正读取的。
2. 哪些字段只是 producer 侧治理信息。
3. 哪些字段可以保留为 advisory metadata，但不能假设 runtime 已消费。

当前最小判断口径：

- `mapping_policy`、`prompt_projection`、`delivery_artifacts`、`dropped_items`、`open_questions` 是 runtime `*.projection.json` 的核心入口。
- `projection`、`concept_mappings`、`relation_mappings`、`constraint_mappings`、`meta` 可以继续保留，但不能因此假设它们已经直接参与 route 选择。

### 第 4 步：consumer 侧落盘时切换到 runtime schema

一旦文件进入 `contracts/projections/<producer>/`，就必须切换到 runtime contract 口径。

固定路径边界：

```text
<skill>/contracts/projections/<producer>/contract-index.json
<skill>/contracts/projections/<producer>/<topic>/<topic>.<view>.projection.json
```

固定 `$schema` 边界：

1. `contract-index.json` 指向 `docs/skill-projection-contract-index.schema.json`
2. runtime `*.projection.json` 指向 `docs/skill-projection-document.schema.json`

只要文件已经进入 consumer `contracts/projections` 目录，就不要继续保留模板 schema 作为主校验基线。

### 第 5 步：运行 runtime contract 校验命令

团队评审前至少执行下面两条命令：

```powershell
.\scripts\validate-skill-projection-contract-index.ps1
.\scripts\validate-skill-projection-document.ps1
```

它们对应的固定职责是：

1. `validate-skill-projection-contract-index.*` 只校验 runtime `contract-index.json`
2. `validate-skill-projection-document.*` 只校验 runtime `*.projection.json`

如果这两条命令没有通过，不要用“producer 模板已经合法”来替代 runtime 验证结果。

### 第 6 步：再核对 loader / resolver 的真实消费边界

schema 通过不等于 runtime 一定会消费所有字段。进入评审前，再核对一次：

1. `SkillLoader` 是否真的读取了新增的 `contract-index.json` 字段。
2. `SkillProjectionResolver` 是否真的读取了新增的 `*.projection.json` 字段。
3. 如果字段只是 schema 允许、但 runtime 还没消费，文档里必须明确标成 advisory-only。

这一步的目的不是让 schema 更严格，而是防止团队把“允许出现”误解成“已进入控制面”。

### 第 7 步：同步更新文档入口，而不是只改 JSON

当使用边界变化时，至少同步更新下面一类入口文档：

1. 总览类：`docs/ontology-slice-projection-summary.md`
2. 字段/口径类：`docs/skill-projection-contracts-schema.md`
3. 方法类：`docs/ncrew-ontology-slice-to-skill-method.md`
4. 版本迁移类：`src/OpenClaw.Gateway/skills/ncrew-ontology/references/SCHEMA_MIGRATION.md`

最小要求不是全部重写，而是让团队能在总览文档里看到：

- producer 模板 schema 负责什么
- runtime contract schema 负责什么
- 当前仓库应跑哪几条命令

### 第 8 步：PR 按同一口径评审

如果一个 PR 同时涉及 producer 模板和 runtime contract，评审时按下面顺序问：

1. 这次改动首先属于 producer 层还是 runtime 层。
2. producer 模板校验是否已经通过。
3. runtime contract 校验是否已经通过。
4. `$schema` 路径是否已经按 consumer / producer 边界切换。
5. loader / resolver 是否真的支持新增字段。
6. 文档入口是否已经同步收口。

只回答“JSON 没报错”不算通过评审。

---

## 三、团队最容易犯错的 5 个点

1. 把 `PROJECTION_TEMPLATE.schema.json` 继续挂在 consumer `*.projection.json` 上，导致 producer 和 runtime 两条边界继续混在一起。
2. 只补 `contract-index.json`，不跑 runtime projection 校验，最后 loader 能发现 contract，但 resolver 读不稳。
3. 只看 schema 是否允许，不看 `SkillLoader` / `SkillProjectionResolver` 是否真的消费该字段。
4. 只改 JSON 文件，不改总览文档，导致团队成员继续沿用旧命令和旧路径。
5. 直接从 consumer 目录改 projection，而不回到 producer 侧确认 slice 和投影映射是否仍然成立。

---

## 四、团队固定检查口令

如果团队想把这件事压缩成一段固定检查口令，可以统一成下面这 6 句：

1. 先分层：这次改的是 producer，还是 runtime。
2. 先验 producer：slice / projection 模板先过模板校验。
3. 再切 runtime：进入 `contracts/projections` 后一律切到 runtime schema。
4. 必跑两条命令：`validate-skill-projection-contract-index` 和 `validate-skill-projection-document`。
5. 再看代码：schema 允许不等于 loader / resolver 已消费。
6. 最后收文档：至少把总览入口和 schema 口径同步掉。

---

## 五、最小通过标准

一份 PR 如果声称“已经完成 producer 模板 schema 与 runtime contract schema 的边界迁移”，最低要满足：

1. producer 文件仍使用模板 schema 校验通过。
2. consumer `contract-index.json` 使用 runtime contract index schema 校验通过。
3. consumer `*.projection.json` 使用 runtime projection schema 校验通过。
4. 至少一份总览或方法文档已经明确新的使用边界。
5. runtime 真实消费边界没有再被文档表述混淆。

如果缺其中任一项，就不应称为“边界已经固定”。
