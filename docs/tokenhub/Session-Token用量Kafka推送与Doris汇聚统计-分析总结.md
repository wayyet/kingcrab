# Session Token 用量 Kafka 推送与 Doris 汇聚统计 — 分析总结

> 配套文档：[设计文档](Session-Token用量Kafka推送与Doris汇聚统计-设计文档.md) ·
> 调用堆栈层次图：[SVG](Session-Token用量Kafka推送与Doris汇聚统计-调用堆栈层次图.svg) ·
> 前置分析：[Token用量统计与提示缓存分析.md](Token用量统计与提示缓存分析.md)
> 日期：2026-06-11

---

## 1. 一句话结论

项目已经把四项 Token 指标（INPUT / OUTPUT / CACHE READ / TOTAL）在 `MafExecutionServiceChatClient.RecordUsage()` 这一个方法里记得清清楚楚，所以**只需在这一个点上加一行"发事件"，再新增一个后台 Kafka 发布器**，剩下的入库和按数字员工汇聚全部交给 Doris 自带的 Routine Load 和物化视图完成——.NET 侧总改动约 2 个新文件 + 2 处小修改，不需要自己写任何 Kafka 消费者。

## 2. 现有功能模块检测结果（需求 #9 的答案）

| 检测项 | 结果 | 处理方式 |
|---|---|---|
| 四项 Token 指标的采集 | ✅ **已存在且完整**：`RecordUsage()` 是唯一写入点，流式/非流式都汇聚于此 | **直接复用**，在末尾加一行 Publish |
| 会话累计值 | ✅ 已存在：`Session.TotalInputTokens` 等字段与 `GetTotalTokens()` | **直接复用**，作为事件中的对账快照字段 |
| 每轮明细模型 | ✅ 已存在：`ProviderTurnUsageEntry`（含 session/channel/provider/model/四类 token/时间戳） | **复用其字段口径**作为 Kafka 消息体蓝本 |
| CACHE READ 归一化 | ✅ 已存在：`NormalizePromptCacheUsage()` + `PromptCacheUsageExtractor` | **直接复用**，事件拿到的已是统一口径 |
| 对外消息推送框架 | ⚠️ 部分存在：`MqttEventBridge`（MQTT，方向相反，是收消息） | **复用其工程模式**（BackgroundService + 退避重连 + 配置开关），代码新写 |
| Kafka 客户端 | ❌ 不存在（全仓库无 Confluent/Kafka 引用） | **新增** NuGet 包 `Confluent.Kafka` |
| 用量观察者抽象（PR #151 的 `ITurnTokenUsageObserver`） | ❌ 不存在 | **新增**简化版 `ITokenUsageEventSink`，未来可平滑对接 PR #151 |
| Doris/数仓相关 | ❌ 不存在 | **新增** Doris 侧 SQL（建表 + Routine Load + 物化视图），纯 SQL 无代码 |

## 3. 方案要点回顾

1. **接入点**：`RecordUsage()` 末尾发布 `SessionTokenUsageEvent`（增量口径），事件附带 `agent_id`（数字员工 ID，默认取 `Session.SenderId`，可配置固定值）。
2. **解耦**：`Publish` 只往**有界内存队列**写（满了丢最旧的），网络 IO 全在后台 `KafkaTokenUsagePublisher` 里做——Kafka 挂了，对话照常。
3. **Kafka**：Topic `session-token-metrics`，**以 agent_id 为 Key** 分区（同一数字员工的事件有序且落在同一分区），`acks=all` + 幂等 producer 保证不因重试翻倍。
4. **Doris**：Routine Load 直接消费 Kafka 写入按天分区的明细表 `session_token_events`；异步物化视图 `agent_token_usage_daily` 每 5 分钟按 `agent_id + 日期` SUM 出四项指标，支撑排行、占比、缓存命中率、日趋势四类报表。
5. **口径**：`total_tokens = input + output`（与项目内 `GetTotalTokens()` 一致，不含 cache write）；跨服务商统计时注意 OpenAI 与 Anthropic 对 INPUT 是否包含缓存命中的口径差异（详见前置分析文档 §5）。

## 4. 给中级开发工程师的通俗讲解

可以把整套链路想象成**超市收银 + 总部报表**：

- **收银台（RecordUsage）**：每次调用 LLM 就像收银员扫了一单。这个项目设计得很好——所有商品（流式、非流式、各家服务商）**都只走这一个收银台**，所以我们要装"摄像头"（发事件）只需要装一个，不用满商场布点。
- **传送带（有界 Channel 队列）**：扫完的小票不直接寄给总部，先扔进收银台旁边的篮子里。篮子是定长的（4096 张），堆满了就把最旧的扔掉——**宁可丢几张小票，也不能让顾客在收银台排队**（推送绝不阻塞对话主流程）。这就是"有界队列 + DropOldest"的含义。
- **快递员（KafkaTokenUsagePublisher）**：一个后台员工不停地从篮子里拿小票寄给总部（Kafka）。快递公司临时罢工（broker 不可用）？他歇 1 秒、2 秒、4 秒……最多 30 秒再试（指数退避），收银台完全无感。这套"歇一会再试"的写法直接抄自项目里已有的 `MqttEventBridge`，不用自己发明。
- **寄件规则（partition key = agent_id）**：同一个数字员工的小票永远寄到同一个分拣口。好处有两个：同一员工的小票按时间顺序到达；总部统计某个员工时不用翻遍所有分拣口。
- **总部录入员（Doris Routine Load）**：这是最省事的一环——**Doris 自带"从 Kafka 收件"的功能**，配一条 SQL 它就每 10 秒去拉一批小票录入明细账（明细表），我们一行消费者代码都不用写。
- **月度报表（物化视图）**：明细账太细没法直接看，Doris 每 5 分钟自动按"员工 × 日期"把四项数字加总成汇总账（`GROUP BY agent_id`），老板要看"哪个数字员工最烧钱""缓存帮我们省了多少"，查汇总账秒出。
- **防错三件套**：① 小票上有唯一编号（event_id），万一快递重复投递可以去重；② 快递要求"总部 3 个仓库至少 2 个签收"才算寄到（acks=all + min.insync.replicas=2）；③ 小票上还印着"该顾客累计消费"（session_total_* 快照），月底对账时拿增量加总和这个数一比，差了就知道有没有丢件。

最容易犯的两个错误：

1. **把"会话累计值"当增量推**——下游 SUM 一聚合就重复计算了。正确做法是推每次调用的增量，累计值只作对账参考。
2. **在热路径上直接 `ProduceAsync`**——Kafka 一抖动，用户的对话就卡住。必须先入内存队列，让后台线程慢慢寄。

## 5. 交付物清单

| 文件 | 内容 |
|---|---|
| [Session-Token用量Kafka推送与Doris汇聚统计-设计文档.md](Session-Token用量Kafka推送与Doris汇聚统计-设计文档.md) | 完整设计：复用分析、消息契约、.NET 代码片段（事件模型/配置/发布器/接入点/DI）、Kafka 规划、Doris DDL 与查询、Mermaid 时序图、可靠性与测试要点、实施清单 |
| [Session-Token用量Kafka推送与Doris汇聚统计-分析总结.md](Session-Token用量Kafka推送与Doris汇聚统计-分析总结.md) | 本文：结论、模块检测结果、通俗讲解 |
| [Session-Token用量Kafka推送与Doris汇聚统计-调用堆栈层次图.svg](Session-Token用量Kafka推送与Doris汇聚统计-调用堆栈层次图.svg) | 从用户消息到 Doris 汇聚的调用堆栈层次图（SVG） |
