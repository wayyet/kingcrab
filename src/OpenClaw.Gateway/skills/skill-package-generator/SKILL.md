---
name: skill-package-generator
description: 根据用户会话描述或上传的 skill 文件，抽取统一 SkillSpec，生成可直接运行的业务技能包，并仅写入当前沙箱 skills/ 目录。
metadata: {"openclaw":{"emoji":"🧩"}}
---

# Skill Package Generator

当用户要求根据描述、Markdown、文本、JSON、YAML 或 zip 文件创建、更新、合并、规范化业务技能包时，使用本技能。

本技能的职责是生成以 `SKILL.md` 为核心的业务技能包。核心思想是先把非结构化输入抽取为统一的 SkillSpec，再映射到固定模板，生成后通过最小质量校验，通过后才落盘。

设计参考 `nuwa-skill` 的可迁移原则：入口分流、先建自包含目录、多源素材归档、结构化提炼、模板填充、质量验证和二次精炼。这里不做人设蒸馏，而是把这些原则改造成业务 skill 包生成流水线。

## 输入类型

支持三类输入：

- 会话描述：例如“它要会处理退货咨询、订单查询”。
- 上传文件：Markdown、文本、JSON、YAML 或 zip。
- 混合输入：上传文件作为基线，会话描述作为增量补充。

同时读取当前沙箱 `skills/` 目录快照，用于同名覆盖、异名新增和去重。

## 统一中间模型

所有输入必须先归一为 SkillSpec：

```json
{
  "name": "refund-order-assistant",
  "display_name": "退货与订单查询助手",
  "description": "处理退货咨询、订单状态查询、进度追踪",
  "triggers": ["退货", "订单查询", "物流进度"],
  "capabilities": [
    {
      "id": "refund_apply",
      "goal": "受理退货申请",
      "inputs": ["订单号", "退货原因"],
      "outputs": ["受理结果", "下一步指引"],
      "fallback": "信息不足时引导补充"
    }
  ],
  "boundaries": [
    "不承诺退款时效",
    "不处理财务打款"
  ],
  "examples": [
    {
      "user": "我要退货",
      "assistant": "请提供订单号和退货原因，我来为你发起申请。"
    }
  ],
  "source": "conversation|upload",
  "version": "1.0.0"
}
```

## 执行流程

### Phase 0: 入口分流

先判断请求路径：

- 直接路径：用户已经给出明确业务域、触发词、能力或上传了候选 skill 文件。
- 模糊路径：用户只说“帮我做个 skill”“把这些能力整理成 skill”，但缺少业务域、能力边界或产物目标。
- 更新路径：现有 `skills/<skill_slug>/` 已存在，需要同名覆盖、增量合并或跳过。

直接路径继续 Phase 0.5。模糊路径先做需求诊断：列出最多 3 个候选业务域、每个候选域的触发词和预计能力，要求用户确认后再落盘。

### Phase 0.5: 创建自包含技能目录

在解析和渲染前先确定目标目录：

```text
skills/<skill_slug>/
  SKILL.md
  metadata.json
  references/
    source-digest.md
    extraction-notes.md
    quality-report.md
  contracts/
    projections/
      ncrew-ontology/
        contract-index.json
        README.md
        <domain-slug>/
          <domain-slug>.<projection-type-short>.projection.json
          README.md
          REVIEW.md
```

目录必须自包含：生成 skill 所需的摘要、来源、质量报告和 projection contract 都放在该 skill 目录内。不要把生成过程依赖散落到 `config/`、`ontology/`、`external/` 或临时目录。

### Phase 1: 输入采集与来源归档

对不同输入执行不同采集策略：

- 会话描述：保留用户原话，写入 `references/source-digest.md` 的 conversation source 区块。
- 上传文件：解析 Markdown、文本、JSON、YAML；zip 递归读取候选 skill 文件；保留文件清单、解析结论和不可解析项。
- 混合输入：上传文件作为基线，会话描述作为增量补充，不用会话描述覆盖文件里更明确的能力定义。

