# SkillProjection Contracts Schema 说明

本文为 `SkillProjection Contracts` 的显式 schema 说明，配套设计文档如下：

- `docs/skill-projection-contracts-design.md`
- `docs/skill-projection-schema-migration-checklist.md`

与本文配套的机器可校验 schema 文件如下：

- `docs/skill-projection-contract-index.schema.json`
- `docs/skill-projection-document.schema.json`

本文不是严格的 JSON Schema 文件，而是把当前代码中隐式存在的字段契约整理成维护文档，并明确区分：

- `Runtime-consumed`：当前代码直接读取并影响运行时行为
- `Advisory-only`：当前样例中存在，但当前 runtime 不直接读取

## 1. 文档范围

当前 SkillProjection Contracts 由两类 JSON 文件组成：

1. `contract-index.json`
2. `*.projection.json`

它们通常位于：

```text
<skill>/contracts/projections/<producer>/contract-index.json
<skill>/contracts/projections/<producer>/<topic>/<topic>.<view>.projection.json
```

当前真实样例位于：

- `src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/contract-index.json`
- `src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/task-execution/task-execution.prompt-constraint.projection.json`

## 2. 解析边界

当前实现由两段代码定义：

- `src/OpenClaw.Core/Skills/SkillLoader.cs`
- `src/OpenClaw.Core/Skills/SkillProjectionResolver.cs`

其中：

- `SkillLoader` 负责解析 `contract-index.json`
- `SkillProjectionResolver` 负责加载并解析 `*.projection.json`

真实 JSON 样例里的字段数量多于当前 runtime 实际消费的字段数量。因此，样例中存在并不等于代码已支持。

## 3. contract-index.json

### 3.1 文件职责

`contract-index.json` 是 projection routing 的总入口，用于：

- 定义 producer 元数据
- 定义默认 selection policy
- 定义 topic scoring
- 定义 target view scoring
- 列出每个 topic 下有哪些 candidate views

### 3.2 顶层字段

当前顶层字段可以分成两组。

`Runtime-consumed` 字段：

- `producer_skill: string`
  说明：producer 名称，优先绑定到 `ProducerName`
- `producer_priority: int`
  说明：producer precedence，默认 `0`
- `producer_precedence: int`
  说明：`producer_priority` 的兼容别名
- `default_selection_policy: object`
  说明：当前只消费其中两个布尔字段
- `topic_scoring: object`
  说明：topic 评分配置
- `target_view_scoring: object`
  说明：target view 评分配置
- `topics: array`
  说明：topic 到 views 的实际 route 清单

`Advisory-only` 字段：

- `consumer_skill: string`
- `topic_conflict_resolution: object`
- `selection_algorithm: object`

### 3.3 最小可用 contract-index.json

当前最小可被 runtime 接受的 `contract-index.json` 可以非常小，例如：

```json
{
  "producer_skill": "ncrew-ontology",
  "producer_priority": 100,
  "default_selection_policy": {
    "prefer_ready_only": true,
    "block_on_open_questions": true
  },
  "topics": [
    {
      "domain_slug": "task-execution",
      "default_target_view": "prompt-constraint",
      "views": [
        {
          "target_view": "prompt-constraint",
          "status": "READY",
          "path": "task-execution/task-execution.prompt-constraint.projection.json"
        }
      ]
    }
  ]
}
```

如果需要 topic/view scoring，再补充 `topic_scoring` 和 `target_view_scoring`。

### 3.4 producer 元数据

`producer_skill`

- 类型：`string`
- 必填：否
- 状态：`Runtime-consumed`
- 用途：loader 优先将其绑定为 `SkillProjectionContractSet.ProducerName`
- 缺失时行为：退回到 `contract-index.json` 所在目录名

`producer_priority`

- 类型：`int`
- 必填：否
- 默认值：`0`
- 状态：`Runtime-consumed`
- 用途：多 producer 场景下的 tie-break precedence
- 生效时机：只有在 route 总分相同的情况下才会生效

`producer_precedence`

- 类型：`int`
- 必填：否
- 状态：`Runtime-consumed`
- 用途：`producer_priority` 的兼容别名
- 生效规则：只有在 `producer_priority` 缺失时，loader 才会读取它

