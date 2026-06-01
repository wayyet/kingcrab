# Implementation Plan

## Overview

This skill has no compiled runtime; "implementation" means authoring/modifying JSON Schemas, projection contracts, playbooks, data-layer files, and reference fixtures. Tasks are ordered so each builds on validated artifacts from prior tasks. Every task ends with a schema-validation or diagnostics check.

The work falls into five layers, executed bottom-up: (1) leaf schemas, (2) the aggregate context/report schemas that reference them, (3) the workflow contract + route index that orchestrate the new steps, (4) the playbooks and docs the agent reads, and (5) reference fixtures that prove the Correctness Properties hold.

## Task Dependency Graph

```
1 (role-catalog schema) ──┐
2 (employee schema) ──────┤
3 (metric schema +4) ─────┼──► 5 (eval_context schema) ──► 6 (eval_report schema)
4 (role-catalog data) ────┘                                      │
                                                                 ▼
1,2,3,4,5,6 ──► 7 (workflow-contract: steps + K9/K17/K18) ──► 8 (route index + role-ontology contract-index)
                                                                 │
        ┌────────────────────────────────────────────────────────┤
        ▼                ▼                ▼                ▼        ▼
   9 (PRE.A/STEP0    10 (STEP1.2      11 (k-rules.md   12 (tainted   13 (SKILL.md +
      playbooks)         playbook)        + pre-flight)    lifecycle)    README + zh)
        │                │                                            │
        └────────────────┴───────────────┬────────────────────────────┘
                                          ▼
                       14 (happy fixtures) ──► 15 (anti-pattern fixtures) ──► 16 (final cross-check)
```

```json
{
  "waves": [
    { "wave": 1, "tasks": ["1", "2", "3", "4"], "rationale": "Leaf artifacts with no intra-feature dependencies: the role-catalog entry schema, employee schema, metric schema extension, and role-catalog seed data can be authored in parallel." },
    { "wave": 2, "tasks": ["5"], "rationale": "evaluation_context schema references the employee/role/metric shapes defined in wave 1." },
    { "wave": 3, "tasks": ["6"], "rationale": "evaluation_report schema surfaces fields that must match the context schema from task 5." },
    { "wave": 4, "tasks": ["7"], "rationale": "Workflow-contract projection encodes the new steps and K-rules over the now-finalized schemas." },
    { "wave": 5, "tasks": ["8"], "rationale": "Route index + role-ontology contract-index wire discovery once the workflow contract references them." },
    { "wave": 6, "tasks": ["9", "10", "11", "12", "13"], "rationale": "Playbooks and docs depend on the finalized contracts/schemas but are independent of each other, so they parallelize." },
    { "wave": 7, "tasks": ["14"], "rationale": "Happy-path fixtures exercise the full authored pipeline." },
    { "wave": 8, "tasks": ["15"], "rationale": "Anti-pattern fixtures build on the happy-path shapes to demonstrate K17/K18 violations." },
    { "wave": 9, "tasks": ["16"], "rationale": "Final cross-artifact consistency check after everything else is in place." }
  ]
}
```

## Tasks

- [x] 1. Author the Role_Catalog entry schema
  - Create `contracts/projections/role-ontology/role-catalog/schemas/role-catalog-entry.schema.json` (draft-07).
  - Required: `role_id` (`^[a-z0-9-]{1,64}$`), `industry` (`^[a-z0-9_]{1,64}$`), `responsibility_tags` (array 1–32 unique, each `^[a-z0-9_]{1,64}$`).
  - Optional: `parent_role` (string or null), `aliases` (0–32 unique, len 1–64), `display_names` (0–32 unique, len 1–128), `recognized_levels` (array of strings).
  - `additionalProperties: false`.
  - _Requirements: 5.2_

- [x] 2. Author the Employee_File schema
  - Create `runtime-schemas/employee.schema.json` (draft-07).
  - Required: `employee_id`, `role_id`, `industry`, `job_responsibilities` (free string), `scenarios` (array ≥1).
  - Optional: `sop_documents` (array of `{uri, title?, version?}`), mirroring the existing `evaluation_context.employee.sop_documents` shape.
  - `additionalProperties: false`.
  - _Requirements: 1.3, 1.4_

