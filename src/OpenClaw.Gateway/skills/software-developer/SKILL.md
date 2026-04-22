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

When a user provides an `ncrew-ontology` projection for this skill to consume:

- Read projection contracts from `contracts/projections/ncrew-ontology/` when a bound contract exists for the current topic.
- Use `contracts/projections/ncrew-ontology/contract-index.json` as the first lookup source for topic and target-view selection.
- Current multi-topic skeleton includes `skill-loading/`, `task-execution/`, `tool-orchestration/`, and `memory-session/` as example domains.

### Projection Routing

When a task can be routed through a bound projection contract, follow this order:

1. Infer the domain topic from the user request.
   - `skill-loading`: requests about skill discovery, config, precedence, overwrite order, or eligibility filtering.
   - `task-execution`: requests about prompt policy, execution guidance, review wording, or implementation constraints.
   - `tool-orchestration`: requests about workflow steps, planner flow, orchestration, or execution preconditions.
   - `memory-session`: requests about memory recall, session management, retention policy, or retrieval boundaries.
2. Score the topic candidates using `contract-index.json`.
   - Start with `topic_scoring` and prefer the highest-scoring `READY` topic.
   - If the top topic scores are too close, surface the ambiguity instead of guessing.
3. Infer the target view from the requested output.
   - Code model, types, guards, implementation structure -> `domain-model`
   - Validation contract, config shape, import/export schema -> `json-schema`
   - Prompt policy, reviewer guidance, constrained reasoning -> `prompt-constraint`
   - Workflow steps, execution graph, gating conditions -> `workflow-contract`
4. Score the target views for the selected topic using `target_view_scoring`.
   - Prefer explicit artifact requests first, then view-specific signals, then the topic's default target view only as a weak fallback.
   - If the top target view scores are too close, surface the ambiguity instead of guessing.
5. Look up the topic and target view in `contract-index.json`.
6. If the exact target view exists and its status is `READY`, read that projection first.
7. If the exact target view does not exist, use the topic's `default_target_view` only when it still supports the user's requested outcome.
8. If no safe target view matches, say that the current bound contracts do not cover the requested output and continue without pretending a projection exists.

### Request Mapping Examples

Use the following examples as concrete routing hints when both the topic and the target view need to be inferred from the user request.

| User request pattern | Topic | Target view | Why |
| --- | --- | --- | --- |
| "实现 skill loading 相关的代码重构" | `skill-loading` | `domain-model` | The request is about implementation structure, domain types, and runtime guards. |
| "给 skill loading 配置生成 JSON Schema" | `skill-loading` | `json-schema` | The request asks for a validation or config contract rather than runtime code. |
| "把 skill loading 流程整理成执行步骤或编排图" | `skill-loading` | `workflow-contract` | The request is about explicit orchestration order and gating conditions. |
| "给实现任务补一份 prompt policy / reviewer guidance" | `task-execution` | `prompt-constraint` | The request is about prompt wording, review constraints, and allowed reasoning paths. |
| "把任务执行约束落成实现对象或策略模型" | `task-execution` | `domain-model` | The request needs execution-oriented objects, associations, and runtime guards. |
| "把任务执行过程拆成 review checkpoint 和 transition" | `task-execution` | `workflow-contract` | The request is asking for step transitions, checkpoints, and blocking preconditions. |
| "给 tool orchestration 生成 planner / workflow contract" | `tool-orchestration` | `workflow-contract` | The request explicitly asks for orchestration flow, planner edges, or execution preconditions. |
| "给 tool orchestration 约束 prompt 术语和推理路径" | `tool-orchestration` | `prompt-constraint` | The request is about prompt-side orchestration rules, not code structure. |
| "把 tool orchestration 的核心对象和路由规则建模" | `tool-orchestration` | `domain-model` | The request is about entities, value objects, enums, and runtime policy. |
| "把 memory/session 的核心对象、retention policy 和 recall boundary 建模" | `memory-session` | `domain-model` | The request is about entities, runtime state boundaries, and implementation policy objects for memory/session behavior. |
| "给 memory/session 的持久化载荷和配置生成 JSON Schema" | `memory-session` | `json-schema` | The request asks for validation and structural contracts rather than executable implementation objects. |
| "给 memory/session 行为补一份 prompt policy / reviewer guidance" | `memory-session` | `prompt-constraint` | The request is about recall boundaries, retention guardrails, and prompt-side clarification policy. |
| "把 memory recall、retention sweep 和 session lifecycle 整理成执行步骤" | `memory-session` | `workflow-contract` | The request emphasizes lifecycle sequencing, cleanup flow, and blocking preconditions for memory/session operations. |

