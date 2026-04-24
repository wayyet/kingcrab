# 从 Ontology Slice 到 Projection 治理：ncrew-ontology 规范包的演进总结

这篇文章不是模板说明，也不是字段手册，而是对本次会话工作的一次整体回顾。它回答的问题是：`ncrew-ontology` 这一套规范，为什么会从“做一个加载 ontology 切片的 skill”逐步演进成今天这套同时覆盖 slice、projection、review、migration 和决策入口的完整包。

如果只想直接使用这套规范，看 [../src/OpenClaw.Gateway/skills/ncrew-ontology/README.md](../src/OpenClaw.Gateway/skills/ncrew-ontology/README.md)。如果想理解它为什么长成现在这样、各个文档之间为什么这么分工、以及这次会话真正完成了什么，这篇文章更合适。

---

## 一、起点：最初并不是要做一整套规范

这次工作的起点很简单：实现一个名为 `ncrew-ontology` 的 skill，用来“加载 ontology 切片”。

如果只停在这个目标上，最直接的交付其实只需要三样东西：

- 一份 `SKILL.md`，说明什么时候触发这个 skill
- 一份模板，约束切片输出格式
- 一份基础校验规则，防止结构跑偏

但一旦真的开始落地，就很快发现“加载 ontology 切片”不是一个孤立动作，而是一个治理链路的起点。团队真正需要的，不只是“生成一份 JSON”，而是：

- 知道什么时候该做 slice，什么时候不该做
- 知道一份 slice 是否只是结构合法，还是已经足够稳定
- 知道 slice 如何继续投影成 codegen、prompt orchestration 或 workflow contract
- 知道 warning 和 fail 的边界到底在哪里
- 知道 schema 升级时模板、样例、校验器和文档应该怎样一起迁移

也就是说，问题很快从“做一个 skill”变成了“做一套可操作、可评审、可迁移的 ontology 规范包”。

---

## 二、第一阶段：先把 slice 这一层做扎实

第一阶段的核心目标，是把 ontology slice 本身定义清楚。

这里最重要的设计选择，不是字段长什么样，而是先把方法论定下来：`ncrew-ontology` 不追求导出整份 ontology，而是围绕当前任务抽取“最小可验证子图”。

围绕这个原则，slice 层逐步沉淀出几类核心资产：

- `templates/TEMPLATE.md`：给人读和人工梳理用的模板
- `templates/TEMPLATE.json`：给工程流水线消费的结构化模板
- `templates/TEMPLATE.schema.json`：严格结构校验规则
- `references/FIELD_GUIDE.md`：字段语义和填报口径
- `examples/ready|warning|invalid/`：三态样例链路
- `src/OpenClaw.Gateway/skills/ncrew-ontology/scripts/validate-slice.ps1`：技能根目录内真实校验器，本地校验与 review 辅助入口
- `scripts/validate-ontology-slice.ps1`：仓库根目录包装入口，适合从仓库根目录或任意当前目录发起普通结构校验

这一步的价值，在于把“切片”从一个容易凭经验乱写的动作，收敛成一个有输入边界、有结构合同、有人工 review 入口的标准交付物。

也是从这里开始，这套规范不再只是 skill 说明，而具备了“团队可以重复使用”的基础形态。

---

## 三、第二阶段：发现只定义 slice 还不够，必须补 projection

如果说 slice 解决的是“把 ontology 子图抽出来”，那么 projection 解决的就是“如何把它安全地下放给下游系统”。

这个阶段是整个会话最关键的一次扩展。因为很多团队实际卡住的地方，不在 slice 本身，而在于：

- slice 里的 concept、relation、constraint 怎么接到代码生成
- 哪些语义可以投影成领域模型，哪些只能保留在 prompt policy 里
- 被裁掉的范围要不要显式记录
- warning 状态的 slice 能不能直接推进到下游

为了解决这些问题，规范包引入了 projection 这一层，并新增了一整套与 slice 平行的交付物：

- `templates/PROJECTION_TEMPLATE.json`
- `templates/PROJECTION_TEMPLATE.schema.json`
- `references/DOWNSTREAM_MAPPING_GUIDE.md`
- `src/OpenClaw.Gateway/skills/ncrew-ontology/scripts/validate-projection.ps1`：技能根目录内真实 projection 校验器
- `scripts/validate-ontology-projection.ps1`：仓库根目录 projection 包装入口
- `examples/ready|warning|invalid/*-projection.*`

