# metrics/

热加载的评估指标库。

## 约定

- **一指标一文件**：`<metric_code>.metric.json`，文件名（不含 `.metric.json`）必须等于 `metric_code`
- **schema**：每个文件遵循 [`metric.schema.json`](../contracts/projections/metric-ontology/metric-library/schemas/metric.schema.json)
- **新增方式**：往本目录放新文件即可，不需要改契约或代码；评估器在 PRE 阶段（`loadMetricRegistry`）扫描本目录构建 registry
- **覆盖路径**：默认本目录；运行时可通过环境变量 `EVAL_METRICS_DIR` 指向其他路径

## 字段速览

| 字段 | 必填 | 说明 |
|---|---|---|
| `metric_code` | ✓ | 机器标识，与文件名一致 |
| `parent_dimension` | ✓ | roll-up 到 5 个固定维度之一 |
| `applicable_roles` / `applicable_scenarios` | ✓ | 用于 STEP 2 enrichTestCases 的指标-用例匹配 |
| `runtime_slice_selector` | ✓ | 切片白/黑名单，决定 fan-out prompt 喂给 LLM 哪些 runtime 数据 |
| `scoring_rubric` | ✓ | 给 LLM 的判分准绳（excellent / acceptable / poor） |
| `aggregation_strategy` | ✓ | 跨场景聚合策略（worst_case / weighted_average_by_difficulty / ...） |
| `red_line` | – | 可选；标记该指标为红线判定来源 |

## 当前指标

- `tool_call_correctness.metric.json`：工具调用准确度（红线指标）
- `interaction_empathy.metric.json`：交互共情度
- `order_refund_policy_accuracy.metric.json`：退款政策表述准确度（仅电商客服适用）

## 与契约的关系

本目录是**数据层**。**契约层**位于 [`contracts/projections/metric-ontology/`](../contracts/projections/metric-ontology/)，由它声明 schema 与治理规则；本目录的实例必须通过 schema 校验。
