# STEP 3 — driveEmployeeOnScenario (driver + simulator dual-role loop)

**Kind**: dual-role (I/O subprocess + host-agent simulator)
**Authority**: workflow contract `S3` + K8 + K14 + K15 (runtime facet)
**Inputs**: enriched test case, `evaluation_context.runtime_driver`, `evaluation_context.runtime_simulator`, `evaluation_context.global_turn_cap`
**Output**: `./runs/<eval_id>/traces/<tc_id>.trace.json` (validated against `execution_trace.schema.json`)

## Asymmetric execution model

| Role | Execution | Lives in |
|---|---|---|
| `runtime_driver` | **Subprocess** — line-delimited JSON over stdin/stdout | `./runtime-drivers/<driver_id>/` |
| `runtime_simulator` | **NOT a subprocess** — role profile consumed by the host agent's own LLM | `./simulators/<simulator_id>/` |

The driver does protocol I/O (WebSocket / JWT / TLS / tool approval). The simulator decides what the customer says — same brain that runs STEP 1.5 / 4 / 8 / 9. The two communicate via the line-JSON protocol below.

## Per-scenario loop (one shell command per agent turn)

For each enriched test case `tc`:

### 1. Resolve

`runtime_driver.driver_id` and `runtime_simulator.simulator_id` from `evaluation_context`. Fail-fast if either is missing.

### 2. Spawn the driver subprocess

```
python -u runtime-drivers/<driver_id>/run.py \
  --evaluation-context <eval_ctx_path> \
  --test-case <enriched_tc_path> \
  --output ./runs/<eval_id>/traces/<tc_id>.trace.json
```

One driver process per scenario. No long-running daemon for the whole evaluation.

### 3. Read first stdout line

Must be `{"event":"ready", ...}`. Anything else → abort STEP 3 for this scenario.

### 4. Turn 0 (deterministic, no LLM)

Write the first `send` action:

```json
{"action":"send","turn_index":0,"text":"<tc.input.opening_message verbatim>","decision":<deterministic turn-0 SimulatorDecision>}
```

DO NOT consult the LLM for turn 0.

### 5. Loop until termination

Each iteration:

1. Read next stdout line. Expect `{"event":"evaluatee_turn", ...}`. Anything else → handle as error event.
2. Render `simulators/<simulator_id>/system_prompt.md` against placeholders:
   - `customer_persona` / `goal` / `stop_conditions` / `context` / `current_emotion` / `dialog_so_far` / `effective_max_turns`
3. The agent's own LLM consumes the rendered prompt and returns a `SimulatorDecision` JSON. Validate against `runtime-schemas/simulator_decision.schema.json`.
4. Compute:
   ```
   effective_max_turns = min(tc.turn_budget.hard_max_turns, evaluation_context.global_turn_cap or 30)
   ```
5. Decide which action to write:

   | Condition | Action |
   |---|---|
   | `turn_index + 1 >= effective_max_turns` | `end` with `termination.reason = "max_turns_reached"` (regardless of `decision.should_continue`) |
   | `decision.should_continue == false` AND `decision.next_utterance` non-empty | first write `send` carrying `next_utterance`, THEN write `end` |
   | `decision.should_continue == false` AND `next_utterance` empty | `end` with `termination.reason` mapped from `stop_reason` |
   | otherwise | `send` carrying `decision.next_utterance` |

   `stop_reason` mapping: `goal_achieved` → `completed_normally`; `bottom_line_violated` → `bottom_line_violated`; `deadlock_detected` / `customer_gave_up` → `deadlock_detected`.

### 6. Wait for `{"event":"trace_written", ...}`

The driver writes the final trace and exits. The trace file at `./runs/<eval_id>/traces/<tc_id>.trace.json` is now the authoritative `ExecutionTrace`.

### 7. On `{"event":"error", ...}`

Surface the detail and abort the scenario. The driver writes a partial trace before exit.

## HARD RULE: no orchestrator scripts (K8)

The agent does this entire loop **interactively in the conversation**, not by generating a script. The conversation itself IS the orchestrator.

The ONLY executable files allowed under the skill are the ones committed at skill-creation time:

- `./runtime-drivers/<driver_id>/run.py` and its sibling files in the same directory
- any future `runtime-*/<id>/` adapter directory shipped with the skill

The agent MUST NOT create ANY new `.py` / `.sh` / `.ts` / `.js` / `.mjs` / `.ipynb` / `Makefile` / `*.cmd` / `*.ps1` file ANYWHERE under the skill root. This includes:

