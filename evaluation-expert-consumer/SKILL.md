---
name: evaluation-expert-consumer
description: Consumer skill for employee evaluation. Drives an 11-step deterministic workflow that resolves the evaluatee, parses/enriches test cases, drives scenarios, fans out per-metric LLM scoring, and produces both per-scenario reports and a final consolidated evaluation report.
metadata: {"openclaw":{"emoji":"📊"}}
---

# evaluation-expert-consumer

This skill is invoked when the user asks to "evaluate employee", "assess performance", "run evaluation", or act as an "evaluation expert".

It is **template-agnostic**: every employee role is evaluated through the **same 11-step workflow**. Per-role variation lives in **four hot-pluggable data layers** (`./metrics/`, `./test-cases/`, `./runtime-drivers/`, `./simulators/`) that are governed by upstream producer skills or by directory-drop convention, NOT by edits to this skill.

## Producer Skill Dependencies

This consumer is bound to **three** projection-publishing producer skills:

| Producer | What it publishes | Where this skill reads |
|---|---|---|
| `ontology_extraction` | Workflow contract, scoring/judgement prompt-constraint, metric-selection prompt-constraint | `contracts/projections/ontology_extraction/` |
| `metric-ontology` | `metric-catalog` projection + `metric.schema.json` for the data layer | `contracts/projections/metric-ontology/` reads, `./metrics/*.metric.json` is the data |
| `testcase-ontology` | `test-case-catalog` projection + `test-case.schema.json` for the data layer | `contracts/projections/testcase-ontology/` reads, `./test-cases/*.tc.json` is the data |

Hot-plug rule: adding a new metric or test case is **a file drop** under `./metrics/` or `./test-cases/`, never an edit of any `*.projection.json`.

## Execution discipline (HARD RULES)

The host agent executes this skill **by directly performing each STEP**, not by generating intermediate scripts. The following rules are **blocking**:

1. **No ad-hoc orchestrator scripts (whitelist, not blacklist).** The ONLY executable files allowed inside this skill package are the ones already committed at skill-creation time, namely:
   - `./runtime-drivers/<driver_id>/run.py` (and its sibling files inside the same driver directory) — the STEP 3 protocol adapter
   - any future `runtime-*/<id>/` adapter directory shipped with the skill at creation time

   The agent MUST NOT create ANY new `.py` / `.sh` / `.ts` / `.js` / `.mjs` / `.ipynb` / `Makefile` / `*.cmd` / `*.ps1` file ANYWHERE under the skill root — not in `./runs/<eval_id>/`, not in the skill root (e.g. `./run_scenario.py`), not in `./scripts/`, not in `./tools/`, not anywhere. This includes:
   - orchestrator / runner / coordinator scripts (e.g. `run_scenario.py`, `run_step3.py`, `run_evaluation.py`, `runner.py`, `orchestrator.py`, `coordinator.py`, `main.py`, `eval.py`)
   - "helper" scripts that render prompts, parse JSON, drive the loop, or call an LLM endpoint
   - test harnesses for the driver (e.g. `test_driver.py`)
   - inline shell scripts that chain multiple agent responsibilities

   If the agent has just written `subprocess.Popen(... runtime-drivers/...)` or `proc.stdin.write(json.dumps(...))` into a file it authored, that is a contract violation. The same logic MUST live as **agent tool-call rounds in the conversation** instead — one terminal command to spawn the driver, then turn-by-turn `read_file` / inline reasoning / shell `echo … >> driver.stdin` (or equivalent live-process interaction) per turn.

   Any agent-authored file matching the patterns above means the run is **tainted**: stop immediately, do NOT continue scoring on its outputs, place a `TAINTED.md` under `./runs/<eval_id>/` (or at the skill root if no run dir exists yet) documenting the violation, and STEP 9 MUST surface this in `EvaluationReport.open_questions`.

2. **Drivers are called, not reimplemented.** STEP 3 MUST spawn the selected driver as a subprocess via shell (e.g. `python -u runtime-drivers/<driver_id>/run.py --evaluation-context <path> --output <trace_path>`) and communicate over its stdin/stdout line-JSON protocol. The agent MUST NOT `import` driver modules into agent-authored code, and MUST NOT replicate the driver's WebSocket / JWT / trace-writing logic anywhere outside the driver directory.

3. **Simulator is the agent's own LLM.** The simulator role profile under `./simulators/<simulator_id>/` is consumed in-process by the host agent's LLM (same brain that runs STEP 1.5 / STEP 4 / STEP 8 / STEP 9). The agent MUST NOT spawn the simulator as a subprocess and MUST NOT configure an independent LLM key for it.

4. **Per-run directory is data-only.** `./runs/<eval_id>/` may contain JSON artifacts (synthesized-cases, enriched-test-cases, traces, scores, reports, logs, `TAINTED.md`). It MUST NOT contain executable code, agent scratchpads, or duplicated implementations of any STEP.

5. **Determinism stays deterministic.** PRE / STEP 1 / STEP 2 / STEP 5 / STEP 6 / STEP 7 are pure file-scan or arithmetic. The agent performs them inline (read files, compute, write JSON). It MUST NOT call the LLM for these steps and MUST NOT defer them to a generated script.