来源归档必须记录：来源类型、可信度、抽取到的能力、未决问题和被丢弃内容。不要把 token、密钥、密码、连接串写入归档。

### Phase 1.5: 采集质量检查点

进入结构化归一前检查：

- 至少有一个可解释的业务域或能力域。
- 至少能推导出一个 trigger。
- 至少能推导出一个 capability。
- 所有敏感字段已脱敏或阻断。
- 上传文件不可解析时，已记录失败原因和补全建议。

检查失败时，不写正式 skill。可以返回草稿 SkillSpec 和待补全项。

### Phase 2: SkillSpec 提炼

把所有来源统一提炼为 SkillSpec。提炼时执行三重验证：

1. 复现性：能力是否在用户描述、文件结构或示例中至少有明确依据。
2. 可执行性：能力是否能落到输入、输出、失败兜底和处理流程。
3. 排他性：能力是否足够具体，不只是“通用助手”“回答问题”这类空泛描述。

每个 capability 都要保留来源摘要和归属理由，写入 `references/extraction-notes.md`。

### Phase 3: Skill 构建

按模板填充产物：

- `SKILL.md`：业务技能说明、触发条件、能力清单、处理流程、边界、不做事项、对话示例、Projection Contracts 消费章节。
- `metadata.json`：完整 SkillSpec、质量门、来源模式、版本和生成策略。
- `contracts/projections/ncrew-ontology/**`：让生成出来的业务 skill 按本仓库 consumer skill 方式消费 `ncrew-ontology` projection。
- `references/source-digest.md`、`references/extraction-notes.md`、`references/quality-report.md`：让生成过程可审阅、可复盘、可迁移。

模板参考文件位于本技能目录：

- `references/generated-skill-template.md`
- `references/projection-contract-template.md`
- `references/quality-checklist.md`

### Phase 4: 质量验证

生成后必须执行最小验证：

- Sanity Check：用 2-3 个典型用户请求检查触发词、能力选择和输出边界是否匹配。
- Edge Case：用 1 个信息不足或越界请求检查是否会补槽、拒绝或转交，而不是编造结果。
- Contract Check：确认 `contract-index.json` 的 path 指向真实 projection 文件，projection 含 `prompt_projection`、`delivery_artifacts`、`dropped_items`、`open_questions`。
- Safety Check：确认产物不含明文 token、密钥、密码、连接串。
- Self-contained Check：复制整个 `skills/<skill_slug>/` 后仍能独立被 loader 发现和人工审阅。

结果写入 `references/quality-report.md`。未通过时阻止落盘或保留草稿并明确失败原因。

### Phase 5: 双视角精炼

质量验证通过后做两轮自检，不需要额外写入主 agent：

- 生成器视角：检查结构完整、模板变量无残留、文件路径正确、质量门完整。
- 消费者视角：检查生成出来的业务 skill 是否容易触发、边界清楚、projection contract 可被 runtime 自动发现。

如果两轮自检发现问题，回到 Phase 2 或 Phase 3 修正，再重新执行 Phase 4。

## 兼容执行清单

1. 输入判型：判断是会话描述、上传文件还是混合输入。
2. 内容解析：
   - 会话描述：抽取触发词、能力项、输入、输出、边界和示例。
   - 上传文件：解析 Markdown、文本、JSON、YAML，并映射到 SkillSpec。
   - zip 文件：递归读取候选 skill 文件，优先保留原文件能力定义，再结构化归一。
3. 结构化归一：补齐缺省字段，规范化 `name`、`display_name`、`description`、`triggers`、`capabilities`、`boundaries`、`examples`、`source`、`version`。
4. Slug 生成：由能力名称生成 `skill_slug`，使用小写短横线，只保留字母、数字和短横线。
5. 冲突处理：读取现有 `skills/`，按同名覆盖、异名新增规则合并。多能力输入可按业务域合并为一个技能，也可在用户启用多技能拆分时按业务域生成多个技能。
6. Projection 契约生成：为产出的业务 skill 生成 consumer-skill projection 目录，使生成后的 skill 能消费 `ncrew-ontology` 创建的 projection。
7. 模板渲染：按固定业务技能模板生成 `SKILL.md`，同时生成 `metadata.json` 与 projection contract 伴随文件。
8. 质量校验：未通过时阻止落盘，返回失败原因。
9. 写入产物：只写入 `skills/<skill_slug>/` 下的技能文件、metadata 与 projection contract 文件。
10. 返回摘要：输出 `technical_artifact` 与 `user_summary`。

