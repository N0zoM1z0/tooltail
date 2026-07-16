# ADR 0009: Import Capsules Only into Pristine State and Rebind through a New Draft

- Status: Accepted
- Date: 2026-07-17

## Context

The v1 Companion Capsule already exports provider-independent identity, immutable SkillSpecs, lineage, and bounded evidence summaries without grants, approvals, plans, journals, receipts, physical roots, credentials, or raw content. M5 deliberately shipped a strict preview parser while native import remained disabled. The final v0.1 acceptance checklist requires import and rebind without allowing a Capsule to restore authority or trusted execution evidence.

Import is a durable local-state mutation with two distinct risks. Merging untrusted histories into an active companion can collide with skill IDs, grants, receipts, or current projections. Replacing a companion piecemeal can crash after identity changes but before all versions are stored. Rebinding an imported SkillSpec in place would rewrite immutable history and could make an old approval or source grant appear current.

## Decision

Keep v1 as one bounded JSON document. The file picker only supplies read intent. The feature reader rejects non-local, non-fixed, reparse, non-file, wrong-suffix, malformed, and over-16-MiB inputs; captures exact bytes once; strictly parses and semantically validates them; and presents the companion name, version count, byte count, and SHA-256 before any state change. The second explicit action imports those exact in-memory bytes. A changed file cannot change the reviewed import.

Require one linear history per skill: versions start at 1, increase contiguously, point to the immediately prior version, and have nondecreasing UTC creation times. Continue rejecting unknown schema/action/enum/property values, duplicate versions, missing parents, unsafe content-policy flags, mismatched source bindings, invalid compatibility, and oversized collections.

Native import is allowed only while SQLite contains exactly one pristine first-run companion and no lease, grant, snapshot, lesson, example, skill, evidence, plan, approval, execution, journal event, receipt, agent run, or domain event. In one `BEGIN IMMEDIATE` transaction:

1. revalidate that exact pristine state and expected companion ID;
2. replace only that empty companion row with the Capsule identity/presentation;
3. insert all validated skill histories in deterministic order;
4. project only the latest version of each history as current;
5. commit all or roll back all.

Every imported version is persisted as `Stale`, with no approval time and no grant row. Exported lifecycle and evidence counts remain display-only Capsule facts; they do not become local trust evidence. Import creates no WindowLease, ResourceGrant, plan, approval, execution, receipt, Undo eligibility, or file effect. Existing/non-pristine state fails closed; v0.1 does not merge, overwrite, rename IDs, or ask the model to resolve conflicts.

Rebind is a separate explicit action after the user creates a new exact folder grant. It reads one current imported Stale skill, creates version `n + 1` as a parent-linked `Draft`, changes only `applicability.rootGrantId`, and records a semantic diff containing only `scope_binding`. The imported parent remains immutable and Stale. The new Draft then uses the existing shared-executor rehearsal, fresh canonical plan, exact fingerprint approval, execution, verification, receipt, and Undo path. Rebind itself grants no execution authority.

The Desktop exposes separate keyboard-accessible preview, import, grant, rebind, and rehearse controls. Persisted imported Stale state projects as `needs_input`, including after restart. Preview or import failure never falls through to a success body state.

## Consequences

- Capsule continuity can preserve identity and skill lineage on a fresh Tooltail state without importing authority.
- Atomic pristine-only replacement avoids partial histories and collision-resolution ambiguity.
- Users must explicitly delete/reset product memory before importing into a non-pristine installation; v0.1 offers no merge.
- Each imported skill requires a new grant and one explicit rebind action; a multi-skill Capsule can therefore contain both rebound Drafts and still-Stale histories until the user finishes.
- The v1 schema does not change. Compatibility freeze hashes and external migration semantics remain unchanged.
- File selection is read-only and local; a future archive, drag/drop import, cloud sync, merge, or multi-companion selector requires a new threat review and ADR.

## Rejected alternatives

### Import grants, approvals, or exported lifecycle

Rejected because Capsule provenance is not local authority or verified evidence on the receiving machine.

### Rewrite imported SkillSpecs to the current grant during import

Rejected because it hides a material executable change, destroys immutable source history, and skips an explicit user decision.

### Merge into an active companion

Rejected for v0.1 because ID, lineage, receipt, current-version, and deletion semantics are ambiguous and substantially expand the recovery surface.

### Persist versions one at a time through the ordinary store API

Rejected because cancellation, disk failure, or a late conflict could leave a partially imported identity/history.

### Trust the selected path again at commit time

Rejected because the file may change after preview. Commit is bound to the exact reviewed in-memory bytes and displayed SHA-256.
