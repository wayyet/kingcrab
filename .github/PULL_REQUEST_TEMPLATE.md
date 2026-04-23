# Summary

- What changed?
- Which skill, topic, or contract path is affected?
- Is this change primarily `producer`-side, `runtime`-side, or both?

## Scope

- [ ] Producer-side slice or projection template changes
- [ ] Runtime `contract-index.json` changes
- [ ] Runtime `*.projection.json` changes
- [ ] Loader / resolver behavior changes
- [ ] Documentation-only changes

## Schema Boundary Checklist

- [ ] I explicitly classified this PR as `producer`, `runtime`, or `both` before changing schema paths or validation flow.
- [ ] Producer-side files still use the template schemas under `src/OpenClaw.Gateway/skills/ncrew-ontology/templates/`.
- [ ] Consumer-side `contract-index.json` files use `docs/skill-projection-contract-index.schema.json`.
- [ ] Consumer-side runtime `*.projection.json` files use `docs/skill-projection-document.schema.json`.
- [ ] I did not use `PROJECTION_TEMPLATE.schema.json` as the main validation baseline for runtime projection contracts.

## Validation

- [ ] I ran `./scripts/validate-ontology-slice.ps1` or the Python equivalent when producer slice output changed.
- [ ] I ran `./scripts/validate-ontology-projection.ps1` or the Python equivalent when producer projection output changed.
- [ ] I ran `./scripts/validate-skill-projection-contract-index.ps1` or the Python equivalent when runtime contract indexes changed.
- [ ] I ran `./scripts/validate-skill-projection-document.ps1` or the Python equivalent when runtime projection contracts changed.
- [ ] If loader or resolver behavior changed, I also ran the relevant focused tests.

## Runtime Consumption Check

- [ ] I verified whether the changed fields are actually consumed by `SkillLoader` or `SkillProjectionResolver`.
- [ ] If a field is schema-allowed but not runtime-consumed, I documented it as advisory-only instead of implying it is already in the control plane.
- [ ] I did not treat “schema accepts the field” as equivalent to “runtime uses the field”.

## Documentation

- [ ] I updated at least one entry-point document when the usage boundary changed.
- [ ] If this PR changes the schema boundary workflow, I checked `docs/skill-projection-schema-migration-checklist.md`.
- [ ] If this PR changes runtime contract behavior, I checked whether `docs/skill-projection-contracts-schema.md` also needs an update.

## Notes

- Relevant commands run:
- Risks or follow-up work:
