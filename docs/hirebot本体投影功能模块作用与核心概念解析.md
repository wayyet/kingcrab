# hirebot 本体投影功能模块作用与核心概念解析

> 分析日期：2026-07-04
> 依据材料：
> - `kingcrab/docs/hirebot本体投影功能模块工作原理与MAF开发可行性分析.md`（参考分析文档）
> - `hirebot/back-end/HireBot.ApiService/Assets/DigitalEmployeeTemplates/employment-coach-conversation/ontology/ontology-slice.md`（工作区约定，20 行）
> - 同模板包 `skills/ontology-slice-extraction/SKILL.md`（抽取技能，约 370 行）
> - 同模板包 `skills/ontology-projection/SKILL.md`（投影技能，约 330 行）
>
> 面向对象：中级开发工程师，用通俗方式讲清"这个模块是干什么的、为什么要有它、几个关键术语什么意思"。

---

## 一、本体投影功能模块的作用是什么？为什么要引入？

### 1.1 它在整个产品里的位置

hirebot 是一个"数字员工雇佣"产品：用户上传自己公司的业务资料（退货政策、排产规则、话术手册……），和"雇佣教练"（employment-coach-conversation 主技能）对话，最终由 `skill-generation` 自动生成一套可运行的数字员工技能包。

问题在于：**用户上传的资料是非结构化的**（Word、Markdown、零散文本），而**代码生成需要的是强结构化的输入**。中间必须有一道"把自然语言资料炼成机器可消费的结构化数据"的工序——这道工序就是本体投影功能模块。

### 1.2 模块组成：三层结构

| 层 | 位置 | 角色 |
|----|------|------|
| 约定层 | `ontology/ontology-slice.md` | 只有 20 行，声明工作区语义边界：参考模板只读、工作模板唯一可写、切片落 `ontology/`、上传文件在 `uploads/` |
| 抽取层 | `skills/ontology-slice-extraction/` | 资料 → 切片：从上传资料中抽取"最小语义闭包"，产出 `ontology/*.slice.json` + `*.slice.md` |
| 投影层 | `skills/ontology-projection/` | 切片 → 按技能投影：把切片按每个业务技能的能力域裁剪成专属投影文件，落 `ontology/projections/<skill_slug>/` |

`ontology/` 目录本身只是一份约定文档；真正干活的是两个下游技能，它们构成一条**两段式语义提炼流水线**，最终产物是 `skill-generation` 的数据契约。

### 1.3 整条流水线（事件驱动）

```
资料阶段收口（material_handoff_summary, isTerminal: true）
  │  payload: workspace_root + items[]（含 source_path）
  ▼
① ontology-slice-extraction
  │  读取上传资料 → 构造最小语义闭包
  │  产出 <topic>.slice.json + <topic>.slice.md → ontology/
  │  发 ontology_slice_extraction_done（completed / blocked + diagnostic）
  ▼
技能定义阶段收口（skill_workorder_summary，含 skills[] + business_rules）
  ▼
② ontology-projection
  │  扫描 ontology/*.slice.json → 逐 skill 语义匹配（宁投不弃）
  │  slice × skill → 最小投影闭包 + business_rules 合并进 constraint_mappings
  │  产出 ontology/projections/<skill_slug>/<domain>.<type>.projection.json
  │  发 ontology_projection_done（含 projection_paths / skipped / diagnostic）
  ▼
③ skill-generation 消费 projection 作为数据契约，物化生成技能包
```

### 1.4 为什么要引入这个模块？

如果没有它，就只能把用户上传的原始资料直接塞给 `skill-generation`，会立刻遇到四个问题：

1. **噪音失控**：全量资料里大部分内容与当前要生成的技能无关，塞进上下文既贵（token 成本）又稀释模型注意力，生成质量下降。
2. **无法校验**：自然语言资料没有结构，下游拿到后无法机器判断"输入合不合格"，出错只能靠人眼发现。
3. **假成功传播**：LLM 常见故障模式是"只在对话里描述了内容就当写入了文件"、"资料只有文件名没有正文也硬编一份产物"。没有中间层的落盘验证与阻断机制，这些假成功会一路传播到最终产物包。
4. **不可追溯**：生成的技能包里某条规则从哪来的？没有中间结构就无从回答，人工评审无从下手。

