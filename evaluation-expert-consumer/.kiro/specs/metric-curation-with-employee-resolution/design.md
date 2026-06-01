# Design Document

## Overview

This feature inserts two new steps into the `evaluation-expert-consumer` workflow — **STEP 0 `resolveEmployee`** (before PRE) and **STEP 1.2 `curateMetrics`** (between STEP 1 and STEP 2) — and adds one new hot-pluggable data layer (`./employees/`) plus one new producer contract (`role-ontology/role-catalog`).

The skill has no compiled runtime. The "EvaluationOrchestrator" is the host LLM agent executing the workflow contract by reading `SKILL.md` + playbooks and performing each step inline. Therefore "design" here means the concrete artifacts the agent reads and the data shapes it produces:

1. **JSON Schemas** under `runtime-schemas/` and the two ontology schema dirs — the validation contracts.
2. **Projection contracts** under `contracts/projections/**` — the authoritative workflow graph + K-rules + route index.
3. **Playbooks** under `playbooks/` — the step-by-step operating procedures the agent follows.
4. **Data layers** — `./metrics/`, `./test-cases/`, `./role-catalog/`, `./employees/` (the latter two new).

The design preserves the deterministic/LLM boundary that defines this skill: STEP 0 is **LLM-with-mandatory-confirmation**, STEP 1 stays **deterministic**, STEP 1.2 is **LLM-bounded-and-auditable**, and everything downstream (STEP 2–9) is unchanged except for the `selected_metrics` provenance now flowing from STEP 1.2 instead of STEP 1.

### Goals

- Replace the brittle "free-form `employee.role` string → exact match" path with a resolve-then-canonicalize step that has an authoritative file source, a user-confirmed dialog source, and a caveat-tagged inferred fallback.
- Replace "string-match is the only metric selector" with a two-layer model: deterministic role-filter (`candidate_metrics`, machine-verifiable) + auditable LLM curation (`selected_metrics = (candidate − removed) ∪ added`).
- Keep every existing K-rule (K1–K16) semantically intact; only K9 is rewritten (same guarantee, new equation), and K17 / K18 are added.
- Zero breakage for the 8+7 existing metric files, legacy `evaluation_context.json`, and `runs/eval-*` fixtures.

### Non-Goals

- No change to the fan-out scoring (STEP 4), aggregation (STEP 5), roll-up (STEP 6), red-line (STEP 7), or report-synthesis (STEP 8/9) algorithms beyond surfacing two new provenance blocks in the STEP 9 report.
- No change to the 5 frozen parent dimensions.
- No new compiled code in the kingcrab `src/` tree. The host runtime (`SkillLoader` / `SkillProjectionResolver`) already discovers `contract-index.json` files generically; the new `role-ontology` contract is picked up by the existing `Directory.GetFiles(..., "contract-index.json", AllDirectories)` scan with no C# change.

## Architecture

### Step graph (before → after)

```
BEFORE:  PRE ─► STEP 1 ─►(missing?)─► STEP 1.5 ─► STEP 2 ─► [STEP 3 ─► STEP 4]×N ─► 5 ─► 6 ─► 7 ─► 8×N ─► 9

AFTER:   STEP 0 ─► PRE ─► STEP 1 ─► STEP 1.2 ─►(missing?)─► STEP 1.5 ─► STEP 2 ─► [STEP 3 ─► STEP 4]×N ─► 5 ─► 6 ─► 7 ─► 8×N ─► 9
         │                  │          │
         │                  │          └─ LLM curate: selected = (candidate − removed) ∪ added
         │                  └─ deterministic role-filter → candidate_metrics
         └─ resolve employee (file | user-dialog | inferred) + canonicalize role_id
```

Key ordering decisions:

- **STEP 0 runs before PRE.** PRE loads the metric registry; it does not need the employee. But STEP 0 needs the Role_Catalog (loaded by a new PRE-adjacent scan), so the Role_Catalog scan is hoisted to run before STEP 0. We model this as **PRE.A loadRoleCatalog** (new) → **STEP 0 resolveEmployee** → **PRE.B loadMetricRegistry** (the existing PRE). Renaming avoids a confusing "STEP -1".
- **STEP 1.2 runs after STEP 1, before STEP 1.5 and STEP 2.** It must consume `candidate_metrics` (STEP 1 output) and produce `selected_metrics` before STEP 1.5 synthesizes cases (so synthesized cases enrich against the final metric set) and before STEP 2 binds metrics to cases.

