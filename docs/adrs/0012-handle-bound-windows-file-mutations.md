# ADR 0012: Use Handle-Bound Windows File Mutations

- Status: Accepted
- Date: 2026-07-17

## Context

The v0.1 File Apprentice already binds plans to a canonical root, relative paths, source identity/hash, destination absence, approval fingerprints, and a current `ResourceGrant`. That prevents many stale-plan and broad-authority failures, but a path-based final check followed by a later path-based mutation still leaves a race against another same-user process. A competitor can replace an ancestor, source, or destination after the final path probe and before the effect.

The hardest case is `ensure_directory`. Undo may remove a directory only when the journal proves that Tooltail created that exact entry and it remains empty and unchanged. If `ensure_directory` succeeds merely because a directory exists after the call, Tooltail cannot truthfully claim ownership.

## Decision

Production Windows execution uses a two-phase mutation contract:

1. `Prepare` opens and validates the fixed grant root, existing ancestors, source entries, and destination parents by native handle. It returns a prepared effect while retaining those handles. Preparation records no mutation and may happen only after the step intent is durable.
2. The executor reloads current authority from `PermissionGateway` after preparation. A revocation committed before that final read blocks the effect and records failure/recovery markers. A revocation committed after that read is concurrent with an already authorized effect; the step still verifies through journal and postcondition evidence before success.
3. `Execute` performs exactly one native effect while the prepared handles remain live, then verification captures evidence before those handles are disposed.

The Windows implementation is owned by `Tooltail.Platform.Windows`. It uses native Windows file APIs directly: `NtCreateFile` with a retained parent `RootDirectory` for relative create/open, `SetFileInformationByHandle` for rename and disposition, no shell, no overwrite, and no learned delete. It rejects reparse traversal, root/ancestor/source changes, destination collisions, cross-root or cross-volume moves, and ambiguous native failures.

`ensure_directory` and `copy_file` must create the destination with precise create-new semantics. For `ensure_directory`, success requires the native create disposition to report that the directory was newly created by this call. An existing directory, a competitor-created directory, or an ambiguous post-create identity failure is not ownership evidence. Undo removal can use only verified mutation evidence whose destination identity matches the final snapshot and whose `destinationCreatedByThisCall` flag is true.

Windows file identity uses `GetFileInformationByHandleEx(FileIdInfo)` when native identity is needed. Persisted and planned Windows identities use the `win32-volume-v2:` and `win32-file-v2:` prefixes. Older pre-alpha `win32-*` v1 64-bit identities are intentionally not upgraded in place; if such state exists, grant restoration, plan revalidation, or receipt proof fails closed and the user must re-grant and re-plan.

The portable fixture engine remains path based only inside Tooltail-owned test/fixture roots. Desktop composition must inject the Windows handle-bound engine; architecture tests reject portable fallback in production.

## Consequences

- The mutation boundary is linearized: durable intent precedes preparation, the final permission read precedes exactly one native effect, and verification/journal markers determine success.
- A same-user competitor can still cause denial or recovery-required outcomes, but cannot make Tooltail claim ownership of a directory it did not create.
- `PermissionGateway` is not a filesystem transaction. It is the authorization line immediately before the prepared native effect.
- Handle sharing choices intentionally prefer failing under contention over silently following a replaced path.
- Some pre-alpha persisted identity material becomes incompatible. This is acceptable before public alpha and safer than treating weaker ReFS-unsafe identities as equivalent.
- Native tests must include races that happen after the final permission check and immediately before the native effect.

## Rejected alternatives

### Add one more path revalidation

Rejected because another path check still leaves a path mutation race and cannot prove create-new ownership.

### Process-wide mutexes or app-local locks

Rejected because a same-user competitor is outside Tooltail and can ignore those locks.

### Transactional NTFS

Rejected because TxF is not a modern supported product boundary and would add recovery semantics outside the v0.1 design.

### Treat existing destination directories as successful `ensure_directory`

Rejected because Undo would then be unable to distinguish a Tooltail-created directory from later user or competitor work.

### Use the portable fixture engine in Desktop

Rejected because the fixture engine is for deterministic owned workspaces and does not provide Windows handle-bound mutation safety.