6. **LLM steps stay in-process.** STEP 1.5 / STEP 4 / STEP 8 / STEP 9 invoke the agent's own LLM brain directly. The agent MUST NOT generate a Python script that calls an HTTP LLM endpoint as a substitute.

If the agent feels tempted to write any `.py` file, that is a signal the prompt or contract is unclear — surface the ambiguity instead of fabricating an orchestrator.

## Top-level workflow

The authoritative execution graph lives in `contracts/projections/ontology_extraction/metric-selection/metric-selection.workflow-contract.projection.json`. The 11 steps are:

| # | Step | Kind | Notes |
|---|---|---|---|
| PRE | `loadMetricRegistry` | deterministic | Filesystem scan of `./metrics/*.metric.json`. Fails fast on empty registry. |
| 1 | `resolveEmployeeAndCheckTestCases` | deterministic | Two duties: (a) RESOLVE the evaluatee from `employee_id` (yielding `role`, `scenarios`, `sop_documents`); (b) **FILTER `metric_registry` by role** to produce `selected_metrics` (the universe used downstream) and `dropped_metrics` (audit trail). Both lists MUST be persisted in `evaluation_context.json`. Then probe `./test-cases/` to set `test_case_status`. See contract S1 `worked_example` and constraint **K9**. |
| 1.5 | `parseTestCases` | LLM, **conditional** (only when no cases match) | **User-first fallback chain.** When `test_case_status == 'missing'`, the host agent MUST FIRST consult the user (constraint **K11**) for real-world scenarios; only on explicit decline does it fall back to SOP synthesis. Tier 1 (high reliability) = user-provided scenarios; Tier 2 (low reliability, must carry `reliability_caveat`) = SOP-derived; Tier 3 = block. Synthesized cases are written to `./runs/<eval_id>/synthesized-cases/`, **never** into `./test-cases/`. The full prompt + user response is persisted as `evaluation_context.user_consultation_log`. |
| 2 | `enrichTestCases` | deterministic, **always runs** | Attaches `applicable_metrics` to every test case using the rule `m matches tc iff role_match(m, role) AND scenario_match(m, tc.scenarios)`, where `*` in either `applicable_roles` or `applicable_scenarios` is a **match-all wildcard** (NOT a literal string). The filtered registry MUST be `selected_metrics` (per **K10**), never the full `metric_registry`. Enforced by `enriched_test_case.schema.json` and contract S2 `wildcard_semantics_note`. |
| 3 | `driveEmployeeOnScenario` | **dual-role** (I/O subprocess + host-agent simulator) | Drives the evaluatee through one test case turn-by-turn and records `ExecutionTrace` (incl. `simulator_trail`). `runtime_driver` (long-lived subprocess under `./runtime-drivers/<driver_id>/`) is the I/O channel: a stdin/stdout JSON loop that talks to the evaluatee. `runtime_simulator` (role profile under `./simulators/<simulator_id>/`) is **NOT a subprocess** — the host evaluation-expert agent itself, with its own LLM, plays the customer using the profile's system_prompt. `turn_budget.hard_max_turns` (default cap 30) is a HARD ceiling. STEP 3 fails fast on unresolved driver or simulator. **The agent MUST drive the loop to completion: write `end` BEFORE closing stdin, then await `{"event":"trace_written"}` (constraint K14). Closing stdin after a single `send` is a protocol violation and the trace will be REJECTED at STEP 4 input.** |
| 4 | `scoreScenario` | **LLM fan-out** | One LLM call per `(test_case, metric)` pair. Uniform fan-out, no exceptions. Output validated against `metric_score.schema.json`. |
|  | LOOP(3, 4) | — | Repeat per test case until all cases done. |
| 5 | `aggregateAcrossScenarios` | deterministic | Per-metric aggregation using the metric's declared `aggregation_strategy`. **Persists `./runs/<eval-id>/aggregated_metric_scores.json` BEFORE STEP 6 begins (constraint K12).** |
| 6 | `rollUpToDimensions` | deterministic | Sub-metrics → parent dimensions. **Persists `./runs/<eval-id>/dimension_scores.json` whose key set MUST equal `{ m.parent_dimension for m ∈ selected_metrics }` — fabricating dimensions whose sub-metrics were dropped at STEP 1 is forbidden (constraints K12, K13).** |
| 7 | `redLineCheck` | deterministic, **LLM-disallowed** | Pure-code application of each metric's `red_line` config over `observed_signals[]` and trace data; no LLM rationalization (constraint **K4**). **Persists `./runs/<eval-id>/red_line_check.json` (constraint K12).** See [STEP 7 red-line pseudo-code](#step-7-red-line-pseudo-code-k4) below. |
| 8 | `buildScenarioReports` | **LLM synthesis** (prose only, per scenario) | One `ScenarioReport` per test case (parallel allowed). Numeric `metric_results.score` are byte-copies of upstream `MetricScore`; the LLM only writes `summary / what_went_well / what_went_wrong / improvement_points`. Validated against `scenario_report.schema.json`. |
| 9 | `buildOverallReport` | **LLM synthesis** (prose only, exactly once) | One `EvaluationReport` after all scenario reports exist. `dimension_scores / overall_score / red_line / passed` are byte-copies of STEP 6 / STEP 7 outputs; the LLM only writes `executive_summary / strengths / weaknesses / cross_scenario_patterns / improvement_plan`. MUST link scenario reports, not inline them. Validated against `evaluation_report.schema.json`. |