- [x] 3. Extend metric.schema.json with 4 optional semantic fields
  - Add to `contracts/projections/metric-ontology/metric-library/schemas/metric.schema.json` (all optional, default empty array):
    - `industry` (array 1–32 unique; each `*` or `^[a-z0-9_]{1,64}$`)
    - `responsibility_tags` (array 0–32 unique; each `^[a-z0-9_]{1,64}$`)
    - `complementary_metrics` (array 0–32 unique; existing metric_code format; no self-reference)
    - `exclusive_with` (array 0–32 unique; metric_code format; no self-reference; disjoint from `complementary_metrics`)
  - Keep `additionalProperties: false` and the existing `required` list unchanged.
  - Validate all 15 existing `*.metric.json` still pass.
  - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 16.1_

- [x] 4. Seed the Role_Catalog data layer
  - Create `role-catalog/` directory with one `<role_id>.role.json` per existing role: `customer-service-ecommerce`, `after-sales-agent`, `hr-attendance`, `bid-writer`, `legal-expert`, `software-engineer`.
  - Populate `industry`, `responsibility_tags`, `aliases` (Chinese + English variants), `display_names`; set `parent_role: "customer-service-ecommerce"` on `after-sales-agent` to exercise inheritance.
  - Add `role-catalog/README.md` documenting the hot-plug convention, filename pattern, env override `EVALUATION_ROLES_DIR`, and inheritance/duplicate/malformed fail-soft rules.
  - Validate every file against the task-1 schema.
  - _Requirements: 5.1, 5.3, 5.5, 5.7, 15.2_

- [x] 5. Upgrade evaluation_context.schema.json
  - 5.1 Change `employee.role` from string to object `{role_id (req), industry (req, empty allowed), responsibility_tags (req, empty allowed), level (opt)}`; add `employee.job_responsibilities` (string); add required `employee.employee_provenance` `{source enum, reliability enum, caveat?}`.
    - _Requirements: 4.1, 7.1, 7.2_
  - 5.2 Add `candidate_metrics` array (same item shape as current `selected_metrics`); keep `selected_metrics` and `dropped_metrics`; document that `selected_metrics` is now the STEP 1.2 output.
    - _Requirements: 9.1, 9.2, 9.4_
  - 5.3 Add `curate_log` array (items: `decision` enum, `metric_code`, `evidence[]` of `{source_field enum(9), quote}`, `confidence` [0,1], `confirmed_by_user` (bool or `"auto_applied"`)).
    - _Requirements: 11.1, 11.2, 11.3, 11.4, 13.2, 13.3, 13.4_
  - 5.4 Add `metric_selection_policy` object with `mode` enum (req, default `auto`) + `max_metrics`/`min_dimensions_covered`/`auto_apply_threshold`/`size_triggers` (all optional with declared bounds & defaults).
    - _Requirements: 12.1, 12.2, 12.3, 12.4, 12.5, 12.6_
  - 5.5 Add `employee_resolution_log` array; confirm `user_consultation_log` accepts STEP 1.2 confirmation entries (same K11 shape).
    - _Requirements: 2.8, 13.7_
  - Validate the existing `runs/eval-*/evaluation_context.json` fixtures still parse (object-form not yet present → covered by backward-compat normalization documented in playbook, task 9/10), and add a new object-form sample.
  - _Requirements: 4.1, 7.1, 7.2, 9.1, 9.2, 9.4, 11.1, 12.1_

- [x] 6. Upgrade evaluation_report.schema.json
  - Add top-level `employee_provenance` (`{source, reliability, caveat?}`).
  - Add top-level `metric_curation` (`{candidate_metrics, selected_metrics, removed[], added[], policy_snapshot}`).
  - Relax `employee.role` in the report from string to accept the object form; keep `employee_id` required.
  - Add both new fields to neither `required` (they are additive, surfaced when present) nor break existing required list; OR add to required if every post-feature run must carry them — choose required for `employee_provenance` (K17 demands it) and optional for `metric_curation` (absent when mode=never).
  - _Requirements: 4.1, 4.2, 14.1, 14.2, 14.3_

