# OpenClaw Skill 机制内部技术文档

> 版本：v1.0  
> 日期：2026-04-26  
> 状态：内部技术参考

---

# 1. 概述与设计哲学

OpenClaw 的 Skill 机制是构建在 .NET 运行时之上的一层行为编排系统，其根本目的并非扩展运行时的能力边界，而是重塑 agent 与大型语言模型（LLM）之间的语义接口。Skill 将 agent 的行为指令外化为可由非开发者独立编写和部署的声明式资产，使得同一个运行时核心能够根据挂载的 Skill 集合呈现出截然不同的领域人格与工具调用模式。

## 1.1 Skill 的定义与定位

### 1.1.1 零代码扩展机制的核心定位

Skill 是一种**零代码扩展机制（zero-code extension mechanism）**：扩展行为无需侵入 .NET 核心代码库，无需创建新的程序集或注册新的服务，甚至无需重启网关进程。一个 Skill 的本质是一个包含 YAML 前置元数据与 Markdown 指令主体的目录单元，运行时通过扫描文件系统将其发现并注入到系统提示词中。

Skill 完全工作在**提示词层（prompt layer）**：它不向运行时注册新的可执行逻辑，而是通过自然语言指令改变 LLM 对既有工具集合的理解方式和调用策略。领域专家只要理解工具语义和任务流程，即可编写有效的 Skill 文件。

### 1.1.2 Skill 与插件架构的关系

OpenClaw 运行时同时支持两种扩展面：插件（Plugin）与 Skill。二者共享"不修改核心即可扩展"的设计目标，但作用域和技术契约存在本质差异。插件通过 `NativePluginRegistry` 向运行时注册编译后的 .NET 类型，提供新的工具实现（Tool）、触发器（Trigger）或中间件（Middleware）。插件的扩展发生在**执行层**：它实际改变的是 agent "能做什么"。相比之下，Skill 的扩展发生在**认知层**：它不改变工具集合，而是改变 LLM "如何理解并使用"既有工具集合。插件开发者专注于工具实现的正确性与性能，Skill 作者专注于领域行为的语义编排，二者可以独立演进。

## 1.2 设计哲学与核心原则

### 1.2.1 声明式配置优于命令式编码

Skill 机制将"声明式优于命令式"（declarative over imperative）作为首要设计原则。整个 Skill 的定义完全由 YAML 前置元数据与 Markdown 主体构成，不存在脚本引擎、条件执行语法或过程化控制流。这一选择并非技术限制，而是深思熟虑的工程约束：命令式脚本需要沙箱执行环境、安全审计与版本兼容性管理，这些复杂性会侵蚀零代码扩展的核心价值。通过将行为表达限制在纯声明式文本中，Skill 系统规避了任意代码执行的全部风险面，同时保留了足够的表达力。

YAML 元数据承担"结构化契约"的角色，声明技能的标识、触发条件与外部关联；Markdown 主体承载"行为叙事"，以编号式程序化指令描述 LLM 应遵循的推理路径和工具调用模式。

### 1.2.2 分层覆盖与优先级仲裁

Skill 系统采用**五层来源架构**（five-tier provenance architecture）实现从内置到工作区的渐进式定制。技能可来自额外目录、内置集合、托管目录、插件注册表以及工作区本地路径五个独立的来源面。当同名技能出现在多个来源时，高优先级来源的技能静默取代低优先级来源的版本，不产生合并逻辑也不抛出冲突异常。技能是**原子化替换单元**而非可组合模块，优先级从组织级别的通用性递减到工作区级别的专用性，形成自然的覆盖漏斗。

### 1.2.3 条件加载与环境感知

并非所有技能都应无条件加载。Skill 系统通过**需求门控机制**（requirement gating）解决环境与能力的匹配问题，使 Skill 具备环境自适应性。需求门控检查操作系统平台、二进制文件可用性、环境变量存在性与配置路径真值四类条件；当且仅当全部条件满足时，技能才被视为"合格"并进入系统提示词。单个技能可通过配置层接收额外的环境变量与配置注入，使得同一技能在不同部署环境中可以绑定不同的凭据或端点地址，而无需分叉 Skill 文件本身。

### 1.2.4 运行时注入与提示词工程

Skill 机制的最终执行形态是系统提示词中的两个 XML 结构：`<available-skills>` 提供技能索引供 LLM 决策调用，`<skill-instructions>` 则嵌入完整的 Markdown 指令主体作为行为约束。这种注入方式将 Skill 直接融入 LLM 的上下文窗口，使其成为提示词工程（prompt engineering）的系统化实践。

将行为指令置于系统提示词而非应用层代码中，意味着能力边界由 LLM 的上下文理解力定义，而非由固定的控制流定义。Skill 作者所编写的不是"如果 A 则执行 B"的命令式规则，而是"在 A 情境下，考虑调用 B 并遵循以下最佳实践"的认知引导。这一范式转换——从编程运行时到提示词运行时——是 Skill 机制最深层的架构隐喻。


# 2. 目录结构与文件规范

Skill 的物理组织方式直接决定了 `SkillLoader` 的扫描规则、名称冲突的判定边界以及运行时资源解析的行为。本章从目录命名约定、文件格式规范到内置技能的源代码树布局，建立一套可复现的物理层规范。

## 2.1 物理目录布局

### 2.1.1 技能目录的标准命名规范

每个技能在文件系统中表现为一个独立目录，目录名称本身即充当技能标识符（skill identifier）。`SkillLoader` 在扫描阶段以目录名进行去重和冲突判定，因此命名规范不仅是风格问题，更是运行时行为的基础约束。

命名规则遵循小写连字符（kebab-case）格式：仅使用小写字母、数字和连字符，不允许空格、下划线或驼峰式写法。例如 `deep-researcher`、`software-developer` 为合法标识；`DeepResearcher`、`deep_researcher` 或 `deep researcher` 均不符合规范。虽然 `SkillLoader` 在内部比较标识符时不区分大小写，但目录名称在磁盘上以原始形式参与路径拼接，并作为 `Location` 属性嵌入 `<available-skills>` XML 块供 LLM 读取，保持一致的命名风格可避免跨平台路径歧义。

### 2.1.2 SKILL.md 作为唯一入口

每个技能目录必须且仅能包含一个 `SKILL.md` 文件，这是 `SkillLoader` 判定子目录是否构成有效技能的唯一依据。扫描规则递归检查每个子目录：若目录根级别存在 `SKILL.md`，则该目录被识别为一个技能实例；若无此文件，即使目录中包含其他 Markdown 或 YAML 文件，也不会被纳入加载范围。

`SKILL.md` 同时承担双重职责：文件顶部的 YAML Front Matter 承载元数据与需求门控配置，其后的 Markdown Body 则作为注入 LLM 系统提示词的行为指令。将元数据与指令主体合并在单一文件中，确保了技能行为的自描述性和可移植性——一个目录即构成完整的、可独立分发的技能单元。

### 2.1.3 伴随文件与资源引用

技能目录中除 `SKILL.md` 外，允许放置任意伴随文件（模板、JSON 数据、示例代码片段等），但运行时对这些文件的引用不依赖硬编码绝对路径。`SkillPromptBuilder` 在构建系统提示词时，将 Markdown 指令主体中的 `{baseDir}` 占位符解析为技能目录在运行时的绝对路径。这一机制使技能指令能够安全引用同目录下的资源，例如提示词中可包含 "读取 `{baseDir}/templates/analysis.md` 作为报告模板" 之类的指令，而无需关心技能实际部署于内置目录、托管目录还是工作区目录。`{baseDir}` 的解析发生在提示词构建阶段，对 LLM 不可见；模型接收到的已是完整路径字符串。

以下展示一个包含多个技能目录的标准树形结构，其中每个叶节点即为技能的唯一入口文件：

```
skills/
├── daily-news-digest/
│   └── SKILL.md
├── data-analyst/
│   └── SKILL.md
├── deep-researcher/
│   └── SKILL.md
├── email-triage/
│   └── SKILL.md
├── homeassistant-operator/
│   └── SKILL.md
├── mqtt-operator/
│   └── SKILL.md
└── software-developer/
    ├── SKILL.md
    └── templates/
        └── pr-description.md
```

树中的 `software-developer` 目录额外包含 `templates/pr-description.md`，技能指令可通过 `{baseDir}/templates/pr-description.md` 引用该模板，体现了伴随文件与主入口文件的共存模式。

## 2.2 文件格式规范

### 2.2.1 YAML Front Matter + Markdown Body 的双段式结构

`SKILL.md` 采用双段式（two-part）结构，由一对 `---` 围栏分隔 YAML 前置元数据（Front Matter）与 Markdown 指令主体（Body）。`SkillLoader` 使用正则解析器提取 `---` 之间的 YAML 区块，剩余内容原样保留作为指令文本。

YAML 区块须符合严格的缩进和键名规范。顶层字段包括 `name`、`description`、`metadata`、`user-invocable`、`disable-model-invocation`、`command-dispatch`、`command-tool`、`command-arg-mode` 和 `homepage`。其中 `name` 为必填字段，用于跨来源的名称冲突去重；`metadata` 为 JSON 字符串格式，内嵌 `"openclaw"` 键以承载需求门控条件。YAML 解析失败将导致整个技能被标记为无效并跳过加载。

Markdown Body 部分不接收额外语法检查，但编写约定要求使用编号式程序化指令（numbered imperative statements），并按 agent 工具注册表中的实际名称引用工具（如 `web_search`、`shell`、`write_file`）。指令文本在 `SkillPromptBuilder` 中被包装为 `<skill-instructions>` XML 块，直接附加于系统提示词末尾，因此每个 token 的成本敏感——冗余描述不仅增加推理费用，还可能稀释 LLM 对核心行为的注意力。

### 2.2.2 编码与换行规范

`SKILL.md` 须以 **UTF-8** 编码保存，这是 `SkillLoader` 读取文件时的默认编码假设。换行符采用 **Unix 风格（LF，`
`）**，而非 Windows 风格的 CRLF（`\r\n`）。虽然 `SkillLoader` 在读取后会对换行符进行规范化处理，但使用 LF 可避免在版本控制或跨平台部署中引入不必要的差异。YAML Front Matter 的解析对换行符类型不敏感，但 Markdown Body 中的行内指令若涉及精确字符位置（如某些代码生成模板），换行符的不一致性可能导致输出差异。

## 2.3 内置技能目录示例

### 2.3.1 内置技能在源代码树中的实际位置

OpenClaw 的七个第一方内置技能随网关程序一起发布，源代码树中位于 `src/OpenClaw.Gateway/skills/` 目录。编译后，该目录被复制到输出路径，运行时通过 `AppContext.BaseDirectory` 解析为绝对路径，成为优先级 2 的 `Bundled` 来源。`skills.load.includeBundled` 配置项控制是否扫描此目录，默认值为 `true`；`skills.allowBundled` 可用于白名单过滤，仅允许列出的内置技能通过阶段 1 过滤。

内置技能目录与额外目录（`skills.load.extraDirs`）、托管目录（`~/.openclaw/skills`）和工作区目录（`$OPENCLAW_WORKSPACE/skills`）共同构成五层来源架构。高优先级来源中的同名技能可静默覆盖内置版本，使内置技能实际上充当默认行为模板，用户或组织可通过在工作区部署同名技能实现行为定制。

### 2.3.2 七个第一方内置技能的功能概览与工具映射关系

下表汇总了内置技能的职责边界、核心工具依赖及典型适用场景。工具映射关系直接决定了该技能在需求门控阶段（阶段 3）对二进制文件和环境变量的检查项，也影响 token 成本估算中 `<skill-instructions>` 块的长度预期。

