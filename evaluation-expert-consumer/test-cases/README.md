# test-cases/

热加载的测试用例库。

## 约定

- **一用例一文件**：`<test_case_id>.tc.json`，文件名（不含 `.tc.json`）必须等于 `test_case_id`
- **schema**：每个文件遵循 [`test-case.schema.json`](../contracts/projections/testcase-ontology/testcase-library/schemas/test-case.schema.json)
- **新增方式**：往本目录放新文件即可生效
- **覆盖路径**：默认本目录；运行时可通过环境变量 `EVAL_TESTCASES_DIR` 指向其他路径
- **自动生成**：当评估请求未携带任何已就绪用例且本目录也未匹配出可用用例时，由 `STEP 1.5 parseTestCases` 按 SOP 优先链自动生成（生成的用例落到 `./runs/<eval-id>/synthesized-cases/` 而非本目录）

## 字段速览

| 字段 | 必填 | 说明 |
|---|---|---|
| `test_case_id` | ✓ | 机器标识，与文件名一致 |
| `applicable_roles` / `applicable_scenarios` | ✓ | 决定哪些岗位/场景下被选中执行 |
| `input.user_message` / `input.context` | ✓ | 给被评测员工的输入与场景上下文 |
| `input.follow_up_messages` | – | 多轮对话的后续触发消息（含触发条件） |
| `expected_output.expected_tool_calls` | – | 期望的工具调用（含 must/should/may 三档） |
| `expected_output.expected_response_traits` | – | 期望话术应满足的特征清单 |
| `expected_output.forbidden_behaviors` | – | 禁止行为；命中即作为扣分证据 |
| `applicable_metrics` | – | 可预绑指标；缺失则由 STEP 2 enrichTestCases 自动绑定 |
| `provenance` | – | 来源标记（employee_sop / synthesized_from_user_scenarios / manual_curation / regression_baseline） |

## 当前用例

- `tc-refund-7day-eligible.tc.json`：7 天无理由退款（happy path）
- `tc-complaint-shipping-delay.tc.json`：物流延迟投诉（含共情与升级判断）

## 与契约的关系

本目录是**数据层**。**契约层**位于 [`contracts/projections/testcase-ontology/`](../contracts/projections/testcase-ontology/)。
