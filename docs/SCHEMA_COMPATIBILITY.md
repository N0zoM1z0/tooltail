# Frozen v1 Schema Compatibility Policy

## Freeze boundary

The five v1 schemas and their five bundled example surfaces are frozen for the bounded v0.1 alpha line:

- `skill-spec.schema.json`;
- `scope-lease.schema.json`;
- `agent-event.schema.json` plus its JSONL example;
- `companion-capsule.schema.json`;
- `research-event.schema.json`.

`eng/schema-freeze-v1.json` stores normalized-LF SHA-256 for all ten files. `Tooltail.ReleaseAudit` fails when a byte-semantic change is made without first updating the reviewed freeze manifest. Updating a hash alone is not a compatibility decision: the same change must update the runtime parser/validator, example, contract and mutation tests, `DATA_AND_PROTOCOLS.md`, `BUILD_STATUS.md`, and an ADR when executable or authority semantics change.

## Reader policy

- v1 readers accept only `schemaVersion: "1.0"`.
- Unknown versions, properties, discriminators, actions, enum values, invalid UTC values, and out-of-bound collections fail closed.
- `additionalProperties: false` means an added optional field is not backward compatible for existing strict readers.
- Adding an enum/discriminator is not backward compatible for existing strict readers.
- Renaming, removing, changing a field type, widening executable behavior, changing canonical ordering, or changing a fingerprint input requires a new schema version.
- A v2 reader may read v1 only after an explicit compatibility adapter and golden migration fixture exist. A v1 reader never guesses how to read v2.

The only pre-freeze exception is documented in ADR 0007: `clarification_completed` and `approval_decided` were added to research-event v1 before an M6 handoff or external export. That exception is closed and cannot be repeated after this freeze.

## Contract-specific migration behavior

### SkillSpec

Executable meaning is immutable. A compatible imported historical version is revalidated and remains bound to its original hash. An unsupported executor/schema combination is marked Stale; Tooltail does not rewrite a signed or receipted historical specification. Any new primitive requires a new schema version, ADR, threat-model update, planner/executor/verifier/receipt/Undo coverage, and explicit user rebind/rehearsal.

### WindowLease

Leases are short-lived context records and contain no mutation authority. Unknown versions or capabilities are rejected. No migration may copy a lease into ResourceGrant authority.

### Agent event

Unknown provider events may be counted and discarded before normalization, but unknown normalized events are rejected. An adapter drift cannot change core body semantics; it disconnects visibly while the deterministic simulator remains available.

### Companion Capsule

Import parsing is bounded and authority-free. Imported skills remain unbound/Stale and require a new ResourceGrant, explicit rebind, and rehearsal. Grants, approvals, journals, receipts, physical roots, credentials, and executable authority are never migrated from a capsule.

### Research event

The local JSONL reader requires one consented study ID and a contiguous sequence per session. Unknown versions or event values stop append/export. Research data has no authority migration path. A user may preview/export the old file or explicitly delete it; Tooltail never silently converts an ambiguous research record.

## Persistence schemas

SQLite uses separately checksummed ordered migrations. Unknown future migrations, checksum drift, integrity failure, or missing objects enter read-only recovery; startup never substitutes a fresh empty identity. A database migration and an external JSON schema version are separate decisions and must not be coupled implicitly.
