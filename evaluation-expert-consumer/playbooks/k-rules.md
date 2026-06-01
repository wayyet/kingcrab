# K-rules at a glance

The workflow contract (`contracts/projections/ontology_extraction/metric-selection/metric-selection.workflow-contract.projection.json`) defines K1–K18 in `constraint_mappings[]`. This table is the human-readable index.

> Note on K-rule namespaces. The workflow contract owns K1–K18 (workflow preconditions). The two prompt-constraint projections each carry their own internal K1–K5 namespaces (scoring-judgement: baseline=50 / red-line / rare-high / evidence / report-completeness; metric-selection: declared-dimensions / weight-sum / registry-only / role+scenario match). The role-catalog projection carries its own K1–K4 (filename / duplicate / inheritance / canonicalization-step0-only). When a SKILL.md or playbook says "K9 violation", it always means the **workflow contract** namespace unless explicitly prefixed (e.g. "scoring-judgement K3" or "role-catalog K2").

## Workflow contract K-rules (the canonical numbers)

| # | Name | Owning step(s) | Severity | What it forbids / requires | Failure handling |
|---|---|---|---|---|---|
| K1  | `MetricRegistryNonEmpty` | PRE | critical | `metric_registry` must be non-empty after PRE filesystem scan | block_or_escalate |
| K2  | `EnrichTestCasesAlwaysRuns` | STEP 2 | high | STEP 2 runs unconditionally, even on fully curated cases that already declare `applicable_metrics` | block_or_escalate |
| K3  | `FanOutIsUniformAndPerMetric` | STEP 4 | critical | Exactly one LLM invocation per `(test_case, metric)`; batching forbidden | block_or_escalate |
| K4  | `AggregationAndRedLineAreDeterministic` | STEP 5 / 6 / 7 | critical | LLM forbidden in 5/6/7; STEP 7 red-line is pure code; STEP 9 LLM cannot overwrite `triggered` | taint run |
| K5  | `SynthesizedCasesIsolatedFromCatalog` | STEP 1.5 | high | STEP 1.5 outputs go to `./runs/<eval-id>/synthesized-cases/`; never into `./test-cases/` | block_or_escalate |
| K6  | `ReportLayerIsTwoTier` | STEP 8 / 9 | high | STEP 9 must not run before every applicable scenario has a ScenarioReport; STEP 9 links, does not inline | block_or_escalate |
| K7  | `ReportNumericFieldsAreCopiesNotRecomputations` | STEP 8 / 9 | critical | All numeric fields in reports are byte-copies of upstream `MetricScore` / STEP 5 / 6 / 7 outputs | regenerate report |
| K8  | `NoAdhocOrchestratorScripts` | all | critical | Host agent MUST NOT author any executable file (`.py` / `.sh` / `.ts` / `.js` / `.mjs` / `.ipynb` / Makefile / `.cmd` / `.ps1`) outside the skill-creation-time whitelist (`./runtime-drivers/<id>/`, `./runtime-*/<id>/`); orchestration runs as agent tool-call rounds in conversation | taint run + write `TAINTED.md` |
| K9  | `SelectedMetricsRoleFilteredAtStep1` | STEP 1 + STEP 1.2 | critical | **(rewritten)** STEP 1 produces `candidate_metrics` (deterministic, machine-verifiable role filter); STEP 1.2 produces `selected_metrics = (candidate_metrics − removed) ∪ added`. Copying the full registry into `candidate_metrics` forbidden; both `candidate_metrics` and `dropped_metrics` persisted; when STEP 1.2 skips/fails, `selected_metrics == candidate_metrics` | taint run |
| K10 | `InlineEnrichedCasesMatchPersistedFiles` | STEP 2 / 3 / 4 | critical | For every tc: inline `evaluation_context.enriched_test_cases[*].applicable_metrics` MUST be byte-identical to the persisted `./runs/<eval-id>/enriched-cases/<tc_id>.json` AND a subset of `selected_metrics` | taint run |
| K11 | `UserScenarioConsultationBeforeSynthesis` | STEP 1.5 | high | When `test_case_status=='missing'`, agent MUST ask the user FIRST before SOP synthesis; consultation persisted to `evaluation_context.user_consultation_log`; Tier-2 cases carry `reliability_caveat` | flag in EvaluationReport.open_questions |
| K12 | `StepIntermediateArtifactsPersisted` | STEP 5 / 6 / 7 | critical | STEP 5 → `aggregated_metric_scores.json`; STEP 6 → `dimension_scores.json`; STEP 7 → `red_line_check.json`; all three written before next step | taint run; STEP 9 lists missing artifact in `open_questions` |
| K13 | `DimensionScoresKeysMatchSelectedMetrics` | STEP 6 | critical | `dimension_scores.json` keys MUST equal `{ m.parent_dimension for m ∈ selected_metrics }`; no fabricated dimensions | taint run |
| K14 | `DriverProtocolLoopComplete` | STEP 3 | critical | Strict alternation `send → read evaluatee_turn → send \| end`; never close stdin before writing `end`; whenever `decision.next_utterance` is non-empty, agent MUST send it before ending; only valid early-stop reasons are `should_continue==false`, `turn_index+1 >= effective_max_turns`, driver `error` event | reject trace; taint run |
| K15 | `StopConditionsAlignedWithExpectedToolCalls` | STEP 1.5 / 2 (design) + STEP 3 (runtime) | high | (design) `stop_conditions.success` MUST not be satisfiable when must-criticality tools never fired; required info handoffs covered; success describes actionable closure. (runtime) Simulator MUST NOT declare `goal_achieved` while the customer's required-info utterance is still locked inside `next_utterance`. | revise case before STEP 3; runtime trip rejects trace via K14 4th clause |
| K16 | `ScoringMustInvokeEvaluatorLLMPerCaseMetric` | STEP 4 | critical | One real LLM call per `(test_case, metric)`; `scored_at` is real per-call timestamp; duplicate `scored_at` strings across files = batch fabrication; reasoning MUST quote concrete substrings of trace | taint run; STEP 9 lists every duplicate-timestamp pair as `critical` |
| K17 | `EmployeeResolutionProvenanceRequired` | STEP 0 | critical | `employee.employee_provenance` MUST exist with valid `source` ∈ {authoritative_file, user_dialog, inferred_fallback} + `reliability` ∈ {high, low}; `reliability=low` requires non-empty `caveat`; report copies it byte-identically; inferred-fallback findings use "indicative" not "definitive"; only STEP 0 may write `employee.role.role_id` | taint run (**atomic**: any taint-action failure fails the whole run) |
| K18 | `CurateDecisionsMustBeAudited` | STEP 1.2 | critical | Every `removed`/`added` decision has a `curate_log` entry with ≥1 evidence citation naming a source field + quoting a verbatim substring; `len(curate_log)==len(removed)+len(added)`; bounds `max_metrics` / `min_dimensions_covered` enforced | taint run (**partial-success**: continue + record failed actions; total failure halts) |

## Severity ladder

| Severity | Effect on run |
|---|---|
| critical | Run is tainted; subsequent steps stop or guard against the tainted scope; STEP 9 surfaces in `open_questions`; `TAINTED.md` is dropped |
| high | Run continues with caveat; STEP 9 surfaces in `open_questions`; language is downgraded ("indicative" / "preliminary") |

## How to find the authoritative text

For each K-rule, `metric-selection.workflow-contract.projection.json → constraint_mappings[i].notes` is authoritative. Playbooks paraphrase; if a playbook and the contract diverge, the contract wins and the playbook MUST be patched.
