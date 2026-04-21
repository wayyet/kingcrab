# ncrew-ontology 规范包

这个目录 references + scripts + templates + examples”的结构：根目录承担技能入口和总览，`references/` 存放参考资料与评审准则，`scripts/` 提供本地可执行校验器，`templates/` 和 `examples/` 负责交付格式与三态样例。

适用场景：

- 需要从大型 ontology / taxonomy / schema 中抽取当前任务相关的最小切片
- 需要把概念、关系、约束整理成稳定结构，供模型、程序或团队协作复用
- 需要让不同成员产出的 ontology slice 能按统一格式校验和审阅

---

## 目录结构

- `SKILL.md`：skill 主说明，定义何时触发、怎么切片、输出应覆盖什么。
- `README.md`：当前规范包总览。
- `templates/`
  - `TEMPLATE.md`：人工整理版模板。
  - `TEMPLATE.json`：工程化 JSON 模板。
  - `TEMPLATE.schema.json`：严格 JSON Schema。
  - `PROJECTION_TEMPLATE.json`：下游 codegen / prompt orchestration 投影模板。
  - `PROJECTION_TEMPLATE.schema.json`：projection 文件校验规则。
- `references/`
  - `FIELD_GUIDE.md`：字段语义说明。
  - `REVIEW_CHECKLIST.md`：同时覆盖 slice 和 projection 两层评审标准。
  - `DOWNSTREAM_MAPPING_GUIDE.md`：下游代码生成 / 提示词编排映射规范。
  - `SCHEMA_MIGRATION.md`：slice 与 projection 的 schema 版本迁移说明。
  - `SESSION_SUMMARY.md`：本次规范包从 slice skill 演进到完整治理包的总结文章。
- `examples/ready/`
  - `sample.json`：`READY` 基线样例。
  - `sample-projection.json`：基于 `sample.json` 的合法 projection 样例。
  - `sample.entry.md`：`sample.json` 的短入口页。
  - `sample-projection.entry.md`：`sample-projection.json` 的短入口页。
  - `sample.md`：`sample.json` 的人读评审版。
  - `sample-projection.md`：`sample-projection.json` 的人读评审版。
- `examples/warning/`
  - `warning-sample.json`：`WARNING` 黄灯样例。
  - `warning-sample.entry.md`：短入口页。
  - `warning-sample.md`：黄灯原因说明。
  - `warning-projection.json`：projection 黄灯样例。
  - `warning-projection.entry.md`：`warning-projection.json` 的短入口页。
  - `warning-projection.md`：`warning-projection.json` 的人读评审版。
- `examples/invalid/`
  - `invalid-sample.json`：`FAIL` 失败样例。
  - `invalid-sample.entry.md`：短入口页。
  - `invalid-sample.md`：失败路径评审说明。
  - `invalid-projection.json`：projection 失败样例。
  - `invalid-projection.entry.md`：`invalid-projection.json` 的短入口页。
  - `invalid-projection.md`：`invalid-projection.json` 的失败路径说明。
- `scripts/`
  - `validate-slice.ps1`：真实校验器，支持 `-ReviewMode`。
  - `validate-projection.ps1`：projection 真实校验器，支持 `-ReviewMode`。

---

## 核心思路

`ncrew-ontology` 的核心不是“把整份 ontology 拿出来”，而是围绕当前任务抽取最小可验证子图：

- `concepts`：当前任务真正依赖的概念
- `relations`：这些概念之间必须保留的关系
- `constraints`：会影响实现、评审或生成结果的规则边界
- `sources`：所有结论的可追溯依据

这让它既能服务人工评审，也能作为 JSON 校验、代码生成和提示词编排的稳定输入。

---

## 推荐使用顺序

### 路径一：先人工梳理，再结构化落地