| 技能名称 | 功能描述 | 核心工具 | 适用场景 |
|---------|---------|---------|---------|
| `software-developer` | 自主编码 agent：读取代码库、编写代码、运行测试、管理 git | `shell`, `read_file`, `write_file`, `git` | 软件开发、代码重构、自动化测试 |
| `deep-researcher` | 多来源网络研究与结构化报告生成 | `web_search`, `web_fetch`, `pdf_read`, `memory` | 深度调研、竞品分析、学术综述 |
| `data-analyst` | 数据库查询执行与数据可视化分析 | `database`, `code_exec` | 数据探索、报表生成、商业智能 |
| `daily-news-digest` | 定时网络新闻聚合与多源摘要 | `web_search`, `web_fetch` | 信息监控、舆情跟踪、简报生成 |
| `email-triage` | 电子邮件扫描、优先级分类与自动归档 | 邮件相关工具 | 收件箱管理、高优先级邮件筛选 |
| `homeassistant-operator` | Home Assistant 实体状态查询与指令下发 | `homeassistant` | 智能家居自动化、场景控制 |
| `mqtt-operator` | MQTT 主题订阅、消息发布与负载处理 | `mqtt` | IoT 设备管理、消息中继、遥测收集 |

从工具映射可以观察到，内置技能的覆盖范围横跨信息获取（`web_search`、`web_fetch`）、数据操作（`database`、`code_exec`）、代码工程（`shell`、`git`）和硬件控制（`homeassistant`、`mqtt`）四个维度。`software-developer` 和 `deep-researcher` 的工具依赖最为复杂，前者需要文件系统与版本控制工具链协同，后者则依赖网络检索与文档解析的组合。`daily-news-digest` 与 `deep-researcher` 共享 `web_search` 和 `web_fetch` 工具，但前者聚焦短周期聚合任务，后者面向深度单次研究，这种工具重叠而职责分离的设计，使同一工具注册表能够支持差异化的行为模式——行为差异完全由 `SKILL.md` 中的 Markdown 指令主体定义，而非工具实现本身。


## 3. YAML 前置元数据详解

OpenClaw 的 SKILL.md 文件在 Markdown 指令主体之前包含一段 YAML 前置元数据（front matter），被 `SkillLoader.cs` 解析为 `SkillMetadata` 对象。这段元数据同时承担三种职责：向运行时声明技能的标识与能力边界、驱动需求门控的条件判断、以及为 CLI 和系统提示词生成提供辅助信息。本章按字段功能分层，完整覆盖所有元数据键的语义与源码级实现细节。

### 3.1 核心字段规范

下表汇总了所有顶层 YAML 字段。其中 `name` 是唯一必填字段，其余字段均有显式默认值或行为回退，因此即使省略也能保证加载器行为可预测。

| 字段名 | 是否必填 | 类型 | 描述 | 默认值 |
|--------|---------|------|------|--------|
| `name` | 是 | string | 技能标识符，不区分大小写，用于跨来源去重 | — |
| `description` | 推荐 | string | 人类可读的功能摘要 | `""` |
| `user-invocable` | 可选 | bool | 是否可作为斜杠命令被用户直接触发 | `true` |
| `disable-model-invocation` | 可选 | bool | 是否从模型的系统提示词中完全排除 | `false` |
| `command-dispatch` | 可选 | string | 斜杠命令名称（不含前导 `/`） | `null` |
| `command-tool` | 可选 | string | 斜杠命令分发时调用的目标工具名称 | `null` |
| `command-arg-mode` | 可选 | string | 斜杠命令的参数传递模式 | `null` |
| `homepage` | 可选 | string | 文档或上游仓库 URL | `null` |
| `metadata` | 可选 | object | 包含 `openclaw` 键的嵌套结构，承载门控与装饰数据 | `null` |

这张表体现了 OpenClaw "默认启用、显式禁用"的配置哲学：所有布尔开关默认均为正向状态，开发者须主动写入 `false` 或 `true` 才能收缩行为边界。这种设计降低了新技能配置门槛，同时允许渐进式权限收紧。

#### 3.1.1 name 字段：技能标识符的命名规则、不区分大小写特性、跨来源去重机制

`name` 是 YAML 前置元数据中唯一没有默认值的字段，`SkillLoader.cs` 在解析时若发现该字段缺失会直接跳过文件。标识符在加载器内部被统一归一化为小写形式，这意味着 `Deep-Researcher`、`deep-researcher` 和 `DEEP-RESEARCHER` 会被视为同一技能。跨来源去重发生在五层扫描流水线（参见第 2 章）的合并阶段：高优先级来源（如工作区）中的同名技能会静默覆盖低优先级来源（如内置目录）的同名技能，不发生合并，也不产生冲突报错。因此 `name` 的命名空间是全局扁平的，开发者应避免在不同来源中使用相同标识符而又期望两者共存的情况。

#### 3.1.2 description 字段：人类可读摘要的编写要求及其在 CLI 和 XML 索引中的双重用途

`description` 在 `SkillPromptBuilder.cs` 的构建逻辑中承担双重角色。其一，在 CLI 命令 `openclaw skills list` 的输出中作为表格的摘要列呈现，帮助终端用户快速理解每个已安装技能的用途。其二，在注入 LLM 系统提示词的 `<available-skills>` XML 块中，`description` 被嵌入为每个 `<skill>` 元素的子内容，充当模型选择调用哪个技能的索引信息。由于 XML 块本身计入 token 预算，`description` 的编写需要在信息密度与简洁性之间取得平衡：过长的描述会直接增加每次请求的 token 开销，而过短的描述则可能降低模型路由的准确率。

#### 3.1.3 user-invocable 与 disable-model-invocation：用户触发与模型可见性的独立控制开关

这两个布尔字段控制技能暴露的两个正交维度，可组合出四种状态。`user-invocable` 决定技能是否注册到斜杠命令调度器：设为 `false` 时用户无法通过 `/command-dispatch` 触发该技能，但模型仍可能在系统提示词中看到它。`disable-model-invocation` 则相反：设为 `true` 时，`SkillPromptBuilder` 将该技能完全排除，模型对其"不可见"，但用户仍可能通过斜杠命令触发（前提是 `user-invocable` 为 `true`）。四种组合中，`user-invocable: false` 配合 `disable-model-invocation: true` 实现了"完全静默"模式：技能既不被用户触发，也不被模型调用，仅保留在运行时内部状态中。

#### 3.1.4 command-dispatch、command-tool、command-arg-mode：斜杠命令分发的完整链路

这三个字段共同定义了从用户输入到工具调用的分发链路。`command-dispatch` 提供用户可见的命令名称（例如 `research` 对应 `/research`）；`command-tool` 指定该命令在 agent 工具注册表中对应的目标工具名称（例如 `web_search`）；`command-arg-mode` 决定用户输入在斜杠命令后的剩余文本如何传递。当前支持的参数模式包括 `prompt`，即将用户输入作为 prompt 参数注入目标工具。若 `command-dispatch` 存在而 `command-tool` 缺失，运行时在命令注册阶段会抛出可诊断错误。

#### 3.1.5 homepage 字段：文档回退机制与上游仓库关联

顶层的 `homepage` 字段为技能提供外部文档或版本控制仓库的链接。当 `metadata.openclaw.homepage` 未设置时，`SkillInspector.cs` 中的信任评估逻辑会回退到该顶层字段作为文档来源依据。在 CLI 的 `skills inspect` 输出中，`homepage` 作为可追溯性信息打印，帮助管理员验证技能的来源可信度。该字段对运行时行为无功能性影响，属于纯元数据。

### 3.2 metadata.openclaw 嵌套结构

`metadata` 字段的值是一个 JSON 对象，其中仅 `openclaw` 键被运行时识别，其余键被静默忽略。下表列出 `metadata.openclaw` 下所有支持的键及其行为：

| 键名 | 类型 | 行为 | 示例 |
|------|------|------|------|
| `always` | bool | 为 `true` 时绕过所有需求门控检查，无条件加载 | `true` |
| `emoji` | string | 显示在管理仪表板和 CLI 列表中的 UI 装饰字符 | `"💻"` |
| `homepage` | string | 元数据层的文档或仓库 URL，未设置时回退到顶层 `homepage` | `"https://example.com"` |
| `primaryEnv` | string | 与单技能配置 `apiKey` 注入关联的环境变量名称 | `"MY_TOOL_API_KEY"` |
| `skillKey` | string | 替代配置查找键，在 `skills.entries.<key>` 中代替技能的 `name` | `"custom-key"` |
| `os` | string[] | 允许加载的操作系统标识，空数组表示任何系统 | `["darwin", "linux"]` |
| `requires.bins` | string[] | 列出的二进制文件必须全部存在于 `$PATH` | `["git", "docker"]` |
| `requires.anyBins` | string[] | 列出的二进制文件中至少有一个存在于 `$PATH` | `["curl", "wget"]` |
| `requires.env` | string[] | 列出的环境变量必须全部已设置（值非空） | `["OPENAI_API_KEY"]` |
| `requires.config` | string[] | 列出的配置路径必须解析为真值 | `["tools.allowShell"]` |

从这张表可以看出，`metadata.openclaw` 遵循分层门控策略：`always` 是最高优先级的全局短路开关；`os` 提供操作系统级别的粗粒度过滤；`requires` 子树则实现环境相关的细粒度准入控制。装饰性字段与功能型字段共存于同一命名空间，扁平化设计减少了嵌套深度，但要求加载器严格区分键的语义类别。

#### 3.2.1 always 标志：无条件加载的绕过机制及其适用场景

当 `metadata.openclaw.always` 为 `true` 时，`SkillLoader.cs` 在阶段 3 直接跳过所有条件判断，包括操作系统兼容性、二进制文件存在性、环境变量及配置真值检查。这一机制解决了"鸡生蛋"问题：某些基础技能（如提供 Shell 工具调用能力的技能）本身是所有后续检查的前提，若要求它们也满足环境条件，会导致启动死锁。`always` 应谨慎使用，仅适用于不依赖外部工具且无平台限制的核心行为注入场景。

#### 3.2.2 emoji 与 homepage：UI 装饰与元数据层文档 URL

`emoji` 是纯视觉装饰字段，在 `openclaw skills list` 的终端输出以及管理仪表板的技能卡片中渲染，对加载决策和提示词构建均无影响。`metadata.openclaw.homepage` 的优先级高于顶层 `homepage`：当元数据层存在时，`SkillInspector` 和 CLI 优先使用它；否则回退到顶层字段。这种双层设计允许开发者在顶层提供稳定的项目主页，同时在元数据层指向特定版本的文档或变更日志。

#### 3.2.3 primaryEnv 与 skillKey：API密钥注入映射与配置别名机制

`primaryEnv` 建立了从单技能配置到运行时环境变量的映射。当 `skills.entries.<skillKey>.apiKey` 存在值时，运行时在需求检查阶段将该值注入到 `primaryEnv` 命名的环境变量中。技能可通过检查该环境变量是否存在来推断 API 密钥是否已配置，无需直接读取配置文件。`skillKey` 提供了配置查找的别名能力：默认下单技能配置使用技能的 `name` 作为查找键，但开发者可通过 `skillKey` 指定不同标识符。这在技能名称变更但需保持历史配置兼容，或在一个配置节下聚合多个技能参数时非常有用。

#### 3.2.4 os 平台过滤：darwin、linux、win32 的三平台支持模型

`os` 字段是一个字符串数组，有效值为 `darwin`、`linux`、`win32`，分别对应 macOS、Linux 和 Windows 平台。在需求检查阶段，运行时通过 .NET 的 `RuntimeInformation.IsOSPlatform` 方法判断当前操作系统，并与 `os` 列表进行匹配。若 `os` 数组为空或未设置，技能被视为平台无关，在所有系统上均可加载。三平台模型覆盖了 OpenClaw 官方支持的全部目标平台，但值得注意的是，`os` 过滤发生在阶段 3，可被 `always: true` 绕过。

### 3.3 需求门控体系

需求门控（requirement gating）是 `SkillLoader.cs` 阶段 3 的核心逻辑，决定是否将技能纳入最终可用集合。门控体系包含两类检查：外部系统依赖（二进制文件、环境变量）和运行时配置依赖（配置真值）。

#### 3.3.1 requires.bins 与 requires.anyBins：PATH 二进制文件的全量与任意存在性检查