### STEP 1 operating playbook (metric filtering by role)

STEP 1 has TWO independent duties; both are deterministic and inline.

1. **Resolve evaluatee.** Look up `employee_id`. Persist `employee.role`, `employee.scenarios`, `employee.sop_documents` to `evaluation_context.employee`.
2. **Role-filter `metric_registry`.** For each metric `m` loaded by PRE:
   - If `employee.role ∈ m.applicable_roles` OR `"*" ∈ m.applicable_roles` → put into `selected_metrics`.
   - Otherwise → put into `dropped_metrics` with `{ metric_code, applicable_roles, drop_reason: "role_mismatch" }`.
3. **Persist both** in `evaluation_context.json`. `selected_metrics` is the universe consumed by STEP 1.5 / STEP 2 / STEP 4 — it is NOT the full registry.
4. **Self-check before continuing**:
   - `len(selected_metrics) + len(dropped_metrics) == len(metric_registry)` ✅
   - Every entry in `selected_metrics` actually contains the role (or `*`) ✅
   - If `selected_metrics == []` and `metric_registry != []` → **block_or_escalate** (no metric applies; do not proceed). ✅
5. **Probe `./test-cases/`** to set `test_case_status` (`ready` / `missing`). This only decides whether STEP 1.5 runs; it does NOT change `selected_metrics`.

**Worked example.** Suppose `employee.role = "customer-service-ecommerce"` and the registry has 8 metrics covering `customer-service-ecommerce`, `after-sales-agent`, `hr-attendance`, `bid-writer`, `legal-expert`, `software-engineer`. Correct STEP 1 output keeps **3** metrics (`tool_call_correctness` via `*`, `interaction_empathy`, `order_refund_policy_accuracy`) and drops **5** (`attendance_rule_compliance`, `bid_clause_completeness`, `legal_citation_accuracy`, `code_change_risk_disclosure`, `confidentiality_boundary_compliance`). Copying all 8 into `selected_metrics` is the bug observed in `runs/eval-001/` — it triggers **K9** and marks the run tainted.

**Cross-step invariant (K10).** STEP 2 narrows further by `applicable_scenarios ∩ tc.scenarios`. Therefore for every enriched test case `tc`: `tc.applicable_metrics ⊆ selected_metrics`. STEP 3 / STEP 4 MUST consume `./runs/<eval_id>/enriched-cases/<tc_id>.json` as the authoritative source — NOT the inline copy embedded in `evaluation_context.enriched_test_cases[]`. The two MUST be byte-identical; any divergence taints the run.

### STEP 1.5 operating playbook (consult user FIRST, SOP only as fallback)

Real-world scenarios from the user are the **highest-fidelity grounding** for an evaluation. SOPs only describe how the employee SHOULD behave — they do NOT tell us what cases the employee ACTUALLY meets. Therefore when STEP 1.5 fires:

1. **STOP and ask the user before any LLM synthesis.** Send a single consultation message; do NOT silently start synthesizing from SOP. Suggested template:
   > 我即将为员工 `<employee_id>`（role=`<role>`）生成测试用例。为了让评估贴近真实业务，请提供该员工在生产环境中实际处理的代表性场景（1-7 个）。每个场景请说明：(a) 场景名称与频率；(b) 客户典型开场话术与诉求；(c) 需要员工调用的关键工具 / 查询 / 决策；(d) 隐含红线。若你明确表示「没有」「你自己合成即可」，我才会退回 SOP 合成并标 caveat。
2. **Classify the user response into one of three branches:**
   - **(A) user supplies scenarios** → Tier 1. Use the user's text verbatim as the seed for each case. The LLM only renders user text into `test-case.schema.json` v2.0 shape. **DO NOT invent scenario types the user didn't mention.** Each case's `provenance = { source: "user_provided_scenarios", reliability: "high" }`.
   - **(B) user explicitly declines** (e.g."你自己合成"/"没有"/"skip") → Tier 2 SOP fallback. Each case's `provenance = { source: "synthesized_from_sop", reliability: "low", reliability_caveat: "synthesized_from_sop_only_no_user_grounding" }`. STEP 9 MUST surface the caveat in `open_questions` and weaken language about findings (use "indicative" / "preliminary" instead of "definitive").
   - **(C) user partially supplies** (e.g. only 1–2 seeds, asks you to fill the rest) → mixed. User-supplied seeds get Tier 1 / `reliability=high`; SOP-derived expansions get Tier 2 / `reliability=low`. Each case is attributed individually.
3. **Persist the consultation** to `evaluation_context.user_consultation_log = [{ asked_at, prompt, user_response, decision: "tier1" | "tier2" | "tier3" }]`. This is auditable evidence the consultation happened.
4. **Tier 3 (block).** If user declined AND `employee.sop_documents` is empty → block_or_escalate. Do NOT fabricate scenarios out of thin air.
5. **Required `provenance` field on every synthesized case.** Schema-level requirement: `{ source, reliability, reliability_caveat? }`. Cases without `provenance` MUST fail validation BEFORE being written to `./runs/<eval_id>/synthesized-cases/`.