If a request matches the topic but not the output shape clearly, use `target_view_scoring` first and only fall back to the topic's `default_target_view` when no stronger view-specific artifact match exists.

If a request appears to span multiple target views, use the scoring model in `contract-index.json`:

1. Start from `explicit_output_match` to identify the user's named artifact, such as `json schema`, `prompt policy`, or `workflow contract`.
2. Add `strong_signal_match` and `supporting_signal_match` for the target view whose vocabulary appears most directly in the request.
3. Apply `cross_view_conflict_penalty` when another target view has the stronger artifact match.
4. Apply any `within_topic_overrides` for the selected topic.
5. Use `topic_default_view_bonus` only as a weak fallback when the request does not strongly point to another view.
6. If the top two target view scores are still too close, do not guess; surface the ambiguity and ask for clarification.

If a request appears to span multiple topics, use these conflict rules:

1. `task-execution` vs `tool-orchestration`: prefer `tool-orchestration` only when planner flow, execution graph, routing sequence, or gating workflow is the main deliverable; otherwise prefer `task-execution`.
2. `skill-loading` vs `task-execution`: prefer `skill-loading` when the request is about config, precedence, or eligibility behavior itself; prefer `task-execution` when the request is about guidance, review, or execution policy layered on top.
3. `skill-loading` vs `tool-orchestration`: prefer `tool-orchestration` only when the user explicitly wants a workflow or planner artifact; otherwise prefer `skill-loading`.
4. `memory-session` vs `task-execution`: prefer `memory-session` when the request is about recall boundaries, retention semantics, or session-state policy itself; prefer `task-execution` when the request is primarily about implementation guidance or review behavior layered on top.
5. `memory-session` vs `skill-loading`: prefer `memory-session` when the main artifact is about recall, retention, session state, or memory provider behavior; prefer `skill-loading` when the request is about skill discovery, source precedence, load order, or eligibility filtering.
6. `memory-session` vs `tool-orchestration`: prefer `tool-orchestration` only when the requested deliverable is an explicit workflow, graph, or ordered execution sequence; otherwise prefer `memory-session`.
7. If no topic clearly dominates after these checks, surface the ambiguity instead of silently choosing one.

Use the scoring model in `contract-index.json` when the request mixes multiple topic signals:

1. Start from `primary_intent_match` and `explicit_artifact_bonus` to identify the user's main deliverable.
2. Add `strong_keyword_match` and `supporting_keyword_match` for the topic whose vocabulary appears most directly in the request.
3. Apply `cross_topic_conflict_penalty` when another topic has the stronger primary artifact match.
4. If the top two topic scores are still too close, do not guess; surface the ambiguity and ask for clarification.

### Projection Consumption

Once a contract is selected:

- Read the chosen projection file before planning implementation details.
- Only consume fields relevant to the current target, especially `concept_mappings`, `relation_mappings`, `constraint_mappings`, `prompt_projection`, and `delivery_artifacts`.
- Treat `target_path`, `target_kind`, `representation`, and `severity_mapping` as execution constraints, not loose hints.
- Use `delivery_artifacts` to decide the most likely output locations or output shapes.
- Preserve source trace and terminology boundaries from `source_slice`, `source_ids`, and `prompt_projection.source_digest`.

### Blocking Rules

- If `mapping_policy` requires `block_or_escalate`, do not silently continue past unmapped or contradictory items.
- If `open_questions` is non-empty, do not finalize generated output without first surfacing the blocking issue.
- Do not recreate `dropped_items` that the projection has intentionally removed from scope.
- Do not switch to a different topic or target view just because it is easier unless the user request clearly changed.