`requires.bins` 要求数组中列出的每个可执行名称都必须在当前进程的 `$PATH` 环境变量所声明的目录中存在对应的文件。`requires.anyBins` 则采用"至少一个"的宽松语义：数组中任一可执行文件存在即满足条件。这两个字段在语义上互补，可组合使用以实现复杂的工具链依赖表达。例如，一个技能可以同时声明 `bins: ["git"]` 和 `anyBins: ["docker", "podman"]`，意味着该技能要求 Git 必须存在，但容器运行时可以是 Docker 或 Podman 中的任意一种。

#### 3.3.2 requires.env 与 requires.config：环境变量和配置真值的条件依赖模型

`requires.env` 执行的是环境变量的存在性检查：变量必须已被设置且值非空字符串。该检查在 `primaryEnv` 注入之后执行，因此单技能配置中通过 `apiKey` 注入的密钥也会被识别为满足条件。`requires.config` 检查的是运行时配置树中指定路径的布尔真值，路径使用点分表示法（例如 `tools.allowShell`）。配置真值检查依赖于 `GatewayConfig.cs` 中定义的配置模型，若路径不存在或解析为非真值（如 `false`、`null`、空字符串），技能将被过滤掉。

#### 3.3.3 需求检查的缓存策略：ConcurrentDictionary 对 PATH 扫描结果的进程级缓存机制

PATH 扫描是需求门控中计算成本最高的操作：每次检查都需遍历 `$PATH` 目录并探测文件存在性。为避免重复磁盘 I/O，`SkillLoader.cs` 维护了一个 `ConcurrentDictionary<string, bool>` 进程级缓存，键为规范化后的可执行名称，值为存在性结果。缓存在技能重载期间保持有效，仅在进程重启时失效。这种设计将最坏复杂度从 `O(n_skills × n_bins × n_path_dirs)` 降至 `O(n_bins × n_path_dirs)`。对于包含大量技能和多目录 PATH 的环境，性能增益尤为显著。


## 4. Markdown 指令主体规范

YAML 前置元数据定义了技能的"何时可用"与"由谁调用"，而 Markdown 指令主体则定义了技能被激活后"具体做什么"。指令主体在加载后通过 `SkillPromptBuilder.Build` 被注入到 `<skill-instructions>` XML 块中，直接成为 LLM 系统提示词的一部分。因此，指令的措辞精度、结构清晰度和 token 效率会直接影响模型调用行为的正确性与运行成本。

---

### 4.1 指令编写风格

#### 4.1.1 程序化指令优于自然语言描述

OpenClaw 的指令主体并非面向人类的操作手册，而是面向 LLM 的行为规约。经验表明，模型对编号步骤、条件分支和明确工具引用的响应稳定性远高于开放式自然语言描述。推荐写作模式如下：

- **编号步骤**：使用 "1.", "2." 等序号标记操作序列，模型倾向于按序执行而非跳过中间环节
- **条件分支**：使用 "If ... then ... else ..." 格式明确决策点，如 "If `web_search` 返回少于 3 条结果，then 使用 `web_fetch` 抓取已知种子 URL"
- **工具引用**：在需要模型调用外部能力的节点，直接写出工具注册名称，避免模糊描述如"使用搜索功能"

这种程序化风格与 `AgentSystemPromptBuilder` 中构建的基础系统提示词形成互补：基础提示词定义了通用交互协议，而 Skill 指令主体则覆盖特定领域的行为覆盖层。

#### 4.1.2 工具名称引用的规范化

Skill 指令中引用的工具名称必须与 agent 工具注册表中的注册名称完全一致，包括大小写。`SkillLoader.cs` 在解析阶段不会验证指令主体中的工具名称有效性，这一校验发生在运行时模型实际输出工具调用时。若名称不匹配，模型将生成无法路由的 `function_call`，导致调用失败并被基础系统提示词中的错误处理段落拦截。

以 deep-researcher 为例，其依赖的工具在注册表中的注册名称为 `web_search`、`web_fetch`、`pdf_read` 和 `memory`。指令主体中若出现 `WebSearch` 或 `web-search` 等变体，均不会被正确路由。

#### 4.1.3 Token 效率原则

每个字符在加载后都会计入 `<skill-instructions>` XML 块并最终进入 LLM 的上下文窗口。`SkillPromptBuilder.EstimateCharacterCost` 的估算模型将技能指令的长度直接纳入 token 预算评估：XML 包装器基础开销 195 字符，每个技能附加标签开销 97 字符，再加上指令主体本身的字符数。对于长上下文模型而言，被加载但未使用的技能指令也会挤压对话历史可用空间。

写作约束包括：避免冗余修辞、合并语义相近的段落、使用缩略句式替代完整从句、在条件分支中省略已在前文声明的默认上下文。例如，"You should use the `web_search` tool to search for relevant information" 可精简为 "Use `web_search` for initial information discovery"。Token 效率不是指牺牲精确性换取简短，而是指在不损失行为明确性的前提下消除一切非功能性语言。

---

### 4.2 结构化指令模板

经过验证的 Skill 指令主体通常遵循三段式结构：触发条件、执行阶段、约束与边界。这种结构便于 `SkillPromptBuilder` 在注入时保持各技能间格式一致，也使模型在上下文混合多技能指令时仍能快速定位当前应执行的行为段落。

#### 4.2.1 触发条件段落

触发条件段落定义该 Skill 应在何种上下文下被激活。这并非需求门控（后者由 YAML 元数据中的 `requires` 和 `os` 等字段在加载阶段处理），而是对模型的显式激活信号。典型触发条件包括用户输入关键词、对话主题或特定请求模式。例如，deep-researcher 的触发条件可表述为：

> "Activate when the user requests research, deep-dive, comprehensive analysis, or asks questions requiring current or factual information beyond your training data."

明确的触发条件段落可降低模型在其他无关对话中误调用的概率，减少不必要的工具调用和 token 消耗。

#### 4.2.2 执行阶段段落

执行阶段是指令主体的核心，描述从 Skill 激活到结果交付的完整操作流程。推荐的组织方式是按阶段（Phase）或步骤（Step）线性展开，每个阶段明确输入、操作和输出。以研究类 Skill 为例，标准执行阶段可设计为：

1. **搜索阶段**：使用 `web_search` 获取初始信息源，保留原始查询的语义完整性
2. **提取阶段**：使用 `web_fetch` 拉取高相关性页面的全文或关键片段
3. **综合阶段**：整合多来源信息，使用 `memory` 记录中间发现以避免上下文丢失
4. **报告阶段**：生成结构化输出，标注信息来源和置信度

每个阶段应包含具体的工具名称和预期的调用参数模式，使模型在调用时能够生成符合注册表 schema 的参数对象。

#### 4.2.3 约束与边界段落

约束段落定义 Skill 的行为边界和明确禁止事项，防止模型在执行中偏离安全范围或陷入无限循环。约束通常涵盖：

- **迭代上限**：如 "Do not perform more than 3 rounds of search-follow-fetch"
- **输出格式限制**：如 "Cite all factual claims with source URLs"
- **禁止操作**：如 "Do not execute shell commands under this Skill"
- **降级策略**：当主要工具不可用时模型应采取的替代路径

约束段落的存在也使开发者能够通过修改 Skill 文件而非核心代码来调整 agent 的安全边界，体现了 Skill 机制"零代码扩展"的设计意图。

---

### 4.3 占位符与动态替换

#### 4.3.1 `{baseDir}` 占位符的解析时机与替换逻辑

`{baseDir}` 是 Skill 指令主体中唯一由框架提供的一阶占位符。其解析发生在 `SkillLoader.cs` 的加载阶段，而非运行时的每次提示词构建阶段。具体行为为：加载器读取 SKILL.md 的 Markdown 内容后，在将指令主体存入技能对象的 `Content` 属性之前，将字符串中所有 `{baseDir}` 实例替换为该技能所在目录的绝对路径。

替换后的路径包含末尾目录分隔符，指向包含该 SKILL.md 文件的技能目录本身。例如，内置 deep-researcher 技能在加载后，`{baseDir}` 将被替换为类似 `/path/to/src/OpenClaw.Gateway/skills/deep-researcher/` 的绝对路径。这一机制使得 Skill 指令能够确定性引用同目录下的伴随文件，而不依赖工作目录或相对路径的假设。

替换逻辑发生在加载时而非运行时，意味着文件系统路径在技能对象的生命周期内保持恒定。即使运行时工作目录发生变化，已加载技能的路径引用仍指向原始加载位置。这与热重载机制配合时需注意：当 `SkillWatcherService` 检测到 SKILL.md 变更并触发重新加载时，`{baseDir}` 将基于新的文件系统位置重新解析。

#### 4.3.2 伴随文件引用模式

伴随文件（Ancillary Files）是指与 SKILL.md 位于同一技能目录、被指令主体引用但不作为独立技能加载的文件。典型伴随文件包括：提示词片段模板（prompt fragments）、示例数据集（reference data）、配置默认值（default configs）等。通过 `{baseDir}` 占位符，指令主体可以精确引用这些文件。

例如，deep-researcher 可以在其目录中放置一个 `report-template.md` 文件，然后在指令主体中写入：

> "Read the report structure template from `{baseDir}report-template.md` before generating output."

加载后，该路径被解析为绝对文件系统路径，模型若具备文件读取工具（如 `read_file`），即可在运行时加载该模板。这种外部化策略将大段静态内容从指令主体中剥离，直接降低 `<skill-instructions>` 的 token 长度，同时允许单独修改模板而不触及行为指令本身。

伴随文件引用模式的使用边界是：指令主体仅负责声明"应读取何文件"，文件读取操作本身仍由模型通过标准工具调用完成，不享受框架级的自动注入。这与 YAML 元数据注入（通过 `primaryEnv` 或 `skills.entries.*.env`）形成对比：后者在加载阶段完成值替换，而伴随文件在运行阶段由模型按需读取。

---

### 4.4 完整指令主体示例（deep-researcher）

以下展示 deep-researcher 技能的完整 Markdown 指令主体，涵盖触发条件、执行阶段与约束边界三个段落。YAML 前置元数据部分已在前章详述，此处从 `---` 分隔符之后开始。

```markdown
---
name: deep-researcher
description: Conducts comprehensive web research across multiple sources and produces structured, cited reports.
metadata: {"openclaw":{"emoji":"🔬","primaryEnv":"RESEARCH_API_KEY"}}
user-invocable: true
disable-model-invocation: false
command-dispatch: research
command-tool: web_search
command-arg-mode: prompt
---

## Deep Researcher Skill

### Activation Conditions

Activate this skill when the user requests research, deep-dive, comprehensive analysis, literature review, or asks questions requiring current or factual information beyond your training data. Also activate when the user uses the `/research` command.

### Execution Protocol

Follow these phases in sequence. Do not skip phases unless explicitly directed by the user.

**Phase 1 — Initial Discovery**

1. Formulate 1-3 search queries based on the user's request.
2. Call `web_search` with each query. Record the returned URLs and titles.
3. If fewer than 3 results are returned, expand the query scope and search again (max 1 retry).

**Phase 2 — Deep Extraction**

4. Select the top 5 most relevant URLs from Phase 1 using title and snippet relevance.
5. For each selected URL, call `web_fetch` to retrieve the full page content or substantial excerpts.
6. If a PDF link is present among the results, call `pdf_read` to extract its text content.

**Phase 3 — Synthesis and Memory**

7. Summarize key findings from all fetched sources. Identify agreements, conflicts, and gaps.
8. Use `memory` to store intermediate synthesis if the source volume exceeds comfortable context limits.
9. Cross-reference claims: if two sources contradict, note the discrepancy and preserve both perspectives.

**Phase 4 — Structured Reporting**

10. Generate a final report with the following sections: Executive Summary, Key Findings, Source Analysis, and Gaps or Uncertainties.
11. Every factual claim must include an inline citation referencing the source URL.
12. If a report template exists at `{baseDir}report-template.md`, load it and follow its structure.

### Constraints and Boundaries

- Do not perform more than 2 rounds of `web_search` per user request (initial + 1 retry).
- Do not use `shell` or `write_file` under this skill unless the user explicitly requests a saved report.
- Cite all factual claims with source URLs. Unsupported claims must be flagged as "unverified inference."
- If `web_search` or `web_fetch` returns errors for all sources, inform the user of the failure and do not fabricate content.
- Maximum report length: 4000 tokens. If content exceeds this, produce a condensed version and offer to expand specific sections.
```