这一步的本质，是把原本容易“拍脑袋下放”的 downstream mapping，变成一套可追溯、可解释、可校验的显式过程。

从此以后，slice 不再只是“终点文档”，而成为 projection 的上游事实源；projection 也不再只是“随便改写一下”，而有了自己的结构合同、风险口径和人工评审标准。

---

## 四、第三阶段：从“能产出”走向“能治理”

当 slice 和 projection 两层都具备后，真正的问题就不再是“能不能写出来”，而是“怎么判断它到底算不算好”。

这直接推动了 review 体系的建立。

最初，review 更偏向 slice 层；但随着 projection 的加入，单层评审已经不够用了。因为一份 projection 可能结构合法，却绑定在 warning slice 上；也可能映射看起来完整，却在 mapping policy 上默认容忍静默猜测。于是，评审必须升级成双层模型：

- slice 评审：判断 ontology 子图本身是否够稳、够准、够可追溯
- projection 评审：判断下游投影是否忠实、安全、可治理

于是有了统一后的 [../src/OpenClaw.Gateway/skills/ncrew-ontology/references/REVIEW_CHECKLIST.md](../src/OpenClaw.Gateway/skills/ncrew-ontology/references/REVIEW_CHECKLIST.md)。

这份清单并不是为了重复 schema 校验，而是为了补上人工判断层。它把两类产物统一收敛到同一套骨架：

- 结构结果
- 评审状态
- 当前结论

配合三态样例，这套评审口径逐渐变得清晰：

- `READY`：结构过关，语义或映射也足够稳定，可以作为基线
- `WARNING`：结构过关，但仍需人工确认或补证据，不能直接定稿
- `FAIL`：结构不合法，或已经不适合进入后续消费

这一步很重要，因为它把“样例、校验脚本、评审话术”三者统一成了一套同语言体系。团队不再需要在 `PASS/FAIL`、`Ready/Review required/Not ready`、`启发式结论/人工结论` 之间来回切换。

---

## 五、第四阶段：再往前走一步，把使用前决策也显式化

当包越来越完整后，又出现了一个现实问题：不是每个任务都适合直接进入 `ncrew-ontology`。

有些场景主题不明确，有些没有事实源，有些实际上想做的是 formal ontology，而不是 task-scoped slice。如果这些情况也强行套进模板，只会得到形式完整但没有治理价值的产物。

因此，规范包后续又补了一份 [../src/OpenClaw.Gateway/skills/ncrew-ontology/references/DECISION_GUIDE.md](../src/OpenClaw.Gateway/skills/ncrew-ontology/references/DECISION_GUIDE.md)。

这份文档做的事很简单，但非常关键：在“写之前”先回答“该不该写”。

它把适用性压缩成一张红黄绿决策图：

- 绿灯：适合进入 `slice -> validate -> projection -> review`
- 黄灯：可以做，但只能作为受控草案或局部治理视图
- 红灯：当前不适合，先补主题、补来源，或改用别的方法

这意味着 `ncrew-ontology` 不再只是一个“输出格式包”，而开始具备流程前置治理能力。

---

## 六、第五阶段：当规范开始稳定，就必须考虑迁移成本

一套规范真正进入可复用阶段后，就绕不开版本迁移问题。

只要 schema 存在，未来就一定会面临这些现实情况：

- slice 模板升级了，但旧样例还没跟上
- projection 规则变了，但校验器还没同步
- README、字段手册和评审清单仍停留在旧版本口径

如果没有显式迁移规则，团队很容易只改 schema 或只改模板，最后造成“表面版本号一致，实际规范断裂”。

所以这次会话后期又专门把迁移规则独立成了 [../src/OpenClaw.Gateway/skills/ncrew-ontology/references/SCHEMA_MIGRATION.md](../src/OpenClaw.Gateway/skills/ncrew-ontology/references/SCHEMA_MIGRATION.md)。

这份文档的意义，不是告诉大家“现在就要升级”，而是提前定义升级时必须一起移动的对象：

- schema
- 模板
- 样例
- 文档
- 校验器

这一步让规范从“当前可用”进一步变成“未来也能演进”。

---

## 七、最后的收口：不是继续堆文档，而是统一文档之间的语言

会话后半段一个很明显的工作重点，是文案和术语统一。