### 3.5 default_selection_policy

当前 runtime 真正消费的字段：

- `prefer_ready_only: bool`
  默认值：`false`
  说明：view 选择时仅保留 `READY` 视图
- `block_on_open_questions: bool`
  默认值：`false`
  说明：projection 若存在 `open_questions`，直接阻断

当前样例存在但 runtime 不消费的字段：

- `fallback_order_by_target_view: string[]`
- `multi_view_resolution_hints: string[]`

### 3.6 topic_scoring

顶层结构如下：

```json
"topic_scoring": {
  "clarify_when_score_gap_below": 2,
  "score_dimensions": [...],
  "topics": [...]
}
```

字段说明：

- `clarify_when_score_gap_below: int`
  状态：`Runtime-consumed`
  默认值：`2`
  说明：top1-topic 与 top2-topic 分差小于该值时视为歧义
- `score_dimensions: array`
  状态：`Runtime-consumed`
  说明：各评分维度与分值
- `topics: array`
  状态：`Runtime-consumed`
  说明：每个 topic 的 request signals
- `selection_rule: string`
  状态：`Advisory-only`
  说明：当前样例中存在，runtime 不读取

`score_dimensions` 项结构：

```json
{
  "dimension": "primary_intent_match",
  "score": 5
}
```

字段：

- `dimension: string`
  必填：是
  说明：评分维度名
- `score: int`
  必填：否
  默认值：`0`
- `description: string`
  必填：否
  状态：`Advisory-only`

`topics` 项结构：

```json
{
  "domain_slug": "task-execution",
  "primary_intent_signals": ["review guidance", "prompt policy"],
  "supporting_signals": ["guidance"],
  "explicit_artifact_signals": ["prompt policy"],
  "demote_when_competing_topic_signals": ["workflow"]
}
```

字段：

- `domain_slug: string`
  必填：是
  状态：`Runtime-consumed`
- `primary_intent_signals: string[]`
  必填：否
  状态：`Runtime-consumed`
  说明：强信号，参与主意图与强匹配打分
- `supporting_signals: string[]`
  必填：否
  状态：`Runtime-consumed`
- `explicit_artifact_signals: string[]`
  必填：否
  状态：`Runtime-consumed`
- `demote_when_competing_topic_signals: string[]`
  必填：否
  状态：`Runtime-consumed`

### 3.7 target_view_scoring

顶层结构如下：

```json
"target_view_scoring": {
  "clarify_when_score_gap_below": 2,
  "score_dimensions": [...],
  "views": [...],
  "within_topic_overrides": [...]
}
```

字段说明：

- `clarify_when_score_gap_below: int`
  状态：`Runtime-consumed`
  默认值：`2`
  说明：top1-view 与 top2-view 分差小于该值时阻断
- `score_dimensions: array`
  状态：`Runtime-consumed`
- `views: array`
  状态：`Runtime-consumed`
- `within_topic_overrides: array`
  状态：`Runtime-consumed`
- `selection_rule: string`
  状态：`Advisory-only`
- `prefer_explicit_user_artifact_requests: bool`
  状态：`Advisory-only`

`views` 项结构：

```json
{
  "target_view": "prompt-constraint",
  "explicit_output_signals": ["prompt policy"],
  "strong_signals": ["review guidance"],
  "supporting_signals": ["guidance"],
  "demote_when_competing_view_signals": ["workflow"]
}
```

字段：

- `target_view: string`
  必填：是
  状态：`Runtime-consumed`
- `explicit_output_signals: string[]`
  必填：否
  状态：`Runtime-consumed`
- `strong_signals: string[]`
  必填：否
  状态：`Runtime-consumed`
- `supporting_signals: string[]`
  必填：否
  状态：`Runtime-consumed`
- `demote_when_competing_view_signals: string[]`
  必填：否
  状态：`Runtime-consumed`

`within_topic_overrides` 项结构：

```json
{
  "domain_slug": "task-execution",
  "bonuses": [
    {
      "target_view": "prompt-constraint",
      "when_request_signals": ["review", "guidance"],
      "score": 2
    }
  ]
}
```

字段：

- `domain_slug: string`
  必填：是
  状态：`Runtime-consumed`