上例中，`{baseDir}report-template.md` 将在 `SkillLoader.cs` 解析阶段被替换为技能目录下的绝对路径；所有工具名称（`web_search`、`web_fetch`、`pdf_read`、`memory`）均与 agent 工具注册表中的注册名称严格匹配；触发条件段落明确限定了激活范围，约束段落则以否定式指令划定了行为边界。该示例同时体现了程序化指令风格、结构化三段模板和占位符动态替换三种机制的实际应用。


## 5. 来源目录与五层优先级架构

OpenClaw 的技能加载器（`SkillLoader.cs`）从多个文件系统来源发现技能，并为每个来源分配一个固定的优先级层级。该架构的核心设计目标是在不引入合并语义的前提下，允许用户以渐进式覆盖的方式定制或替换内置行为。当同一技能标识符出现在多个来源中时，系统采用单向扫描与静默获胜（silent-wins）原则进行仲裁。

### 5.1 五层来源模型

`SkillLoader` 在初始化阶段将五个来源枚举为 `SkillSource` 值，并按优先级从低到高依次扫描。每个来源对应一个具体的文件系统路径或运行时提供的目录集合。下表汇总了五层来源的全部属性：

| 优先级 | 来源名称 | 目录路径 | `SkillSource` 枚举 | 默认值 | 用途说明 |
|--------|---------|---------|-------------------|--------|---------|
| 1（最低） | Extra 额外目录 | `skills.load.extraDirs` 中配置的任意路径 | `Extra` | `[]`（空数组） | 用于注入第三方技能包或团队共享技能库，不随应用分发 |
| 2 | Bundled 内置 | `{AppContext.BaseDirectory}/skills` | `Bundled` | `includeBundled: true` | 随网关（Gateway）一同分发的第一方技能，位于源码树 `src/OpenClaw.Gateway/skills/` 下 |
| 3 | Managed 托管 | `~/.openclaw/skills` | `Managed` | `includeManaged: true` | 用户级全局技能目录，通过 `openclaw skills install --managed` 安装，跨项目共享 |
| 4 | Plugin 插件 | 运行时由 `NativePluginRegistry` 动态提供 | `Plugin` | 不适用 | 原生插件在初始化时通过 `IPluginContext` 注册的技能目录，生命周期与插件绑定 |
| 5（最高） | Workspace 工作区 | `$OPENCLAW_WORKSPACE/skills` | `Workspace` | `includeWorkspace: true` | 项目特定技能，仅对当前工作区生效，用于覆盖内置行为 |

Extra 层作为最低优先级的扩展点，完全依赖用户显式配置。`skills.load.extraDirs` 是一个字符串数组，允许指定多个外部目录，系统对每个目录执行相同的子目录扫描逻辑。该层的设计意图是支持企业或团队在不修改核心仓库的前提下，将内部技能包挂载到运行时的搜索路径中。由于优先级最低，Extra 目录中的同名技能可以被任何更高层级的来源覆盖，从而降低了外部依赖与内置行为发生冲突的风险。

Bundled 层对应网关可执行文件所在目录下的 `skills/` 子目录。该目录在构建时由项目文件或 CI 流程填充，包含 OpenClaw 官方维护的第一方技能（如 `deep-researcher`、`software-developer`）。`includeBundled` 配置项控制是否扫描此目录，默认为 `true`。当需要完全禁用所有内置技能时，可将此项设为 `false`；若仅需选择性启用，则应使用 `skills.allowBundled` 白名单（详见第 6 章）。

Managed 层位于用户主目录下的 `.openclaw/skills`，是用户级全局安装的目标位置。CLI 命令 `openclaw skills install --managed` 将技能压缩包或目录解压至此。该层的优先级高于 Bundled，意味着用户可以通过全局安装同名技能来覆盖内置版本，而无需修改任何项目文件。这一设计支持用户建立个人偏好的技能基线，使其在所有工作区中默认生效。

Plugin 层是五层中唯一不直接绑定到静态文件路径的来源。`NativePluginRegistry` 在运行时枚举所有已加载的原生插件，每个插件可通过 `IPluginContext` 暴露一个或多个技能目录。由于插件在 `AgentRuntime` 初始化之后才完成注册，Plugin 层的扫描发生在其他四层之后。其优先级高于 Managed 而低于 Workspace，确保插件可以提供默认行为，但无法覆盖项目特定的显式定制。

Workspace 层以环境变量 `$OPENCLAW_WORKSPACE` 为基准定位 `skills/` 子目录，是五层中优先级最高的来源。该层的设计哲学是"工作区自治"——每个项目可以拥有完全独立的技能集合，并通过同名覆盖机制屏蔽全局或内置版本中不符合项目需求的行为。Workspace 层也是 CLI 安装命令的默认目标（省略 `--managed` 和 `--workdir` 时），降低了项目特定定制的使用门槛。

### 5.2 名称冲突仲裁机制

#### 5.2.1 静默获胜原则

当同一技能名称（不区分大小写）出现在多个来源中时，高优先级来源的实例完全覆盖低优先级实例，系统不执行字段级合并，也不向用户输出任何冲突警告或日志。这一行为在源码层面体现为：扫描过程中使用一个按名称索引的字典（或等效映射结构），后写入的实例直接替换先写入的实例。由于扫描顺序严格按照优先级从低到高执行，最终保留下来的永远是最高优先级来源中的版本。

静默获胜原则的设计意图源于对 LLM 系统提示词注入场景的深刻理解。技能的内容是面向模型的自然语言指令，而非结构化的配置数据。字段级合并（例如合并两个来源的指令主体）在语义上缺乏明确规则，且极易产生矛盾或冗余的指令，反而降低模型的遵循能力。完全覆盖策略将决策权交还给工作区或用户级别的配置者，确保最终注入系统提示词的技能内容具有单一、自洽的行为描述。

此外，避免合并也显著简化了加载器的实现复杂度。`SkillLoader` 无需处理冲突检测、差异计算或三方合并算法，仅需维护一个单调递增的扫描循环。这使得加载逻辑的确定性极高，也为热重载机制提供了可复现的基础——每次重新扫描都会产生相同的最终集合，不受扫描顺序之外的非确定性因素影响。

#### 5.2.2 冲突仲裁的扫描顺序

`SkillLoader` 在 `LoadAll` 方法中按照固定顺序执行单向扫描：Extra → Bundled → Managed → Plugin → Workspace。每个来源的扫描逻辑在概念上独立，但共享相同的子目录发现机制：遍历来源目录下的直接子目录（或来源目录自身），检测其中是否包含名为 `SKILL.md` 的文件。若存在，则解析该文件的前置 YAML 元数据和 Markdown 指令主体，构造 `Skill` 实例并尝试加入结果字典。

扫描的单向性意味着一旦某个技能被高优先级来源覆盖，低优先级来源的原始信息即被完全丢弃，不会保留用于调试或回退。这一行为在工程实践中要求工作区级别的覆盖者承担完整的维护责任——若工作区技能存在缺陷，系统不会自动回退到内置版本。用户需通过显式删除或重命名工作区中的同名技能来恢复低优先级来源的行为。

Plugin 层的动态性在扫描顺序中引入了唯一的不确定因素：插件的加载时机取决于 `NativePluginRegistry` 的初始化顺序。然而，由于 Plugin 的优先级始终固定在第四层，无论具体哪个插件先注册，其技能都受 Workspace 层的支配。这种"动态来源、静态优先级"的混合模型在保留插件扩展能力的同时，确保了最终用户的项目级配置始终拥有最高仲裁权。


## 6. 加载机制与三阶段过滤流水线

Skill 的加载并非简单的文件读取操作，而是经过严格编排的过滤流水线。`SkillLoader` 对每个来源目录执行统一扫描，随后将解析出的技能候选对象依次投递至三阶段过滤器。只有全部阶段均通过的技能，才获得向系统提示词注入的资格。以下按执行顺序说明各阶段的扫描规则与判断逻辑。

### 6.1 发现阶段

#### 6.1.1 目录扫描规则

`SkillLoader` 对每一个来源目录执行递归扫描，查找包含 `SKILL.md` 文件的子目录。扫描器同时识别两种合法位置：一是子目录内部的 `SKILL.md`（常规模式，如 `skills/deep-researcher/SKILL.md`）；二是来源目录根目录下直接放置的 `SKILL.md`。这一双重识别策略允许技能既可以按目录分组组织，也可以扁平化部署在来源根目录。每当扫描器定位到一个 `SKILL.md` 文件，即触发一次独立的技能解析流程，由前置 YAML 解析器提取元数据，并将技能目录路径绑定为 `Location` 属性。

#### 6.1.2 Location 属性的派生

技能目录的绝对文件系统路径被记录为 `Location` 属性，该属性在构建系统提示词时对 LLM 可见。其核心用途是支持占位符 `{baseDir}` 的解析：`SkillPromptBuilder` 将技能指令主体中出现的 `{baseDir}` 字符串替换为 `Location` 值，使技能能够引用其目录中的伴随文件（如示例代码、配置文件或模板）。由于五层来源架构中不同来源的技能可能具有相同 `name`，`Location` 同时承担了去重后的物理唯一性辅助标识职责——在 `<available-skills>` XML 块中，`Location` 被列出以辅助 LLM 区分同名技能。

### 6.2 阶段一：AllowBundled 过滤

#### 6.2.1 `skills.allowBundled` 白名单机制

第一阶段过滤仅作用于来源类型为 `Bundled`（内置技能）的技能。`skills.allowBundled` 配置项定义一个字符串数组白名单，运维人员可通过该列表精确控制哪些第一方技能进入后续处理。当配置非空时，`SkillLoader` 将每个内置技能的 `name` 与白名单条目逐一比对；未列入白名单的技能在此阶段被剔除，不再向下传递。该机制的设计意图是将网关附带的全部内置技能按运维策略裁剪为最小可用集合，避免无关能力污染系统提示词并占用 token 预算。

#### 6.2.2 空数组的默认放行语义

`skills.allowBundled` 的默认值为空数组 `[]`。这一空值具有明确的放行语义：当数组为空时，所有内置技能均不受阶段一拦截，全部继续向下传递。只有当运维人员显式填入一个或多个技能名称时，阶段一才进入严格模式，将列表之外的技能排除。这种"空即放行"的设计避免了默认配置下的意外阻断——若默认行为为全部拒绝，则新部署的网关实例将没有任何可用内置技能，显然不符合开箱即用的预期。同时，非空数组立即启用精确控制，使运维人员能够按最小权限原则裁切技能集。

### 6.3 阶段二：单技能配置覆盖

#### 6.3.1 `skills.entries.<skillKey>.enabled` 的终止开关语义

第二阶段过滤引入针对单个技能的运行时禁用能力。`skills.entries.<skillKey>.enabled` 字段的布尔值构成一个终止开关：当取值为 `false` 时，无论该技能在阶段一是否通过白名单、阶段三是否满足环境需求，均在此阶段被强制跳过。此设计面向两种典型运维场景：一是临时禁用某个技能而不删除其目录或文件；二是在高优先级来源（如工作区）的配置中显式关闭从低优先级来源（如内置目录）继承的同名技能。由于五层来源架构中高优先级技能静默覆盖低优先级技能，阶段二的终止开关成为覆盖机制不可或缺的互补手段——它不仅决定是否加载，还提供"显式关闭"的否定语义。

#### 6.3.2 `skillKey` 解析优先级

配置查找键的解析遵循明确的优先级规则。`SkillLoader` 首先读取技能元数据中 `metadata.openclaw.skillKey` 字段；若该字段存在且非空，则以其值作为配置路径 `skills.entries.<skillKey>` 中的键名。仅当 `skillKey` 未定义时，才回退使用 YAML 前置元数据中的 `name` 字段。这一优先级设计允许技能作者为其声明一个与展示名称解耦的稳定配置键：即使技能的 `name` 因版本升级或品牌调整发生变更，`skillKey` 保持不变，用户已有的单技能配置引用不会被破坏。例如，一个 `name` 为 "Deep Researcher v2" 的技能可声明 `skillKey: deep-researcher`，使既有配置 `skills.entries.deep-researcher.enabled` 继续生效。