这听起来像小事，但实际上非常关键。因为当一套规范开始同时拥有模板、字段手册、评审清单、决策图、迁移文档、三态样例和双层 projection 样例时，如果术语不统一，团队理解成本会迅速升高。

这也是为什么后面做了几轮“看起来像纯文案”的工作：

- 把 projection 样例翻译成中文
- 把 slice 和 projection 六份评审文档的结构拉平
- 把 `REVIEW_CHECKLIST.md` 的术语收口到和样例一致
- 把 `结构结果 / 评审状态 / 当前结论` 变成统一表达方式

这类工作不直接增加功能，但会显著降低维护成本。因为一旦语言统一了，校验器输出、样例解释、人工 review 和 README 导航之间就能互相对得上。

---

## 八、这次会话最终交付的，不是一份 skill，而是一套治理包

如果从最终结果回看，这次会话真正完成的，其实是一套多层资产：

### 1. Skill 入口层

- `SKILL.md`
- `README.md`

### 2. Slice 定义层

- `TEMPLATE.md`
- `TEMPLATE.json`
- `TEMPLATE.schema.json`
- `FIELD_GUIDE.md`
- slice 三态样例
- `validate-slice.ps1` / `validate-ontology-slice.ps1`：分别对应技能根目录真实校验器与仓库根目录包装入口

### 3. Projection 定义层

- `PROJECTION_TEMPLATE.json`
- `PROJECTION_TEMPLATE.schema.json`
- `DOWNSTREAM_MAPPING_GUIDE.md`
- projection 三态样例
- `validate-projection.ps1` / `validate-ontology-projection.ps1`：分别对应技能根目录真实校验器与仓库根目录包装入口

### 4. Review 与流程治理层

- `REVIEW_CHECKLIST.md`
- `DECISION_GUIDE.md`
- 六份平行的人读评审文档

### 5. 演进与维护层

- `SCHEMA_MIGRATION.md`
- 根目录脚本包装入口
- README 中的统一导航

换句话说，这次工作的结果已经不是“新增一个 skill”，而是把 `ncrew-ontology` 建成了一套完整的 ontology slice / projection 工作流规范。

---

## 九、这套规范真正解决了什么问题

把这次工作抽象一下，它真正解决的是五类常见混乱：

### 1. 把“切片”从拍脑袋行为变成标准交付

以前团队可能会说“先整理一下 ontology 相关概念”，但产物格式、边界和评审口径都不稳定。现在 slice 有了明确模板、schema 和 review 入口。

### 2. 把“下游映射”从临时改写变成显式 projection

以前很多 codegen 或 prompt mapping 都是在聊天或实现过程中临时发生的。现在它有了单独的 projection 层，可以被校验、解释和追溯。

### 3. 把“结构合法”和“可接受”彻底区分开

一份 JSON 能过 schema，并不意味着它值得被定稿。`WARNING` 这层治理状态，就是专门解决这个误判问题。

### 4. 把“该不该用这套规范”前置到写之前

不是每个知识建模问题都该强行变成 ontology slice。`DECISION_GUIDE.md` 让团队先判断适用性，再决定是否投入这套流程。

### 5. 把“文档堆积”收敛成可维护的分工结构

这次会话不是简单加文件，而是把不同文档的职责逐步拆清楚：

- README 负责导航
- FIELD_GUIDE 负责字段语义
- REVIEW_CHECKLIST 负责评审口径
- DECISION_GUIDE 负责适用性判断
- SCHEMA_MIGRATION 负责版本迁移

这种分工，是规范能长期维护下去的前提。

---

## 十、结语：为什么这篇总结值得保留

一套规范包最容易丢失的，往往不是模板本身，而是“为什么当初要这样设计”。

模板能看出“现在怎么用”，但不一定能看出：

- 为什么一定要分 slice 和 projection 两层
- 为什么 warning 不是失败，但也不能当 ready
- 为什么 review、decision、migration 要拆成独立文档
- 为什么后期还要专门做术语统一

而这些，恰恰决定了这套规范未来是否还能被团队正确继承。

所以，这篇文章的价值，不在于替代任何操作手册，而在于给后来者提供一条清晰的理解路径：

`ncrew-ontology` 不是一份模板集合，而是一套围绕 task-scoped ontology slicing 建立起来的、可校验、可评审、可投影、可迁移的治理体系。