**Anti-patterns (will trigger K11 violation):**
- Detecting `test_case_status == "missing"` and immediately calling LLM to synthesize cases from SOP without asking the user.
- Asking the user but proceeding with SOP synthesis BEFORE the user has answered.
- Tagging SOP-derived cases as `reliability="high"` or omitting `reliability_caveat`.
- STEP 9 omitting the `synthesized_from_sop_only_no_user_grounding` caveat in `open_questions` when any case in the run is Tier 2.

#### stop_conditions ↔ expected_tool_calls alignment check (K15)

Before STEP 3 begins, **every** synthesized/enriched test case MUST pass this self-check:

1. **If `expected_tool_calls` contains any `criticality="must"` entries**, ask: *"Can `stop_conditions.success` be true if those tools were NEVER called?"* If yes → the case has an internal contradiction. Rewrite `stop_conditions.success` to require an outcome that implies the must-tools fired (e.g. `"退款申请已提交并确认订单符合退款条件"` rather than `"获得退货指引"`).
2. **If `context` contains information the evaluatee will need** (e.g. `order_reference`) **but `opening_message` intentionally omits it**, ask: *"Does `stop_conditions.success` assume the customer provided that info?"* If not → the simulator may declare `goal_achieved` before providing the info, creating a dead-end trace. Rewrite the success condition to include the info-handoff step.
3. **Actionable closure test**: `stop_conditions.success` MUST describe an outcome where the customer's problem is **on track to resolution** (action taken or in progress), NOT merely passive reception of a process explanation. Template: `"<verb: 已提交/已确认/已发起> + <object: 退款申请/催派工单/订单查询结果>"`, not `"获得流程说明"`.

**Worked example (eval-xiaofu-001 tc-004-refund-request bug).** Original:
```
stop_conditions.success = "获得明确的退换货指引和流程说明"
expected_tool_calls = [query_order_status(must), query_refund_policy(must)]
context.order_reference = "ORD20240528003"  (not in opening_message)
```
→ Simulator sees employee list steps → declares `goal_achieved` at turn 2 → employee never gets order number → never calls tools → red_line triggered for missing tools → **evaluation is unfair**.

Corrected:
```
stop_conditions.success = "员工已查询订单并确认符合退款条件，或已为客户发起退货退款申请"
```
→ Simulator must keep talking until the employee actually queries the order and confirms eligibility.

#### Boundary coverage: positive & negative case pairs

When synthesizing test cases (Tier 1 or Tier 2), apply **equivalence-class partitioning** to maximize decision-path coverage:

1. **Identify decision boundaries** in the scenario seed or SOP:
   - Amount thresholds (e.g. ">500 → human handoff", ">1000 → manager approval")
   - Time limits (e.g. "within 7 days → no-questions-asked return")
   - Category restrictions (e.g. "electronics require quality inspection")
   - Customer tier gates (e.g. "VIP → priority queue")

2. **Generate paired cases** for each boundary:

   | Polarity | Meaning | Example (threshold = 500) |
   |---|---|---|
   | `positive` | Within normal/allowed path | order_amount=350, direct refund approved |
   | `negative` | Exceeds boundary, restricted path | order_amount=899, must handoff to human |
   | `boundary` | Exactly at threshold (optional) | order_amount=500, edge case behavior |

3. **Cross-reference with `paired_case_id`**: each case's `paired_case_id` points to its counterpart so reviewers can verify both sides of every decision boundary are covered.

4. **Adjust `expected_tool_calls` per polarity**: the positive case may expect `process_refund(must)` while the negative case expects `create_handoff_ticket(must)`. Different paths → different must-tools → different red-line triggers.

5. **Tag with `polarity`**: set `polarity = "positive" | "negative" | "boundary"` on each case. This is a schema-level optional field, not a blocking requirement.

**This is a best practice, not a blocking constraint.** If the user's scenario seed has no identifiable decision boundary (e.g. a pure information query), single unpaired cases are acceptable.

### STEP 3 operating playbook

The agent executes STEP 3 turn-by-turn **without any orchestrator script**. For each enriched test case `tc`:

1. Resolve `runtime_driver.driver_id` and `runtime_simulator.simulator_id` from `evaluation_context`. Fail-fast if either is missing.
2. **Spawn the driver subprocess** via shell, e.g.
   ```
   python -u runtime-drivers/<driver_id>/run.py \
     --evaluation-context <eval_ctx_path> \
     --test-case <enriched_tc_path> \
     --output ./runs/<eval_id>/traces/<tc_id>.trace.json
   ```
   One driver process per scenario; the agent does NOT instantiate a long-running daemon for the whole evaluation.