### 6.4 阶段三：需求检查

阶段三执行环境兼容性验证，但在 `metadata.openclaw.always` 为 `true` 时整阶段被短路跳过。该短路语义意味着技能作者可以声明某些技能为"无条件加载"状态，绕过所有平台、工具链和配置检查。未设置 `always`（默认 `false`）或显式设为 `false` 的技能，则依次接受以下四项检查。

#### 6.4.1 操作系统兼容性检查

需求检查的首项验证是平台匹配。`requires.os` 字段定义允许的操作系统标识符数组，有效值为 `darwin`、`linux`、`win32`。检查逻辑调用 `RuntimeInformation.IsOSPlatform` 方法，将当前运行时的操作系统与数组条目逐一比对。若数组为空或未定义，则任何操作系统均通过此项检查；若数组非空且当前平台不在列出的标识符之中，技能被判定为环境不兼容并剔除。该检查使技能作者能够声明平台专属行为（如 macOS 上的 `launchctl` 操作或 Windows 上的 PowerShell 集成），避免在不兼容平台上暴露无效指令。

#### 6.4.2 二进制文件可用性验证

对于依赖外部命令行工具的技能，`requires.bins` 和 `requires.anyBins` 提供两种验证模式。`bins` 采用"全部满足"语义：列出的所有二进制文件必须均存在于当前进程的 `$PATH` 环境变量搜索路径中。`anyBins` 采用"任一满足"语义：列出的二进制文件中至少有一个可用即可通过。`SkillLoader` 通过扫描 `$PATH` 目录验证每个可执行文件的存在性，并将扫描结果缓存在一个 `ConcurrentDictionary<string, bool>` 中。缓存以二进制名称为键、布尔可用性为值，避免在多个技能共享同一工具依赖时产生重复的磁盘 I/O 查询。当 `bins` 中有任一未命中缓存或返回 `false`，或 `anyBins` 中全部条目均返回 `false` 时，技能在此阶段被过滤。

#### 6.4.3 环境变量与配置真值检查

第三项验证覆盖环境状态和配置状态两个维度。`requires.env` 列出的环境变量名称必须全部已设置（值可为空字符串，但键必须存在于当前进程环境中）。`requires.config` 列出的配置路径必须解析为真值（truthy）。需要特别注意的是，单技能配置注入在此阶段生效：`skills.entries.<skillKey>.env` 中定义的键值对，以及 `skills.entries.<skillKey>.apiKey` 对应的环境变量（通过 `metadata.openclaw.primaryEnv` 命名），均在需求检查前被注入到当前进程环境。这意味着用户可以通过单技能配置满足 `requires.env` 的门控条件，使技能在特定工作区中通过验证，而无需修改全局操作系统环境变量。例如，技能声明 `requires.env: ["RESEARCH_API_KEY"]`，用户可在配置中设置 `apiKey: sk-xxx`，`SkillLoader` 将其以 `RESEARCH_API_KEY=sk-xxx` 的形式注入，随后需求检查发现变量已设置，检查通过。

#### 6.4.4 `always=true` 的短路语义

`metadata.openclaw.always` 布尔字段为 `true` 时，阶段三的全部四项检查——操作系统匹配、二进制文件可用性、环境变量存在性、配置真值——被无条件短路跳过，技能直接进入合格集合。该机制适用于不依赖特定外部环境的基础行为技能，或作者希望技能始终向 LLM 暴露的场景。需要明确区分的是，`always=true` 仅短路阶段三的需求检查，不绕过阶段一的 `AllowBundled` 白名单过滤和阶段二的 `enabled` 终止开关。这意味着即使技能声明了 `always=true`，仍可能在前两个阶段被运维策略或单技能配置禁用；短路语义不能凌驾于配置层的显式控制之上。

三阶段过滤流水线的执行顺序经过精心设计：阶段一在来源层面执行粗粒度裁剪，阶段二在个体层面执行显式覆盖，阶段三在环境层面执行动态门控。这种由外向内、由策略到环境的递进式过滤，确保最终进入系统提示词的技能集合既符合运维意图，又适配运行时环境。


## 7. 系统提示词集成

合格的技能在通过 `SkillLoader` 的三阶段过滤流水线后，其内容并不会直接作为独立输入传递给 LLM，而是由 `SkillPromptBuilder.Build` 方法将其格式化并嵌入到系统提示词内部。该构建器采用**双块输出**策略：第一个 XML 块提供 LLM 可快速扫描的索引信息，第二个 XML 块提供完整的行为指令正文。两套内容随后由 `AgentSystemPromptBuilder` 追加在基础系统提示词末尾，形成统一的行为扩展层。

### 7.1 SkillPromptBuilder 的双块输出

`SkillPromptBuilder` 将每个活跃技能拆分为两部分写入系统提示词，这一设计源于 LLM 在调用决策与指令执行两个阶段的注意力需求差异：索引信息需要高信噪比以实现快速路由，而行为指令则需要完整的语境以保证执行正确。

#### 7.1.1 <available-skills> 索引块：名称、描述、位置的紧凑型 XML 列表作为 LLM 决策索引

`<available-skills>` 块为每个活跃技能生成一条紧凑的 XML 元素，包含三项核心字段：

| XML 属性 | 来源 | 作用 |
|---------|------|------|
| `name` | YAML 前置元数据中的 `name` | LLM 引用的技能标识符 |
| `description` | YAML 前置元数据中的 `description` | 供模型判断当前任务匹配度的语义摘要 |
| `location` | 技能目录的绝对路径 | 使模型能在必要时引用 `{baseDir}` 指向的伴随文件 |

该块被设计为**纯索引结构**，不包含任何可执行指令。LLM 通过扫描此列表中的 `name` 和 `description`，在收到用户请求后判断应激活哪个技能。例如，当用户输入 "帮我查一下这个主题的最新研究" 时，LLM 会匹配 `deep-researcher` 的 `description` 字段，从而将后续行为约束在该技能的指令框架内。

#### 7.1.2 <skill-instructions> 指令块：完整 Markdown 指令主体以 ## Skill: <name> 为前缀

`<skill-instructions>` 块承载每个技能的实际行为定义。`SkillPromptBuilder` 在嵌入指令主体前执行两项标准化处理：

1. **路径占位符解析**：将 SKILL.md 中的 `{baseDir}` 替换为技能目录的绝对路径，使模型生成的工具调用参数能够精确定位伴随资源；
2. **标题注入**：在每个技能的 Markdown 指令主体前插入 `## Skill: <name>` 二级标题，作为技能边界标记。该标题不参与 LLM 的行为指令语义，仅用于在超长系统提示词中快速定位特定技能的内容范围。

指令块中的内容直接引用工具注册表中的工具名（如 `web_search`、`shell`），而非抽象概念。这种设计确保了技能指令与 agent 的实际工具链对齐，避免了因工具名不一致导致的调用失败。

### 7.2 与 AgentSystemPromptBuilder 的拼接

`SkillPromptBuilder` 的输出并非独立系统提示词，而是作为**追加层**融入由 `AgentSystemPromptBuilder` 构建的基础系统提示词。

#### 7.2.1 基础系统提示词后的追加语义：Skill 内容作为行为扩展而非替换

拼接遵循严格的顺序约束：基础系统提示词在前，两个 XML 块在后。这一追加语义体现了 Skill 机制的核心设计原则——**行为扩展而非行为替换**。基础系统提示词定义了 agent 的全局角色、安全策略和通用工具使用规范；Skill 内容在此基础上叠加特定领域的行为细化。当某技能的指令与基础系统提示词存在冲突时，基础系统提示词中的安全约束和硬性规则仍具最高优先级。

拼接后的完整系统提示词在传入 LLM 前，会经过 `SkillPromptBuilder.EstimateCharacterCost` 进行字符级成本预估算（参见第 9 章），Token 预算控制器据此决定是否截断或拒绝该轮对话。

#### 7.2.2 disable-model-invocation 的排除语义：被标记技能完全不出现在两个 XML 块中

当某个技能的 YAML 前置元数据中包含 `disable-model-invocation: true` 时，该技能在构建阶段即被从双块输出中**完全剔除**。这一标记语义区别于 `user-invocable: false`：后者仅禁用斜杠命令入口，技能指令仍会在系统提示词中呈现；而 `disable-model-invocation: true` 意味着模型在 prompt 层面完全不可见该技能，其行为不会以任何形式干扰 LLM 的生成过程。

被排除的技能成本贡献为零——其名称、描述、位置和指令主体的字符长度均不计入 `EstimateCharacterCost` 的累加计算。这使得 `disable-model-invocation` 不仅是功能开关，也是精细化的 token 预算控制手段。

### 7.3 运行时注入效果示例

#### 7.3.1 完整的系统提示词 Skill 片段示例：展示注入后的 XML 结构与内容形态

以下片段展示当 `deep-researcher` 和 `data-analyst` 两个技能通过全部过滤阶段后，`SkillPromptBuilder` 生成的实际注入内容。`deep-researcher` 的 SKILL.md 前置元数据定义了 `name: deep-researcher`、`description: Conducts comprehensive web research across multiple sources and generates structured reports`，并设置了 `disable-model-invocation: false`。其 Markdown 指令主体包含多步骤研究协议，引用 `web_search`、`web_fetch`、`pdf_read` 和 `memory` 四个工具。`data-analyst` 具有类似结构。

```xml
<available-skills>
  <skill name="deep-researcher"
         description="Conducts comprehensive web research across multiple sources and generates structured reports"
         location="/home/user/.openclaw/skills/deep-researcher" />
  <skill name="data-analyst"
         description="Executes database queries and performs data analysis with visualization"
         location="/home/user/.openclaw/skills/data-analyst" />
</available-skills>

<skill-instructions>
## Skill: deep-researcher

You are a deep research agent. Your goal is to produce comprehensive, well-sourced reports on any topic.

1. **Planning Phase**: Before searching, define 3-5 research angles and a target deliverable format (report, comparison, timeline, etc.).
2. **Multi-Source Search**: Use `web_search` to find primary sources, then use `web_fetch` to extract full content from promising URLs. Also search academic PDFs with `pdf_read` when available.
3. **Synthesis**: Cross-reference findings across sources. Flag conflicting information and note confidence levels.
4. **Reporting**: Write the final report using `write_file`. Include inline citations mapping claims back to source URLs.
5. **Memory**: Store key findings in `memory` under the research topic for future sessions.

Base directory for skill resources: /home/user/.openclaw/skills/deep-researcher

## Skill: data-analyst

You are a data analysis specialist. Your goal is to extract insights from structured data.

1. **Schema Discovery**: If database tables are unknown, first use `database` tool to list schema.
2. **Query Construction**: Write SQL queries with `database`. Prefer CTEs for readability. Always include row count estimates.
3. **Execution & Validation**: Run queries, check for edge cases (nulls, duplicates, outliers), iterate as needed.
4. **Visualization**: When data is ready, use `code_exec` to generate charts (matplotlib, seaborn). Save outputs alongside analysis.
5. **Interpretation**: Summarize statistical significance and business implications, not just raw numbers.

Base directory for skill resources: /home/user/.openclaw/skills/data-analyst
</skill-instructions>
```

上述示例呈现了双块结构的完整形态。`<available-skills>` 以扁平列表形式提供机器可扫描的索引，三个 XML 属性（`name`、`description`、`location`）完整对应 `Skill` 模型的公开属性。`<skill-instructions>` 则保留原始 Markdown 的层级结构，每个技能的指令主体以 `## Skill: <name>` 为前缀，形成明确的视觉边界。`{baseDir}` 占位符在注入阶段已解析为绝对路径，因此模型生成的文件引用可直接落地到正确的文件系统位置。该片段被附加在 `AgentSystemPromptBuilder` 生成的基础系统提示词末尾，最终作为单次请求的系统角色消息传递给 LLM。