- `bonuses: array`
  必填：否
  状态：`Runtime-consumed`

`bonuses` 项字段：

- `target_view: string`
  必填：是
  状态：`Runtime-consumed`
- `when_request_signals: string[]`
  必填：否
  状态：`Runtime-consumed`
- `score: int`
  必填：否
  默认值：`0`
  状态：`Runtime-consumed`
- `reason: string`
  必填：否
  状态：`Advisory-only`

### 3.8 topics 路由表

这一段定义最终可加载的 projection 文件路径。

`topics` 项结构：

```json
{
  "domain_slug": "task-execution",
  "default_target_view": "prompt-constraint",
  "views": [ ... ]
}
```

字段：

- `domain_slug: string`
  必填：是
  状态：`Runtime-consumed`
- `default_target_view: string`
  必填：是
  状态：`Runtime-consumed`
- `views: array`
  必填：否
  状态：`Runtime-consumed`

`views` 项结构：

```json
{
  "target_view": "prompt-constraint",
  "status": "READY",
  "path": "task-execution/task-execution.prompt-constraint.projection.json"
}
```

字段：

- `target_view: string`
  必填：是
  状态：`Runtime-consumed`
- `status: string`
  必填：是
  状态：`Runtime-consumed`
  说明：当前实现按字符串比对 `READY`
- `path: string`
  必填：是
  状态：`Runtime-consumed`
  说明：相对 `contract-index.json` 目录的 projection 文件路径

## 4. *.projection.json

### 4.1 文件职责

`*.projection.json` 是单个 topic + target view 的实际 payload 文件。

当前 runtime 在 route 已选定后再加载该文件，并提取：

- `mapping_policy`
- `prompt_projection`
- `delivery_artifacts`
- `dropped_items`
- `open_questions`

### 4.2 顶层字段

`Runtime-consumed` 字段：

- `mapping_policy: object`
  说明：当前只读取两个字段
- `prompt_projection: object`
  说明：prompt patch 的核心输入
- `delivery_artifacts: array`
  说明：仅解析结构，目前不直接参与 route scoring
- `dropped_items: (string | object)[]`
  说明：runtime 会归一化为可显示文本，再追加到 prompt patch
- `open_questions: (string | object)[]`
  说明：runtime 会归一化为可显示文本，并以归一化后的非空数组参与 blocking checks

`Advisory-only` 字段：

- `$schema: string`
- `template_type: string`
- `projection_version: string`
- `projection: object`
- `concept_mappings: array`
- `relation_mappings: array`
- `constraint_mappings: array`
- `meta: object`

### 4.3 mapping_policy

当前 runtime 只消费这两个字段：

- `unresolved_item_policy: string`
  必填：否
  说明：如果值为 `block_or_escalate` 且有 `open_questions`，则阻断
- `prompt_assumption_policy: string`
  必填：否
  说明：会被解析，但当前只进入内存模型，不直接驱动阻断

样例中还可能存在以下字段，但当前代码不读取：

- `preserve_source_trace`
- `preserve_constraints`
- `relation_flattening_policy`
- `dropped_item_policy`

### 4.4 prompt_projection

这是当前 prompt patch 的核心来源：

```json
"prompt_projection": {
  "allowed_terms": [...],
  "forbidden_assumptions": [...],
  "required_clarifications": [...],
  "reasoning_paths": [...],
  "source_digest": [...]
}
```

字段：

- `allowed_terms: string[]`
  状态：`Runtime-consumed`
  说明：追加到 `[Projection Route]` patch
- `forbidden_assumptions: string[]`
  状态：`Runtime-consumed`
- `required_clarifications: string[]`
  状态：`Runtime-consumed`
- `reasoning_paths: string[]`
  状态：`Runtime-consumed`
- `source_digest: string[]`
  状态：`Runtime-consumed`

### 4.5 delivery_artifacts

结构如下：

```json
{
  "artifact_name": "TaskExecutionPromptPolicy.md",
  "artifact_type": "prompt_fragment",
  "path": "src/.../TaskExecutionPromptPolicy.md",
  "status": "planned"
}
```

字段：

- `artifact_name: string`
  必填：是
  状态：`Runtime-consumed`
  说明：缺失时该项会被忽略