3. **Read the first stdout line** — must be `{"event":"ready", ...}`. Anything else → abort STEP 3 for this scenario.
4. **Turn 0**: write the agent's first `send` action to driver stdin. `text` MUST be `tc.input.opening_message` verbatim; `decision` is a deterministic turn-0 SimulatorDecision (no LLM call). DO NOT consult the LLM for turn 0.
5. **Loop until termination**, each iteration:
   1. Read the next stdout line. Expect `{"event":"evaluatee_turn", ...}` (anything else → handle as error event).
   2. Render `simulators/<simulator_id>/system_prompt.md` against placeholders {`customer_persona` / `goal` / `stop_conditions` / `context` / `current_emotion` / `dialog_so_far` / `effective_max_turns`}. **The agent's own LLM** consumes this prompt and returns a `SimulatorDecision` JSON. Validate against `runtime-schemas/simulator_decision.schema.json`.
   3. Compute `effective_max_turns = min(tc.turn_budget.hard_max_turns, evaluation_context.global_turn_cap or 30)`. If `turn_index + 1 >= effective_max_turns`, write an `end` action with `termination.reason = "max_turns_reached"` regardless of `decision.should_continue`.
   4. Else if `decision.should_continue == false`, write an `end` action with `termination.reason` mapped from `decision.stop_reason` (`goal_achieved` → `completed_normally`; `bottom_line_violated` → `bottom_line_violated`; `deadlock_detected` / `customer_gave_up` → `deadlock_detected`).
   5. Else write a `send` action carrying `decision.next_utterance` (as `text`) and the full `decision`.
6. **Wait for `{"event":"trace_written", ...}`**, then the driver process exits. The trace file at `./runs/<eval_id>/traces/<tc_id>.trace.json` is now the authoritative `ExecutionTrace`. Move on to STEP 4.
7. On any `{"event":"error", ...}`, surface the detail and abort the scenario; the driver writes a partial trace before exit.

The agent does this entire loop **interactively in the conversation**, not by generating a script. The driver runs as the only subprocess; the agent's LLM brain produces each `SimulatorDecision`. **No `.py` file is created at any point of STEP 3.**

#### STEP 3 anti-patterns (each is an immediate stop-and-taint)

If you find yourself about to do any of the following, STOP and re-read HARD RULE 1:

- creating `run_scenario.py`, `run_step3.py`, `run_evaluation.py`, `run_full_evaluation.py`, `runner.py`, `orchestrator.py`, `coordinator.py`, `main.py`, `eval.py`, `test_driver.py`, `driver_client.py` (or any similarly-named file) anywhere in the skill
- writing a function that contains `subprocess.Popen([...,'runtime-drivers/...'])` followed by `proc.stdin.write(...)` / `proc.stdout.readline()` in agent-authored code
- writing a `while True:` loop that bundles multiple turns of driver I/O into one execution
- writing a script that reads a system_prompt template and "calls the LLM" via an HTTP client — the LLM is the host agent itself, not an HTTP endpoint
- writing a `.sh` / `Makefile` that chains the spawn command with anything else

The correct shape is: **one shell command per agent turn**. Spawn the driver in one tool call, then each subsequent agent turn does exactly one round-trip with the driver (read one stdout line, decide, write one stdin line). The conversation itself IS the orchestrator; you are not allowed to externalize it.

#### STEP 3 LOOP completeness (K14)

The driver expects strict alternation: `send → read evaluatee_turn → send | end`. Closing stdin before writing an `end` action is a **protocol violation**, not a graceful shutdown.

Forbidden shapes (each REJECTS the trace at STEP 4 input gate, see K14):

- write one `send` and close stdin → driver writes `termination.detail = "stdin closed before 'end' action received"`, `turns_used = 1`, `actual_tool_calls = []`. STEP 4 MUST NOT score on this trace.
- forget to read `{"event":"trace_written"}` after the final `end` → trace file may be incomplete or missing.
- bail out of the loop because the LLM "thinks the conversation is over" without first writing `end` → same bug.

If the host agent enters a state where it has written `send` and then has nothing further to write (e.g. an LLM rendering error), the recovery is to write `{"action":"end","termination":{"reason":"deadlock_detected","detail":"<reason>"}}` THEN close stdin — NEVER close stdin first.

**Trace rejection rule** (applied by STEP 4 / STEP 9): a trace is rejected iff

```
termination.reason == "evaluatee_error"
AND termination.detail contains "stdin closed before 'end' action received"
OR  (termination.reason == "evaluatee_error" AND turns_used == 1 AND actual_tool_calls == [])
OR  (termination.reason == "max_turns_reached"
     AND turns_used < effective_max_turns
     AND simulator_trail[-1].should_continue == true)
OR  (simulator_trail is non-empty
     AND simulator_trail[-1].next_utterance is a non-empty string
     AND that exact string is NOT the content of the LAST
         dialog_turns entry whose actor == "evaluator")
```

The **third clause** catches the "demonstration shortcut" bug: the agent self-caps turns below `effective_max_turns` (e.g. `detail = "Reached max turns for demonstration"`) while the simulator still wants to continue. This is a K14 violation — the agent must not invent its own early-stop reason.

The **fourth clause** catches the **eval-soul-001 "simulator decided but agent never delivered"** bug: simulator_trail records `next_utterance = "订单号是 ORD…"` with `should_continue = false` and `stop_reason = "goal_achieved"`, but `dialog_turns` shows the customer never actually said it because the agent closed stdin before issuing the final `send`. **Whenever the simulator decision yields a non-empty `next_utterance`, the agent MUST first write a `send` carrying that exact text, THEN write `end` — even when `should_continue == false`.** The customer's last utterance (providing an order number, saying "thanks, bye", etc.) is part of the dialog and MUST appear in `dialog_turns`.

**Symmetric simulator-side rule (K15 operability-loop).** A simulator decision MUST NOT set `goal_progress = "goal_achieved"` or `stop_reason = "goal_achieved"` on the first decision after the evaluatee asked the customer for required information (e.g. order_number, refund_id) UNLESS the customer's reply containing that information has already been delivered to the evaluatee in a prior turn. Self-declaring `goal_achieved` while the required information is still locked inside `next_utterance` is rejected via the fourth clause above.