## 8. 运行时热重载机制

当配置项 `skills.load.watch` 设为 `true` 时，`SkillWatcherService` 在运行时持续监控技能来源目录的文件系统变动，并在检测到 `SKILL.md` 文件变更后自动触发完整重载流水线。该机制使开发者能够在不重启 Gateway 进程的前提下迭代调试技能内容。

### 8.1 SkillWatcherService 架构

#### 8.1.1 FileSystemWatcher 的多目录监控

`SkillWatcherService` 不为整个文件系统注册单一监听器，而是为每个活动的来源目录创建独立的 `FileSystemWatcher` 实例。这种设计源于 OpenClaw 的五层来源架构（见第 5 章）：内置目录、托管目录、工作区目录以及 `skills.load.extraDirs` 中配置的额外目录可能分布在完全不同的文件系统路径下，甚至跨越不同的挂载点。每个 `FileSystemWatcher` 实例绑定到一个具体的目录路径上，其 `IncludeSubdirectories` 属性设为 `true`，从而覆盖该来源下所有技能子目录。服务初始化时仅监控在配置中启用的来源，未激活的目录不分配监听资源。

#### 8.1.2 SKILL.md 专属监控策略

监听器并非响应所有文件变更。`SkillWatcherService` 将 `FileSystemWatcher` 的 `Filter` 属性精确设为 `SKILL.md`，这意味着只有目标文件的创建、删除、修改和重命名事件能够穿透到处理层。技能目录中伴随的示例文件、配置文件或开发者草稿都不会触发重载。当事件到达后，服务通过事件类型判断变动的语义：新建 `SKILL.md` 对应技能首次可用；删除对应技能失效；修改对应指令内容更新；重命名则同时触发旧路径删除和新路径创建的等效语义。

### 8.2 防抖与并发控制

#### 8.2.1 500ms 防抖合并

文件系统事件在高频编辑场景下具有爆发性。开发者在 IDE 中连续保存文件、批量替换文本或使用格式化工具时，`FileSystemWatcher` 可能在极短窗口内产生多次事件。`SkillWatcherService` 通过 500ms 的防抖间隔将这些离散事件聚合为单一的重载请求。具体实现中，每次事件到达时启动或重置一个定时器，直到连续 500ms 内无新事件到达才实际触发 `AgentRuntime.ReloadSkillsAsync`。该防抖窗口的配置由 `skills.load.watchDebounceMs` 控制（默认 250ms），实际聚合行为在内部被规范为 500ms 级别的事件合并，以避免半写状态（half-written state）被加载。

#### 8.2.2 Interlocked 标志的串行化保护

完整的 `SkillLoader.LoadAll` 流水线涉及磁盘扫描、YAML 解析、需求门控评估和系统提示词构建，执行期间需要数百毫秒。如果在一次重载尚未完成时，新的文件系统事件再次到达，不加保护将导致并发重载重叠执行，产生竞态条件。`SkillWatcherService` 使用 `Interlocked` 原子操作维护一个布尔标志位：重载开始前通过 `Interlocked.CompareExchange` 尝试将标志从 `0` 置为 `1`，若失败则说明已有重载在进行中，当前事件被静默丢弃；重载完成后通过 `Interlocked.Exchange` 将标志复位。该机制将可能重叠的并发文件更改强制串行化为单次重载周期，无需显式锁对象，避免了线程池环境下的死锁风险。

### 8.3 重载流水线

#### 8.3.1 AgentRuntime.ReloadSkillsAsync 的完整重新执行

防抖定时器到期后，`SkillWatcherService` 调用 `AgentRuntime.ReloadSkillsAsync`，后者并非执行增量补丁，而是完整地重新执行 `SkillLoader.LoadAll` 流水线。这意味着热重载周期重新经历目录扫描、YAML 前置元数据解析、三阶段过滤（`AllowBundled` 白名单、单技能启用开关、需求门控）以及 `SkillPromptBuilder.Build` 的 XML 块构建。选择全量重载而非增量更新，是因为技能之间存在依赖关系：一个技能的 `SKILL.md` 变动可能触发 `requires.config` 门控条件的重新评估，进而影响其他技能的加载资格；同时工作区来源对内置来源的覆盖关系也需要全局重新计算。

#### 8.3.2 缓存系统提示词的原子替换

`AgentRuntime` 维护当前生效的系统提示词缓存。`ReloadSkillsAsync` 在 `SkillLoader.LoadAll` 返回新结果后，将新构建的 `<available-skills>` 与 `<skill-instructions>` XML 块原子地替换到缓存中，随后通知对话引擎使用更新后的提示词参与下一轮 LLM 调用。替换操作发生在内存引用层面：新提示词字符串构建完成后，一次赋值即完成生效，不存在新旧提示词混合的中间状态。这保证了一旦重载成功，所有后续对话轮次立即看到更新后的技能集；若重载过程中发生异常（如 `SKILL.md` YAML 格式错误），`ReloadSkillsAsync` 捕获异常并保留上一次成功的缓存不变，避免将半解析或损坏的技能内容暴露给模型。


## 9. Token 成本估算与预算控制

### 9.1 成本估算模型

#### 9.1.1 `EstimateCharacterCost` 的算法

`SkillPromptBuilder.EstimateCharacterCost` 提供技能注入的字符级预先成本估算，供 Token 预算准入控制器在调用 LLM 前执行对话轮次预检。算法由三项累加构成：XML 包装器基础开销（固定 195 字符），技能级标签开销（每技能 97 字符），以及所有激活技能的 `name`、`description`、`Location` 路径与完整 Markdown 指令主体（含 `{baseDir}` 替换结果）的字符总数之和。

| 参数名 | 数值（字符） | 描述/作用 |
|--------|-------------|----------|
| `BaseXmlWrapperOverhead` | 195 | `<available-skills>` 与 `<skill-instructions>` 外层 XML 容器的固定标签结构开销 |
| `PerSkillXmlTagOverhead` | 97 | 每个技能在列表块与指令块中的标签包装和标题前缀开销 |
| `ContentLength` | 动态计算 | 所有激活技能的名称、描述、路径与 SKILL.md 指令主体的字符数总和 |

以字符数而非精确 Token 数作为估算依据，是因为不同模型的 Token 化规则（BPE、SentencePiece 等）存在差异，且运行时不调用实际编码器无法获得精确值。字符数乘以经验系数（约 4 字符/Token）即可提供与模型无关的快速筛选。静态开销（195 + 97 × N）与动态内容长度分离，使调试者能由技能数量 N 直接估算成本下限。

#### 9.1.2 `disable-model-invocation` 的零成本语义

`disable-model-invocation: true` 在成本层面将该技能完全排除于系统提示词之外。`SkillPromptBuilder` 构建 XML 时执行硬排除：不生成 `<skill>` 标签，也不附加 Markdown 指令主体，其成本贡献为零字符。权限控制与成本控制绑定于同一布尔开关——仅通过斜杠命令触发的技能，在常规轮次中不消耗提示词预算。

### 9.2 预算准入控制

#### 9.2.1 Token 预算准入控制器的拒绝逻辑

Token 预算准入控制器在每次调用 LLM 前执行对话轮次预检。其通过 `EstimateCharacterCost` 获取技能集增量成本，叠加基础系统提示词、历史消息与当前输入的预估消耗形成总请求量预测。若预测值超过 per-request 预算上限，控制器在 HTTP 请求发出前拒绝该轮次，避免无效 API 往返与上下文超限错误。

#### 9.2.2 技能数量与提示词成本的权衡策略

技能数量与提示词成本之间存在由功能覆盖与预算约束决定的帕累托前沿。单技能固定开销 97 字符叠加内容长度后，每技能通常增加数百至数千字符。对 4K 或 8K 上下文窗口的模型，激活全部技能可能使常规轮次预检拒绝率显著上升。

工程权衡从两个维度展开。**条件加载**：通过 `skills.entries.<key>.enabled` 与需求门控（`requires.bins`、`requires.env`）的组合，仅在环境满足依赖时加载技能，不可用技能不产生成本。**权限分离**：将技能标记为 `disable-model-invocation: true`，保留斜杠命令触发路径的同时将其从模型可见提示词中剔除，实现"常驻行为"与"按需调用"的分离——前者持续消耗预算，后者仅在用户主动请求时加载。最优配置需结合工作负载的对话深度分布、技能调用频率与模型上下文窗口实验性调参。


## 10. CLI 技能管理

OpenClaw 提供 `openclaw skills` 命令族，使用户能够在不直接操作配置文件的前提下完成技能的离线检查、安装和目录浏览。该 CLI 层封装了 `SkillInspector`、`SkillInstaller` 及相关文件系统工具，所有操作均在本地执行，不涉及网络请求或模型调用。

### 10.1 检查命令

#### 10.1.1 `openclaw skills inspect` 的只读解析功能

`inspect` 子命令接受一个本地路径或 `.tgz` 压缩包作为输入，对其中的 `SKILL.md` 执行完全只读的解析与诊断。该命令复用 `SkillLoader` 的解析管线前半段，但跳过所有写入操作和运行时集成步骤。其输出包含三个维度。

**信任评估**：`SkillInspector` 对技能内容执行静态安全扫描，检查指标包括——是否存在可执行脚本注入模式、是否引用超出技能目录的外部路径、以及前置元数据中 `user-invocable` 和 `disable-model-invocation` 的组合是否存在矛盾配置。信任级别以离散标签输出（如 `trusted`、`review`、`untrusted`），供用户在安装前做出人工判断。

**需求摘要**：该命令完整执行需求门控的静态分析阶段，列出 `metadata.openclaw.requires` 中声明的全部硬性条件（`bins`、`env`、`config`、`os`），并与当前运行环境进行比对。输出明确标注哪些条件已满足、哪些缺失，使用户在安装前即可预知该技能能否在当前工作站上成功加载。

**元数据展示**：以结构化格式回显 YAML 前置元数据中的所有字段，包括 `name`、`description`、`metadata` 嵌套对象、命令调度相关字段（`command-dispatch`、`command-tool`、`command-arg-mode`）以及 `homepage`。若存在 `skillKey` 覆盖，则同时显示原始 `name` 与派生 `skillKey`。

#### 10.1.2 支持目录与 `.tgz` 压缩包两种输入源

`inspect` 的输入参数类型为松散路径字符串，内部通过文件系统探测区分处理。若路径指向一个目录，则直接在该目录下查找 `SKILL.md`。若路径指向一个 `.tgz` 压缩包，CLI 首先将其解压到一个临时目录（使用系统 `temp` 路径，前缀 `openclaw-skill-inspect-`），随后在该临时目录中执行解析，最终清理临时文件。

这一临时解压机制意味着 `inspect` 对压缩包的处理是无状态的——不会在磁盘上保留解压产物。安全层面，临时目录的创建与清理包裹在 `try-finally` 块中，确保即使解析异常退出也能回收资源。此外，`inspect` 不会执行 `SKILL.md` 中的任何指令主体，仅解析其元数据和文本内容，因此不受指令中可能存在的占位符（如 `{baseDir}`）影响。

### 10.2 安装命令

#### 10.2.1 安装目标的三路解析规则

`openclaw skills install <path|tarball>` 在安装前必须确定目标目录，其解析逻辑遵循三路优先级，与命令行显式标志严格绑定：

| 标志 | 目标目录 | 对应来源层级 |
|------|---------|-------------|
| `--managed` | `~/.openclaw/skills/<slug>` | Managed（来源优先级 3） |
| `--workdir <path>` | `<path>/skills/<slug>` | 自定义工作区 |
| 无标志（默认） | `$OPENCLAW_WORKSPACE/skills/<slug>` | Workspace（来源优先级 5） |

三路规则互斥，不可同时指定。`--managed` 直接映射到用户主目录下的托管技能空间，适用于安装希望跨多个工作区复用的通用技能。`--workdir` 允许将技能安装到任意指定目录下的 `skills/` 子目录，适用于多工作区隔离场景。默认路径则依赖 `OPENCLAW_WORKSPACE` 环境变量，将技能绑定到当前活动工作区，该路径在运行时加载优先级中位于最顶层（优先级 5），因此默认可覆盖任何同名内置或托管技能。

