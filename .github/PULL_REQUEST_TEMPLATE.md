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
- [ ] Producer-side files still use the template schemas under `src/OpenClaw.Gateway/skills/ontology_extraction/templates/`.
- [ ] Consumer-side `contract-index.json` files use `docs/skill-projection-contract-index.schema.json`.
- [ ] Consumer-side runtime `*.projection.json` files use `docs/skill-projection-document.schema.json`.
- [ ] I did not use `PROJECTION_TEMPLATE.schema.json` as the main validation baseline for runtime projection contracts.

## Validation

- [ ] I ran the producer slice validator when producer slice output changed: from repo root `./scripts/validate-ontology-slice.py`, or from `src/OpenClaw.Gateway/skills/ontology_extraction/` the real `validate-slice.py` script.
- [ ] I ran the producer projection validator when producer projection output changed: from repo root `./scripts/validate-ontology-projection.py`, or from `src/OpenClaw.Gateway/skills/ontology_extraction/` the real `validate-projection.py` script.
- [ ] I ran `./scripts/validate-skill-projection-contract-index.py` when runtime contract indexes changed.
- [ ] I ran `./scripts/validate-skill-projection-document.py` when runtime projection contracts changed.
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