本体投影模块用"先炼切片、再按技能裁剪"的两段式设计逐一解决：最小语义闭包控噪音；JSON Schema + 校验脚本保证可校验；入口门禁 + 落盘验证 + 失败也收口防假成功；每条概念/关系/约束都带 `source_ids` 回链保证可追溯。一句话概括：

> **用提示词实现了一个带阶段门、事件驱动、有落盘验证和降级策略的两段式 ETL 流水线，把非结构化业务资料逐步收敛成可被代码生成消费的强结构数据契约（RAG 的"提炼-固化"变体）。**

---

## 二、两个技能分别是什么意思？

### 2.1 ontology-slice-extraction（资料 → 切片）

**通俗理解**：好比让一个新员工读完你上传的一摞公司文件后，整理出一张"学习笔记卡片"——只记和当前任务有关的要点，每条结论都标注出处，看不懂的地方明确写"待确认"，绝不凭空编。

**输入**：资料阶段收口时的 `material_handoff_summary`，里面有 `workspace_root`（工作区根目录）和 `items[]`（每条资料含 `source_path` 指向上传文件）。

**做什么**：读取上传文件正文，围绕当前任务抽取四类信息，构成"最小语义闭包"：

- `concepts`：当前任务真正依赖的核心概念（含定义、类型、关键属性、术语映射）
- `relations`：概念之间必须保留的关系（主体-谓词-客体）
- `constraints`：会改变判断或生成结果的规则边界（含触发条件、禁止项、严重级别）
- `sources`：所有结论的可追溯依据；未决项显式写入 `ambiguities`

**输出**：双格式产物写入 `<workspace_root>/ontology/`：

- `<topic>.slice.md` —— 给人评审用
- `<topic>.slice.json` —— 给工程消费用，受 `TEMPLATE.schema.json` 严格校验

两份文件必须描述同一个切片，缺一不可。

**关键防御机制**：

| 机制 | 内容 |
|------|------|
| 反造假 | 资料只有文件名没有正文必须 blocked，禁止写"占位 slice" |
| 落盘验证 | 发 done 前逐一确认文件真实存在于文件系统，"只在对话里描述"不算写入 |
| 有界自愈 | `source_path` 暂不可读时按 500ms 重试、最长 5 秒；只允许在 `uploads/` 内做一次窄范围文件名恢复，失败即阻断 |
| 失败也收口 | 无论成败都发 terminal artifact；失败时 `diagnostic` 只能取 `insufficient_material` / `source_unreadable` / `scan_error` 三个枚举值 |

### 2.2 ontology-projection（切片 → 按技能投影）

**通俗理解**：好比裁缝拿到一匹布（切片）和三张不同客人的尺寸单（已确认的技能定义），给每个客人裁出一件合身的衣服（投影文件）——每件只含这个客人需要的部分，剪掉的布头也登记在册（`dropped_items` 附剔除原因）。

**输入**：技能定义阶段收口时的 `skill_workorder_summary`，含 `workspace_root`、`skills[]`（每项含不可变主键 `skill_slug`）和 `business_rules`（技能定义阶段收集的业务规则，如交期口径、拆单偏好、CIP 清洗矩阵）。

**做什么**：

1. 扫描 `ontology/` 下的合法切片文件（首选 `*.slice.json`）。
2. 逐 skill 做语义匹配：用 skill 的 `triggers` + `description` 关键词去匹配各切片的 `topic` / `concepts` / `constraints`，取最匹配的一个切片作为来源。
3. 从切片中裁出该 skill 专属的 `concept_mappings` / `relation_mappings` / `constraint_mappings`；无关项进 `dropped_items`，解决不了的进 `open_questions`。
4. 把 `business_rules` 中已有规则直接合并进 `constraint_mappings`（禁止对已有规则重复提问）；缺口必须以"哪个 skill 缺哪条规则 + 2~5 个选项"的形式精确提问，禁止开放式追问。
5. 写入 `<workspace_root>/ontology/projections/<skill_slug>/<domain>.<type>.projection.json`，然后逐路径做落盘与完整性验证，最后发 `ontology_projection_done`。