## SKILL.md 业务模板

生成的业务技能必须使用以下结构：

```markdown
---
name: {{name}}
description: |
  {{description}}
  当用户提到：{{triggers_joined}} 时触发。
---

# {{display_name}}

## 适用场景
- {{scenario_1}}
- {{scenario_2}}

## 能力清单
### {{capability_1.goal}}
- 输入：{{capability_1.inputs}}
- 输出：{{capability_1.outputs}}
- 失败兜底：{{capability_1.fallback}}

## 处理流程
1. 意图识别与槽位补全
2. 执行动作或给出指引
3. 返回结果并提示下一步

## 边界与不做
- {{boundary_1}}
- {{boundary_2}}

## 对话示例
用户：{{example_user}}
助手：{{example_assistant}}
```

如果当前运行时的 skill 解析器需要单行 frontmatter，则把 `description` 渲染为单行，不改变正文语义。

## 生成 Skill 的 Projection Contract 模板

生成出来的业务 skill 必须按本仓库 consumer skill 方式接入 `ncrew-ontology` projection，而不是让 `skill-package-generator` 自己消费 projection。

每个生成的技能包默认包含：

```text
skills/<skill_slug>/
  SKILL.md
  metadata.json
  references/
    source-digest.md
    extraction-notes.md
    quality-report.md
  contracts/
    projections/
      ncrew-ontology/
        contract-index.json
        README.md
        <domain-slug>/
          <domain-slug>.<projection-type-short>.projection.json
          README.md
          REVIEW.md
```

生成的业务 `SKILL.md` 必须包含以下 consumer skill 章节，并按具体业务域裁剪 supported deliverables、projection types 和 local exclusions：

```markdown
## Projection Contracts

This skill may be augmented by bound `ncrew-ontology` projection contracts discovered under `contracts/projections/**/contract-index.json`.

- Projection discovery, route selection, and prompt patching are handled by runtime rather than by manual rules in this file.
- For human review, read `contracts/projections/ncrew-ontology/contract-index.json` first, then the selected topic's `README.md` and `REVIEW.md`, and then the chosen `*.projection.json` file.

### Projection Consumption

- Read the selected projection before planning implementation details.
- Only consume the projection fields and target views this skill actually supports, especially `concept_mappings`, `relation_mappings`, `constraint_mappings`, `prompt_projection`, `delivery_artifacts`, `mapping_policy`, `open_questions`, and `dropped_items`.
- Treat the selected projection as authoritative for terminology, clarifications, dropped scope, and blocking conditions.

### Blocking Rules

- If route selection is blocked, ambiguous, or does not safely cover the request, surface that limitation instead of guessing.
- If `mapping_policy` requires `block_or_escalate`, or `open_questions` is non-empty, do not finalize the output before surfacing the issue.
- Do not recreate items listed in `dropped_items`.
```

生成 `contract-index.json` 时必须：

- 设置 `producer_skill` 为 `ncrew-ontology`。
- 设置 `consumer_skill` 为当前生成的 `{{name}}`。
- 至少包含 1 个 topic，`domain_slug` 优先来自业务域 slug。
- 至少包含 1 个 READY view，path 指向同目录下真实存在的 `*.projection.json`。
- 默认启用 `prefer_ready_only: true` 与 `block_on_open_questions: true`。

生成 projection document 时必须：

- 使用 `docs/skill-projection-document.schema.json` 兼容结构。
- 包含 `prompt_projection`、`delivery_artifacts`、`dropped_items`、`open_questions`。
- 对 `mapping_policy.unresolved_item_policy` 使用 `block_or_escalate`。
- 将 `delivery_artifacts.path` 限定到该业务 skill 真实会产出的文件或响应结构。
- 如果用户输入中没有足够信息生成 READY projection，生成 WARNING/草稿摘要但不要伪造 READY contract。