#### 10.2.2 slug 派生规则

安装目录的最后一级组件（即 `<slug>`）并非来自用户输入，而是从被安装技能的前置元数据中自动派生。派生顺序如下：优先使用 `metadata.openclaw.skillKey`（若存在），否则回退到顶层 `name` 字段。无论原始值为何，slug 均经过规范化处理：全小写转换，所有非字母数字字符替换为单个连字符（`-`），并去除首尾连字符。

安全层面，安装逻辑明确拒绝符号链接（symbolic links）和重新解析点（reparse points）作为目标目录或中间路径组件。这一拒绝策略防止了通过路径劫持将技能写入非预期的文件系统位置，同时也确保了 `SkillLoader` 在运行时扫描到的目录结构与其元数据记录保持一致。

#### 10.2.3 `--dry-run` 的预览语义

`--dry-run` 标志将 `install` 从写入操作转换为纯预览模式。在此模式下，CLI 完整执行以下步骤但不执行最终的文件复制或目录创建：解析输入源（含临时解压）、派生 slug、计算目标目录路径、检查目标路径是否已存在同名技能、执行信任评估与需求摘要分析。输出报告以结构化文本呈现安装将产生的全部副作用，包括目标目录的绝对路径、是否覆盖现有文件、以及该技能在当前环境中的可用性预判。`--dry-run` 的语义设计遵循"所见即所得"原则——预览输出中的目标路径和 slug 与真实安装完全一致，仅缺少最后的文件系统写入调用。

### 10.3 列表命令

#### 10.3.1 `openclaw skills list` 的输出维度

`openclaw skills list` 枚举指定目标目录中的所有已安装技能，输出以表格或结构化文本形式呈现，包含三个核心维度。

**信任级别**：基于与 `inspect` 相同的 `SkillInspector` 静态分析逻辑，为每个已安装技能标注当前信任评估结果。与 `inspect` 的区别在于，`list` 对已安装技能执行的是"本地再评估"——若技能文件自安装后被外部修改，评估结果可能发生变化。

**来源标签**：尽管 `list` 本身仅扫描单一目录，其输出中的 `source` 字段将该目录映射到已知的来源分类（`Managed` 或 `Workspace`），帮助用户理解这些技能在运行时加载优先级中的位置。若使用 `--workdir` 指定非标准路径，来源标签显示为 `Custom`。

**文件系统路径**：显示每个技能目录的绝对路径，使用户能够直接定位到 `SKILL.md` 文件进行手动审查或编辑。路径输出解析任何存在的符号中间链接，以规范化物理路径呈现，与加载器内部的 `Location` 属性保持一致。

`list` 同样支持 `--managed` 和 `--workdir` 标志控制扫描范围，其路径解析规则与 `install` 完全一致。默认情况下扫描 `$OPENCLAW_WORKSPACE/skills`，与安装命令的默认行为形成对称，确保用户在安装后立即使用无参 `list` 即可看到新技能。


# 11. 配置参考

## 11.1 加载配置

### 11.1.1 `skills.enabled` 主开关

`skills.enabled` 是整个技能系统的总控断路器。该布尔值决定 `SkillLoader` 是否参与初始化流水线；设为 `false` 时，`AgentRuntime.BootstrapAsync` 跳过技能发现与加载阶段，系统提示词中不产生 `<available-skills>` 和 `<skill-instructions>` 块，等效于技能子系统未安装。该开关的默认值为 `true`，意味着在干净的 `GatewayConfig.Skills` 节缺失时，技能系统默认处于启用状态。

### 11.1.2 Load 子节的五类配置项

`skills.load` 节定义了来源扫描策略与运行时行为，包含六个独立配置项，分为两类语义：来源控制（ExtraDirs、IncludeBundled、IncludeManaged、IncludeWorkspace）与运行时行为（Watch、WatchDebounceMs）。

**来源控制**决定 `SkillLoader.ScanAllSources` 在哪些目录上执行扫描。ExtraDirs 是最高优先级的额外加载路径，允许开发者在标准来源之外挂载私有技能仓库。IncludeBundled、IncludeManaged、IncludeWorkspace 分别对应技能五层优先级中的第2、3、5层（详见第5章），通过将任一布尔值设为 `false` 可关闭对应来源。这些开关与 `skills.allowBundled` 的区别在于：前者决定是否扫描目录，后者在扫描完成后对内置技能做白名单过滤（阶段1过滤）。

**运行时行为**控制 `SkillWatcherService` 的激活策略。`skills.load.watch` 开启后，运行时将为每个已启用的来源目录创建独立的 `FileSystemWatcher`，仅监听 `SKILL.md` 文件的变动事件；`watchDebounceMs` 指定防抖间隔，默认 250 毫秒，该值是单个 `FileSystemWatcher` 触发后到 `ReloadSkillsAsync` 实际执行之间的最小等待时间，目的是合并密集的文件系统事件（如 IDE 保存时触发的多次写入）。

下表汇总加载子节全部配置项：

| 配置路径 | 类型 | 默认值 | 描述 |
|---------|------|--------|------|
| `skills.enabled` | `bool` | `true` | 技能系统总开关；`false` 时完全跳过加载流水线 |
| `skills.load.extraDirs` | `string[]` | `[]` | 额外技能扫描路径；优先级高于内置和托管来源 |
| `skills.load.includeBundled` | `bool` | `true` | 是否扫描 `{AppContext.BaseDirectory}/skills` 目录 |
| `skills.load.includeManaged` | `bool` | `true` | 是否扫描 `~/.openclaw/skills` 目录 |
| `skills.load.includeWorkspace` | `bool` | `true` | 是否扫描 `$OPENCLAW_WORKSPACE/skills` 目录 |
| `skills.load.watch` | `bool` | `false` | 启用文件系统监听；`SkillWatcherService` 创建 `FileSystemWatcher` |
| `skills.load.watchDebounceMs` | `int` | `250` | 防抖间隔（毫秒）；合并连续文件事件后触发重载 |

来源控制项在 `GatewayConfig.cs` 中被建模为 `SkillLoadOptions` 子对象，其属性名采用 PascalCase（`ExtraDirs`、`IncludeBundled` 等），而 JSON 反序列化同时支持 camelCase 与 PascalCase 键名。`extraDirs` 的扫描顺序由数组索引决定，索引 0 的目录具有比索引 1 更高的优先级，但所有 ExtraDirs 整体仍低于 Workspace 来源。

## 11.2 单技能配置

### 11.2.1 Entries 子节的四层配置

`skills.entries` 是一个以技能键（skill key）为索引的字典，每个值包含四层独立语义的配置子节：`enabled`、`apiKey`、`env`、`config`。这四层在 `SkillLoader` 的三阶段过滤流水线中分别作用于不同环节。

`enabled` 是阶段2过滤的判定条件。`skills.entries["<key>"].enabled` 为 `false` 时，无论需求检查是否通过，该技能被静默排除，不进入 `<available-skills>` 列表，也不产生 token 成本。默认值为 `true`，意味着未在配置中显式声明的技能默认启用。

`env` 提供需求门控期间注入的额外环境变量字典。键值对在阶段3的需求检查之前被写入进程环境上下文，因此 `requires.env` 的检查结果受 `skills.entries["<key>"].env` 的影响。该字典的典型用途是为特定技能覆盖全局工具链版本，例如 `{ "PYTHON_VERSION": "3.11" }`。

`config` 是用于 `requires.config` 路径判定的自定义配置包。其键值结构与 `GatewayConfig` 的其余部分一致，但仅在 `requires.config` 表达式求值时可见。例如，若某技能的 `SKILL.md` 元数据包含 `requires.config: ["tools.allowShell"]`，而全局配置 `tools.allowShell` 为 `false`，则可通过在该技能条目的 `config` 中设置 `{ "tools": { "allowShell": true } }` 使其通过检查。这实现了技能级别的配置隔离，避免全局安全策略被永久放宽。

### 11.2.2 `apiKey` 到 `primaryEnv` 的注入映射

`apiKey` 是 `skills.entries` 中最具设计特殊性的字段，它并非直接暴露给 LLM，而是作为环境变量注入的简写语法。当 `SKILL.md` 的前置元数据包含 `"primaryEnv": "<ENV_NAME>"` 时，配置中的 `skills.entries["<key>"].apiKey` 被自动转换为环境变量 `<ENV_NAME>`，其值取自配置字符串。这一映射发生在阶段3需求检查之前，因此 `requires.env` 的验证结果同样受 `apiKey` 配置的影响。

该设计将两种常见配置模式合并为一个字段：为技能提供 API 凭证，以及满足 `requires.env` 门控。以 `deep-researcher` 为例，其 `SKILL.md` 的 `metadata` 中声明 `"primaryEnv": "RESEARCH_API_KEY"`，则配置 `apiKey: "sk-xxxx"` 等效于在运行前设置环境变量 `RESEARCH_API_KEY=sk-xxxx`。若 `apiKey` 未设置而环境变量已存在，运行时保留环境变量值，不发生覆盖；若两者皆未设置，且 `requires.env` 包含该变量，则技能在阶段3被过滤。

下表汇总单技能条目的全部配置字段：

| 配置路径 | 类型 | 默认值 | 描述 |
|---------|------|--------|------|
| `skills.entries.<key>.enabled` | `bool` | `true` | 单个技能的阶段2过滤开关 |
| `skills.entries.<key>.apiKey` | `string?` | `null` | 简写凭证；按 `primaryEnv` 注入为具名环境变量 |
| `skills.entries.<key>.env` | `Dictionary<string, string>` | `{}` | 需求门控期间注入的额外环境变量 |
| `skills.entries.<key>.config` | `Dictionary<string, object>` | `{}` | 用于 `requires.config` 求值的技能级配置覆盖 |

配置查找键 `<key>` 的解析遵循 `skillKey` 回退机制：若技能的 `metadata.openclaw.skillKey` 已设置，则使用该值替代 `name` 作为字典索引。这意味着开发者可在不改变技能目录名称的前提下，通过 `skillKey` 定义一个稳定的配置锚点。注意 `apiKey` 与 `env` 在作用域上存在重叠——两者都产生环境变量，但 `apiKey` 只能注入一个具名变量（由 `primaryEnv` 决定），而 `env` 可注入任意数量的无预声明键值对。

## 11.3 完整配置示例

### 11.3.1 `GatewayConfig.Skills` 的 JSON 配置模板

以下 JSON 配置展示了一个典型工作场景的组合：技能系统启用，包含全部标准来源，开启热重载，对 `deep-researcher` 注入 API 凭证、覆盖模型环境变量、对 `software-developer` 临时放宽 `tools.allowShell` 以支持容器构建，同时通过 `allowBundled` 白名单仅保留 `software-developer` 和 `deep-researcher` 两个内置技能。

```json
{
  "Skills": {
    "Enabled": true,
    "Load": {
      "ExtraDirs": [
        "/opt/company-skills"
      ],
      "IncludeBundled": true,
      "IncludeManaged": true,
      "IncludeWorkspace": true,
      "Watch": true,
      "WatchDebounceMs": 500
    },
    "AllowBundled": [
      "software-developer",
      "deep-researcher"
    ],
    "Entries": {
      "deep-researcher": {
        "Enabled": true,
        "ApiKey": "sk-research-xxxx",
        "Env": {
          "RESEARCH_MODEL": "gpt-4o",
          "MAX_RESULTS": "50"
        },
        "Config": {}
      },
      "software-developer": {
        "Enabled": true,
        "ApiKey": null,
        "Env": {},
        "Config": {
          "tools": {
            "allowShell": true
          }
        }
      },
      "data-analyst": {
        "Enabled": false,
        "ApiKey": null,
        "Env": {},
        "Config": {}
      },
      "homeassistant-operator": {
        "Enabled": true,
        "ApiKey": "ha-token-yyyy",
        "Env": {
          "HOME_ASSISTANT_URL": "http://home.local:8123"
        },
        "Config": {}
      }
    }
  }
}
```

