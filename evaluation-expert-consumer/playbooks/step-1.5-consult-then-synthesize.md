# STEP 1.5 — parseTestCases (consult user FIRST, SOP only as fallback)

**Kind**: LLM, conditional (only when `test_case_status == "missing"`)
**Authority**: workflow contract `S1_5` + K5 + K11 + K15 (design facet)
**Outputs**: `./runs/<eval_id>/synthesized-cases/<tc_id>.json` files + `evaluation_context.user_consultation_log`

Real-world scenarios from the user are the **highest-fidelity grounding** for an evaluation. SOPs only describe how the employee SHOULD behave — they do NOT tell us what cases the employee ACTUALLY meets.

## The user-first protocol

When STEP 1.5 fires, **STOP and ask the user before any LLM synthesis.**

### 1. Send a single consultation message (suggested template)

> 我即将为员工 `<employee_id>`（role=`<role>`）生成测试用例。为了让评估贴近真实业务，请提供该员工在生产环境中实际处理的代表性场景（1–7 个）。每个场景请说明：(a) 场景名称与频率；(b) 客户典型开场话术与诉求；(c) 需要员工调用的关键工具 / 查询 / 决策；(d) 隐含红线。若你明确表示「没有」「你自己合成即可」，我才会退回 SOP 合成并标 caveat。

### 2. Classify the response into one of three branches

| Branch | Trigger | Tier | provenance.source | reliability | Notes |
|---|---|---|---|---|---|
| (A) supplies | user provides scenarios | Tier 1 | `user_provided_scenarios` | `high` | LLM only renders user text into `test-case.schema.json` v2.0; MUST NOT invent scenario types not mentioned |
| (B) declines | "你自己合成" / "没有" / "skip" | Tier 2 | `synthesized_from_sop` | `low` (must carry `reliability_caveat`) | STEP 9 surfaces caveat in `open_questions`; language downgraded to "indicative" / "preliminary" |
| (C) partial | user gives 1–2 seeds, asks you to fill rest | mixed | `mixed` | per-case (`high` for seeds, `low` for SOP expansion) | Each case attributed individually |

### 3. Persist the consultation

```jsonc
evaluation_context.user_consultation_log = [
  { "asked_at": "...", "prompt": "...", "user_response": "...", "decision": "tier1" | "tier2" | "tier3" }
]
```

This is the auditable evidence the consultation actually happened.

### 4. Tier 3 (block)

If user declined AND `employee.sop_documents` is empty → `block_or_escalate`. Do **NOT** fabricate scenarios out of thin air.

## Required `provenance` shape (schema-enforced)

```jsonc
{
  "source": "user_provided_scenarios" | "synthesized_from_sop" | "mixed",
  "reliability": "high" | "medium" | "low",
  "reliability_caveat": "synthesized_from_sop_only_no_user_grounding"  // required when reliability == "low"
}
```

Cases without `provenance` MUST fail validation BEFORE being written to `./runs/<eval_id>/synthesized-cases/`.

## v2.0 simulator-driven required fields

Every synthesized case MUST include:

- `input.opening_message` (verbatim user-supplied or rendered from SOP — NEVER use the deprecated `user_message`)
- `input.customer_persona` (`name`, `age_band`, `personality[]`, `communication_style`, `patience_level`)
- `input.initial_emotion` (one of `angry` / `anxious` / `neutral` / `curious` / `satisfied` / `skeptical` / `frustrated`)
- `input.goal` (`primary` required, `secondary` and `bottom_line` recommended)
- `input.context` (free-form scenario context the employee will see)
- `input.stop_conditions` (`success` / `failure` / `deadlock` plain-language descriptions)
- `turn_budget.hard_max_turns` (5–30 typical, 50 max)
- `provenance` (per above)

Forbidden in v2.0: `input.user_message`, `input.follow_up_messages`. STEP 3 ignores them.

## stop_conditions ↔ expected_tool_calls alignment (K15 design facet)

Before STEP 3 begins, **every** synthesized/enriched case MUST pass three self-checks:

1. **Must-tools imply observable outcome.** If `expected_tool_calls` contains any `criticality="must"` entries, ask: *"Can `stop_conditions.success` be true if those tools were NEVER called?"* If yes, the case has an internal contradiction. Rewrite `stop_conditions.success` to require an outcome that implies the must-tools fired.
2. **Required info handoff.** If `context` carries info the evaluatee will need (e.g. `order_reference`) but `opening_message` intentionally omits it, ask: *"Does `stop_conditions.success` assume the customer provided that info?"* If not, rewrite the success condition to include the info-handoff step.
3. **Actionable closure.** `stop_conditions.success` MUST describe an outcome where the customer's problem is **on track to resolution** (action taken or in progress), not merely passive reception of a process explanation.

  Template: `"<verb: 已提交 / 已确认 / 已发起> + <object: 退款申请 / 催派工单 / 订单查询结果>"`

### Worked example (`runs/eval-xiaofu-001/` tc-004-refund-request bug)

```diff
  // Original
- stop_conditions.success = "获得明确的退换货指引和流程说明"
  expected_tool_calls = [query_order_status(must), query_refund_policy(must)]
  context.order_reference = "ORD20240528003"  // not in opening_message

  // Corrected
+ stop_conditions.success = "员工已查询订单并确认符合退款条件，或已为客户发起退货退款申请"
```

The original lets the simulator declare `goal_achieved` at turn 2 (after the employee lists steps), so the employee never receives the order number, never calls the must-tools, and the red-line trips even though the conversation followed the success script. **K15 design facet** catches this before STEP 3.

## Boundary coverage (best practice, not blocking)

Apply equivalence-class partitioning when the scenario seed contains a decision boundary (amount thresholds, time limits, category restrictions, customer tier gates). Pair `positive` ↔ `negative` cases via `paired_case_id`; use `polarity = "boundary"` only when SOP ambiguity at the threshold itself warrants it.

| polarity | Meaning | Example (threshold = 500) |
|---|---|---|
| `positive` | Within normal/allowed path | order_amount=350, direct refund approved |
| `negative` | Exceeds boundary, restricted path | order_amount=899, must handoff to human |
| `boundary` | Exactly at threshold (optional) | order_amount=500, edge case behavior |

Not blocking: pure information-query cases without a decision boundary may stand alone.

## Anti-patterns

| Anti-pattern | K-rule | Failure mode |
|---|---|---|
| Detect `test_case_status == "missing"` and immediately call LLM to synthesize from SOP | K11 | EvaluationReport flags missing consultation |
| Ask the user but proceed with SOP synthesis BEFORE the user has answered | K11 | Same as above |
| Tag SOP-derived cases as `reliability="high"` or omit `reliability_caveat` | K11 | EvaluationReport flagged |
| Write synthesized cases into `./test-cases/` instead of `./runs/<eval-id>/synthesized-cases/` | K5 | block_or_escalate |
| `stop_conditions.success` satisfiable without firing must-tools | K15 (design) | Case rejected at STEP 3 input gate |
| STEP 9 omits `synthesized_from_sop_only_no_user_grounding` caveat when run has any Tier-2 case | K11 | Report flagged |