1. 先看 `SKILL.md`，确认当前任务适不适合做 ontology slice。
2. 用 `templates/TEMPLATE.md` 梳理范围、来源、核心概念和约束。
3. 参考 `references/FIELD_GUIDE.md`，统一字段语义和填报口径。
4. 如需接到 codegen 或 prompt orchestration，补看 `references/DOWNSTREAM_MAPPING_GUIDE.md`，先明确投影规则。
5. 再把结果落到 `templates/TEMPLATE.json` 对应结构。
6. 需要形成下游交付物时，复制 `templates/PROJECTION_TEMPLATE.json` 填写 projection。
7. 用 `templates/PROJECTION_TEMPLATE.schema.json` 校验 projection 文件结构。
8. 最后使用 `templates/TEMPLATE.schema.json` 或 `scripts/validate-slice.ps1` 做 slice 校验。

### 路径二：直接生成工程化产物

1. 先看 `SKILL.md` 明确切片目标和边界。
2. 直接基于 `templates/TEMPLATE.json` 生成结果。
3. 遇到字段拿不准时，回看 `references/FIELD_GUIDE.md`。
4. 需要面向代码生成或提示词编排时，按 `references/DOWNSTREAM_MAPPING_GUIDE.md` 做投影。
5. 复制 `templates/PROJECTION_TEMPLATE.json`，形成可交付的 projection 文件。
6. 使用 `templates/PROJECTION_TEMPLATE.schema.json` 做 projection 结构校验。
7. 使用 `templates/TEMPLATE.schema.json` 做 slice 结构校验。

### 路径三：按三态样例做团队对齐

1. 先看 `examples/ready/sample.json -> sample.entry.md -> sample.md` 建立正向基线。
2. 再看 `examples/ready/sample-projection.json -> sample-projection.entry.md -> sample-projection.md`，理解 READY slice 如何落成可交付 projection。
3. 再看 `examples/warning/warning-sample.json -> warning-sample.entry.md -> warning-sample.md` 学会识别黄灯风险。
4. 再看 `examples/warning/warning-projection.json -> warning-projection.entry.md -> warning-projection.md` 理解 projection 为什么也可能是黄灯。
5. 最后看 `examples/invalid/invalid-sample.json -> invalid-sample.entry.md -> invalid-sample.md` 理解失败路径和报错边界。
6. 再看 `examples/invalid/invalid-projection.json -> invalid-projection.entry.md -> invalid-projection.md` 理解 projection 结构失败长什么样。
7. 回到 `references/REVIEW_CHECKLIST.md`，把三态样例统一成同一套评审口径。

---

## 快速选择

