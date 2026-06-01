# runs/

Per-run output directory. Each evaluation run gets its own subdirectory `./<eval_id>/` containing all artifacts produced by the workflow.

## Layout per run

```
runs/<eval_id>/
├── evaluation_context.json            # STEP 0/1/1.2: resolved employee + candidate/selected/dropped metrics + curate_log
├── synthesized-cases/<tc_id>.json      # STEP 1.5 (only when test_case_status == 'missing')
├── enriched-cases/<tc_id>.json         # STEP 2: applicable_metrics attached
├── traces/<tc_id>.trace.json           # STEP 3: ExecutionTrace per scenario
├── scores/<tc_id>__<metric>.json        # STEP 4: per-(case, metric) MetricScore
├── aggregated_metric_scores.json       # STEP 5
├── dimension_scores.json               # STEP 6
├── red_line_check.json                 # STEP 7
├── reports/scenarios/<tc_id>.report.json  # STEP 8
├── reports/evaluation_report.json      # STEP 9 (machine) — incl. employee_provenance + metric_curation
├── reports/evaluation_report.html      # STEP 9 (human)
└── TAINTED.md                          # only when a HARD RULE was violated
```

Override the run root via `EVALUATION_RUN_DIR` (default: `./runs/<eval_id>/`).

## ⚠️ Built-in subdirectories are reference fixtures

These directories are **not** outputs of recent evaluations — they are committed reference fixtures used by SKILL.md / playbooks to illustrate correct shapes and anti-patterns. Do **not** delete them.

### Anti-pattern fixtures (what NOT to do)

| Fixture | Demonstrates |
|---|---|
| `eval-soul-001/`   | K14 / K16 anti-patterns: closing stdin before `end`; all 10 score files sharing identical `scored_at` (batch fabrication) |
| `eval-xiaofu-001/` | K9 / K10 / K12 / K13 / K15 anti-patterns: full registry copied as `selected_metrics`; dimensions fabricated whose sub-metrics were dropped at STEP 1; `stop_conditions` mis-aligned with `expected_tool_calls` |
| `eval-xiaofu-002/` | A fixed re-run of `eval-xiaofu-001` showing the corrected shape |
| `eval-k17-violation/` | K17 anti-pattern: `employee_provenance` missing → `TAINTED.md` + report `open_questions`; demonstrates **atomic-fail** |
| `eval-k18-violation/` | K18 anti-pattern: a curate decision with empty `evidence` → `TAINTED.md` + report `open_questions`; demonstrates **partial-success-continue** |

### Happy-path fixtures (correct shapes for the metric-curation feature)

| Fixture | Demonstrates |
|---|---|
| `eval-emp-resolve-001/` | STEP 0 `authoritative_file` resolution (role `电商客服` → `customer-service-ecommerce`) + STEP 1.2 auto-skip (candidate count in range ⇒ `selected_metrics == candidate_metrics`); report carries `employee_provenance` + empty `metric_curation` |
| `eval-curate-001/` | STEP 1.2 curation under `mode=always`: removes `tool_call_correctness` (evidence-cited) + adds `bid_clause_completeness` (low-confidence, user-confirmed); `curate_log` audited per K18 |

New evaluation runs go into directories named with a fresh `eval_id`.

## Tainted run lifecycle

A run becomes tainted when:

- a HARD RULE in SKILL.md is violated (e.g. agent authored an orchestrator script under `./runs/`)
- a K-rule fails the input-gate validator at the next step (e.g. K12 missing artifact, K14 rejected trace, K16 duplicate `scored_at`)

When tainted:

1. The agent stops scoring on the offending output
2. A `TAINTED.md` is written under the run directory (or skill root if no run dir exists yet) explaining the violation
3. STEP 9 EvaluationReport.open_questions surfaces the violation with severity `critical`
4. Numerical scores from the tainted scope MUST NOT be cited as definitive in the final report

A tainted run is not auto-deleted. To recover: create a new `eval_id`, fix the upstream cause, re-run from the earliest affected step. Tainted directories should be retained for audit until reviewed.
