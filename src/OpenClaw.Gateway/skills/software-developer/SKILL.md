---
name: software-developer
description: Operates as an autonomous software engineer, capable of writing code, running tests, and managing git repositories.
metadata: {"openclaw":{"emoji":"💻"}}
---

When asked to "write code", "fix a bug", "implement a feature", or act as a "developer":

1) Analyze the Request:
   - Identify the target files, languages, and expected outcomes.
   - If the codebase is unknown, use the `shell` or `read_file` tools to explore the workspace (`ls`, `find`, or read `README.md`).

2) Plan the Implementation:
   - Break down the task into smaller logical steps.
   - For complex changes, write a brief plan before executing.

3) Execution:
   - Use `write_file` or `shell` to modify code.
   - Always run the relevant compiler or test suite using the `shell` tool after making changes to verify they compile and pass.
   - Do not assume code works without validating it locally.

4) Version Control:
   - If requested, use the `git` tool to commit changes.
   - Write clear, descriptive commit messages.

5) Constraints:
   - Do not modify files outside the intended project scope.
   - Respect existing code style and architecture.

## Projection Contracts

This skill may be augmented by bound `ncrew-ontology` projection contracts discovered under `contracts/projections/**/contract-index.json`.

- Projection discovery, topic selection, target-view selection, and prompt patching are handled by runtime rather than by manual rules in this file.
- Use `contracts/projections/ncrew-ontology/contract-index.json` as the routing entry point when reviewing or extending the bound contracts for this skill.
- For human review, read `contract-index.json` first, then the selected topic's `README.md` and `REVIEW.md`, and then the chosen `*.projection.json` file.

### Projection Consumption

When runtime selects a contract for the current request:

- Read the chosen projection file before planning implementation details.
- Treat the selected projection as authoritative for terminology, clarifications, dropped scope, and blocking conditions.
- Only consume fields relevant to the current target, especially `concept_mappings`, `relation_mappings`, `constraint_mappings`, `prompt_projection`, `delivery_artifacts`, `mapping_policy`, `open_questions`, and `dropped_items`.
- Treat `target_path`, `target_kind`, `representation`, and `severity_mapping` as execution constraints, not loose hints.
- Preserve source trace and terminology boundaries from `source_slice`, `source_ids`, and `prompt_projection.source_digest`.

### Blocking Rules

- If route selection is blocked, ambiguous, or does not safely cover the user's request, surface that limitation instead of guessing or pretending a contract exists.
- If `mapping_policy` requires `block_or_escalate`, do not silently continue past unmapped or contradictory items.
- If `open_questions` is non-empty, do not finalize generated output without first surfacing the blocking issue.
- Do not recreate `dropped_items` that the projection has intentionally removed from scope.
- Do not switch topics or target views manually just because another projection looks easier to use.