- 只想先讨论概念边界：用 `templates/TEMPLATE.md`
- 想输出给程序或流水线：用 `templates/TEMPLATE.json`
- 想把 slice 投影成 codegen / prompt orchestration 输入：用 `templates/PROJECTION_TEMPLATE.json`
- 想检查结果合不合法：用 `templates/TEMPLATE.schema.json`
- 想检查 projection 结构是否合法：用 `templates/PROJECTION_TEMPLATE.schema.json`
- 想直接从一个合法 projection 样例开始改：用 `examples/ready/sample-projection.json`
- 想看 projection 的短入口：用 `examples/ready/sample-projection.entry.md`
- 想看 projection 为什么算 READY：用 `examples/ready/sample-projection.md`
- 想统一字段口径：看 `references/FIELD_GUIDE.md`
- 想统一 slice 和 projection 两层评审标准：看 `references/REVIEW_CHECKLIST.md`
- 想看 schema 升级时模板、样例和校验器该怎么一起迁移：看 `references/SCHEMA_MIGRATION.md`
- 想快速理解这套规范为什么会演进成现在这套结构：看 `references/SESSION_SUMMARY.md`
- 想把 slice 稳定接到 codegen 或 prompt orchestration：看 `references/DOWNSTREAM_MAPPING_GUIDE.md`
- 想直接在真实样例上改：用 `examples/ready/sample.json`（`READY`）
- 想看 `READY` 的短入口：用 `examples/ready/sample.entry.md`
- 想看 `READY` 的完整解释：用 `examples/ready/sample.md`
- 想演示“合法但仍需 review”：用 `examples/warning/warning-sample.json`（`WARNING`）
- 想看 `WARNING` 的短入口：用 `examples/warning/warning-sample.entry.md`
- 想看 `WARNING` 的完整解释：用 `examples/warning/warning-sample.md`
- 想看 projection 的黄灯样例：用 `examples/warning/warning-projection.json`
- 想看 projection 黄灯的短入口：用 `examples/warning/warning-projection.entry.md`
- 想看 projection 为什么是 WARNING：用 `examples/warning/warning-projection.md`
- 想验证失败报错是否可读：用 `examples/invalid/invalid-sample.json`（`FAIL`）
- 想看 `FAIL` 的短入口：用 `examples/invalid/invalid-sample.entry.md`
- 想逐条 review 失败点：用 `examples/invalid/invalid-sample.md`
- 想看 projection 的失败样例：用 `examples/invalid/invalid-projection.json`
- 想看 projection 失败的短入口：用 `examples/invalid/invalid-projection.entry.md`
- 想看 projection 为什么是 FAIL：用 `examples/invalid/invalid-projection.md`
- 想单独分享“该不该用这套规范”的判定入口：看 `references/DECISION_GUIDE.md`
- 想一条命令校验样例或自定义 slice：用 `scripts/validate-slice.ps1`
- 想一条命令校验样例或自定义 projection：用 `scripts/validate-projection.ps1`

---

## 红黄绿决策图

如果团队在使用前只想先判断一件事: `ncrew-ontology` 到底适不适合当前场景，先看 `references/DECISION_GUIDE.md`。

这份独立指南专门回答三类问题：

- 当前任务到底该不该做 ontology slice。
- 当前阶段能不能直接用本规范沉淀结果。
- 当前产出更接近 `READY` 候选、`WARNING` 草案，还是暂时不该进入本规范。

判定口径保持不变：

- `绿灯`：适合按本规范继续做 slice / projection / review。
- `黄灯`：可以用，但只能作为受控草案、局部治理视图，或需要补证据后再推进。
- `红灯`：当前不适合，应先补主题、补来源，或改用其他 ontology / knowledge modeling 方法。

---

## 最低交付标准

一份可接受的 ontology slice，至少应满足：

- 明确当前任务和切片主题
- 明确纳入范围和排除范围
- 至少有一个可追溯来源
- 至少有一个定义清晰的核心概念
- 概念、关系、约束之间不存在引用断裂
- 已显式记录冲突、歧义或不确定项
- 能通过 `templates/TEMPLATE.schema.json` 校验

---

## ReviewMode 说明

`scripts/validate-slice.ps1 -ReviewMode` 会在结构校验结果后，额外输出一个启发式判定：`READY / WARNING / FAIL`。

当前启发式结论含义：

- `FAIL`：结构校验未通过。
- `READY`：结构校验通过，且没有命中当前 warning 信号；内置 `examples/ready/sample.json` 也直接视为 ready baseline。
- `WARNING`：结构校验通过，但命中了 warning 信号；内置 `examples/warning/warning-sample.json` 也直接视为 yellow-light baseline。

当前会触发 `WARNING` 的信号：

- `sources` 中没有任何 `trust_level = high` 的来源。
- `sources` 中存在任意 `trust_level = low` 的来源。
- `conflicts` 中存在 `status = open` 或 `status = deferred` 的冲突。
- `ambiguities` 中存在 `status = open` 或 `status = deferred` 的歧义。
- `uncertainties` 数组非空。

这套规则用于快速分流，不替代 `references/REVIEW_CHECKLIST.md` 的人工评审。

---

## 校验脚本用法

如果当前目录就是本技能根目录，可直接执行：

