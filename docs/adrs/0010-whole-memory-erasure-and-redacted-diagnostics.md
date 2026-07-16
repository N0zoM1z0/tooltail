# ADR 0010: Keep Erasure Whole-Memory and Export Only Closed Redacted Diagnostics

- Status: Accepted
- Date: 2026-07-17

## Context

M7 requires explicit data export, deletion, retention disclosure, and a user-previewed diagnostic export. The v1 SQLite model deliberately preserves immutable SkillSpecs, parent lineage, plans, approvals, append-only journal events, receipts, and recovery links. A receipt is meaningful only while its exact skill version, plan fingerprint, journal prefix, and recovery relationships remain inspectable.

The threat model proposed future per-lesson, per-skill, and per-receipt retention controls. Physically deleting one row in v1 is not an isolated operation: foreign-key-restricted plans and receipts depend on skill history, SkillSpecs embed lesson/example provenance, recovery receipts link original steps, and append-only triggers intentionally reject journal/receipt deletion. Updating text fields to placeholders would not erase copies already present in immutable canonical documents and could make body/recovery projections lie.

At the same time, ordinary support diagnostics must not become a path/name/title/content/model-text data sink. Existing application logs are not a sufficiently closed or inspectable export format.

## Decision

For v0.1, keep ADR 0008 whole-product-memory deletion as the only actual product-state erasure operation. It deletes the fixed SQLite/WAL/SHM/intent boundary after two-step confirmation and preserves labs and separately exported files. Do not add per-object SQL deletion, history rewriting, automatic purge, or a misleading “disable equals delete” action. Skill Card Disable and Delete Local History remain visibly disabled with a precise explanation. Revoking a grant remains the way to remove future resource authority; it does not erase history.

Continue to disclose current retention in Home: companion identity, grants, lessons, skills, plans, approvals, journals, and receipts remain until whole-memory deletion. Undo authorization expiry limits future recovery authority but does not silently remove durable evidence. The suggested per-object/automatic retention table remains a future design target, not a current promise. A future implementation requires a new migration/ADR covering dependency closure, receipt validity, recovery behavior, compaction, crash injection, and export semantics.

Implement `tooltail.diagnostic-export/1` as an internal, non-authority JSON document. Its builder accepts only typed body/tool state, stable reason codes, UTC/product version, and bounded aggregate counts for grants, current skill lifecycles, teaching evidence, executions/receipts, recovery candidates, and the separately consented research sink. The input and document contain no field for a path, filename, file content, window title, prompt/transcript/model text, credential, companion ID/name, user, or machine identity.

The builder:

- bounds output to 64 KiB;
- uses closed string enums and a bounded reason-code alphabet;
- sets every sensitive-content policy flag to false;
- rejects unknown properties and invalid enum/count/time/reason values;
- serializes, strictly parses, and value-compares its own output before preview.

Home displays the exact JSON, byte count, and SHA-256 before export. Export revalidates the same exact bytes/hash/document and writes once with `CreateNew` under the Tooltail-owned, non-reparse `Diagnostics` root. It performs no upload, network request, overwrite, deletion, environment dump, or automatic collection. Diagnostic exports are preserved by whole-memory deletion and portable uninstall; the user controls copies outside Tooltail.

## Consequences

- Users have one truthful erasure boundary instead of unsafe partial deletion that could invalidate receipts or recovery.
- Granular deletion and automatic retention remain deliberately unsupported, prominently disclosed, and not represented by enabled controls.
- Support evidence is locally inspectable and content-minimized by type, not by best-effort string redaction.
- Diagnostic counts can reveal rough usage volume; the user must review the exact preview before exporting or sharing it.
- Diagnostic exports are not audit authority, telemetry, research data, or a substitute for the SQLite recovery view.
- No SQLite or external v1 schema migration is required.

## Rejected alternatives

### Cascade-delete one lesson, skill, execution, or receipt

Rejected because it breaks immutable provenance, append-only evidence, plan/receipt integrity, and recovery relationships.

### Replace sensitive values inside historical JSON

Rejected because canonical fingerprints and immutable evidence would no longer verify, while duplicated values could remain elsewhere.

### Treat disable or revocation as deletion

Rejected because stopping future authority does not erase durable local history.

### Export ordinary logs or the SQLite database

Rejected because they can contain raw paths, relative filenames, titles, identities, or other material outside the closed diagnostic contract.

### Automatically upload diagnostics

Rejected because v0.1 is local-first and telemetry-free by default. Sharing the reviewed export is an explicit external user action.
