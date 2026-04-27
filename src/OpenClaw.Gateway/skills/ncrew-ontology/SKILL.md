---
name: ncrew-ontology
description: 当用户提到 ontology、本体、slice、schema/projection/mapping、taxonomy 或概念关系建模时，从文档、schema 或代码中抽取当前任务所需的最小可验证 slice，保留 concepts、relations、constraints 与 sources，供评审、codegen 和 prompt 编排使用。
metadata: {"openclaw":{"emoji":"🧠"}}
---

# ncrew-ontology

Task-scoped ontology slicing for extracting the smallest verifiable subgraph needed by the current job.

## Core Concept

不要导出整份 ontology，而是围绕当前任务构造最小语义闭包：

- `concepts`：当前任务真正依赖的核心概念
- `relations`：概念之间必须保留的关系
- `constraints`：会改变判断、实现或生成结果的规则边界
- `sources`：所有结论的可追溯依据

目标不是泛泛介绍 ontology，而是交付一个能被评审、校验、代码生成或提示词编排消费的局部本体。

## When to Use

| Trigger | Action |
| --- | --- |
| 用户明确提到 `ontology`、`本体`、`slice` | 抽取任务相关本体切片 |
| 用户明确提到 `projection`、`schema mapping`、`mapping` | 将 slice 约束到下游 projection 语义，并保留映射关系与边界 |
| 需要从大图中只拿当前子域 | 收缩范围，构造最小闭包 |
| 需要统一术语、层级、约束 | 输出标准化概念/关系/规则 |
| 需要判断某实体或规则属于哪层 | 标注边界、上位概念和排除项 |
| 需要给 projection / codegen / prompt 提供稳定输入 | 产出结构化 slice JSON，作为下游消费的稳定语义输入 |

## Output Contract

默认输出遵循 `templates/TEMPLATE.json` 对应结构，至少覆盖：

```yaml
slice_request: 当前任务、主题、目标、期望产出
scope: 纳入范围 + 排除范围
sources: 切片依据与信任度
summary: 一句话结论与选取依据
concepts: 核心概念、定义、类型、关键属性
relations: 主体、谓词、客体、条件、来源
constraints: 规则、触发条件、禁止项、严重级别
ambiguities/uncertainties: 未决问题
next_actions: 后续衔接动作
meta: 生成信息
```

如果只需要人读版，可先用 `templates/TEMPLATE.md` 草拟，再落到 JSON。

## Workflow

### 1. Identify the slice boundary

先识别：

- 领域主题
- 核心实体
- 关键关系
- 约束条件
- 下游用途

如果用户要求“整份 ontology”，先收缩到当前任务直接相关的子图。

### 2. Locate authoritative sources

优先查找：

- 文档说明
- schema / taxonomy / vocabulary
- JSON / YAML / Markdown / RDF / OWL / Turtle 等结构化定义
- 代码中的类型系统、枚举、关系映射、命名常量

如果有多个来源：

- 优先最新、最近、最稳定、最贴近事实源的材料
- 明确记录本次采用了哪些来源
- 把冲突写进 `conflicts`，不要静默合并

如果没有可信来源：

- 直接说明缺失
- 说明是缺切片文件，还是只有零散术语
- 不要臆造 ontology 内容

### 3. Build the minimal semantic closure

只保留完成任务所需的：

- 目标实体及其直接相关实体
- 关键属性
- 关键关系
- 必须继承的上位概念
- 会改变判断结果的约束、规则、禁止项

默认排除：

- 无关平行领域
- 不再生效的历史定义
- 无法确认真伪的补充概念
- 只会增加噪音的扩展属性

### 4. Normalize terminology

统一输出：

- 中文名称
- 英文名称或原始标识符
- 别名 / 同义词
- 上下位关系
- 易混淆概念差异

如果不同来源命名不一致，显式写术语映射，不默认完全等价。

### 5. Validate and hand off

交付前至少确认：