- `artifact_type: string`
  必填：是
  状态：`Runtime-consumed`
  说明：缺失时该项会被忽略
- `path: string`
  必填：是
  状态：`Runtime-consumed`
  说明：缺失时该项会被忽略
- `status: string`
  必填：否
  状态：`Runtime-consumed`
  说明：当前仅解析到内存模型

注意：当前 runtime 会解析 `delivery_artifacts`，但不会把它们加入 prompt patch，也不会直接用它们做阻断判断。

### 4.6 dropped_items

- 类型：`(string | object)[]`
- 必填：否
- 状态：`Runtime-consumed`
- 用途：如果非空，会追加到 prompt patch 的 `Dropped items` 段落
- 兼容基线：
  - 可以是简单字符串数组
  - 也可以是带 `item_type`、`item_id`、`reason` 的结构化对象数组
- 归一化规则：
  - 若存在 `reason`，runtime 会优先输出 `item_type item_id: reason`
  - 若缺少上述字段，则退回对象原始 JSON 文本

### 4.7 open_questions

- 类型：`(string | object)[]`
- 必填：否
- 状态：`Runtime-consumed`
- 用途一：如果 `block_on_open_questions = true` 且该数组非空，则阻断
- 用途二：如果 `mapping_policy.unresolved_item_policy = block_or_escalate` 且该数组非空，也会阻断
- 兼容基线：
  - 可以是简单字符串数组
  - 也可以是带 `question`、`impact`、`required_input` 的结构化对象数组
- 归一化规则：
  - 若存在 `question`，runtime 会优先输出问题文本，并附带 `impact` / `required_input`
  - 阻断判断只看归一化后的数组是否非空

## 5. 运行时实际使用到的字段清单

如果只关注当前 runtime 真正依赖的字段，可以压缩成下面这份最小集合。

### 5.1 contract-index.json 最小 runtime 集合

```json
{
  "producer_skill": "ncrew-ontology",
  "producer_priority": 100,
  "default_selection_policy": {
    "prefer_ready_only": true,
    "block_on_open_questions": true
  },
  "topic_scoring": {
    "clarify_when_score_gap_below": 2,
    "score_dimensions": [
      { "dimension": "primary_intent_match", "score": 5 },
      { "dimension": "strong_keyword_match", "score": 3 },
      { "dimension": "supporting_keyword_match", "score": 1 },
      { "dimension": "cross_topic_conflict_penalty", "score": -2 },
      { "dimension": "explicit_artifact_bonus", "score": 4 }
    ],
    "topics": [
      {
        "domain_slug": "task-execution",
        "primary_intent_signals": ["review guidance", "prompt policy"],
        "supporting_signals": ["guidance"],
        "explicit_artifact_signals": ["prompt policy"],
        "demote_when_competing_topic_signals": ["workflow"]
      }
    ]
  },
  "target_view_scoring": {
    "clarify_when_score_gap_below": 2,
    "score_dimensions": [
      { "dimension": "explicit_output_match", "score": 5 },
      { "dimension": "strong_signal_match", "score": 3 },
      { "dimension": "supporting_signal_match", "score": 1 },
      { "dimension": "cross_view_conflict_penalty", "score": -2 },
      { "dimension": "topic_default_view_bonus", "score": 1 }
    ],
    "views": [
      {
        "target_view": "prompt-constraint",
        "explicit_output_signals": ["prompt policy"],
        "strong_signals": ["review guidance"],
        "supporting_signals": ["guidance"],
        "demote_when_competing_view_signals": []
      }
    ],
    "within_topic_overrides": []
  },
  "topics": [
    {
      "domain_slug": "task-execution",
      "default_target_view": "prompt-constraint",
      "views": [
        {
          "target_view": "prompt-constraint",
          "status": "READY",
          "path": "task-execution/task-execution.prompt-constraint.projection.json"
        }
      ]
    }
  ]
}
```

### 5.2 *.projection.json 最小 runtime 集合