### Data-layer topology

```
evaluation-expert-consumer/
├── metrics/                      (existing, 15 files)
├── test-cases/                   (existing)
├── runtime-drivers/              (existing)
├── simulators/                   (existing)
├── employees/                    (NEW data layer — <employee_id>.json)
│   └── README.md
├── role-catalog/                 (NEW data layer — <role_id>.role.json)
│   └── README.md
├── runtime-schemas/
│   ├── employee.schema.json      (NEW)
│   ├── evaluation_context.schema.json   (MODIFIED — employee object, candidate_metrics, curate_log, metric_selection_policy, *_log)
│   ├── evaluation_report.schema.json    (MODIFIED — employee_provenance, metric_curation)
│   └── ...
└── contracts/projections/
    ├── role-ontology/            (NEW producer contract)
    │   ├── contract-index.json
    │   └── role-catalog/
    │       ├── role-catalog.role-catalog.projection.json
    │       └── schemas/role-catalog-entry.schema.json
    ├── metric-ontology/
    │   └── metric-library/schemas/metric.schema.json   (MODIFIED — 4 optional fields)
    └── ontology_extraction/
        ├── contract-index.json   (MODIFIED — upstream dep + topic signals for role-catalog)
        └── metric-selection/
            └── metric-selection.workflow-contract.projection.json
                                   (MODIFIED — STEP 0, STEP 1.2, K9 rewrite, K17, K18)
```

### Why `role-ontology` is a peer producer, not a sub-topic of metric-ontology

`role-ontology` is parallel to `metric-ontology` and `testcase-ontology` because: (a) it is a distinct authoritative registry with its own schema and hot-plug directory; (b) the host's `SkillLoader` discovers it automatically via the recursive `contract-index.json` scan, so no code change is needed; (c) STEP 0 depends on it the way STEP 2 depends on metric-ontology — a clean producer/consumer edge that `ontology_extraction/contract-index.json` declares in `upstream_producer_dependencies`.

## Components and Interfaces

### Component 1: Role_Catalog (contract + data layer)

**Contract**: `contracts/projections/role-ontology/role-catalog/role-catalog.role-catalog.projection.json` (mirrors the metric-catalog projection shape: `concept_mappings` declaring discovery rules, `constraint_mappings` for governance, a `delivery_artifacts` pointer to the schema).

**Entry schema**: `contracts/projections/role-ontology/role-catalog/schemas/role-catalog-entry.schema.json`

```jsonc
{
  "role_id":            "customer-service-ecommerce",   // ^[a-z0-9-]{1,64}$, required
  "industry":           "ecommerce",                     // ^[a-z0-9_]{1,64}$, required
  "responsibility_tags":["customer_facing","tool_use","policy_application"], // 1–32 unique, required
  "parent_role":        null,                            // role_id of another entry or null, optional
  "aliases":            ["电商客服","客服","cs-ecommerce"],  // 0–32 unique, optional
  "display_names":      ["电商客服专员"],                  // 0–32 unique, optional
  "recognized_levels":  ["employee","supervisor","manager"] // optional; feeds Req 7 AC2
}
```

**Loader behavior** (PRE.A `loadRoleCatalog`, deterministic, inline):

| Concern | Rule | Requirement |
|---|---|---|
| Discovery | scan `EVALUATION_ROLES_DIR` or `./role-catalog/*.role.json` | R5.3 |
| Key | map keyed by `role_id` | R5.4 |
| Inheritance | `industry` override (child wins); `responsibility_tags` set-union dedup | R5.5 |
| Inheritance failure | absent parent / cycle / depth > 8 → load without inheritance + open_question, proceed | R5.6 |
| Duplicate role_id | keep lexicographically-first filename + open_question, proceed | R5.8 |
| Malformed file | skip + open_question, proceed | R5.9 |

The loader is fail-soft (skip + warn) because a bad role file should not block the whole evaluation; only a missing match for the *evaluatee's* role degrades to caveat (handled in STEP 0).

### Component 2: Employee_Resolver (STEP 0)

**Inputs**: `employee_id`, optional user free-form description, Role_Catalog map (from PRE.A).
**Output**: `evaluation_context.employee` (new object shape) + `employee.employee_provenance` + `evaluation_context.employee_resolution_log`.

**Resolution priority (state machine):**

