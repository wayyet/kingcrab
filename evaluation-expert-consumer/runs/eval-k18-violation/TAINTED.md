# TAINTED — eval-k18-violation

**Detected at**: 2026-06-01T11:35:00Z
**Violated rule(s)**: K18 (CurateDecisionsMustBeAudited)
**Violation subtype**: empty_evidence
**Offending entry**: `curate_log[0]` — `decision=removed`, `metric_code=tool_call_correctness`

**Trigger**:
- STEP 1.2 removed `tool_call_correctness` from `candidate_metrics`, but the corresponding `curate_log` entry has an **empty `evidence` array**.
- K18 requires every removed/added decision to carry ≥1 evidence citation that names a `source_field` (from the 9-value enum) AND quotes a verbatim substring of that field's value.
- The removal itself is plausibly correct (a software-engineer code-change proposal invokes no evaluatee tools), but without an evidence citation it is unauditable.

**Detected by**: STEP 1.2 self-check / STEP 2 input gate.

**Taint actions (PARTIAL-SUCCESS tolerant — distinct from K17's atomic-fail)**:
1. ✅ This `TAINTED.md` written under `./runs/eval-k18-violation/`.
2. ✅ Offending decision surfaced in `open_questions`.
3. ⚠️ Scoring on already-valid metrics MAY continue; only the curation transparency is in question.

**Outcome**: per K18 partial-success rule — because at least one taint action succeeded, the run records the failed-action state in `open_questions` and may continue with the already-computed valid scores. (If NONE of the three taint actions had succeeded, the run would halt with a non-success status and no successful EvaluationReport.)

**Recovery path**: Re-run STEP 1.2 forward in the same `eval_id` with a proper evidence citation, e.g.
`{ "source_field": "employee.job_responsibilities", "quote": "提交代码变更并披露影响面" }` plus
`{ "source_field": "metric.description", "quote": "invoked all required tools" }`.
Alternatively set `metric_selection_policy.mode = "never"` so `selected_metrics = candidate_metrics` (the deterministic baseline) and re-run from STEP 1.2.

**Audit note**: This directory is a committed **reference fixture** demonstrating the K18 partial-success pattern. Do not delete.