```json
{
  "mapping_policy": {
    "unresolved_item_policy": "block_or_escalate",
    "prompt_assumption_policy": "disallow_unmapped_terms"
  },
  "prompt_projection": {
    "allowed_terms": ["skills_config"],
    "forbidden_assumptions": ["Do not invert source precedence."],
    "required_clarifications": ["Clarify the managed path first."],
    "reasoning_paths": ["skills_config -> source_precedence"],
    "source_digest": ["Primary source: SkillLoader.cs"]
  },
  "delivery_artifacts": [],
  "dropped_items": [],
  "open_questions": []
}
```

## 6. 当前忽略字段的处理原则

当前实现遵循“宽输入，窄消费”的策略：

- 如果字段不存在，解析器尽量回退到默认值
- 如果数组项缺少关键字段，该项会被忽略，而不是整个文件报错
- 如果整个 JSON 无法解析，loader 或 resolver 才会记录 parse failure

这意味着：

- 样例可以带比 runtime 更多的人类说明字段
- 但如果要让字段真正参与运行时行为，必须先在代码里显式接线

## 7. 推荐维护规则

### 7.1 contract-index.json

推荐至少保持以下字段完整：

- `producer_skill`
- `producer_priority`
- `default_selection_policy.prefer_ready_only`
- `default_selection_policy.block_on_open_questions`
- `topic_scoring`
- `target_view_scoring`
- `topics`

### 7.2 *.projection.json

推荐至少保持以下字段完整：

- `mapping_policy.unresolved_item_policy`
- `prompt_projection`
- `open_questions`

### 7.3 多 producer 场景

如果同一个 consumer skill 绑定多个 producer：

- 每个 producer 的 `contract-index.json` 都应声明 `producer_priority`
- 只有在希望兼容旧字段名时，才使用 `producer_precedence`
- precedence 只用于同分 tie-break，不要拿它替代 topic/view scoring

## 8. 新增第 4 个 Topic 的可复制模板

如果要在现有 `skill-loading`、`task-execution`、`tool-orchestration` 之外新增第 4 个 topic，最小闭环是三部分：

1. 在 `contract-index.json` 中补 topic 路由定义
2. 在 `contract-index.json` 中补 topic / target view 的评分信号
3. 在新 topic 目录下至少放一个真实 `*.projection.json`

下面这份模板刻意只保留当前 runtime 真正依赖的字段，适合作为最小起点。

### 8.1 contract-index.json 模板片段

把下面三段内容并入现有 `contract-index.json`。

第一段：在 `topic_scoring.topics` 中新增该 topic 的信号。

```json
{
  "domain_slug": "new-topic",
  "primary_intent_signals": [
    "new topic",
    "new topic policy",
    "new topic contract"
  ],
  "supporting_signals": [
    "new topic guidance",
    "new topic workflow"
  ],
  "explicit_artifact_signals": [
    "new topic schema",
    "new topic model"
  ],
  "demote_when_competing_topic_signals": [
    "skill loading",
    "task execution",
    "tool orchestration"
  ]
}
```

第二段：在 `target_view_scoring.views` 中补默认 view 的评分信号。

```json
{
  "target_view": "prompt-constraint",
  "explicit_output_signals": [
    "new topic policy",
    "new topic guidance"
  ],
  "strong_signals": [
    "constraint",
    "review guidance"
  ],
  "supporting_signals": [
    "guidance"
  ],
  "demote_when_competing_view_signals": [
    "domain model",
    "json schema",
    "workflow contract"
  ]
}
```

第三段：在顶层 `topics` 中补路由表定义。

```json
{
  "domain_slug": "new-topic",
  "default_target_view": "prompt-constraint",
  "views": [
    {
      "target_view": "prompt-constraint",
      "status": "READY",
      "path": "new-topic/new-topic.prompt-constraint.projection.json"
    }
  ]
}
```

如果希望新 topic 一开始就支持多个输出形态，可以继续在该 `views` 数组中加入 `domain-model`、`json-schema` 或 `workflow-contract`，前提是：

- `target_view_scoring.views` 里也有对应 view 的评分定义
- 磁盘上存在与 `path` 对应的真实 projection 文件

### 8.2 真实目录结构模板

新增 topic 后，目录形态至少如下：

```text
<skill>/contracts/projections/<producer>/
  contract-index.json
  new-topic/
    new-topic.prompt-constraint.projection.json
```

以当前真实样例为参照，路径会是：