该配置的执行语义可从三个维度解读。来源维度上，`/opt/company-skills` 作为 Extra 来源被挂载，其技能可覆盖同名内置技能；但 `allowBundled` 白名单仅放行 `software-developer` 与 `deep-researcher`，其余内置技能（如 `data-analyst`）在阶段1即被过滤，即使 `entries` 中显式启用也无济于事。过滤维度上，`data-analyst` 的 `enabled: false` 提供了阶段2排除的示范；当技能同时被 `allowBundled` 和 `enabled` 排除时，阶段1优先执行，但结果一致。凭证维度上，`deep-researcher` 的 `apiKey` 通过其 `primaryEnv`（假设为 `RESEARCH_API_KEY`）注入，`homeassistant-operator` 的 `apiKey` 则通过其自身的 `primaryEnv`（假设为 `HA_TOKEN`）注入，两者互不干扰。

当该配置通过 `GatewayConfig` 绑定到 .NET 配置系统时，可通过环境变量 `OpenClaw__Skills__Entries__deep-researcher__ApiKey` 在部署时覆盖 JSON 中的硬编码值，无需修改配置文件本身。这一层级绑定特性使 `apiKey` 凭证管理可完全移交至外部机密存储（如 Kubernetes Secrets 或 Azure Key Vault）。


# 12. 编写指南与最佳实践

作为 Skill 机制文档的收束章，本章将前面章节分散阐述的目录结构、元数据语义、指令风格、加载优先级、CLI 工具及配置模型整合为一套可操作的实践流程，并以一份完整的模板文件提供即用的起点。

## 12.1 五步编写流程

从零开始编写一个 Skill，推荐按以下五个步骤执行。每一步对应一个决策点，确保编写者不会遗漏影响加载或执行的关键细节。

### 12.1.1 选择位置：项目特定 vs 全局可用的目录选择决策树

Skill 存放位置直接决定其可见范围与加载优先级。下表给出决策路径：

| 场景 | 目标目录 | 优先级 | 覆盖行为 |
|------|---------|--------|---------|
| 仅对当前工作区生效，且可能覆盖同名内置技能 | `$OPENCLAW_WORKSPACE/skills/<name>/` | 最高（5） | 静默覆盖低优先级同名技能 |
| 全局可用，跟随用户漫游至所有工作区 | `~/.openclaw/skills/<name>/` | 3 | 可被工作区技能覆盖 |
| 团队共享或 CI/CD 统一注入 | `skills.load.extraDirs` 配置项 | 最低（1） | 所有工作区统一可见，可被任何高优先级来源覆盖 |

目录命名必须采用 kebab-case（如 `deep-researcher`），且内部仅含 `SKILL.md` 一个入口文件。存放位置一旦选定，后续 CLI 的 `install` 命令必须对应使用 `--workdir` 或 `--managed` 标志，否则会安装至默认的工作区目录。

### 12.1.2 编写前置元数据：最小必填字段与条件加载配置

`SKILL.md` 最顶部的 YAML front-matter 包含两类字段：**身份声明**与**门控条件**。最小可行的元数据仅需两个字段：

```yaml
---
name: my-skill
description: A concise, single-sentence summary of what this skill does.
---
```

`name` 是技能标识符，不区分大小写，用于五层来源中的名称冲突去重。`description` 虽在技术层面为可选，但它在 `<available-skills>` XML 块中直接呈现给 LLM，缺失会导致模型无法判断何时调用该技能，因此视为事实必填。

条件加载通过 `metadata` 字段中的 `openclaw` 对象实现。典型配置包括 `requires` 子结构——用于声明二进制文件、环境变量、配置项的依赖——以及 `always: true` 用于无条件加载。若技能仅在 macOS 生效，附加 `os: [darwin]`；若需要外部 API 密钥，声明 `primaryEnv: MY_API_KEY`，以便与 `skills.entries.<key>.apiKey` 的注入路径对齐。

### 12.1.3 编写指令主体：程序化语言、工具引用、Token 效率的三重约束

前置元数据之后是 Markdown 正文，即注入 `<skill-instructions>` 的指令主体。编写时需同时满足三条约束：

**程序化语言。** 使用编号步骤替代自然语言叙述，例如：

```markdown
1. Use `web_search` to find the latest version of the dependency.
2. Read `package.json` using `read_file`.
3. If the version differs, invoke `write_file` with the updated JSON.
4. Run `shell` with `npm install` to verify.
```

**工具引用精确匹配。** 指令中出现的工具名称必须与 agent 工具注册表中的名称完全一致，如 `web_search`、`shell`、`write_file`。拼写偏差会导致模型尝试调用不存在的工具，引发运行时失败。

**Token 效率。** `SkillPromptBuilder.EstimateCharacterCost` 为每个技能计入约 97 字符的 XML 标签开销，加上指令正文本身长度。冗长的自然语言描述会线性增加系统提示词长度，挤压对话上下文空间。建议将每条指令压缩至一行，删除冗余连接词，仅保留动词-宾语-工具的三元结构。

若技能需要引用伴随文件（如示例代码片段、JSON schema），使用 `{baseDir}` 占位符，解析时 `SkillLoader` 会自动将其替换为该技能目录的绝对路径。

### 12.1.4 验证与部署：inspect 检查、install 安装、watch 热重载的完整链路

编写完成后，按以下顺序验证与部署：

1. **本地审查。** 运行 `openclaw skills inspect <directory>` 调用 `SkillInspector` 解析文件，确认元数据格式合法、信任评估通过、需求摘要与预期一致。此步骤为只读操作，不会修改任何目录。
2. **安装部署。** 执行 `openclaw skills install <directory> --managed`（或 `--workdir <path>`）将技能复制到目标目录。安装过程中 CLI 拒绝符号链接和重新解析点，防止路径逃逸风险。slug 自动从 `skillKey` 或 `name` 派生，非字母数字字符替换为连字符并小写化。
3. **运行时确认。** 启动 Gateway 后执行 `openclaw skills list` 验证技能出现在目标来源中，且 `description` 与 `trust` 级别显示正确。
4. **热重载调优。** 若处于迭代开发阶段，在 `GatewayConfig` 中启用 `skills.load.watch: true`，`SkillWatcherService` 将为来源目录创建 `FileSystemWatcher`。保存 `SKILL.md` 后 500ms 防抖窗口内自动触发 `AgentRuntime.ReloadSkillsAsync`，重新执行完整 `SkillLoader.LoadAll` 流水线。`Interlocked` 标志确保并发修改不会导致重入。此机制避免每次修改后重启 Gateway。

## 12.2 质量检查清单

在将 Skill 提交至代码库或共享给团队之前，建议逐条核对以下清单。清单分为元数据与指令两个维度，每条均对应前面章节中已介绍的加载或执行机制。

### 12.2.1 元数据完整性检查

- `name` 字段已填写，且采用小写 kebab-case 格式，确保与目录名一致，减少跨来源名称冲突时的歧义。
- `description` 字段已填写，长度控制在 80 个字符以内，清晰说明技能触发场景，便于 LLM 在 `<available-skills>` 索引中做出正确路由决策。
- `metadata.openclaw` 中的条件字段逻辑自洽：若同时声明了 `always: true` 与 `requires.*`，前者会绕过所有需求检查，此类组合虽合法但可能违背设计意图，需显式确认。
- `requires.bins` 所列二进制文件已在目标环境的 `$PATH` 中验证存在；`requires.env` 所列环境变量名称与实际注入路径（`skills.entries.<key>.env` 或 `primaryEnv`）对齐。
- `command-dispatch` 若已设置，确认对应斜杠命令名称不会与已有内置命令冲突。

### 12.2.2 指令可执行性检查

- 工具名称与 agent 工具注册表中的名称逐字匹配，区分大小写。例如 `web_search` 不可写作 `webSearch` 或 `WebSearch`。
- 步骤编号连续，条件分支（"if ... then ..."）有对应的 else 或终止状态，避免模型陷入未定义路径。
- 边界条件完整：文件不存在、命令返回非零退出码、API 响应为空等异常情况在指令中有显式处理分支。
- `{baseDir}` 仅用于引用技能目录内的伴随文件，不用于构造指向工作区或其他目录的绝对路径。
- 若技能声明了 `disable-model-invocation: true`，确认该技能确实不需要 LLM 直接调用（例如仅作为 CLI 斜杠命令的后端处理器），否则该技能将彻底从系统提示词中消失。

## 12.3 完整 SKILL.md 模板示例

以下是一份示范文件，展示从元数据到指令主体的完整结构。模板中所有可选字段均已填充，并附注释说明其语义和典型取值。实际使用时，删除不需要的字段及注释即可。

### 12.3.1 从元数据到指令主体的完整模板

```markdown
---
# 必填：技能标识符，kebab-case，不区分大小写
name: api-schema-validator

# 必填（事实层面）：人类可读摘要，展示于 CLI 列表和 available-skills XML 块
description: Validates OpenAPI schemas against a set of compliance rules and reports violations.

# 可选：OpenClaw 扩展元数据，JSON 格式
metadata:
  openclaw:
    # 可选：设为 true 则跳过所有 requires 检查，无条件加载
    always: false

    # 可选：显示在 CLI / 仪表板的装饰字符
    emoji: "🔍"

    # 可选：技能文档或上游仓库 URL
    homepage: https://example.com/skills/api-schema-validator

    # 可选：关联环境变量名称，与 skills.entries.<key>.apiKey 注入对齐
    primaryEnv: SCHEMA_VALIDATOR_API_KEY

    # 可选：替代配置查找键，若设置则代替 name 用于 entries 索引
    skillKey: schema-validator

    # 可选：允许的操作系统列表；空数组表示任何系统
    os: [darwin, linux]

    # 可选：需求门控条件
    requires:
      # 必须全部存在的二进制文件
      bins: [node, npm]
      # 至少存在一个的二进制文件
      anyBins: [curl, wget]
      # 必须全部已设置的环境变量
      env: [OPENAPI_SPEC_PATH]
      # 必须为真值的配置路径
      config: [tools.allowShell]

# 可选：false 时禁用斜杠命令触发；默认 true
user-invocable: true

# 可选：true 时从模型系统提示词中完全排除；默认 false
disable-model-invocation: false

# 可选：斜杠命令名称，例如 /validate
command-dispatch: validate

# 可选：斜杠命令分发时调用的目标工具名称
command-tool: shell

# 可选：斜杠命令参数传递模式
command-arg-mode: prompt

# 可选：顶层主页，若 metadata.openclaw.homepage 未设置则回退至此
homepage: https://example.com/skills/api-schema-validator
---

## Skill: api-schema-validator

1. Read the OpenAPI specification file from the path provided by the user or from the environment variable `OPENAPI_SPEC_PATH` using `read_file`.
2. Parse the JSON/YAML content to extract all endpoint definitions, request schemas, and response schemas.
3. For each schema object, check the following compliance rules:
   a. Every endpoint must have a `description` field.
   b. Every request body schema must reference a named component under `#/components/schemas/`.
   c. Every response code `200` or `201` must have a non-empty `content` block.
4. If any rule fails, record the violation with the JSON path and the rule identifier.
5. After all schemas are checked, invoke `write_file` to save the report to `{baseDir}/reports/validation-report.md` with a markdown table summarizing all violations.
6. If no violations are found, output a single-line confirmation message via the `respond` tool.

### Edge Cases

- If the specification file does not exist or cannot be parsed, stop and report the error using `respond` with a clear failure message.
- If `{baseDir}/reports/` does not exist, create it using `shell` with `mkdir -p` before writing the report.
- If `OPENAPI_SPEC_PATH` is unset and the user did not provide a path, prompt the user for the path instead of failing silently.
```

这份模板展示了 Skill 声明的两个独立语义层：YAML front-matter 负责**身份与门控**，Markdown 正文负责**行为与逻辑**。两者通过 `name` 字段绑定，缺一不可。开发者可基于此模板，根据实际场景删减可选字段、替换指令内容，快速生成符合 OpenClaw 加载规范的 Skill 文件。