```powershell
# 校验默认 READY 基线样例
.\scripts\validate-slice.ps1

# 校验单个自定义 slice
.\scripts\validate-slice.ps1 .\my-slice.json

# 一次校验多个 slice
.\scripts\validate-slice.ps1 .\examples\ready\sample.json .\team-a.json .\team-b.json

# 查看半合法样例（应通过校验，但仍需人工 review）
.\scripts\validate-slice.ps1 .\examples\warning\warning-sample.json

# 输出结构层结论，并提示人工 review 入口
.\scripts\validate-slice.ps1 .\examples\warning\warning-sample.json -ReviewMode

# 输出 READY / WARNING / FAIL 的启发式提示
.\scripts\validate-slice.ps1 .\examples\ready\sample.json -ReviewMode

# 查看失败样例的报错输出
.\scripts\validate-slice.ps1 .\examples\invalid\invalid-sample.json
```

如果需要显式指定 schema 路径：

```powershell
.\scripts\validate-slice.ps1 .\examples\ready\sample.json -SchemaPath .\templates\TEMPLATE.schema.json
```

在仓库根目录执行：

```powershell
.\scripts\validate-ontology-slice.ps1
.\scripts\validate-ontology-slice.ps1 .\path\to\team-slice.json
.\scripts\validate-ontology-slice.ps1 .\src\OpenClaw.Gateway\skills\ncrew-ontology\examples\invalid\invalid-sample.json
```

根目录包装脚本仍保持“普通结构校验入口”的角色，不额外承载 `-ReviewMode`。

### Projection 校验

如果当前目录就是本技能根目录，可直接执行：

```powershell
# 校验默认 READY projection 基线样例
.\scripts\validate-projection.ps1

# 校验单个自定义 projection
.\scripts\validate-projection.ps1 .\my-projection.json

# 一次校验多个 projection
.\scripts\validate-projection.ps1 .\examples\ready\sample-projection.json .\team-a-projection.json .\team-b-projection.json

# 查看黄灯 projection 样例（应通过结构校验，但仍需人工 review）
.\scripts\validate-projection.ps1 .\examples\warning\warning-projection.json

# 输出结构层结论，并提示 projection review 入口
.\scripts\validate-projection.ps1 .\examples\warning\warning-projection.json -ReviewMode

# 输出 projection 的 READY / WARNING / FAIL 启发式提示
.\scripts\validate-projection.ps1 .\examples\ready\sample-projection.json -ReviewMode

# 查看 projection 失败样例的报错输出
.\scripts\validate-projection.ps1 .\examples\invalid\invalid-projection.json
```

如果需要显式指定 projection schema 路径：

```powershell
.\scripts\validate-projection.ps1 .\examples\ready\sample-projection.json -SchemaPath .\templates\PROJECTION_TEMPLATE.schema.json
```

在仓库根目录执行：

```powershell
.\scripts\validate-ontology-projection.ps1
.\scripts\validate-ontology-projection.ps1 .\path\to\team-projection.json
.\scripts\validate-ontology-projection.ps1 .\src\OpenClaw.Gateway\skills\ncrew-ontology\examples\invalid\invalid-projection.json
```

根目录 projection 包装脚本同样保持“普通结构校验入口”的角色，不额外承载 `-ReviewMode`。

---

## Schema 版本迁移说明

迁移规则已单独整理到 `references/SCHEMA_MIGRATION.md`，README 这里只保留最小入口。

当前规范包仍使用两条独立版本线：

- slice：`schema_version`
- projection：`projection_version`

当前两者都固定为 `1.0.0`。在没有明确结构变更前，不要单独修改任何样例、模板或产出文件里的版本号。

如果后续真的要升级版本，优先阅读 `references/SCHEMA_MIGRATION.md`，按“先 schema、再模板、再样例、再文档、最后校验”的顺序迁移。

---

## 后续可扩展方向

- 增加自动校验脚本或 CI 校验入口
- 增加可机读的 projection 模板或示例