A rejected trace taints the run; the affected `tc_id`s MUST appear in `EvaluationReport.open_questions`.

**FORBIDDEN SHORTCUT (K14).** The agent MUST NOT terminate the STEP 3 loop early citing "demonstration", "preview", "sample", "testing", "brevity", or any other self-invented reason that overrides the computed `effective_max_turns`. The **ONLY** valid reasons to write an `end` action inside the loop are:

1. `decision.should_continue == false` (simulator decided to stop)
2. `turn_index + 1 >= effective_max_turns` (hard budget exhausted)
3. Driver emits `{"event":"error"}` (unrecoverable driver failure)

Any other reason is a K14 violation and the resulting trace will be REJECTED.

#### STEP 4 fan-out: no demonstration shortcut (K16)

STEP 4 is the **only** LLM-bounded step in the back half of the workflow (per K4). Every score file at `./runs/<eval-id>/scores/<tc_id>__<metric_code>.json` MUST be the literal output of an evaluator LLM call — ONE call per `(test_case_id, metric_code)` pair where `metric_code ∈ enriched_test_cases[tc].applicable_metrics`.

**Hard rules (K16):**

1. **No batch fabrication.** The agent MUST NOT compute scores from its own knowledge of the trace and metric definitions, then emit all score files at once with a uniform timestamp. Each prompt is built from (i) that exact trace + (ii) that exact metric definition + (iii) the rubric/red-line config + (iv) per-case `stop_conditions`, and submitted independently to the evaluator LLM.
2. **Real `scored_at`.** `MetricScore.scored_at` MUST be the real ISO8601 timestamp captured at LLM-response receipt time, accurate to at least the second, and **different across distinct LLM calls** (millisecond/microsecond drift expected).
3. **Duplicate-timestamp taint.** If MORE THAN ONE score file in the same run shares an identical `scored_at` value (string equality), the run is marked tainted and STEP 9 MUST list every duplicate-timestamp pair in `open_questions` with severity `critical`. This catches the **eval-soul-001 pattern** where all 10 score files carried `scored_at = "2026-05-29T14:30:00Z"` verbatim.
4. **Reasoning must cite evidence.** `MetricScore.scoring_reasoning` MUST quote at least one concrete substring from `dialog_turns` or `actual_tool_calls` of the trace being scored. Reasoning that consists only of generic phrases ("based on standards", "reasonable demonstration result", "as a typical case", "基于评估标准生成") with no observable evidence is rejected as fabrication; the score file MUST be regenerated.
5. **FORBIDDEN SHORTCUT (mirror of K14).** The agent MUST NOT skip the per-(case, metric) LLM call citing "demonstration", "preview", "sample run", "illustrative scoring", "time pressure", or any other reason. There is no demonstration mode — every metric on every case requires a real evaluator LLM call.

**Validation pseudo-code (applied at STEP 5 input gate):**

```
scored_at_set = { read(f).scored_at for f in scores/*.json }
assert len(scored_at_set) == count(scores/*.json), \
    f"K16 violation: duplicate scored_at across score files — evaluator LLM was not invoked per (case, metric)"
```

### Why fan-out

A single prompt that bundles "all metrics + all rubrics + full trace + output schema" explodes token usage and dilutes attention. STEP 4 instead runs **one slim LLM call per `(test_case, metric)` pair**, where each prompt is built from:

- the relevant slice of `scoring-judgement.prompt-constraint.projection.json` (only constraints whose `applies_to_layer = per_metric_fanout_prompt`)
- the single metric's `scoring_rubric` and `runtime_slice_selector`
- the runtime data filtered through that selector (typically: this test case's expected output + this scenario's trace, scoped further per metric)
- the strict response schema `metric_score.schema.json`

### Why red-line judgement is deterministic

LLMs may underweight red lines under social/empathy pressure. STEP 4 LLM calls may only **raise `observed_signals`** (e.g. `missing_required_tool_call`). The final pass/fail decision is computed in STEP 7 by deterministic code, using each metric's declared `red_line` config. The LLM never sees `red_line_passed` and cannot return it.

### STEP 5/6/7 persistence (K12, K13)

Each of the three deterministic steps MUST persist a typed JSON artifact under `./runs/<eval-id>/` BEFORE the next step begins. STEP 9 byte-copies values from these files (per K7) and MUST NOT run if any is missing.

| Step | Artifact path | Key constraint |
|---|---|---|
| 5 | `./runs/<eval-id>/aggregated_metric_scores.json` | keys ⊇ `{ m.metric_code for m ∈ selected_metrics }` |
| 6 | `./runs/<eval-id>/dimension_scores.json` | keys **==** `{ m.parent_dimension for m ∈ selected_metrics }` (K13) |
| 7 | `./runs/<eval-id>/red_line_check.json` | one entry per metric whose `red_line` config is non-null |

