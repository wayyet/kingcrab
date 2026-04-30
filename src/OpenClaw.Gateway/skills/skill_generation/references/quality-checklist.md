# Skill Generation Quality Checklist

Run these checks before writing a generated skill package.

## Input And Extraction

- [ ] Input type is classified as conversation, upload, or mixed.
- [ ] Source summary is recorded in `references/source-digest.md`.
- [ ] Every capability has a source or extraction note.
- [ ] Ambiguous capabilities are listed as pending instead of silently finalized.

## SkillSpec

- [ ] `name` is normalized and slug-safe.
- [ ] `description` is non-empty and specific.
- [ ] At least one trigger exists.
- [ ] At least one capability exists.
- [ ] Every capability has inputs, outputs, and fallback.
- [ ] Boundaries include what the skill will not do.

## Projection Consumer Contract

- [ ] Generated `SKILL.md` includes the Projection Contracts section.
- [ ] `contracts/projections/ontology_extraction/contract-index.json` exists.
- [ ] The index `consumer_skill` matches generated skill `name`.
- [ ] Every READY view points to an existing projection file.
- [ ] Projection document contains `prompt_projection`, `delivery_artifacts`, `dropped_items`, and `open_questions`.
- [ ] `open_questions` is empty before marking a projection READY.

## Safety

- [ ] No plaintext token, secret, password, API key, connection string, or credential is written.
- [ ] No files outside `skills/<skill_slug>/` are written.
- [ ] Main agent behavior constraints are excluded from generated business skill content.

## Final Report

- [ ] `references/quality-report.md` records passed checks and any skipped checks.
- [ ] `technical_artifact` lists all generated files.
- [ ] `user_summary` groups新增、更新、跳过、失败。