- [x] 7. Update the workflow-contract projection
  - 7.1 Add concept_mappings `PRE_A` (loadRoleCatalog, deterministic) and `S0` (resolveEmployee, llm-with-confirmation) with attributes covering the three-source priority, provenance, canonicalization single-writer rule, and the fail-soft/block/taint outcomes.
    - _Requirements: 1.1, 1.2, 1.5, 2.1, 2.2, 2.6, 3.1, 3.4, 5.4, 6.1, 6.4, 6.5_
  - 7.2 Add concept_mapping `S1_2` (curateMetrics, llm-bounded) with the invocation gate, curate algorithm, deterministic post-processing, bounds enforcement, and degrade-to-candidate failure handling.
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5, 10.6, 10.7, 10.8, 10.9, 10.10, 12.7, 12.8, 12.9, 12.10, 12.11, 12.12, 13.1, 13.5, 13.6_
  - 7.3 Add relation_mappings encoding `PRE_A → S0 → PRE → S1 → S1_2 → (S1_5 | S2)`; rename STEP 1 output to `candidate_metrics` in the S1 attributes.
    - _Requirements: 9.1, 9.3, 9.6, 9.7_
  - 7.4 Rewrite constraint K9 `notes` to the equation `selected_metrics = (candidate_metrics − removed) ∪ added` with candidate_metrics machine-verifiable; keep severity critical.
    - _Requirements: 9.5_
  - 7.5 Add constraint K17 `EmployeeResolutionProvenanceRequired` (critical) and K18 `CurateDecisionsMustBeAudited` (critical) with full taint semantics.
    - _Requirements: 4.5, 4.6, 4.7, 11.5, 11.6, 11.7, 11.8, 17.1, 17.2_
  - Validate the projection is well-formed JSON.
  - _Requirements: 9.5, 17.1, 17.2_

- [x] 8. Wire the role-ontology producer contract + route index
  - 8.1 Create `contracts/projections/role-ontology/contract-index.json` declaring the `role-library` topic and `role-catalog` target view (mirror the metric-ontology contract-index shape so `SkillLoader` discovers it).
    - _Requirements: 5.1, 5.7_
  - 8.2 Create `contracts/projections/role-ontology/role-catalog/role-catalog.role-catalog.projection.json` with `concept_mappings` (discovery rules per design Component 1 table), `constraint_mappings` (filename=role_id, fail-soft governance), and a `delivery_artifacts` pointer to the entry schema.
    - _Requirements: 5.2, 5.6, 5.8, 5.9_
  - 8.3 Add an `upstream_producer_dependencies` entry for `role-ontology` in `ontology_extraction/contract-index.json` (required_for_views: metric-selection/workflow-contract).
    - _Requirements: 5.1_
  - Validate all three JSON files parse.
  - _Requirements: 5.1, 5.2, 15.2_

- [x] 9. Author the STEP 0 playbook (PRE.A + resolveEmployee)
  - Create `playbooks/step-00-resolve-employee.md`: PRE.A role-catalog load (with inheritance/dup/malformed fail-soft), the three-source resolution state machine, user-confirmation loop (display → confirm/correct/decline, 5-round cap, 120s timeout), inferred-fallback caveat, role canonicalization (trim + case-insensitive + first-match-wins), single-writer enforcement, and the backward-compat bare-string `employee.role` wrapping.
  - Include an anti-patterns table (silent assumption, role_id mutation by later step, missing provenance).
  - _Requirements: 1.1, 1.2, 1.5, 1.6, 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 3.1, 3.2, 3.3, 3.4, 3.5, 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 7.3, 7.4, 7.5, 16.2, 16.3_

- [x] 10. Author the STEP 1.2 playbook (curateMetrics)
  - Create `playbooks/step-1.2-curate-metrics.md`: candidate_metrics input, invocation gate (mode/size-triggers truth table), the curate prompt construction (slices), the deterministic post-processing pipeline, confidence-gated user confirmation (one-at-a-time, 300s timeout → decline), bounds enforcement (max_metrics/min_dimensions → block_or_escalate), and the degrade-to-candidate failure/timeout handling.
  - Document the legacy `selected_metrics`-only → `candidate_metrics` mapping and both-present resolution.
  - Include an anti-patterns table (no-evidence decision, batch-fabricate, exceed max_metrics, silent low-confidence add).
  - _Requirements: 9.4, 10.1, 10.4, 10.5, 10.6, 10.7, 10.8, 10.9, 10.10, 11.1, 11.2, 11.3, 12.7, 12.8, 12.9, 12.10, 12.11, 12.12, 13.1, 13.2, 13.3, 13.4, 13.5, 13.6, 13.7, 16.4, 16.5_

- [x] 11. Update k-rules.md and pre-flight-invariants.md
  - Add K9 (rewritten equation), K17, K18 rows to `playbooks/k-rules.md` with severity, owning step, one-line summary, failure handling.
  - Add pre-flight invariants for: role-catalog directory readable, employees directory readable, `metric_selection_policy` defaults resolvable.
  - _Requirements: 9.5, 17.4, 18.1, 18.4_

- [x] 12. Update tainted-run-lifecycle.md
  - Add K17 and K18 recovery procedures (trigger, corrective action, resume-in-fresh-eval_id) and the atomicity rules (K17 atomic-fail; K18 partial-success-continue / total-fail-halt; TAINTED.md write failure → still halt + log).
  - _Requirements: 4.6, 11.7, 11.8, 17.1, 17.2, 17.3, 17.5_