## 伴随文件模板

生成业务 skill 时优先读取本技能目录下的伴随模板：

- `references/generated-skill-template.md`：生成 `SKILL.md` 的扩展业务模板。
- `references/projection-contract-template.md`：生成 consumer projection contract 的最小结构。
- `references/quality-checklist.md`：生成后质量检查与失败处理。

如果伴随模板缺失，可以使用本文件中的内联模板继续生成，但必须在 `user_summary` 中说明使用了内联降级模板。

## 质量校验

落盘前必须通过以下检查：

- 完整性：`name`、`description`、`capabilities` 必填。
- 可触发性：至少包含 1 个 trigger。
- 可执行性：每个 capability 都必须有输入、输出和兜底。
- 安全性：不得写入明文 token、密钥、密码、连接串或凭据。
- 自包含性：来源摘要、提炼说明、质量报告和 projection contract 必须随生成 skill 一起落在 `skills/<skill_slug>/`。
- 可消费性：生成 skill 的 `contract-index.json` 必须能被 runtime 从 `contracts/projections/**/contract-index.json` 自动发现。

如检测到敏感明文即将写入，拒绝写入并输出安全告警。敏感内容应替换为 `[REDACTED]`，但不要把真实值写入任何产物。

## 限制与边界

- 只生成业务技能包，不更新主 agent 行为约束。
- 不识别或吸收行为约束类信息；这类内容应交给主 skill 更新 `agent.md`。
- 不修改 `config/`、`ontology/`、`external/`。
- 不直接推送 UI，不触发诊断 skill 重跑，不更新主流程 todo。
- 不覆盖旧技能，除非新 SkillSpec 的规范化 `name` 与现有技能同名。
- 不把 `skill-package-generator` 自身注册为 projection consumer；projection consumer 结构只写入生成出来的业务 skill。

## 失败与回退

- 上传文件不可解析：不覆盖旧技能，返回可读错误，并建议用户改用会话描述补全。
- 必填字段严重缺失：生成草稿 SkillSpec，但不落盘正式技能，在 `user_summary` 中列出待补全项。
- 模板渲染异常：保留写入前状态，返回异常上下文。
- 校验失败：阻止落盘，返回失败项列表。

## 输出格式

最终输出必须包含两个部分：

```json
{
  "technical_artifact": [
    "skills/<skill_slug>/SKILL.md",
    "skills/<skill_slug>/metadata.json",
    "skills/<skill_slug>/references/source-digest.md",
    "skills/<skill_slug>/references/extraction-notes.md",
    "skills/<skill_slug>/references/quality-report.md",
    "skills/<skill_slug>/contracts/projections/ncrew-ontology/contract-index.json",
    "skills/<skill_slug>/contracts/projections/ncrew-ontology/README.md",
    "skills/<skill_slug>/contracts/projections/ncrew-ontology/<domain-slug>/<domain-slug>.<projection-type-short>.projection.json",
    "skills/<skill_slug>/contracts/projections/ncrew-ontology/<domain-slug>/README.md",
    "skills/<skill_slug>/contracts/projections/ncrew-ontology/<domain-slug>/REVIEW.md"
  ],
  "user_summary": "已新增 1 个技能：退货与订单查询助手；现在能处理退货咨询、订单状态查询和物流进度追踪。"
}
```

如果发生新增和更新混合，摘要必须按新增、更新、跳过、失败分类说明。

## References

- `references/generated-skill-template.md`：生成业务 `SKILL.md` 的扩展模板。
- `references/projection-contract-template.md`：生成 consumer projection contract 的最小结构。
- `references/quality-checklist.md`：落盘前质量检查与失败处理。
- `../ncrew-ontology/references/CONSUMER_PROJECTION_LAYOUT_GUIDE.md`：consumer projection 目录命名规范。
- `../../../docs/skill-projection-contract-index.schema.json`：contract index runtime schema。
- `../../../docs/skill-projection-document.schema.json`：projection document runtime schema。