# Local Data Lifecycle

Tooltail v0.1 is local-first, requires no account, performs no cloud sync, and has no automatic telemetry or upload. This guide describes the implemented Windows data boundaries; it does not imply that SQLite or the desktop process is a sandbox.

## Default locations

For a normal standard-user launch, the application root is `%LOCALAPPDATA%\Tooltail`.

| Location below the application root | Contents | Current lifecycle |
| --- | --- | --- |
| `state\tooltail.db` plus `-wal`/`-shm` | companion identity, grants, lessons/evidence, skill versions, plans, approvals, journals, receipts | retained until explicit whole-memory deletion |
| `Labs\<grant-id>` | synthetic safe-lab files and later file-skill results | preserved by product-memory deletion and uninstall |
| `Exports` | authority-free Companion Capsule JSON | preserved by product-memory deletion and uninstall |
| `Research` | opt-in consent, bounded event JSONL, and internal reviewed exports | absent before opt-in; explicit research deletion disables consent and truncates exact owned artifacts |
| `Rehearsals` | temporary owned rehearsal copies or an ambiguous cleanup residual | removed only after identity-checked successful cleanup; residuals are preserved for inspection |

The `--window-shell-smoke-test` uses a unique process-specific directory under the system temporary root instead of normal user state.

## Retention implemented in v0.1

- Product SQLite records remain durable until the user confirms whole-memory deletion. There is no hidden age-based database purge.
- A production receipt's Undo eligibility is one day in the Desktop workflow. Expiry prevents new Undo authorization but does not silently erase the journal or receipt.
- Raw `FileSystemWatcher` hints are in-memory hints; authoritative baseline/final snapshots and reconciled bounded evidence are persisted.
- Local research is capped at 1,000 events and 8 MiB. It has no automatic upload and no time-based purge; explicit deletion is always available after initialization.
- A successfully cleaned rehearsal workspace is removed through the reviewed owned-workspace path. Unsafe or ambiguous residuals remain visible rather than being recursively erased.

The threat model's per-lesson, per-skill, per-receipt, and automatic retention targets are not yet individual UI actions. Until those are implemented and tested, whole-memory deletion is the only product-state erasure control and public-alpha readiness remains gated by the declared known limitations.

## Export before deletion

The File Apprentice section can export a Companion Capsule containing provider-independent companion identity, validated SkillSpecs, immutable version lineage, and bounded evidence summaries. It contains no physical root, active grant, approval, plan, journal, receipt, Undo authority, credential, raw content, or model transcript. Parsing an exported capsule creates no authority; a future import requires a new grant, explicit rebind, and rehearsal.

Optional research mode has a separate exact JSONL preview and `CreateNew` export. Its internal export remains part of the Research boundary and is truncated by research deletion. A copy the user deliberately places outside Tooltail storage is not removed by Tooltail.

There is no live raw-database export button. A forensic database copy should be made only while Tooltail is closed and must be handled as sensitive local data because it can contain protected physical grant roots and normalized relative filenames.

## Whole-memory deletion

In Home, open **Local data, retention, export, and deletion**:

1. Select **Prepare deletion preview**.
2. Review both the deleted and preserved lists and export a Capsule first if desired.
3. Stop teaching or cancel any active Tooltail-owned operation.
4. Type the exact case-sensitive phrase `DELETE LOCAL STATE` before the five-minute preview expires.
5. Select **Delete local state and exit**.

The workflow revokes the current exact folder grant, deletes/disables the separate local research data, persists a bounded deletion intent, and removes only `tooltail.db`, `tooltail.db-wal`, `tooltail.db-shm`, and the intent marker. It then closes Tooltail. It never removes safe labs, user files, Capsule exports, separately copied research exports, or rehearsal residuals.

## Interrupted deletion and recovery

Every launch checks for `state\local-state-deletion.intent.json` before opening SQLite. A valid intent completes only the fixed remaining file removals and starts fresh. A missing application directory is treated as a clean first launch.

If the intent, fixed layout, ancestry, fingerprint, version, size, or file identity is invalid, Tooltail stops before SQLite initialization and displays a recovery-required error. It does not rename, replace, replay, or silently create an empty database over ambiguous state. Preserve the application root for inspection; do not edit the marker while Tooltail is running.

## Uninstall boundary

The portable v0.1 package does not need elevation or install a service. Removing its program directory must not remove `%LOCALAPPDATA%\Tooltail`; user data is retained unless the user first performs the explicit in-app deletion. Packaging/uninstall verification and exact commands are recorded separately in release evidence and must pass before public-alpha readiness is claimed.
