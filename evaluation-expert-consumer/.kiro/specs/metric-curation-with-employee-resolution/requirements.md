# Requirements Document

## Introduction

This feature extends the `evaluation-expert-consumer` skill with two new workflow steps and one new data layer that together raise the fidelity of the deterministic 11-step evaluation pipeline before scoring begins.

- **STEP 0 — `resolveEmployee`** runs before PRE. The Employee_Resolver produces a structured `employee` object (with `role_id`, `industry`, `job_responsibilities`, `scenarios`) from one of three sources, in a fixed priority order: an authoritative file under a new `./employees/` data layer; an LLM parse of a user dialog narrative followed by mandatory user confirmation; or an LLM-inferred best-guess that carries an explicit reliability caveat. STEP 0 is the only step authorized to canonicalize a free-form `role` string into a `role_id` from the new Role_Catalog.
- **STEP 1.2 — `curateMetrics`** runs after STEP 1's deterministic role filter and before STEP 2's `enrichTestCases`. The Metric_Curator refines the candidate metric set by reasoning over `employee.industry`, `employee.job_responsibilities`, and metric semantic fields (`description`, `tags`, the new `industry` and `responsibility_tags`). The Metric_Curator MAY both remove candidates that pass string-match but are semantically inappropriate and add metrics from the registry that string-match missed. Every removal and addition is evidence-cited and persisted to an auditable curate log. Low-confidence additions go through a user confirmation prompt that mirrors the existing K11 user-consultation pattern.
- **Role Catalog** is a new contract under `role-ontology/role-catalog`, parallel to `metric-ontology` and `testcase-ontology`. The Role_Catalog declares the authoritative roles with `industry`, `responsibility_tags`, optional `parent_role` for inheritance, and `aliases` / `display_names` for canonicalization. The catalog is hot-pluggable through directory drop, with no contract edits.
- **Metric schema additions** add four optional fields (`industry[]`, `responsibility_tags[]`, `complementary_metrics[]`, `exclusive_with[]`) so STEP 1.2 has signal beyond the existing role / scenario / description fields. The additions are backward compatible with the 8 existing metric files.
- **employee.role schema upgrade** turns `evaluation_context.employee.role` from a bare string into an object containing `role_id`, `industry`, `responsibility_tags`, and `level` (employee / supervisor / etc), with backward-compatible allowance for free-form `role_id` when no Role_Catalog entry exists (caveat-tagged through STEP 0 provenance).

K3, K10, K13, and K16 remain unchanged. K9 is rewritten so that `selected_metrics = (candidate_metrics − removed) ∪ added`, with `candidate_metrics` deterministic and `removed` / `added` LLM-authored but auditable. Two new K-rules are introduced: K17 for STEP 0 employee resolution provenance and K18 for STEP 1.2 curate auditability.

The default-passing criteria, the 5 fixed parent dimensions, and the existing fan-out scoring contract (K3 / K16) are preserved without modification. The two new steps are subject to the same tainted-run lifecycle as the existing steps.

## Glossary

- **Employee_Resolver**: The STEP 0 component that produces the structured `employee` object. Runs once per evaluation run, before PRE.
- **Metric_Curator**: The STEP 1.2 component that refines the candidate metric set using LLM reasoning over employee and metric semantic fields. Runs once per evaluation run, after STEP 1 and before STEP 2.
- **Role_Catalog**: The new authoritative role registry published by the `role-ontology/role-catalog` contract. Hot-pluggable through directory drop.
- **Role_Catalog_Entry**: A single role declaration in the Role_Catalog, with `role_id`, `industry`, `responsibility_tags`, optional `parent_role`, `aliases`, and `display_names`.
- **Employee_File**: A JSON file under the new `./employees/` data layer, named `<employee_id>.json`, that declares an employee's `role_id`, `industry`, `job_responsibilities`, and `scenarios`.
- **Employee_Provenance**: The metadata block STEP 0 attaches to the resolved `employee` object, with fields `source`, `reliability`, and optional `caveat`.
- **Candidate_Metrics**: The role-filtered metric subset produced by STEP 1. The previous name `selected_metrics` is reassigned to STEP 1.2 output.
- **Selected_Metrics**: The metric set produced by STEP 1.2 according to the equation `selected_metrics = (candidate_metrics − removed) ∪ added`. Consumed by STEP 2 / STEP 4 in place of the previous STEP 1 output.
- **Curate_Log**: The persisted audit trail of STEP 1.2 decisions, written to `evaluation_context.curate_log[]`. Each entry records one `removed` or `added` decision with evidence citations.
- **Metric_Selection_Policy**: The configuration block governing STEP 1.2 behavior, with fields `mode`, `max_metrics`, `min_dimensions_covered`, `auto_apply_threshold`, and `size_triggers`.
- **Auto_Apply_Threshold**: A confidence value in `metric_selection_policy`. Curate decisions with confidence at or above this threshold apply automatically; decisions below it require user confirmation.
- **EvaluationReport**: The STEP 9 final report produced once per `evaluation_id`, with schema `runtime-schemas/evaluation_report.schema.json`.
- **EvaluationContext**: The runtime aggregate persisted at `./runs/<eval_id>/evaluation_context.json`, schema `runtime-schemas/evaluation_context.schema.json`.
- **Hot_Plug_Rule**: The existing skill convention that adding data is a directory drop with no contract edits. Extended in this feature to `./employees/` and the Role_Catalog directory.
- **Tainted_Run_Lifecycle**: The existing taint mechanism described in `playbooks/tainted-run-lifecycle.md`, including `TAINTED.md` drop and STEP 9 `open_questions` surfacing.
- **K-rule**: A workflow precondition declared in `metric-selection.workflow-contract.projection.json#/constraint_mappings`. Existing K-rules are K1 through K16. This feature introduces K17 and K18 and rewrites K9.

## Requirements

### Requirement 1: STEP 0 Authoritative File Resolution