- `source_ids` 能回到 `sources`
- 概念、关系、约束引用不悬空
- 冲突、歧义、不确定项已显式记录
- 能通过 `{baseDir}/templates/TEMPLATE.schema.json` 对应校验，或直接运行 `{baseDir}/scripts/validate-slice.ps1` / `{baseDir}/scripts/validate-slice.py`；如果从仓库根目录执行，则使用 `scripts/validate-ontology-slice.ps1` / `scripts/validate-ontology-slice.py`

## Quality Rules

- 优先做切片，不做全量转储
- 优先保留关系和约束，不只列名词
- 优先基于用户指定文件或目录加载
- 找不到来源时直接说明，不补造本体
- 当前任务已隐含切片范围时，不反复追问无关问题

## Clarify Before Proceeding

以下情况应先澄清或显式声明假设：

- 同时存在多个 ontology 来源且定义冲突
- 用户要求范围过大，已经变成全量 ontology 导出
- 当前任务缺少明确主题，无法判断切哪一层
- 用户要求基于不存在或未提供的 ontology 文件继续推理

## Forbidden Moves

- 把未验证的常识当作正式本体定义
- 把示例数据误当作概念层
- 省略关键约束后直接给出结论
- 在没有来源的情况下声称“这是标准 ontology 结构”

## References

- `{baseDir}/templates/TEMPLATE.md`：人工阅读和讨论模板
- `{baseDir}/templates/TEMPLATE.json`：工程化输出模板
- `{baseDir}/templates/TEMPLATE.schema.json`：严格结构校验规则
- `{baseDir}/templates/PROJECTION_TEMPLATE.json`：下游投影输出模板
- `{baseDir}/templates/PROJECTION_TEMPLATE.schema.json`：下游投影结构校验规则
- `{baseDir}/references/FIELD_GUIDE.md`：字段语义与填报口径
- `{baseDir}/references/REVIEW_CHECKLIST.md`：三态样例统一评审标准
- `{baseDir}/references/DOWNSTREAM_MAPPING_GUIDE.md`：下游代码生成 / 提示词编排映射规范
- `{baseDir}/references/PROJECTION_CONSUMPTION_GUIDE.md`：其他 skill 如何消费 projection.json
- `{baseDir}/references/CONSUMER_PROJECTION_LAYOUT_GUIDE.md`：consumer skill 专用 projection 目录与命名规范
- `{baseDir}/references/SCHEMA_MIGRATION.md`：slice 与 projection 的 schema 版本迁移说明
- `{baseDir}/examples/ready/sample.json`：READY 基线样例
- `{baseDir}/scripts/validate-slice.ps1`：skill 目录内真实 PowerShell 校验器，支持 `-SchemaPath` 与 `-ReviewMode`
- `{baseDir}/scripts/validate-slice.py`：skill 目录内真实 Python 校验器，支持 `--schema-path` 与 `--review-mode`
- `scripts/validate-ontology-slice.ps1`：仓库根目录 PowerShell 包装入口，适合从任意当前目录直接校验，支持 `Paths` 与 `-SchemaPath`
- `scripts/validate-ontology-slice.py`：仓库根目录 Python 包装入口，适合从任意当前目录直接校验，支持 `paths` 与 `--schema-path`
- `{baseDir}/README.md`：规范包总览

## Instruction Scope

该 skill 作用于工作区内的 ontology 相关文件、模板、样例和本地校验脚本。可读取和生成 `templates/`、`references/`、`examples/`、`scripts/` 下的内容。

进行结构校验时，优先按所在层级选择入口：

- 在 skill 目录内直接工作时，使用 `{baseDir}/scripts/validate-slice.ps1` 或 `{baseDir}/scripts/validate-slice.py`
- 需要从仓库根目录或任意当前目录直接校验时，使用 `scripts/validate-ontology-slice.ps1` 或 `scripts/validate-ontology-slice.py`
- review 辅助模式仅真实校验器支持：PowerShell 使用 `-ReviewMode`，Python 使用 `--review-mode`
- 两类入口默认都落到 `templates/TEMPLATE.schema.json`，默认样例都是 `examples/ready/sample.json`

它不会自动发明缺失 ontology，也不会在没有来源的情况下把经验性描述写成本体结论。