```text
src/OpenClaw.Gateway/skills/software-developer/contracts/projections/ncrew-ontology/
  contract-index.json
  new-topic/
    new-topic.prompt-constraint.projection.json
```

### 8.3 `*.projection.json` 最小模板

新 topic 的第一个 projection 文件可以直接从下面这个最小模板起步：

```json
{
  "$schema": "../../../../../../../../docs/skill-projection-document.schema.json",
  "mapping_policy": {
    "unresolved_item_policy": "block_or_escalate",
    "prompt_assumption_policy": "disallow_unmapped_terms"
  },
  "prompt_projection": {
    "allowed_terms": [
      "new topic"
    ],
    "forbidden_assumptions": [
      "Do not invent rules outside the mapped projection."
    ],
    "required_clarifications": [],
    "reasoning_paths": [
      "new-topic -> prompt-constraint"
    ],
    "source_digest": [
      "Primary source: new-topic projection contract"
    ]
  },
  "delivery_artifacts": [],
  "dropped_items": [],
  "open_questions": []
}
```

### 8.4 最小检查清单

新增第 4 个 topic 后，至少检查下面 5 点：

1. `topics[].domain_slug`、`topic_scoring.topics[].domain_slug` 与目录名一致
2. `default_target_view` 能在该 topic 的 `views[]` 中找到
3. `views[].path` 指向的文件在磁盘上真实存在
4. 新建 `*.projection.json` 的 `$schema` 指向 `docs/skill-projection-document.schema.json`
5. 编辑器对 `contract-index.json` 和新建 projection 文件都没有诊断错误

## 9. 如何验证 `$schema`

维护这两类 JSON 文件时，建议把 `$schema` 验证拆成三步。

### 9.1 先验证引用目标是否正确

- 真实 `contract-index.json` 应引用 `docs/skill-projection-contract-index.schema.json`
- 真实 `*.projection.json` 应引用 `docs/skill-projection-document.schema.json`
- 不要继续引用 `ncrew-ontology/templates/PROJECTION_TEMPLATE.schema.json` 作为 runtime contract 的主校验入口

如果是新增 producer 目录，应优先检查相对路径层级是否正确，因为这类错误最容易在复制现有文件时引入。

### 9.2 再验证编辑器诊断

最小维护要求是：

- JSON 文件语法无错误
- `$schema` 可被编辑器解析
- 与 schema 不匹配的字段类型、缺失必填项或非法值会直接暴露出来

如果编辑器诊断已经报错，不要继续假设 runtime 会兜底，因为 loader / resolver 的行为是“宽输入，窄消费”，不代表所有错误都能在运行时被明确提示。

### 9.3 最后做显式 schema 校验

当前仓库内建议直接用统一命令完成这一步，而不是依赖环境外 JSON Schema 工具。

`contract-index.json`：

```powershell
# PowerShell
.\scripts\validate-skill-projection-contract-index.ps1

# Python
c:/python314/python.exe .\scripts\validate-skill-projection-contract-index.py
```

`*.projection.json`：

```powershell
# PowerShell
.\scripts\validate-skill-projection-document.ps1

# Python
c:/python314/python.exe .\scripts\validate-skill-projection-document.py
```

最小基线要求：

- 至少校验一个真实 `contract-index.json`
- 至少校验一个真实 `*.projection.json`
- `contract-index.json` 使用 `docs/skill-projection-contract-index.schema.json`
- `*.projection.json` 使用 `docs/skill-projection-document.schema.json`
- producer 侧模板仍继续使用 `templates/PROJECTION_TEMPLATE.schema.json`
- Python 环境默认可能没有 `jsonschema` 模块

因此在没有额外工具依赖时，维护上的最低可执行标准是：

- `$schema` 指向正确
- 编辑器诊断为零
- 至少抽查一个真实 `contract-index.json` 和一个真实 `*.projection.json`

## 10. 结论

到当前为止，这套 schema 可以概括为一句话：

- `contract-index.json` 负责“如何选 route”
- `*.projection.json` 负责“选中 route 后给 runtime 提供什么 prompt-side contract”

其中真正进入当前 runtime 行为的字段已经在本文标清；其余字段目前仍属于扩展元数据或人类可读说明，不应默认视为已被代码支持。