**关键原则**：

| 原则 | 含义 |
|------|------|
| 宁投不弃 | 只有 1 个切片时默认对所有 skill 适用（除非业务域完全无交集）；部分覆盖也投影；同一切片可被多个 skill 复用 |
| slug 不可变 | `skill_slug` 是流程确认后的业务主键，目录名与 `intended_consumers` 必须逐字使用；发现冲突要阻断上报，不能自行改写 |
| 禁止 stub | 投影文件必须是自包含完整 JSON，不允许只写 `note` + 引用路径的占位文件 |
| 超时降级 | 文件未就绪按 500ms 轮询最长 5 秒，超时的 skill 降级进 `skipped_skills` 并重新计数；零投影必须给 `diagnostic` 枚举 |

### 2.3 两段为什么要拆开？

- **触发时机不同**：抽取只依赖资料阶段收口；投影还要等技能定义阶段收口（要知道有哪些技能、`skill_slug` 是什么才能裁剪）。
- **复用关系不同**：切片是"一次抽取、多技能复用"的公共中间产物；投影是 per-skill 的专属产物。拆开后补一个技能不用重读全部资料，只需重跑投影。
- **失败隔离**：资料不足在抽取段就 blocked，不会浪费投影段的工作；投影段单个 skill 匹配失败只记 `skipped_skills`，不阻断其他 skill。

---

## 三、四个核心概念解释

### 3.1 入口门禁

**是什么**：技能开头的一段硬性准入检查——本技能只接受来自上游主技能的内部下游触发（internal downstream trigger），且 `artifact_payload` 必须携带指定字段（抽取段要求 `workspace_root` + `items`/`total_items`；投影段要求 `workspace_root` + `skills[]`）。用户在聊天里只是提到"本体 / 切片 / 投影"这类词，不会触发执行，只会收到一句引导话术让流程回到正确阶段。

**通俗理解**：工厂流水线上的工位只接受上一工位传来的"随工单"，路人隔着窗户喊一嗓子"给我做个零件"是不作数的。

**为什么需要**：
1. 防止用户（或模型自己）绕过阶段门直接跳到中游工序，导致前置产物缺失、状态错乱；
2. 保证输入完整性——`workspace_root` 等字段缺失时技能根本无从正确落盘，与其执行到一半出错，不如在门口就拒绝。

### 3.2 数据契约

**是什么**：流水线上下游之间事先约定好的、机器可校验的数据格式。生产方承诺"我产出的文件长这样"，消费方按这个结构编写消费逻辑。本模块中：

- 切片受 `templates/TEMPLATE.schema.json` 校验，必含 `slice_request` / `scope` / `sources` / `concepts` / `relations` / `constraints` 等字段；
- 投影必含 `projection_type` / `source_slice` / `intended_consumers` / `concept_mappings` 等顶层字段，`skill-generation` 直接按此消费；
- 配套三态样例（ready / warning / invalid）和 Python 校验脚本（`validate-slice.py` / `validate-projection.py`）做确定性检查。

**通俗理解**：就是"文件版的 API 接口定义"。接口有 OpenAPI/Swagger 约束请求响应结构，这里用 JSON Schema 约束中间产物结构——上下游不用互相猜格式，校验脚本说了算。

**为什么需要**：LLM 输出天然不稳定，没有契约时下游拿到什么全凭运气；有了契约+校验脚本，不合格产物在交接处就被拦下，且校验是确定性代码而非"再问一次模型"，不花 token 也不会看走眼。

### 3.3 最小语义闭包

**是什么**：不导出整份本体，只保留完成当前任务所需的最小子图，且这个子图内部引用完整、不悬空：