**K13 — what NOT to do.** If `selected_metrics` covers only `{interaction_empathy, order_refund_policy_accuracy, tool_call_correctness}` (e.g. customer-service-ecommerce after STEP 1 filtering), `dimension_scores.json` MUST contain exactly the parent dimensions those three roll up to. Inserting `process_compliance=87`, `problem_resolution=82`, etc. when no selected metric rolls up to those dimensions is the **eval-xiaofu-001 fabrication bug** — the LLM in STEP 9 invented numeric scores for dimensions that have no upstream evidence. K13 hard-blocks this; STEP 9 MUST reject any `dimension_scores.json` whose key set is a strict superset.

#### STEP 7 red-line pseudo-code (K4)

STEP 7 is pure code — no LLM, no rationalization. The exact algorithm:

```
red_line_check = {}
for m in selected_metrics:
    cfg = m.red_line                     # may be null → skip
    if cfg is None: continue
    triggered = False
    evidence = []
    if cfg.trigger_kind == "missing_required_signal":
        for tc_id, score in per_metric_scores[m.metric_code].items():
            tc    = enriched_cases[tc_id]
            trace = traces[tc_id]
            must_tools = [t for t in tc.expected_tool_calls if t.criticality == "must"]
            absent     = [t for t in must_tools if t.tool_name not in trace.actual_tool_calls]
            if absent:
                triggered = True
                evidence.append({"tc_id": tc_id, "missing": [t.tool_name for t in absent]})
    elif cfg.trigger_kind == "score_below_threshold":
        for tc_id, score in per_metric_scores[m.metric_code].items():
            if score.overall_score < cfg.threshold:
                triggered = True
                evidence.append({"tc_id": tc_id, "score": score.overall_score, "threshold": cfg.threshold})
    # ... other trigger_kinds per metric_score.schema.json
    red_line_check[m.metric_code] = {
        "trigger_kind": cfg.trigger_kind,
        "triggered": triggered,
        "evidence": evidence,
    }
```

**The LLM is not allowed to overwrite `triggered`.** Narrative justifications such as *"tool_call_correctness scored 10/100 but red_line is not triggered because the agent had reasonable substitute behavior"* are K4 violations — the eval-xiaofu-001 bug. The LLM in STEP 9 may surface the triggered red lines in `executive_summary` prose, but the `red_line.triggered` field is byte-copied from `red_line_check.json` (per K7).

### STEP 9 dual-format output (JSON + HTML)

STEP 9 MUST produce **two** report files:

| File | Path | Purpose |
|---|---|---|
| JSON | `./runs/<eval-id>/reports/evaluation_report.json` | Machine-readable, validated against `evaluation_report.schema.json` |
| HTML | `./runs/<eval-id>/reports/evaluation_report.html` | Human-readable, self-contained single-file report |

**HTML generation procedure:**

1. Load the template at `./runtime-schemas/report-template.html`.
2. Collect all scenario data: for each test case, gather `{ report: <scenario .report.json>, trace: <.trace.json>, enriched: <enriched-case .json> }`.
3. Replace `{{REPORT_DATA}}` with the full `evaluation_report.json` content (JSON string).
4. Replace `{{SCENARIOS_DATA}}` with the array of scenario objects (JSON string).
5. Replace `{{EMPLOYEE_NAME}}` in the `<title>` tag with the employee display name.
6. Write the final HTML to `./runs/<eval-id>/reports/evaluation_report.html`.

**HTML report features:**
- **能力雷达图**: 5 维度能力覆盖范围，同心圆参考线（0/20/40/60/80/100），灰色虚线目标值（85分），维度标签外置并注明权重
- **场景 Tab 切换**: 每个用例一个 Tab，展示会话聊天历史、模拟器决策过程、工具调用（工具名 + 参数 + 结果）、指标得分、叙述分析
- **自包含**: 单个 HTML 文件，仅依赖 Chart.js CDN，可直接用浏览器打开
- **可与员工绑定**: HTML 文件名可以加员工 ID 后缀，作为能力评估产物归档

## Skill-Specific Constraints

- **Supported deliverables**: evaluation_report, scoring_criteria, workflow_contract, metric_set
- **Supported projection types**: workflow-contract, prompt-constraint, domain-model, metric-catalog, test-case-catalog
- **Supported projection fields beyond the shared minimum**: `concept_mappings.target_path`, `concept_mappings.target_kind`, `constraint_mappings.severity_mapping`, `constraint_mappings.applies_to_layer`, `delivery_artifacts.path`, `metric_catalog.scoring_dimensions`, `evaluation_criteria.red_lines`, `workflow_step.kind`, `workflow_step.fallback_chain`, `workflow_step.always_runs`, `workflow_step.uniform_fanout`, `workflow_step.llm_disallowed`
- **Hot-plug data**: `./metrics/*.metric.json` (one metric per file, file basename MUST equal `metric_code`), `./test-cases/*.tc.json` (one case per file, file basename MUST equal `test_case_id`)
- **Local exclusions**: do not invent unsupported evaluation criteria, do not bypass mapped constraints, do not modify files outside `./runs/<eval_id>/`, do not write runtime evidence back into any `*.projection.json`

## Projection Contracts

This skill is augmented by bound projection contracts discovered under `contracts/projections/**/contract-index.json`.

- Discovery, route selection, and prompt patching are handled by runtime, not by manual rules in this file.
- For human review: read `contract-index.json` first, then the selected topic's `README.md` and `REVIEW.md`, then the chosen `*.projection.json`.
- The selected projection is authoritative for terminology, clarifications, dropped scope, and blocking conditions.
- If `mapping_policy` requires `block_or_escalate`, or `open_questions` is non-empty, do not finalize the output before surfacing the issue.
- Do not recreate items listed in `dropped_items`.

