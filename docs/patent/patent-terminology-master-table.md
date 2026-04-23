# 专利文档术语总表（统一口径）

## 一、适用范围

本术语总表适用于以下 5 份专利文档，并作为后续摘要、说明书、权利要求书、答审材料的统一口径基线：

1. docs/patent-claims-attorney-style-ontology-slice-skill-runtime.md
2. docs/patent-agent-summary-ontology-slice-skill-runtime.md
3. docs/patent-disclosure-ontology-slice-skill-runtime.md
4. docs/patent-novelty-comparison-table-ontology-slice-skill-runtime.md
5. docs/patent-ontology-slice-skill-runtime.md

## 二、核心术语主表

| 序号 | 统一中文术语（唯一主用） | 可接受英文对照（仅首次括注） | 禁用或不推荐写法 | 使用说明 |
| --- | --- | --- | --- | --- |
| 1 | 本体 | ontology | ontology（正文反复直接使用） | 正文统一使用“本体”；如需英文，仅首次写“本体（ontology）”。 |
| 2 | 本体切片 | ontology slice | slice、task-oriented slice | 指“任务导向最小语义闭包”的核心对象。 |
| 3 | 任务导向本体切片 | task-oriented ontology slice | task-oriented slice | 当需强调任务导向时使用全称；一般段落可简写为“本体切片”。 |
| 4 | 投影文件 | projection | projection（单独出现） | 指由本体切片变换得到的可消费交付物。 |
| 5 | 契约索引 | contract index | contract index、index contract | 统一指路由入口文件。 |
| 6 | 消费技能 | consumer skill | consumer skill | 指消费投影文件的业务技能。 |
| 7 | 技能加载器 | skill loader | loader、skill loader | 指自动发现和绑定契约索引的组件。 |
| 8 | 技能运行时 | skill runtime | runtime、skill runtime | 仅在泛指“运行时”上下文明确时可用“运行时”。 |
| 9 | 请求时 | request time / request-time | request-time（正文长期使用） | 用于表达“按请求动态执行”，建议写作“在请求时”。 |
| 10 | 主题 | topic | topic | 路由第一层选择对象。 |
| 11 | 目标视图 | target view | view、target view | 路由第二层选择对象。 |
| 12 | 路由 | route / routing | route（名词）、route resolution（未翻译） | “解析动作”建议写“路由解析”。 |
| 13 | 路由解析 | route resolution | route resolution | 用于动作短语，避免中英混写。 |
| 14 | 阻断检查 | blocking checks | blocking checks | 指不满足条件时终止消费的规则集合。 |
| 15 | 生产方 | producer | producer | 指提供契约/投影来源的一方。 |
| 16 | 生产方优先级 | producer priority | priority（未限定） | 与评分值共同用于多生产方裁决。 |
| 17 | 评分值 | score | score | 不再单独使用“score”作为正文术语。 |
| 18 | 歧义 | ambiguity | ambiguity | 可具体写“主题歧义”“视图歧义”。 |
| 19 | 发现诊断信息 | discovery diagnostics | diagnostics、discovery diagnostics | 指发现绑定状态与失败原因。 |
| 20 | 模式 | schema | schema | 用于“模式校验”“模式文件”；不再单独写 schema。 |

## 三、投影内容字段口径表

| 统一中文术语（主用） | 常见旧写法 | 使用建议 |
| --- | --- | --- |
| 映射策略 | mapping policy | 在权利要求、说明书中统一写“映射策略”。 |
| 提示投影 | prompt projection | 表示面向提示层的投影内容集合。 |
| 交付物 | delivery artifacts | 表示下游产出目标。 |
| 裁剪项 | dropped items | 表示被显式剔除的信息项。 |
| 未决问题 | open questions | 表示继续消费前待澄清事项。 |
| 提示补丁 | prompt patch | 统一指注入到技能指令的补丁输入。 |
| 允许术语 | allowed terms | 用于术语白名单语义。 |
| 禁止性假设 | forbidden assumptions | 用于不可推断边界。 |
| 必要澄清项 | required clarifications | 用于触发澄清的条目。 |
| 推理路径 | reasoning paths | 用于限制或引导推理路径。 |
| 来源摘要 | source digest | 用于来源压缩表达。 |

## 四、目标视图统一命名

| 统一中文命名（主用） | 英文对照（仅首次括注） | 备注 |
| --- | --- | --- |
| 领域模型视图 | domain-model | 用于对象建模与边界表达。 |
| 数据结构约束视图 | json-schema | 用于结构约束与校验表达。 |
| 提示约束视图 | prompt-constraint | 用于提示边界和假设约束。 |
| 工作流契约视图 | workflow-contract | 用于流程步骤与前置约束。 |

## 五、多生产方裁决术语口径

| 场景 | 统一写法 |
| --- | --- |
| 一般描述 | 多生产方受控裁决 |
| 排序规则 | 按评分值降序、生产方优先级降序排序 |
| 同分处理 | 评分值与生产方优先级同时相同时执行阻断 |
| 不推荐写法 | tie-break、score DESC、priority DESC（正文长期裸用） |

## 六、固定句式模板（建议复用）

1. 术语首次出现模板：
所述本体切片（ontology slice）为围绕当前任务抽取的最小语义闭包。

2. 路由流程模板：
在请求时，先对候选主题执行动态选择，再在选定主题内对候选目标视图执行动态选择。

3. 阻断模板：
当主题歧义、视图歧义、投影文件缺失或未决问题触发阻断策略时，执行阻断检查并停止继续消费。

4. 注入模板：
在阻断检查通过后，将投影文件转换为技能运行时正式输入并注入运行时消费链路。

## 七、跨文档一致性检查清单

后续新增或修改文档时，提交前建议逐项检查：

1. 是否仍出现 ontology、slice、projection、contract index、runtime 等英文裸词。
2. 是否将 topic/view/route 与主题/目标视图/路由混用。
3. 是否将 open questions、blocked、diagnostics 等写法中英混用。
4. 是否将 schema 与模式混用。
5. 是否在同一段中同时出现“技能运行时”“runtime”而无必要。
6. 是否把“生产方优先级”误写为未限定的“priority”。

## 八、维护规则

1. 本表作为专利文本统一用语基线，后续改动应先更新本表，再批量更新正文。
2. 如代理人提出替代表述，优先在“统一中文术语（唯一主用）”列维护新主用词，并同步调整“禁用或不推荐写法”。
3. 若需保留英文术语，建议仅在首次出现时括注，后文一律使用中文主用词。