- orchestrator / runner / coordinator scripts (`run_scenario.py`, `run_step3.py`, `run_evaluation.py`, `runner.py`, `orchestrator.py`, `coordinator.py`, `main.py`, `eval.py`, `test_driver.py`, `driver_client.py`, ...)
- helper scripts that render prompts, parse JSON, drive the loop, or call an LLM endpoint
- inline shell scripts that chain multiple agent responsibilities

If the agent has just written `subprocess.Popen(... runtime-drivers/...)` or `proc.stdin.write(json.dumps(...))` into a file it authored, that is a **K8 violation**. The same logic MUST live as **agent tool-call rounds in the conversation** instead.

Path whitelist for executables: `./runtime-drivers/<driver_id>/**`, `./runtime-*/<id>/**`. Anything else taints the run.

## Loop completeness (K14)

The driver expects strict alternation: `send → read evaluatee_turn → send | end`. Closing stdin before writing `end` is a **protocol violation**, not a graceful shutdown.

### Trace rejection rule

A trace is rejected at the STEP 4 input gate iff any of:

```
1. termination.reason == "evaluatee_error"
   AND termination.detail contains "stdin closed before 'end' action received"

2. termination.reason == "evaluatee_error"
   AND turns_used == 1
   AND actual_tool_calls == []

3. termination.reason == "max_turns_reached"
   AND turns_used < effective_max_turns
   AND simulator_trail[-1].should_continue == true

4. simulator_trail is non-empty
   AND simulator_trail[-1].next_utterance is a non-empty string
   AND that exact string is NOT the content of the LAST
       dialog_turns entry whose actor == "evaluator"
```

- **Clause 3** catches the "demonstration shortcut" bug: the agent self-caps turns below `effective_max_turns` while the simulator still wants to continue.
- **Clause 4** catches the **`runs/eval-soul-001/` "simulator decided but agent never delivered"** bug: simulator_trail records `next_utterance = "订单号是 ORD…"` with `should_continue=false` and `stop_reason=goal_achieved`, but `dialog_turns` shows the customer never actually said it because the agent closed stdin before issuing the final `send`.

### The fix: send-then-end

Whenever `decision.next_utterance` is non-empty, the agent MUST first write a `send` carrying that exact text, THEN write `end` — even when `should_continue==false`. The customer's last utterance (providing an order number, saying "thanks, bye", etc.) is part of the dialog and MUST appear in `dialog_turns`.

### Recovery when the LLM rendering errors mid-loop

Write `{"action":"end","termination":{"reason":"deadlock_detected","detail":"<reason>"}}` THEN close stdin. NEVER close stdin first.

### Forbidden shortcut (K14)

The agent MUST NOT terminate the loop early citing "demonstration", "preview", "sample", "testing", "brevity", or any other self-invented reason. The only valid reasons to write `end` inside the loop are:

1. `decision.should_continue == false` (simulator decided to stop)
2. `turn_index + 1 >= effective_max_turns` (hard budget exhausted)
3. Driver emits `{"event":"error"}` (unrecoverable driver failure)

### Symmetric simulator-side rule (K15 runtime facet)

A simulator decision MUST NOT set `goal_progress = "goal_achieved"` or `stop_reason = "goal_achieved"` on the **first** decision after the evaluatee asked the customer for required information (e.g. `order_number`, `refund_id`) UNLESS the customer's reply containing that information has already been delivered to the evaluatee in a prior turn. Self-declaring `goal_achieved` while the required info is still locked inside `next_utterance` trips the trace-rejection rule above (clause 4).

A rejected trace taints the run; the affected `tc_id`s MUST appear in `EvaluationReport.open_questions`.

## Anti-patterns (each is a stop-and-taint)

| Anti-pattern | K-rule | Cure |
|---|---|---|
| Author any `run_*.py` / `runner.py` / `orchestrator.py` to drive the loop | K8 | Drive the loop turn-by-turn in conversation |
| `subprocess.Popen([..., 'runtime-drivers/...'])` in agent-authored code | K8 | Spawn from a single shell tool call |
| `while True:` loop bundling multiple turns into one execution | K8 | One round-trip per agent turn |
| HTTP call to "the LLM" from a script you wrote | K8 | The simulator IS the host LLM |
| `.sh` / `Makefile` chaining the spawn with anything else | K8 | Single shell command per turn |
| Write one `send` and close stdin | K14 | Always write `end` before closing |
| Self-cap turns below `effective_max_turns` for "demo" | K14 | Let the budget exhaust naturally |
| Skip the final `send` when `should_continue=false` and `next_utterance` is non-empty | K14 (clause 4) | send-then-end pattern |
| Simulator declares `goal_achieved` before customer's required info reached evaluatee | K15 runtime | Customer must utter required info first |
