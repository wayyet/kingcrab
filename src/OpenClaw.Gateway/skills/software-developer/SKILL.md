---
name: software-developer
description: Operates as an autonomous software engineer, capable of writing code, running tests, and managing git repositories.
metadata: {"openclaw":{"emoji":"💻"}}
---

# software-developer

When asked to "write code", "fix a bug", "implement a feature", or act as a "developer":

1) Identify the task boundary:
   - Identify the target files, languages, expected behavior, and validation path.
   - If the request is ambiguous, narrow the required implementation outcome before editing.

2) Load the right inputs:
   - Prefer user-provided files, nearby implementation context, and runtime-selected projection contracts.
   - If the codebase is unknown, use `read_file`, search tools, or terminal commands via `run_in_terminal` to explore the workspace.

3) Execution:
   - Break down the task into smaller logical steps when the change is non-trivial.
   - Implement only the code, tests, schema changes, or workflow changes this skill is responsible for.
   - Use `apply_patch` for file edits and `run_in_terminal` for focused commands that validate or support the change.
   - Always run the relevant compiler or test suite using `run_in_terminal`, `runTests`, or another narrow validation tool after making changes to verify they compile and pass.
   - Do not assume code works without validating it locally.
   - Preserve important terminology, constraint mappings, and downstream traceability that affect correctness.

4) Validate before finalizing:
   - Check that the final change still respects the selected scope, mapped constraints, and blocked items.
   - Surface uncertainty, blocked routes, or unmapped requirements instead of guessing.

5) Version Control:
   - If requested, use `run_in_terminal` for git commands and `get_changed_files` for change inspection.
   - Write clear, descriptive commit messages.

6) Constraints:
   - Do not modify files outside the intended project scope.
   - Respect existing code style and architecture.

## Projection Contracts

This skill may be augmented by bound `ontology_extraction` projection contracts discovered under `contracts/projections/**/contract-index.json`.

- Use `../ontology_extraction/templates/CONSUMER_SKILL_PROJECTION_SECTION.md` as the shared minimal template for consumer skills.
- Projection discovery, route selection, and prompt patching are handled by runtime rather than by manual rules in this file.
- For human review, read `contract-index.json` first, then the selected topic's `README.md` and `REVIEW.md`, and then the chosen `*.projection.json` file.

### Projection Consumption

- Read the chosen projection file before planning implementation details.
- Only consume the projection fields and target views this skill actually supports, especially `concept_mappings`, `relation_mappings`, `constraint_mappings`, `delivery_artifacts`, `mapping_policy`, `open_questions`, and `dropped_items`.
- Treat the selected projection as authoritative for terminology, clarifications, dropped scope, and blocking conditions.

### Blocking Rules

- If route selection is blocked, ambiguous, or does not safely cover the request, surface that limitation instead of guessing.
- If `mapping_policy` requires `block_or_escalate`, or `open_questions` is non-empty, do not finalize the output before surfacing the issue.
- Do not recreate items listed in `dropped_items`.

## Skill-Specific Constraints

- Supported deliverables: code, tests, schema, workflow
- Supported projection types: domain-model, json-schema, workflow-contract
- Supported projection fields beyond the shared minimum: `concept_mappings.target_path`, `concept_mappings.target_kind`, `constraint_mappings.severity_mapping`, `delivery_artifacts.path`
- Local exclusions: do not invent unsupported APIs, do not bypass mapped constraints, and do not modify files outside the intended implementation scope

## References

- `../ontology_extraction/templates/CONSUMER_SKILL_PROJECTION_SECTION.md`: shared minimal `Projection Contracts` section
- `../ontology_extraction/templates/NEW_CONSUMER_SKILL_CHECKLIST.md`: post-copy checklist for trimming unsupported fields and placeholders
- `../ontology_extraction/references/PROJECTION_CONSUMPTION_GUIDE.md`: how consumer skills should consume projection contracts
- `../ontology_extraction/references/CONSUMER_PROJECTION_LAYOUT_GUIDE.md`: where to place local bound projection files