- **最小**：只留目标实体及其直接相关实体、关键属性、关键关系、会改变判断结果的约束；默认排除无关平行领域、失效历史定义、无法确认真伪的补充概念。
- **闭包**：留下来的部分自洽完整——每个 `source_ids` 都能回链到 `sources`，每条关系的主客体都在 `concepts` 里，不存在"引用了却没定义"的悬空项。

**通俗理解**：开卷考试不许把整个图书馆搬进考场，只带一页浓缩讲义——但这页讲义必须自成体系，看它不需要再翻别的书。

**为什么要构造它**：
1. **控噪音控成本**：全量本体转储会污染下游上下文，token 又贵；
2. **提升下游质量**：`skill-generation` 的上下文里只出现相关信息，注意力不被稀释；
3. **可评审**：人工能在合理时间内看完一个切片，全量导出没人看得完；
4. **可校验**：闭包性质（引用不悬空）是可以用脚本机械检查的，"内容够不够"很难检查，但"引用断没断"可以。

### 3.4 阶段门

**是什么**：雇佣流程按阶段推进（资料阶段 → 技能定义阶段 → 技能生成阶段……），每个阶段以一个 `isTerminal: true` 的 terminal artifact 收口（如 `material_handoff_summary`、`ontology_slice_extraction_done`、`skill_workorder_summary`、`ontology_projection_done`）。下一阶段的工作只有在上一阶段的收口事件发出后才能开始——这个"只有收口才放行"的关卡就是阶段门。整个流程没有独立的状态机，阶段推进完全靠这些事件驱动。

**通俗理解**：游戏通关必须打完这一关的 Boss（发出 terminal artifact）才解锁下一关；没打 Boss 直接跳关是被门禁拦住的（这也是入口门禁存在的原因——两者配套：阶段门定义"什么时候放行"，入口门禁负责"没放行就不准进"）。

**关键设计——失败也收口**：阶段门不是"成功才关门"。抽取失败也必须发 `ontology_slice_extraction_done`，只是 `status: "blocked"` 并附 `diagnostic` 枚举；投影零产出也必须发 done 并附 `diagnostic`。这样上游永远能收到一个机器可分流的明确信号（继续推进 / 提示补资料），而不会因为某段静默失败导致整个流程挂起。

---

## 四、概念之间的关系（一图串联）

```
阶段门（何时放行）──配套──入口门禁（没放行不准进）
        │
        ▼
ontology-slice-extraction ──产出── 切片（最小语义闭包 ①：任务相关子图）
        │                              │ 受 JSON Schema 校验
        ▼                              ▼
ontology-projection ──────产出── 投影（最小语义闭包 ②：per-skill 裁剪）
                                       │
                                       ▼
                         数据契约 ──被 skill-generation 消费──▶ 技能包
```

- **阶段门**保证工序顺序，**入口门禁**保证工序不被绕过；
- 两段技能各自构造一层**最小语义闭包**（先按任务收敛，再按技能收敛）；
- 收敛的产物以**数据契约**的形式交付下游，靠 Schema + 校验脚本 + 落盘验证保证"下游拿到的一定是合格品"。

---

## 附：关键文件索引

| 文件 | 职责 |
|------|------|
| `ontology/ontology-slice.md` | 工作区语义边界约定（20 行） |
| `skills/ontology-slice-extraction/SKILL.md` | 抽取技能执行手册（约 370 行） |
| `skills/ontology-slice-extraction/templates/TEMPLATE.schema.json` | 切片结构校验 Schema |
| `skills/ontology-slice-extraction/scripts/validate-slice.py` | 切片确定性校验器 |
| `skills/ontology-projection/SKILL.md` | 投影技能执行手册（约 330 行） |
| `skills/ontology-projection/templates/PROJECTION_TEMPLATE.json` | 投影输出模板 |
| `skills/ontology-projection/scripts/validate-projection.py` | 投影确定性校验器 |
| `skills/skill-generation/` | 下游消费方：把投影物化为技能包 |

> 相关文档：《hirebot本体投影功能模块工作原理与MAF开发可行性分析.md》（同目录，含 MAF 代码化下沉的可行性与落地建议）。