- [x] 13. Update SKILL.md, metrics/README, SKILL.zh.md for the new layers and steps
  - SKILL.md: add STEP 0 and STEP 1.2 rows to the 11→13-step table; add K17/K18 to the K-rules glance table; add `./employees/` and `./role-catalog/` to the Path defaults table (env vars `EVALUATION_EMPLOYEES_DIR`/`EVALUATION_ROLES_DIR`); change "4 hot-pluggable data layers" → "6"; update the high-level flow diagram.
  - Add `employees/README.md` documenting the data layer (filename `<employee_id>.json`, env override, fail-soft on bad file).
  - SKILL.zh.md: update the index rows and the "6 层热插拔" mention.
  - _Requirements: 15.1, 15.3, 15.4, 15.5, 18.5_

- [x] 14. Author happy-path reference fixtures
  - 14.1 `runs/eval-emp-resolve-001/`: authoritative-file STEP 0 → candidate_metrics → STEP 1.2 auto-skip (count in range) → evaluation_context.json + minimal report showing `employee_provenance.source=authoritative_file` and `metric_curation` with empty removed/added.
    - _Requirements: 1.1, 4.1, 9.4, 14.1_
  - 14.2 `runs/eval-curate-001/`: candidate > 15 triggers STEP 1.2; remove 2 + add 1 with evidence citations; one low-confidence add user-confirmed; curate_log + report `metric_curation` populated.
    - _Requirements: 10.1, 10.3, 11.1, 11.3, 13.1, 13.2, 14.3_
  - Add a `runs/README.md` entry describing both new fixtures.
  - _Requirements: 14.1, 14.2, 14.3_

- [x] 15. Author anti-pattern reference fixtures
  - 15.1 `runs/eval-k17-violation/`: missing `employee_provenance` → `TAINTED.md` (K17) + report `open_questions` entry; demonstrates atomic-fail.
    - _Requirements: 4.5, 17.1_
  - 15.2 `runs/eval-k18-violation/`: a curate_log entry with empty `evidence` → `TAINTED.md` (K18) + report `open_questions` entry; demonstrates partial-success-continue.
    - _Requirements: 11.6, 17.2_
  - Register both in `runs/README.md` as reference fixtures (do not delete).
  - _Requirements: 17.1, 17.2_

- [x] 16. Final cross-artifact consistency check
  - Run getDiagnostics on every modified `.md` and confirm all JSON (schemas, projections, fixtures, role/employee data) parse via a validator pass.
  - Verify the 10 Correctness Properties are each checkable against the task-14 happy fixtures: recompute candidate_metrics (P1), bounded transform (P2), evidence-backed decisions (P3), bounds (P4), K10 subset (P5), K13 keys (P6), provenance (P7), report byte-copy (P8), single role_id writer (P9), degrade-to-candidate (P10).
  - Confirm the 15 existing metric files and existing `runs/eval-*` fixtures are unmodified.
  - _Requirements: 8.6, 16.1, 16.5, 18.2, 18.3, 18.5, 18.6, 18.7_

## Notes

- **No compiled code.** No task touches the kingcrab `src/` tree. The host runtime (`SkillLoader`, `SkillProjectionResolver`) already discovers new `contract-index.json` files and routes generically, so the new `role-ontology` producer needs zero C# change (design Decision #4).
- **Supervised-mode caveat.** New `*.metric.json` / `*.role.json` / fixture files must omit the remote `$schema` reference line (it is blocked in supervised mode); reference the schema via the README/projection instead, matching how the 7 generic metric files were authored.
- **Backward compatibility is a hard gate.** Tasks 3, 5, and 16 each re-validate the 15 existing metric files and existing `runs/eval-*` fixtures. Any task that would require editing those is wrong — the four new metric fields are optional and the context normalization is loader-time only.
- **Fixtures are the test suite.** Because there is no executable, the happy-path (task 14) and anti-pattern (task 15) fixtures under `runs/` are the regression evidence. They are committed as permanent reference fixtures (like `eval-soul-001`), not transient run output.
- **Degrade-to-candidate is the safety net.** Per Correctness Property 10, every STEP 1.2 failure path resolves to `selected_metrics = candidate_metrics`. Reviewers should confirm this in task 10's playbook and task 14.2's fixture.
- **K-rule numbering.** K9 is rewritten in place (same number, new equation); K17/K18 are net-new. The prompt-constraint projections keep their independent K1–K5 namespaces — do not renumber them.