## Path defaults & overrides

| Layer | Default path (relative to skill root) | Override env var |
|---|---|---|
| Metrics data | `./metrics/` | `EVALUATION_METRICS_DIR` |
| Test-cases data | `./test-cases/` | `EVALUATION_TEST_CASES_DIR` |
| Per-run artifacts | `./runs/<eval_id>/` | `EVALUATION_RUN_DIR` |
| Synthesized test cases (STEP 1.5 output) | `./runs/<eval_id>/synthesized-cases/` | derived from run dir |
| Runtime drivers (STEP 3 protocol adapters) | `./runtime-drivers/` | `EVALUATION_DRIVERS_DIR` |
| Selected driver id | (none — required field on `evaluation_context.runtime_driver`) | `EVALUATION_DRIVER_ID` |
| User simulators (STEP 3 customer-brain role profiles, consumed by the host agent's own LLM — NOT subprocesses) | `./simulators/` | `EVALUATION_SIMULATORS_DIR` |
| Selected simulator id | (none — required field on `evaluation_context.runtime_simulator`) | `EVALUATION_SIMULATOR_ID` |
| Per-scenario hard turn cap | `turn_budget.hard_max_turns` on each `*.tc.json`; falls back to `evaluation_context.global_turn_cap` (default 30) | — |

## Built-in Route Selection (E-commerce Customer Service)

For the built-in `customer-service-ecommerce` template, the runtime route table is:

| Employee Template | Primary Topic | Default View | Trigger Signals |
|------------------|---------------|--------------|-----------------|
| customer-service-ecommerce | customer-service-ecommerce | workflow-contract | "客服", "售后", "退货", "投诉", "电商" |
| customer-service-ecommerce | metric-selection | workflow-contract | "测试用例", "用例匹配", "指标库", "评估流程", "fan-out" |
| customer-service-ecommerce | metric-selection | prompt-constraint | "指标", "评分维度", "评估标准" |
| customer-service-ecommerce | scoring-judgement | prompt-constraint | "打分", "评分", "严格评估" |

### The 5 fixed parent dimensions

These names are **frozen** so red-line floors stay stable even as sub-metrics evolve. New sub-metrics roll up here via `metric.parent_dimension`.

1. `functional_completeness` (default weight 0.25)
2. `interaction_quality` (default weight 0.20)
3. `process_compliance` (default weight 0.20)
4. `problem_resolution` (default weight 0.15)
5. `tool_call_correctness` (default weight 0.20)

### Default red-line floors (built-in)

Any of these triggers automatic failure regardless of weighted total:

- `tool_call_correctness = 0` (a metric with `criticality = must` had no matching call in the trace)
- `process_compliance ≤ 30`
- `interaction_quality ≤ 30`
- `functional_completeness ≤ 40`

These floors are evaluated by STEP 7 `redLineCheck` after STEP 6 roll-up. New metrics can declare their own `red_line` block in `*.metric.json`; STEP 7 unions them with the floors above.

### Default passing criteria (built-in)

- Overall weighted score ≥ 70
- All 5 parent dimensions ≥ 60
- No red lines triggered

## References

- `contracts/projections/ontology_extraction/contract-index.json`: route selection index (now declares `upstream_producer_dependencies` to metric-ontology and testcase-ontology)
- `contracts/projections/ontology_extraction/metric-selection/metric-selection.workflow-contract.projection.json`: the 10-step deterministic flow
- `contracts/projections/ontology_extraction/metric-selection/metric-selection.prompt-constraint.projection.json`: metric selection guardrails (K1–K4)
- `contracts/projections/ontology_extraction/scoring-judgement/scoring-judgement.prompt-constraint.projection.json`: layered scoring policy (K1–K5 with `applies_to_layer`)
- `contracts/projections/metric-ontology/metric-library/metric-library.metric-catalog.projection.json`: metric registry contract
- `contracts/projections/testcase-ontology/testcase-library/testcase-library.test-case-catalog.projection.json`: test-case registry contract
- `metrics/README.md`, `test-cases/README.md`: data-layer authoring rules
- `runtime-schemas/README.md`: per-run data shapes (evaluation_context, enriched_test_case, execution_trace, metric_score, scenario_score, scenario_report, evaluation_report) and the runtime_driver / simulator manifest shapes
- `runtime-drivers/README.md`: how to add / select a STEP 3 protocol adapter without touching the contract layer
- `simulators/README.md`: how to add / select a STEP 3 user-simulator persona without touching the contract layer
- `contracts/projections/ontology_extraction/templates/CONSUMER_SKILL_PROJECTION_SECTION.md`: shared minimal `Projection Contracts` section
- `contracts/projections/ontology_extraction/templates/NEW_CONSUMER_SKILL_CHECKLIST.md`: post-copy checklist for trimming unsupported fields
- `contracts/projections/ontology_extraction/references/PROJECTION_CONSUMPTION_GUIDE.md`: how consumer skills should consume projection contracts
- `contracts/projections/ontology_extraction/references/CONSUMER_PROJECTION_LAYOUT_GUIDE.md`: where to place local bound projection files