**User Story:** As an evaluation operator, I want STEP 0 to resolve the evaluatee from a committed Employee_File when one exists, so that production runs use the authoritative employee record without LLM inference.

#### Acceptance Criteria

1. WHEN STEP 0 starts AND `employee_id` is non-empty after trimming whitespace AND a regular file exists at `<employees_dir>/<employee_id>.json` (where `employee_id` is matched case-sensitively, exactly), THE Employee_Resolver SHALL load that file as the authoritative source AND SHALL NOT consult any other source for this resolution.
2. IF `employee_id` is empty, whitespace-only, or contains a path separator (`/` or `\`), THEN THE Employee_Resolver SHALL halt STEP 0 with a `block_or_escalate` outcome identifying `employee_id_invalid` as the cause.
3. WHEN the Employee_Resolver loads an Employee_File, THE Employee_Resolver SHALL set `employee_provenance.source` to the literal value `authoritative_file` and SHALL set `employee_provenance.reliability` to the literal value `high`.
4. WHEN the Employee_Resolver loads an Employee_File, THE Employee_Resolver SHALL populate `employee.role_id`, `employee.industry`, `employee.job_responsibilities`, and `employee.scenarios` from the file fields of the same names.
5. IF the Employee_File fails JSON parse OR fails schema validation against `runtime-schemas/employee.schema.json`, THEN THE Employee_Resolver SHALL halt the run with a `block_or_escalate` outcome identifying the offending file path and validation error category, AND SHALL NOT fall through to other resolution sources.
6. THE Employee_Resolver SHALL read the employees directory path from environment variable `EVALUATION_EMPLOYEES_DIR` when set, otherwise from the default path `./employees/` resolved relative to the orchestrator working directory.

### Requirement 2: STEP 0 User-Dialog Resolution with Confirmation

**User Story:** As an evaluation operator, I want STEP 0 to parse a spoken description of the employee when no Employee_File exists, then confirm the inferred draft with the user before proceeding, so that user-supplied narrative becomes high-fidelity grounding without silent misinterpretation.

#### Acceptance Criteria

1. WHEN STEP 0 cannot find an Employee_File for `employee_id` AND the user has supplied a free-form employee description in the run input that is non-empty after trimming whitespace and contains between 1 and 10,000 characters, THE Employee_Resolver SHALL invoke the host LLM to parse the description into a draft `employee` object containing the fields `role_id`, `industry`, `job_responsibilities`, and `scenarios`, with each field populated as either a parsed value or the explicit marker `unknown` when the description does not provide that information.
2. IF the user-supplied description is empty after trimming whitespace, exceeds 10,000 characters, or the host LLM invocation fails to return a parsable draft within 30 seconds, THEN THE Employee_Resolver SHALL skip the user-dialog path and fall back to the inferred-fallback path defined in Requirement 3, recording the reason for the fallback in `evaluation_context.employee_resolution_log`.
3. WHEN the Employee_Resolver produces a draft `employee` object from a user description, THE Employee_Resolver SHALL display the draft to the user with all four fields (`role_id`, `industry`, `job_responsibilities`, `scenarios`) visible and SHALL request an explicit confirmation response of either `confirm`, `correct`, or `decline` before proceeding.
4. WHEN the user submits a `confirm` response on the displayed draft without modification, THE Employee_Resolver SHALL set `employee_provenance.source` to `user_dialog` and SHALL set `employee_provenance.reliability` to `high`.
5. WHEN the user submits a `correct` response with corrections to one or more fields of the displayed draft, THE Employee_Resolver SHALL apply the corrections to the draft, SHALL re-display the corrected draft with all four fields visible, AND SHALL request an explicit confirmation response again, up to a maximum of 5 correction rounds per `employee_id`.
6. WHEN the user submits a `confirm` response on a corrected draft, THE Employee_Resolver SHALL set `employee_provenance.source` to `user_dialog` and SHALL set `employee_provenance.reliability` to `high`.
7. IF the user submits a `decline` response on any draft, OR fails to submit any response within 120 seconds of a draft being displayed, OR the maximum of 5 correction rounds is reached without a `confirm` response, THEN THE Employee_Resolver SHALL fall back to the inferred-fallback path defined in Requirement 3.
8. THE Employee_Resolver SHALL persist one entry per correction round into `evaluation_context.employee_resolution_log`, with each entry containing the displayed draft, the user response type (`confirm`, `correct`, `decline`, or `timeout`), the user-supplied corrections (when applicable), and the final confirmed draft (when applicable).

### Requirement 3: STEP 0 Inferred Fallback with Caveat

**User Story:** As an evaluation operator, I want STEP 0 to produce a best-guess employee object when neither an Employee_File nor a user response is available, so that the evaluation can proceed while clearly marking the result as low-fidelity.

#### Acceptance Criteria

1. WHEN STEP 0 cannot find an Employee_File AND no user-supplied description is available (or the user-dialog path returned per Requirement 2's fall-back conditions) AND `employee_id` is a non-empty string of 1 to 256 characters, THE Employee_Resolver SHALL invoke the host LLM to produce a best-guess `employee` object based on the available `employee_id` and run context, with each of `role_id`, `industry`, `job_responsibilities`, and `scenarios` populated as either an inferred value or the explicit marker `unknown`.
2. IF the host LLM invocation in criterion 1 fails to return a parsable best-guess object within 30 seconds, THEN THE Employee_Resolver SHALL halt STEP 0 with a `block_or_escalate` outcome identifying `inferred_fallback_llm_failure` as the cause.
3. IF `employee_id` is missing or empty when the inferred-fallback path is reached, THEN THE Employee_Resolver SHALL halt STEP 0 with a `block_or_escalate` outcome identifying `employee_id_required_for_inferred_fallback` as the cause AND SHALL NOT invoke the host LLM.
4. WHEN the Employee_Resolver produces an inferred-fallback `employee` object, THE Employee_Resolver SHALL set `employee_provenance.source` to `inferred_fallback`, SHALL set `employee_provenance.reliability` to `low`, AND SHALL set `employee_provenance.caveat` to the literal value `employee_inferred_no_authoritative_source`.
5. WHEN the Employee_Resolver populates an inferred-fallback `employee` object, THE Employee_Resolver SHALL emit exactly one entry into `evaluation_context.open_questions` that lists every field populated as `unknown` and explicitly states that no Employee_File was found and no user description was confirmed.

### Requirement 4: STEP 0 Provenance Surfacing in EvaluationReport (K17)

**User Story:** As an evaluation reviewer, I want every EvaluationReport to surface the employee resolution source and reliability, so that I can weigh findings against the fidelity of the input.

#### Acceptance Criteria

1. THE EvaluationReport SHALL include a top-level field `employee_provenance` typed as a JSON object with required string fields `source` and `reliability`, and optional string field `caveat` of length at most 1000 characters.
2. THE EvaluationReport's `employee_provenance` field SHALL be byte-identical to `evaluation_context.employee.employee_provenance`, including key ordering and string contents.
3. IF `employee_provenance.reliability` equals `low`, THEN THE EvaluationReport SHALL include the value of `employee_provenance.caveat` as a string entry within `EvaluationReport.open_questions`.
4. IF `employee_provenance.source` equals `inferred_fallback`, THEN THE EvaluationReport SHALL describe findings derived from inferred-fallback employee data using the qualifier `indicative` AND SHALL NOT use the qualifier `definitive` for any such finding.
5. IF `evaluation_context.employee.employee_provenance` is absent, OR is not a JSON object, OR is missing the required fields `source` or `reliability`, OR has any of those required fields typed as non-string, THEN THE EvaluationOrchestrator SHALL apply the Tainted_Run_Lifecycle, write a `TAINTED.md` describing the K17 violation, AND list the violation in `EvaluationReport.open_questions`.
6. WHEN any of the three actions in criterion 5 (apply Tainted_Run_Lifecycle, write `TAINTED.md`, list in `open_questions`) fails, THE EvaluationOrchestrator SHALL fail the entire run, recording a non-success completion status AND emitting no successful EvaluationReport.
7. IF `employee_provenance.reliability` equals `low` AND `employee_provenance.caveat` is absent or an empty string, THEN THE EvaluationOrchestrator SHALL apply the Tainted_Run_Lifecycle, write a `TAINTED.md` describing the missing-caveat K17 violation, AND list the violation in `EvaluationReport.open_questions`.

### Requirement 5: Role Catalog Contract and Data Layer

**User Story:** As a contract owner, I want a Role_Catalog declared as a new `role-ontology/role-catalog` contract with hot-pluggable data, so that authoritative roles can evolve without skill-package edits.

#### Acceptance Criteria

1. THE Role_Catalog contract SHALL be published at `contracts/projections/role-ontology/role-catalog/role-catalog.role-catalog.projection.json`, parallel to the existing metric-ontology and testcase-ontology contracts.
2. THE Role_Catalog SHALL declare a JSON schema for one Role_Catalog_Entry per file, with: required field `role_id` (string of 1 to 64 characters matching `^[a-z0-9-]+$`); required field `industry` (string of 1 to 64 characters matching `^[a-z0-9_]+$`); required field `responsibility_tags` (array of 1 to 32 unique strings each matching `^[a-z0-9_]{1,64}$`); optional field `parent_role` (string equal to a `role_id` of another entry, or `null`); optional field `aliases` (array of 0 to 32 unique strings each of length 1 to 64); optional field `display_names` (array of 0 to 32 unique strings each of length 1 to 128).
3. THE Role_Catalog data layer SHALL load files matching `<roles_dir>/*.role.json`, where `<roles_dir>` defaults to `./role-catalog/` resolved relative to the orchestrator working directory, and is overridable through environment variable `EVALUATION_ROLES_DIR`.
4. WHEN PRE scans the Role_Catalog data layer, THE EvaluationOrchestrator SHALL build a Role_Catalog map keyed by `role_id` AND SHALL persist the map in memory for STEP 0 and downstream steps.
5. IF a Role_Catalog_Entry declares a non-null `parent_role`, THEN THE Role_Catalog loader SHALL inherit `industry` from the parent entry by direct override (child wins when child declares it; otherwise child's `industry` is set to parent's value), AND SHALL inherit `responsibility_tags` by set union with deduplication.
6. IF the inheritance computation in criterion 5 fails because `parent_role` references an absent entry, OR the inheritance chain forms a cycle, OR the inheritance chain depth exceeds 8, THEN THE Role_Catalog loader SHALL load the offending entry without parent inheritance, SHALL emit one entry into `evaluation_context.open_questions` describing the inheritance failure category, AND SHALL allow the run to proceed.
7. THE Role_Catalog data layer SHALL be hot-pluggable through directory drop, with no edits to any `*.projection.json` file required to add a new Role_Catalog_Entry.
8. IF two Role_Catalog_Entry files declare the same `role_id` (case-sensitive equality), THEN THE Role_Catalog loader SHALL retain only the entry from the lexicographically-first filename, SHALL emit one entry into `evaluation_context.open_questions` listing the duplicate `role_id` and the conflicting filenames, AND SHALL allow the run to proceed.
9. IF a `*.role.json` file fails JSON parse OR fails schema validation against the Role_Catalog schema, THEN THE Role_Catalog loader SHALL skip the file, SHALL emit one entry into `evaluation_context.open_questions` listing the offending filename and validation error category, AND SHALL allow the run to proceed.

### Requirement 6: STEP 0 Role Canonicalization

**User Story:** As a workflow author, I want STEP 0 to be the only step authorized to rewrite `employee.role` from free-form text into a canonical `role_id` from the Role_Catalog, so that downstream steps consume one consistent role identifier.

#### Acceptance Criteria

1. WHEN the Employee_Resolver produces an `employee` object, THE Employee_Resolver SHALL match the resolved free-form role string (after trimming whitespace) against `Role_Catalog_Entry.role_id` and against every entry in `Role_Catalog_Entry.aliases`, using case-insensitive exact-string comparison, in catalog iteration order, with first-match-wins tie-breaking.
2. WHEN a Role_Catalog match is found, THE Employee_Resolver SHALL set `employee.role.role_id` to the matched `role_id` exactly as defined in the catalog entry, AND SHALL copy `industry` and `responsibility_tags` from the matched Role_Catalog_Entry into `employee.role.industry` and `employee.role.responsibility_tags`.
3. IF no Role_Catalog match is found AND the resolved free-form role string is non-empty after trimming, THEN THE Employee_Resolver SHALL set `employee.role.role_id` to the trimmed free-form string AND SHALL append the literal value `role_id_no_catalog_entry` exactly once to `employee_provenance.caveat` (deduplicating against any value already present).
4. IF the resolved free-form role string is empty, null, or whitespace-only, THEN THE Employee_Resolver SHALL halt STEP 0 with a `block_or_escalate` outcome identifying `role_string_empty` as the cause.
5. IF any step other than STEP 0 attempts to write to `employee.role.role_id`, THEN THE EvaluationOrchestrator SHALL reject the write, SHALL preserve the prior `employee.role.role_id` value, AND SHALL surface an `unauthorized_role_id_mutation` error identifying the offending step.
6. WHEN STEP 1 reads `employee.role.role_id`, STEP 1 SHALL apply the existing role filter using the canonical `role_id` value as-is without re-normalization, including when the value was set via the free-form fallback in criterion 3.

### Requirement 7: employee.role Schema Upgrade

**User Story:** As a runtime schema owner, I want `evaluation_context.employee.role` to become a structured object with backward-compatible behavior for unmapped free-form roles, so that the new STEP 1.2 has structured signal while existing fixtures continue to load.

#### Acceptance Criteria

1. THE `evaluation_context.employee.role` field SHALL be a JSON object with required string field `role_id` (1 to 256 characters), required string field `industry` (0 to 128 characters; empty string permitted to signal "unset"), required array-of-string field `responsibility_tags` (0 to 32 entries; empty array permitted), and optional string field `level` (1 to 64 characters when present).
2. THE `employee.role.level` field SHALL accept the values `employee`, `supervisor`, `manager`, or any string declared as a recognized level in any `Role_Catalog_Entry`.
3. WHEN `evaluation_context.employee.role` is a bare string in a legacy run input, THE Employee_Resolver SHALL wrap the bare string into the new object form by setting `role_id` to the trimmed string, setting `industry` to the empty string, setting `responsibility_tags` to the empty array, omitting `level`, AND appending `role_id_no_catalog_entry` exactly once to `employee_provenance.caveat`.
4. WHEN `evaluation_context.employee.role` is already a JSON object that validates against the schema in criterion 1, THE Employee_Resolver SHALL leave the object unchanged AND SHALL NOT append any caveat solely on the basis of the wrapping rule.
5. IF `evaluation_context.employee.role` is neither a string nor a JSON object that validates against the schema in criterion 1, THEN THE Employee_Resolver SHALL halt STEP 0 with a `block_or_escalate` outcome identifying `employee_role_invalid_form` as the cause.
6. THE `runtime-schemas/evaluation_context.schema.json` update declaring the new `employee.role` object form SHALL be deployed before the Employee_Resolver applies the legacy-string wrapping rule from criterion 3 to any run input.

### Requirement 8: Metric Schema Semantic Field Additions

**User Story:** As a metric author, I want optional `industry`, `responsibility_tags`, `complementary_metrics`, and `exclusive_with` fields on `metric.json`, so that STEP 1.2 can reason about metric applicability beyond role and scenario.

#### Acceptance Criteria

1. THE `metric.schema.json` SHALL declare exactly four new fields named `industry`, `responsibility_tags`, `complementary_metrics`, and `exclusive_with`, each typed as an array of strings, each marked optional (not present in the schema's `required` list), and each defaulting to an empty array when omitted from a `metric.json` instance.
2. WHEN a `metric.json` instance includes the `industry` field, THE `metric.schema.json` SHALL accept the value only if it is an array of 1 to 32 non-empty strings where each element is either the literal wildcard string `*` (interpreted by consumers as match-all) or an industry identifier matching the pattern `^[a-z0-9_]{1,64}$`, with no duplicate elements.
3. WHEN a `metric.json` instance includes the `responsibility_tags` field, THE `metric.schema.json` SHALL accept the value only if it is an array of 0 to 32 non-empty strings drawn from the same vocabulary as `Role_Catalog_Entry.responsibility_tags`, where each element matches the pattern `^[a-z0-9_]{1,64}$` and no element is duplicated.
4. WHEN a `metric.json` instance includes the `complementary_metrics` field, THE `metric.schema.json` SHALL accept the value only if it is an array of 0 to 32 strings, each conforming to the existing `metric_code` format defined elsewhere in `metric.schema.json`, with no duplicate elements and with no element equal to this metric's own `metric_code`.
5. WHEN a `metric.json` instance includes the `exclusive_with` field, THE `metric.schema.json` SHALL accept the value only if it is an array of 0 to 32 strings, each conforming to the existing `metric_code` format, with no duplicate elements, no element equal to this metric's own `metric_code`, and no element also present in this metric's `complementary_metrics` array.
6. IF a `metric.json` instance provides a value for `industry`, `responsibility_tags`, `complementary_metrics`, or `exclusive_with` that violates the array type, element pattern, size bounds, duplication rule, or self-reference rule defined in criteria 1 through 5, THEN THE `metric.schema.json` validator SHALL reject the instance with a validation error that names the offending field and the specific rule violated, AND SHALL leave the metric registry unchanged.
7. WHEN the metric loader validates each of the 8 existing metric files (which omit all four new fields) against the updated `metric.schema.json`, THE metric loader SHALL report all 8 files as valid without requiring edits to those files, AND SHALL treat each omitted field as an empty array for downstream consumers.

### Requirement 9: STEP 1 Output Renamed to candidate_metrics (Updated K9)

**User Story:** As a workflow author, I want STEP 1 to produce `candidate_metrics` rather than `selected_metrics`, so that STEP 1.2 has a deterministic input distinct from the LLM-curated output that STEP 2 / STEP 4 consume.

#### Acceptance Criteria

1. WHEN STEP 1 completes, THE EvaluationOrchestrator SHALL persist `evaluation_context.candidate_metrics` as the deterministic role-filtered subset of `metric_registry`, where a metric is included if and only if its role attribute matches the active evaluation role per the existing STEP 1 role-match rule, such that identical `metric_registry` and active-role inputs always produce an identical `candidate_metrics` set.
2. WHEN STEP 1 completes, THE EvaluationOrchestrator SHALL persist `evaluation_context.dropped_metrics` so that every metric in `metric_registry` not present in `candidate_metrics` appears exactly once, with each entry retaining the metric's identifier and carrying `drop_reason = role_mismatch`.
3. WHEN STEP 1 completes, THE EvaluationOrchestrator SHALL maintain the invariant `len(candidate_metrics) + len(dropped_metrics) == len(metric_registry)` AND SHALL ensure no metric identifier appears in both `candidate_metrics` and `dropped_metrics`.
4. WHEN STEP 1.2 is configured with mode `never`, OR WHEN STEP 1.2 has not yet executed in the current evaluation run, THE EvaluationOrchestrator SHALL set `evaluation_context.selected_metrics` equal to `evaluation_context.candidate_metrics` before STEP 2 begins, so STEP 2 has a stable input.
5. THE EvaluationOrchestrator SHALL enforce the updated K9 rule by declaring the equation `selected_metrics = (candidate_metrics − removed) ∪ added` AND SHALL expose `candidate_metrics` such that an automated verifier can recompute it from `metric_registry` and the active role using the STEP 1 role-match rule and confirm set equality with the persisted value.
6. IF `candidate_metrics` is empty AND `metric_registry` is non-empty after STEP 1 completes, THEN THE EvaluationOrchestrator SHALL apply the `block_or_escalate` action defined by the existing K-rules, SHALL emit a halt indication identifying `candidate_metrics_empty` as the cause, AND SHALL NOT proceed to STEP 1.2 or any subsequent step.
7. IF `metric_registry` is empty, THEN THE EvaluationOrchestrator SHALL apply the `block_or_escalate` action defined by the existing K-rules, SHALL emit a halt indication identifying `metric_registry_empty` as the cause, AND SHALL NOT proceed to STEP 1, STEP 1.2, or any subsequent step, preserving the existing K1 precondition under the renamed STEP 1 output.

### Requirement 10: STEP 1.2 Curate Algorithm

**User Story:** As an evaluation operator, I want STEP 1.2 to refine the candidate metric set by removing semantically inappropriate matches and adding registry metrics that string-match missed, so that the final `selected_metrics` set reflects the employee's actual industry and responsibilities.

#### Acceptance Criteria

1. WHEN STEP 1 has completed AND the active `metric_selection_policy.mode` is `auto` or `always`, THE Metric_Curator SHALL start after STEP 1 has completed AND finish before STEP 2 begins.
2. WHEN the Metric_Curator runs, THE Metric_Curator SHALL receive non-null typed inputs `candidate_metrics`, `metric_registry`, `employee.industry`, `employee.role.responsibility_tags`, and `employee.job_responsibilities`.
3. WHEN the Metric_Curator runs, THE Metric_Curator SHALL produce two arrays `removed[]` and `added[]`, where `removed[]` is a subset of `candidate_metrics`, `added[]` is a subset of `(metric_registry − candidate_metrics)`, the two arrays share no `metric_code`, AND `len(removed) + len(added) <= 2 * len(metric_registry)`.
4. WHEN the Metric_Curator completes successfully, THE EvaluationOrchestrator SHALL compute `selected_metrics = (candidate_metrics − removed) ∪ added` AND SHALL persist `selected_metrics` in `evaluation_context.selected_metrics`.
5. WHEN `metric_selection_policy.mode` is `never`, THE Metric_Curator SHALL NOT run AND `selected_metrics` SHALL equal `candidate_metrics`. The `never` mode SHALL take precedence over the STEP 1 completion trigger in criterion 1.
6. WHEN `metric_selection_policy.mode` is `always`, THE Metric_Curator SHALL run regardless of size triggers, including when the size-trigger evaluation itself fails or throws an error.
7. WHEN `metric_selection_policy.mode` is `auto`, THE Metric_Curator SHALL run only when at least one of the configured size triggers in `metric_selection_policy.size_triggers` evaluates to true on the `candidate_metrics` set.
8. IF the Metric_Curator fails, returns malformed output, or violates the subset constraints in criterion 3, THEN THE EvaluationOrchestrator SHALL fall back to setting `selected_metrics` equal to `candidate_metrics`, SHALL emit one entry into `evaluation_context.open_questions` identifying `metric_curator_failure` and the failure category, AND SHALL allow the run to proceed.
9. IF any of the inputs required by criterion 2 is null or absent when the Metric_Curator is scheduled to run, THEN THE EvaluationOrchestrator SHALL skip the Metric_Curator, SHALL set `selected_metrics` equal to `candidate_metrics`, AND SHALL emit one entry into `evaluation_context.open_questions` identifying the missing input.
10. IF the Metric_Curator does not return its output within 30 seconds of being invoked, THEN THE EvaluationOrchestrator SHALL apply the failure-handling behavior in criterion 8.

### Requirement 11: STEP 1.2 Curate Decision Auditability (K18)

**User Story:** As an evaluation reviewer, I want every removed and added decision to cite specific evidence and persist to a curate log, so that LLM curation is fully auditable.

#### Acceptance Criteria

1. WHEN the Metric_Curator emits one removal decision, THE Metric_Curator SHALL record one Curate_Log entry containing `decision: "removed"`, `metric_code`, `evidence` array (at least one citation), and `confidence` score in the closed range `[0.0, 1.0]`.
2. WHEN the Metric_Curator emits one addition decision, THE Metric_Curator SHALL record one Curate_Log entry containing `decision: "added"`, `metric_code`, `evidence` array (at least one citation), and `confidence` score in the closed range `[0.0, 1.0]`.
3. THE Curate_Log entry's `evidence` array SHALL contain at least one citation, where each citation names a source field (one of `employee.industry`, `employee.job_responsibilities`, `employee.role.responsibility_tags`, `metric.description`, `metric.tags`, `metric.industry`, `metric.responsibility_tags`, `metric.complementary_metrics`, or `metric.exclusive_with`) AND quotes a verbatim (case-sensitive, contiguous), at-least-one-character substring of that field's value.
4. WHEN the Metric_Curator finishes emitting all curation decisions, THE EvaluationOrchestrator SHALL persist the Curate_Log array at `evaluation_context.curate_log`, AND `len(curate_log) == len(removed) + len(added)`, AND every `metric_code` appearing in `removed` or `added` SHALL appear in exactly one Curate_Log entry.
5. THE new K18 rule SHALL declare that every entry in `removed[]` and `added[]` MUST have a corresponding Curate_Log entry with non-empty `evidence`, with a citation that satisfies criterion 3, AND that any decision violating these constraints marks the run as tainted.
6. IF any Curate_Log entry's `evidence` array is empty, OR a decision in `removed[]` / `added[]` has no corresponding Curate_Log entry, OR a citation fails the source-field-and-substring check in criterion 3, THEN THE EvaluationOrchestrator SHALL apply the Tainted_Run_Lifecycle, write a `TAINTED.md` describing the K18 violation category, AND list the offending decision in `EvaluationReport.open_questions`.
7. WHEN at least one but not all of the three actions in criterion 6 (apply Tainted_Run_Lifecycle, write `TAINTED.md`, list in `open_questions`) succeeds, THE EvaluationOrchestrator SHALL accept partial state, SHALL continue with the evaluation, AND SHALL record the failed actions in `evaluation_context.open_questions`.
8. IF none of the three actions in criterion 6 succeeds, THEN THE EvaluationOrchestrator SHALL halt the run with a non-success completion status AND SHALL NOT emit a successful EvaluationReport.

### Requirement 12: metric_selection_policy Configuration

**User Story:** As an evaluation operator, I want a configurable `metric_selection_policy`, so that I can control STEP 1.2 invocation and bound the final `selected_metrics` set.

#### Acceptance Criteria

1. THE `evaluation_context.metric_selection_policy` field SHALL be an object with required field `mode` and optional fields `max_metrics`, `min_dimensions_covered`, `auto_apply_threshold`, and `size_triggers`.
2. THE `metric_selection_policy.mode` field SHALL accept exactly the values `auto`, `always`, and `never`, with default value `auto` when the field or the entire `metric_selection_policy` block is omitted.
3. THE `metric_selection_policy.max_metrics` field SHALL be an integer in the closed range `[1, 100]` with default value `8`, representing the upper bound on `len(selected_metrics)`.
4. THE `metric_selection_policy.min_dimensions_covered` field SHALL be an integer in the closed range `[1, 5]` with default value `1`, representing the minimum number of distinct `parent_dimension` values that must appear across `selected_metrics`.
5. THE `metric_selection_policy.auto_apply_threshold` field SHALL be a number in the closed range `[0.0, 1.0]` with default value `0.7`, representing the confidence threshold at or above which a Metric_Curator add decision is auto-applied without user confirmation.
6. THE `metric_selection_policy.size_triggers` field SHALL be an object with optional integer fields `candidate_count_lower_bound` (default `3`) and `candidate_count_upper_bound` (default `15`), each in the closed range `[0, 200]`.
7. WHILE `metric_selection_policy.mode` is `auto` AND STEP 1 has completed, THE Metric_Curator SHALL run when `len(candidate_metrics) < size_triggers.candidate_count_lower_bound` OR `len(candidate_metrics) > size_triggers.candidate_count_upper_bound`; otherwise THE Metric_Curator SHALL NOT run AND THE EvaluationOrchestrator SHALL set `selected_metrics` equal to `candidate_metrics`.
8. IF the post-curation `selected_metrics` set has strictly more than `max_metrics` entries, THEN THE Metric_Curator SHALL block the run with `block_or_escalate` AND SHALL emit one Curate_Log entry citing the observed `len(selected_metrics)` value and the configured `max_metrics` value.
9. IF the post-curation `selected_metrics` set covers strictly fewer distinct `parent_dimension` values than `min_dimensions_covered`, THEN THE Metric_Curator SHALL block the run with `block_or_escalate` AND SHALL emit one Curate_Log entry citing the observed dimension count and the configured `min_dimensions_covered` value.
10. WHEN `metric_selection_policy.mode` is `always`, THE Metric_Curator SHALL run after STEP 1 completion regardless of the values of `candidate_metrics` or `size_triggers`, subject only to the failure handlers in Requirement 10 criteria 8–10.
11. WHEN `metric_selection_policy.mode` is `never`, THE Metric_Curator SHALL be skipped AND THE EvaluationOrchestrator SHALL set `selected_metrics` equal to `candidate_metrics`.
12. WHEN `evaluation_context.metric_selection_policy` is omitted entirely OR any of the optional fields is omitted, THE EvaluationOrchestrator SHALL apply the defaults declared in criteria 2 through 6 for every omitted field.

### Requirement 13: STEP 1.2 User Confirmation for Low-Confidence Adds

**User Story:** As an evaluation operator, I want low-confidence add decisions to require user confirmation, so that the Metric_Curator does not silently inject metrics the LLM is uncertain about.

#### Acceptance Criteria

1. WHEN one Curate_Log entry has `decision: "added"` AND `confidence < metric_selection_policy.auto_apply_threshold`, THE Metric_Curator SHALL display to the user the metric identifier, the supporting `evidence` array, and the `confidence` value, AND SHALL request explicit user confirmation before including the metric in `selected_metrics`.
2. WHEN the user confirms the proposed addition, THE Metric_Curator SHALL include the metric in `selected_metrics` AND SHALL set the Curate_Log entry's `confirmed_by_user` field to the boolean value `true`.
3. WHEN the user declines the proposed addition, THE Metric_Curator SHALL exclude the metric from `selected_metrics` AND SHALL set the Curate_Log entry's `confirmed_by_user` field to the boolean value `false`.
4. WHEN one Curate_Log entry has `decision: "added"` AND `confidence >= metric_selection_policy.auto_apply_threshold`, THE Metric_Curator SHALL include the metric in `selected_metrics` without user confirmation AND SHALL set the Curate_Log entry's `confirmed_by_user` field to the string literal `"auto_applied"`.
5. IF the user fails to submit any response within 300 seconds of a confirmation prompt being displayed, THEN THE Metric_Curator SHALL apply the declined-addition behavior defined in criterion 3 (exclude from `selected_metrics`, set `confirmed_by_user` to boolean `false`), AND SHALL record the timeout in the corresponding `evaluation_context.user_consultation_log` entry.
6. WHEN multiple low-confidence add decisions require user confirmation in the same run, THE Metric_Curator SHALL prompt the user for one decision at a time, in the order the entries appear in `Curate_Log`.
7. THE Metric_Curator SHALL persist every user confirmation prompt and response into `evaluation_context.user_consultation_log` using the same record schema as the K11 user-consultation log.

### Requirement 14: STEP 9 EvaluationReport Surface for Curate Decisions

**User Story:** As an evaluation reviewer, I want the EvaluationReport to surface STEP 1.2 curate decisions, so that I can audit how `selected_metrics` was derived from `candidate_metrics`.

#### Acceptance Criteria

1. THE EvaluationReport SHALL include a top-level field `metric_curation` with subfields `candidate_metrics`, `selected_metrics`, `removed`, `added`, and `policy_snapshot`.
2. THE EvaluationReport's `metric_curation.candidate_metrics` field SHALL be byte-identical to `evaluation_context.candidate_metrics`, AND `metric_curation.selected_metrics` SHALL be byte-identical to `evaluation_context.selected_metrics`, including key ordering and string contents.
3. THE EvaluationReport's `metric_curation.removed` field SHALL contain every `evaluation_context.curate_log` entry whose `decision` equals `removed`, in the original Curate_Log order, with each entry's content byte-identical to the source; `metric_curation.added` SHALL contain every `decision: "added"` entry under the same rules; AND when no entries of either type exist, the corresponding subfield SHALL be the empty array, not absent.
4. IF `evaluation_context.curate_log` contains at least one entry with `confirmed_by_user: false`, THEN THE EvaluationReport SHALL include in `EvaluationReport.open_questions` exactly one description per declined-add entry, where each description identifies the `metric_code` AND copies the `evidence` array byte-identically from the source Curate_Log entry.
5. WHEN `evaluation_context.curate_log` contains no entries with `confirmed_by_user: false`, THE EvaluationReport SHALL NOT include any declined-addition descriptions in `EvaluationReport.open_questions` derived from this requirement.
6. IF the EvaluationReport generator cannot produce the description of declined additions required by criterion 4 due to a technical error, THEN THE EvaluationOrchestrator SHALL fail EvaluationReport generation entirely, SHALL emit no EvaluationReport artifact, AND SHALL surface a STEP 9 metric-curation failure indication in run logs.

### Requirement 15: Hot-Pluggability of New Data Layers

**User Story:** As a contract owner, I want the new `./employees/` and Role_Catalog data layers to follow the existing Hot_Plug_Rule, so that adding a new employee or role is a directory drop.

#### Acceptance Criteria

1. WHEN a new file matching `*.json` is added to the `./employees/` directory between two evaluation runs, THE EvaluationOrchestrator SHALL include the file in its directory scan on the next run AND SHALL load it through the existing Employee_File loader without any edit to a `*.projection.json` file.
2. WHEN a new file matching `*.role.json` is added to the Role_Catalog directory between two evaluation runs, THE EvaluationOrchestrator SHALL include the file in its directory scan on the next run AND SHALL load it through the Role_Catalog loader without any edit to a `*.projection.json` file.
3. THE SKILL.md SHALL document the new `./employees/` and Role_Catalog directories in the Path defaults table, parallel to the existing 4 hot-pluggable data layers, listing default path, override env var, and required filename pattern for each.
4. THE SKILL.md SHALL declare 6 hot-pluggable data layers after this feature lands, replacing the existing "4 hot-pluggable data layers" wording.
5. IF a file in `./employees/` or in the Role_Catalog directory fails to parse or fails schema validation, THEN THE EvaluationOrchestrator SHALL skip that single file, SHALL emit one entry into `evaluation_context.open_questions` listing the offending filename and validation error category, AND SHALL allow the run to proceed using the remaining valid files.

### Requirement 16: Backward Compatibility for Existing Fixtures

**User Story:** As a fixture maintainer, I want the 8 existing metric files, the existing `evaluation_context.json` shape, and the existing `runs/eval-*` fixtures to keep working unchanged, so that this feature does not break historical artifacts.

#### Acceptance Criteria

1. WHEN the metric loader validates each of the 8 existing metric files in `./metrics/` against the updated `metric.schema.json`, THE metric loader SHALL report all 8 files as valid (i.e. schema validation succeeds without edits to those files), because all 4 new fields are optional.
2. WHEN STEP 1 reads a legacy `evaluation_context.json` containing a bare-string `employee.role` AND no object-form `employee.role` is present, THE Employee_Resolver SHALL wrap the bare string into the new object form per Requirement 7, criterion 3; an `employee.role` already in object form SHALL be left unchanged.
3. IF the Employee_Resolver fails to perform the legacy-string wrapping in criterion 2 (for any reason such as malformed string content or write failure), THEN THE EvaluationOrchestrator SHALL halt the run with a `block_or_escalate` outcome identifying `legacy_role_wrap_failed` as the cause, SHALL preserve the legacy file unchanged, AND SHALL require manual migration before re-running.
4. WHEN STEP 2 reads an `evaluation_context.json` containing only `selected_metrics` and not `candidate_metrics`, THE EvaluationOrchestrator SHALL treat the legacy `selected_metrics` as `candidate_metrics`, SHALL copy it to `selected_metrics`, AND SHALL emit exactly one entry in `evaluation_context.open_questions` that identifies the field mapping `legacy_selected_metrics_treated_as_candidate_metrics` and the source file path.
5. IF an `evaluation_context.json` contains both legacy `selected_metrics` (intended as the STEP 1 output) and the new `candidate_metrics` field, THEN THE EvaluationOrchestrator SHALL treat the new `candidate_metrics` as authoritative, SHALL ignore the legacy `selected_metrics`, AND SHALL emit one entry in `evaluation_context.open_questions` identifying `legacy_selected_metrics_ignored_in_favor_of_candidate_metrics`.
6. THE existing `runs/eval-*` fixture directories SHALL be preserved byte-for-byte AND SHALL remain parseable in their original artifact formats for review purposes without modification.

### Requirement 17: Tainted-Run Handling for STEP 0 and STEP 1.2

**User Story:** As a workflow author, I want the two new steps to participate in the existing Tainted_Run_Lifecycle, so that violations of K17 or K18 produce the same observable outcome as violations of K1 through K16.

#### Acceptance Criteria

1. WHEN a K17 violation is detected (missing or invalid `employee_provenance` per Requirement 4 criterion 5, or missing caveat per Requirement 4 criterion 7), THE EvaluationOrchestrator SHALL write a `TAINTED.md` under `./runs/<eval_id>/` whose contents include the rule id `K17`, the offending field path, and the violation subtype; SHALL halt all remaining scoring steps before any numeric score is produced; AND SHALL surface in `EvaluationReport.open_questions` an entry naming `K17`, the offending field, and the violation subtype.
2. WHEN a K18 violation is detected (Curate_Log entry with empty `evidence`, unmatched `removed` / `added` entry, or citation failing the source-field-and-substring check per Requirement 11 criterion 6), THE EvaluationOrchestrator SHALL write a `TAINTED.md` whose contents include the rule id `K18`, the offending Curate_Log entry index and `metric_code`, and the violation subtype; SHALL halt all remaining scoring steps before any numeric score is produced; AND SHALL surface in `EvaluationReport.open_questions` an entry naming `K18`, the offending entry index, and the violation subtype.
3. THE `playbooks/tainted-run-lifecycle.md` document SHALL be updated so that each K17 and K18 recovery procedure covers the trigger, the corrective action, and the resume steps for re-running the affected step in a fresh `eval_id`.
4. THE updated K-rules table in `playbooks/k-rules.md` SHALL list K17 and K18 with severity, owning step, and failure handling fields populated with non-empty values.
5. IF the `TAINTED.md` write in criterion 1 or 2 fails (e.g. filesystem write error), THEN THE EvaluationOrchestrator SHALL emit the violation indication into run logs identifying the rule id, the offending entity, and the write-failure category, AND SHALL still halt all remaining scoring steps.

### Requirement 18: Preservation of Unchanged K-Rules

**User Story:** As a workflow author, I want K3, K10, K13, and K16 to remain unchanged, so that this feature adds capability without weakening existing scoring guarantees.

#### Acceptance Criteria

1. WHEN STEP 4 executes, THE EvaluationOrchestrator SHALL enforce the K3 rule on uniform per-(case, metric) fan-out as currently declared in `metric-selection.workflow-contract.projection.json#/constraint_mappings`, with no edits to that constraint mapping.
2. WHEN STEP 2 produces `enriched_test_cases`, THE EvaluationOrchestrator SHALL enforce the K10 rule that for every entry e in `enriched_test_cases`, `e.applicable_metrics ⊆ selected_metrics`, where `selected_metrics` now refers to the STEP 1.2 output.
3. WHEN `dimension_scores.json` is produced, THE EvaluationOrchestrator SHALL enforce the K13 rule that its key set equals exactly (case-sensitive, exact string equality, no missing or extra or renamed keys) `{ m.parent_dimension : m ∈ selected_metrics }`, where `selected_metrics` now refers to the STEP 1.2 output.
4. WHEN STEP 4 invokes the LLM, THE EvaluationOrchestrator SHALL enforce the K16 rule on per-(case, metric) LLM invocation as currently declared, with no edits to its constraint mapping.
5. THE 5 fixed parent dimensions SHALL remain frozen, meaning the EvaluationOrchestrator SHALL NOT add, remove, rename, or reorder parent dimensions either at definition time or during execution.
6. IF a `metric.parent_dimension` value does not match one of the 5 fixed parent dimensions exactly (case-sensitive equality), THEN THE EvaluationOrchestrator SHALL reject the metric with `block_or_escalate`, SHALL identify the offending value in the error indication, AND SHALL leave `selected_metrics` unchanged.
7. IF the `dimension_scores.json` key set does not match `{ m.parent_dimension : m ∈ selected_metrics }` exactly, THEN THE EvaluationOrchestrator SHALL reject the output with `block_or_escalate`, SHALL identify the mismatched keys in the error indication, AND SHALL NOT persist the `dimension_scores.json` output.