```
                    ┌─────────────────────────────┐
   employee_id ───► │ employees/<id>.json exists?  │
                    └──────┬───────────────┬───────┘
                       yes │            no │
                           ▼               ▼
                  ┌──────────────┐  ┌──────────────────────────┐
                  │ load + parse │  │ user description present? │
                  │ validate     │  └────┬─────────────────┬────┘
                  └──────┬───────┘   yes │              no │
                  ok │   │ fail          ▼                 ▼
                     ▼   ▼          ┌───────────┐   ┌──────────────┐
            source=authoritative   │ LLM parse │   │ LLM infer    │
            reliability=high       │ → draft   │   │ best-guess   │
                  │ block_or_      │ → display │   │ source=      │
                  │ escalate       │ → confirm │   │ inferred_    │
                  ▼                └────┬──────┘   │ fallback     │
            [canonicalize role]   confirm│ decline │ reliability= │
                                        ▼   │      │ low + caveat │
                              source=user_  │      └──────┬───────┘
                              dialog        │             │
                              reliability=  └──────────────┤ (decline → fallback)
                              high                         ▼
                                                    [canonicalize role]
```

**Provenance object** (`employee_provenance`, fed to K17):

```jsonc
{
  "source":      "authoritative_file" | "user_dialog" | "inferred_fallback",  // required
  "reliability": "high" | "low",                                              // required
  "caveat":      "employee_inferred_no_authoritative_source"                  // optional; ≤1000 chars
                 // | "role_id_no_catalog_entry" (appended on canonicalization miss)
}
```

**Canonicalization** (R6): trim → case-insensitive exact match against `role_id` then every `alias`, catalog iteration order, first-match-wins. Match → copy `industry` + `responsibility_tags` into `employee.role`. Miss → keep free-form string as `role_id` + append `role_id_no_catalog_entry` to caveat (dedup). STEP 0 is the **only** writer of `employee.role.role_id`; any later write is rejected with `unauthorized_role_id_mutation` (R6.5).

> **Design note — enforcing "only STEP 0 writes role_id".** There is no runtime guard object; the enforcement is a workflow-contract precondition the agent self-checks (parallel to how K9/K13 are self-checked). The K17 playbook instructs the agent that if any step's output would change `employee.role.role_id`, it is a violation. This matches the existing skill's "agent follows the contract" model — no new mechanism is introduced.

### Component 3: Metric_Curator (STEP 1.2)

**Inputs**: `candidate_metrics` (STEP 1), `metric_registry` (PRE.B), `employee.industry`, `employee.role.responsibility_tags`, `employee.job_responsibilities`, `metric_selection_policy`.
**Output**: `evaluation_context.selected_metrics`, `evaluation_context.curate_log[]`, appended `evaluation_context.user_consultation_log` entries.

**Invocation gate** (R10 + R12):

```
mode == "never"   → skip; selected_metrics = candidate_metrics
mode == "always"  → run unconditionally (even if size-trigger eval errors)
mode == "auto"    → run iff  len(candidate) < size_triggers.lower (default 3)
                          OR len(candidate) > size_triggers.upper (default 15)
                    else skip; selected_metrics = candidate_metrics
```

**Curate algorithm** (one LLM call, returns structured JSON):

```
INPUT prompt slices:
  - employee.{industry, role.responsibility_tags, job_responsibilities}
  - candidate_metrics[*].{metric_code, description, tags, industry, responsibility_tags}
  - (metric_registry − candidate_metrics)[*].{same fields}   ← the "addable" pool
  - metric_selection_policy

LLM emits:
  removed[]  ⊆ candidate_metrics                          (semantically inappropriate)
  added[]    ⊆ (metric_registry − candidate_metrics)      (string-match missed)
  per decision: { metric_code, decision, evidence[], confidence∈[0,1] }

DETERMINISTIC post-processing (orchestrator, not LLM):
  1. validate removed/added disjoint, subset constraints (R10.3)
  2. resolve low-confidence adds via user confirmation (R13)
  3. selected_metrics = (candidate − removed) ∪ confirmed_adds
  4. enforce max_metrics, min_dimensions_covered (R12.8/9) → block_or_escalate on violation
  5. persist curate_log + verify K18 (every decision has evidence citation)
```

**Confidence-gated confirmation** (R13):

