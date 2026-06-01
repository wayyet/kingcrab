# TAINTED — eval-k17-violation

**Detected at**: 2026-06-01T11:05:00Z
**Violated rule(s)**: K17 (EmployeeResolutionProvenanceRequired)
**Violation subtype**: provenance_absent
**Offending field**: `evaluation_context.employee.employee_provenance`

**Trigger**:
- STEP 0 produced an `employee` object (with a canonical `role.role_id`) but never attached the required `employee_provenance` block (`{ source, reliability, caveat? }`).
- Per K17, the report cannot certify the resolution source or fidelity of the evaluatee — the evaluation's meaning is undefined.

**Detected by**: STEP 0 self-check / STEP 9 input gate.

**Taint actions (ATOMIC — all three must succeed or the whole run fails)**:
1. ✅ This `TAINTED.md` written under `./runs/eval-k17-violation/`.
2. ✅ Scoring stopped before any numeric score was produced.
3. ✅ Violation surfaced in `EvaluationReport.open_questions` / `evaluation_context.open_questions`.

**Outcome**: run **failed** with non-success completion status; **no successful EvaluationReport** is emitted (this is the atomic-fail semantics of K17 — distinct from K18's partial-success tolerance).

**Recovery path**: Full restart. Create a fresh `eval_id` and re-run from PRE.A / STEP 0, ensuring STEP 0 attaches a valid `employee_provenance`. Do NOT patch provenance into this half-built context — the identity uncertainty invalidates everything downstream.

**Audit note**: This directory is a committed **reference fixture** demonstrating the K17 atomic-fail pattern. Do not delete.