| confidence vs threshold | `confirmed_by_user` | included? |
|---|---|---|
| `>= auto_apply_threshold` (default 0.7) | `"auto_applied"` (string literal) | yes |
| `< threshold`, user confirms | `true` | yes |
| `< threshold`, user declines / 300s timeout | `false` | no |

Multiple low-confidence adds are prompted **one at a time in curate_log order** (R13.6).

**Failure handling** (R10.8/9/10): curator failure, malformed output, subset violation, missing input, or 30s timeout → fall back to `selected_metrics = candidate_metrics` + open_question, run proceeds. This makes STEP 1.2 a **safe enhancement**: worst case, it degrades to today's STEP 1 behavior.

### Component 4: Schema changes

#### `runtime-schemas/employee.schema.json` (NEW)

```jsonc
{
  "employee_id": "...",                 // required
  "role_id": "...",                     // required (pre-canonicalization in file; STEP 0 canonicalizes)
  "industry": "...",                    // required
  "job_responsibilities": "...",        // required, free text
  "scenarios": ["..."],                 // required, ≥1
  "sop_documents": [ { "uri": "..." } ] // optional
}
```

#### `evaluation_context.schema.json` (MODIFIED)

| Field | Change | Requirement |
|---|---|---|
| `employee.role` | string → object `{role_id, industry, responsibility_tags, level?}` | R7 |
| `employee.employee_provenance` | NEW required object `{source, reliability, caveat?}` | R4 |
| `employee.job_responsibilities` | NEW string | R1 |
| `candidate_metrics` | NEW array (was `selected_metrics`'s old role) | R9 |
| `selected_metrics` | KEPT, now = STEP 1.2 output | R9.4 |
| `dropped_metrics` | KEPT (role_mismatch entries) | R9.2 |
| `curate_log` | NEW array | R11.4 |
| `metric_selection_policy` | NEW object | R12 |
| `employee_resolution_log` | NEW array | R2.8 |
| `user_consultation_log` | KEPT (K11), now also holds STEP 1.2 confirmations | R13.7 |

Backward-compat (R16): a loader-time normalization — bare-string `employee.role` is wrapped; legacy `selected_metrics`-only context is read as `candidate_metrics`; both-present → `candidate_metrics` wins.

#### `metric.schema.json` (MODIFIED — 4 optional fields, R8)

```jsonc
"industry":             { array of 1–32, "*" or ^[a-z0-9_]{1,64}$, unique },
"responsibility_tags":  { array of 0–32, ^[a-z0-9_]{1,64}$, unique },
"complementary_metrics":{ array of 0–32, metric_code fmt, unique, no self },
"exclusive_with":       { array of 0–32, metric_code fmt, unique, no self, disjoint from complementary_metrics }
```

All optional → existing 15 metric files validate unchanged (R8.6, R16.1).

#### `evaluation_report.schema.json` (MODIFIED)

Add two top-level fields:

```jsonc
"employee_provenance": { "source", "reliability", "caveat?" },   // byte-copy of context, R4
"metric_curation": {                                              // R14
  "candidate_metrics": [...],   // byte-copy
  "selected_metrics":  [...],   // byte-copy
  "removed":  [ curate_log entries where decision=removed ],
  "added":    [ curate_log entries where decision=added ],
  "policy_snapshot": { ...metric_selection_policy }
}
```

Also: `employee.role` in the report becomes the object form (the report's `employee` block currently requires a string `role`; relax to accept the object, keeping `employee_id` required).

### Component 5: Workflow-contract projection changes

`metric-selection.workflow-contract.projection.json`:

- **Add concept_mappings**: `PRE_A` (loadRoleCatalog), `S0` (resolveEmployee), `S1_2` (curateMetrics).
- **Add relation_mappings**: `PRE_A → S0 → PRE_B(existing PRE) → S1 → S1_2 → (S1_5 | S2)`.
- **Rewrite K9** `notes`: keep the audit equation but state `selected_metrics = (candidate_metrics − removed) ∪ added`; `candidate_metrics` is the machine-verifiable deterministic role-filter; `removed`/`added` are auditable via curate_log.
- **Add K17** `EmployeeResolutionProvenanceRequired` (critical): `employee.employee_provenance` must exist with valid `source`/`reliability`; low reliability requires caveat; violation taints.
- **Add K18** `CurateDecisionsMustBeAudited` (critical): every removed/added decision has a curate_log entry with ≥1 evidence citation quoting a named source field; violation taints.

### Component 6: Route index changes

`ontology_extraction/contract-index.json`:

- Add `upstream_producer_dependencies` entry for `role-ontology` (required for `metric-selection/workflow-contract`).
- The `role-ontology/contract-index.json` declares its own topic (`role-library`) + `role-catalog` target view — discovered automatically by `SkillLoader`.

## Data Models

### Curate_Log entry

```jsonc
{
  "decision": "removed" | "added",            // required
  "metric_code": "factual_accuracy",           // required, matches a registry metric
  "evidence": [                                 // required, ≥1 (K18)
    {
      "source_field": "employee.job_responsibilities",  // enum of 9 allowed fields (R11.3)
      "quote": "handles refund disputes"                 // verbatim substring, ≥1 char
    }
  ],
  "confidence": 0.82,                           // required, [0.0,1.0]
  "confirmed_by_user": true | false | "auto_applied"   // set during R13 resolution
}
```

`evidence.source_field` enum: `employee.industry`, `employee.job_responsibilities`, `employee.role.responsibility_tags`, `metric.description`, `metric.tags`, `metric.industry`, `metric.responsibility_tags`, `metric.complementary_metrics`, `metric.exclusive_with`.

### metric_selection_policy (defaults)

```jsonc
{
  "mode": "auto",                  // auto | always | never
  "max_metrics": 8,                // [1,100]
  "min_dimensions_covered": 1,     // [1,5]
  "auto_apply_threshold": 0.7,     // [0.0,1.0]
  "size_triggers": { "candidate_count_lower_bound": 3, "candidate_count_upper_bound": 15 }  // each [0,200]
}
```

## Error Handling

The design distinguishes three escalation tiers, matching the existing skill's vocabulary:

| Tier | Trigger examples | Action |
|---|---|---|
| **fail-soft** (skip + open_question, proceed) | malformed role file (R5.9), duplicate role_id (R5.8), inheritance failure (R5.6), curator failure/timeout (R10.8/10), bad employees/role file (R15.5) | run continues; degraded fidelity noted in open_questions |
| **block_or_escalate** (halt, require fix) | invalid employee_id (R1.2), Employee_File parse/schema fail (R1.5), empty role string (R6.4), empty candidate_metrics (R9.6), empty registry (R9.7), max_metrics/min_dimensions violation (R12.8/9), legacy-wrap failure (R16.3), parent_dimension mismatch (R18.6/7) | run stops cleanly with a named cause; no taint |
| **taint** (TAINTED.md + stop scoring + open_questions) | missing/invalid employee_provenance (K17, R4.5/17.1), missing caveat (R4.7), curate decision without evidence (K18, R11.6/17.2) | Tainted_Run_Lifecycle; STEP 9 surfaces |

Atomicity rules:
- **K17 taint** (R4.6): the 3 taint-actions are atomic — any failure fails the whole run.
- **K18 taint** (R11.7/11.8): partial success allowed (continue + record failed actions); total failure halts.
- **TAINTED.md write failure** (R17.5): still halt scoring, emit to run logs.

This asymmetry is intentional: a missing employee provenance (K17) is a structural integrity failure (we don't know who we evaluated), so it is strict. A curate-log persistence hiccup (K18) is recoverable as long as the violation is surfaced somewhere.

## Correctness Properties

These are the invariants an automated verifier (or a reviewing agent) can check on any run's artifacts. They are the machine-verifiable core of the design.

### Property 1: candidate_metrics is recomputable (K9)
For any run, recomputing the role-filter from `metric_registry` and `employee.role.role_id` MUST reproduce `evaluation_context.candidate_metrics` exactly (set equality). Formally: `candidate_metrics == { m ∈ metric_registry : role_id ∈ m.applicable_roles ∨ "*" ∈ m.applicable_roles }`.
**Validates: Requirements 9.1, 9.3, 9.5**

### Property 2: selected_metrics is a bounded transform of candidate_metrics (K9 + K18)
`selected_metrics == (candidate_metrics − removed) ∪ added` where `removed ⊆ candidate_metrics`, `added ⊆ (metric_registry − candidate_metrics)`, and `removed ∩ added == ∅`. Every `metric_code` in `removed ∪ added` appears in exactly one `curate_log` entry, and `len(curate_log) == len(removed) + len(added)`.
**Validates: Requirements 10.3, 10.4, 11.4**

### Property 3: every curate decision is evidence-backed (K18)
For every `curate_log` entry, `len(evidence) ≥ 1` and at least one citation has a `source_field` from the 9-value enum AND a `quote` that is a verbatim, ≥1-char substring of that field's actual value in the run's data.
**Validates: Requirements 11.1, 11.2, 11.3, 11.5**

### Property 4: bounds hold post-curation (K9 + R12)
`len(selected_metrics) ≤ metric_selection_policy.max_metrics` AND `|{ m.parent_dimension : m ∈ selected_metrics }| ≥ metric_selection_policy.min_dimensions_covered`. Violation ⇒ the run is in `block_or_escalate`, not `passed`.
**Validates: Requirements 12.8, 12.9**

### Property 5: downstream binding still subsets selected_metrics (K10, unchanged)
For every enriched test case, `applicable_metrics ⊆ selected_metrics` (where `selected_metrics` is now the STEP 1.2 output). The persisted `enriched-cases/<tc>.json` and the inline `evaluation_context.enriched_test_cases[*]` agree byte-for-byte.
**Validates: Requirements 18.2**

### Property 6: dimension key set matches selected_metrics (K13, unchanged)
`keys(dimension_scores) == { m.parent_dimension : m ∈ selected_metrics }`, and every key is one of the 5 frozen parent dimensions.
**Validates: Requirements 18.3, 18.5, 18.7**

### Property 7: provenance present and consistent (K17)
`employee.employee_provenance` exists with string `source ∈ {authoritative_file, user_dialog, inferred_fallback}` and string `reliability ∈ {high, low}`. If `reliability == low` then `caveat` is a non-empty string. The report's `employee_provenance` is byte-identical to the context's.
**Validates: Requirements 4.1, 4.2, 4.5, 4.7**

### Property 8: report numerics are copies, not recomputations (K7, unchanged)
`evaluation_report.metric_curation.candidate_metrics` / `.selected_metrics` are byte-identical to the context fields; `dimension_scores` / `overall_score` / `red_line` / `passed` are byte-identical to STEP 5/6/7 outputs. The LLM authors prose only.
**Validates: Requirements 14.2, 14.3**

### Property 9: single writer of role_id (R6)
`employee.role.role_id` is written exactly once, by STEP 0. No later step's output changes it. A diff of `role_id` across step boundaries after STEP 0 MUST be empty.
**Validates: Requirements 6.4, 6.5**

### Property 10: STEP 1.2 never worsens the baseline (safety property, Decision #1)
If STEP 1.2 fails, times out, or is skipped, `selected_metrics == candidate_metrics`. Therefore the metric set used downstream is always at least the deterministic role-filter result — adding STEP 1.2 can only refine, never degrade below today's behavior.
**Validates: Requirements 9.4, 10.5, 10.8, 10.9, 10.10**

## Testing Strategy

Because there is no compiled code, "tests" are **schema-validation fixtures + worked-example traces** committed under `runs/` as reference fixtures (parallel to the existing `eval-soul-001` / `eval-xiaofu-001` anti-pattern fixtures).

### Schema-level (deterministic, runnable via any JSON-Schema validator)

1. **metric.schema.json backward-compat**: all 15 existing `*.metric.json` validate unchanged (R8.6/R16.1). Add 1 fixture metric using all 4 new fields → validates; 1 with `exclusive_with` overlapping `complementary_metrics` → rejected (R8.5).
2. **role-catalog-entry.schema.json**: valid entry with/without parent; entry with cyclic parent → loader emits open_question not schema error; duplicate role_id across two files → lexicographic-first wins.
3. **employee.schema.json**: valid file; missing `industry` → block_or_escalate fixture.
4. **evaluation_context.schema.json**: object-form `employee.role` validates; bare-string legacy → normalized fixture shows the wrap + caveat.
5. **evaluation_report.schema.json**: report with `employee_provenance` + `metric_curation` validates; report whose `metric_curation.selected_metrics` ≠ context → flagged by the K7 byte-copy check.

### Worked-example fixtures (one happy-path + targeted anti-patterns)

| Fixture dir | Demonstrates |
|---|---|
| `runs/eval-emp-resolve-001/` (NEW, happy) | STEP 0 authoritative-file path → STEP 1 → STEP 1.2 auto-skip (candidate count in range) → normal scoring |
| `runs/eval-curate-001/` (NEW, happy) | STEP 1.2 fires (candidate > 15), removes 2 + adds 1 with evidence, one low-confidence add user-confirmed |
| `runs/eval-k17-violation/` (NEW, anti) | missing employee_provenance → TAINTED.md + open_question |
| `runs/eval-k18-violation/` (NEW, anti) | curate decision with empty evidence → TAINTED.md + open_question |

### Constraint cross-checks (manual review checklist, added to playbooks)

- K9 recomputability: given `metric_registry` + `role`, an automated verifier recomputes `candidate_metrics` and confirms set-equality (R9.5).
- K13 unchanged: `dimension_scores` keys == `{parent_dimension : m ∈ selected_metrics}` where `selected_metrics` now = STEP 1.2 output (R18.3).
- K18: `len(curate_log) == len(removed) + len(added)`; every decision metric_code appears in exactly one entry (R11.4).

## Design Decisions and Rationales

1. **STEP 1.2 degrades to candidate_metrics on any failure** rather than blocking. Rationale: the deterministic role-filter already produces a correct (if coarse) metric set. Making the LLM curation a *refinement* that can always fall back means adding STEP 1.2 never makes an evaluation *worse* than today — it only ever improves or no-ops. This is the single most important safety property of the design.

2. **`candidate_metrics` is a rename, not a new concept.** STEP 1 already computed exactly this set under the name `selected_metrics`. We rename the STEP 1 output and reassign `selected_metrics` to the STEP 1.2 output. Downstream K10/K13 keep referencing `selected_metrics` and need no logic change — only the producer of `selected_metrics` moved one step later. This minimizes blast radius.

3. **Role canonicalization is centralized in STEP 0.** Rationale: today the role string is whatever the LLM wrote, and any drift breaks the STEP 1 exact-match. Forcing all role normalization through one step with one authoritative catalog eliminates the "evaluation blocks because role spelled differently" failure class identified in the requirements discussion.

4. **role-ontology as a peer producer reuses existing discovery.** The host `SkillLoader.TryLoadProjectionContracts` already does a recursive `contract-index.json` scan, and `SkillProjectionResolver` already routes by topic/view. A new producer directory is picked up with zero C# changes — consistent with the skill's "hot-plug via directory drop" principle extended to contracts.

5. **K17 strict / K18 lenient asymmetry.** Not knowing who was evaluated (K17) invalidates the entire report's meaning, so it is atomic-fail. A curate-audit gap (K18) is a transparency issue that can be surfaced without discarding otherwise-valid scores, so it tolerates partial taint-action success.

6. **Confidence gate uses a string literal `"auto_applied"` for the auto path** instead of a separate boolean. Rationale: a reviewer reading `confirmed_by_user` can distinguish "human said yes" (`true`) from "policy auto-applied without asking" (`"auto_applied"`) from "human said no" (`false`) — three meaningfully different audit states in one field.

## Requirements Coverage

| Requirement | Covered by |
|---|---|
| R1 Authoritative file | Component 2 (resolution priority), employee.schema.json |
| R2 User-dialog + confirm | Component 2 (state machine), employee_resolution_log |
| R3 Inferred fallback | Component 2 (fallback branch), provenance caveat |
| R4 Provenance surfacing (K17) | Component 5 (K17), Component 4 (report schema), Error Handling (atomic) |
| R5 Role Catalog | Component 1 (contract + loader + schema) |
| R6 Canonicalization | Component 2 (canonicalization), single-writer design note |
| R7 employee.role upgrade | Component 4 (context schema), backward-compat normalization |
| R8 Metric schema fields | Component 4 (metric.schema.json) |
| R9 candidate_metrics rename (K9) | Component 5 (K9 rewrite), Decision #2 |
| R10 Curate algorithm | Component 3 (algorithm + gate + failure handling) |
| R11 Curate auditability (K18) | Component 5 (K18), Data Models (curate_log), Error Handling |
| R12 metric_selection_policy | Component 3 (gate), Data Models (defaults) |
| R13 Low-confidence confirmation | Component 3 (confirmation table), Decision #6 |
| R14 Report surface | Component 4 (report schema metric_curation) |
| R15 Hot-pluggability | Component 1, Architecture (data-layer topology), Decision #4 |
| R16 Backward compat | Component 4 (normalization), Testing (backward-compat fixtures) |
| R17 Tainted handling | Error Handling (taint tier), playbook updates |
| R18 Unchanged K-rules | Decision #2 (selected_metrics rename keeps K10/K13/K3/K16 intact), Testing cross-checks |
