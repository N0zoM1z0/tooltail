# Local Data Lifecycle

Tooltail v0.1 is local-first, requires no account, performs no cloud sync, and has no automatic telemetry or upload. This guide describes the implemented Windows data boundaries; it does not imply that SQLite or the desktop process is a sandbox.

## Default locations

For a normal standard-user launch, the application root is `%LOCALAPPDATA%\Tooltail`.

| Location below the application root | Contents | Current lifecycle |
| --- | --- | --- |
| `state\tooltail.db` plus `-wal`/`-shm` | companion identity, grants, lessons/evidence, skill versions, plans, approvals, journals, receipts | retained until explicit whole-memory deletion |
| `Labs\<grant-id>` | synthetic safe-lab files and later file-skill results | preserved by product-memory deletion and uninstall |
| `Exports` | authority-free Companion Capsule JSON | preserved by product-memory deletion and uninstall |
| `Diagnostics` | user-previewed closed redacted diagnostic JSON | created only on explicit export; preserved by product-memory deletion and uninstall |
| `Research` | opt-in consent, bounded event JSONL, and internal reviewed exports | absent before opt-in; explicit research deletion disables consent and truncates exact owned artifacts |
| `Rehearsals` | temporary owned rehearsal copies or an ambiguous cleanup residual | removed only after identity-checked successful cleanup; residuals are preserved for inspection |

The `--window-shell-smoke-test` uses a unique process-specific directory under the system temporary root instead of normal user state.

## Retention implemented in v0.1

- A user-selected canonical grant root is stored only in the existing bounded protected-root blob using Windows current-user DPAPI. Safe-lab roots are derived instead. DPAPI reduces plaintext path exposure but does not defend against another process running as the same user; database copies remain sensitive.
- Product SQLite records remain durable until the user confirms whole-memory deletion. There is no hidden age-based database purge.
- A production receipt's Undo eligibility is one day in the Desktop workflow. Expiry prevents new Undo authorization but does not silently erase the journal or receipt.
- Raw `FileSystemWatcher` hints are in-memory hints; authoritative baseline/final snapshots and reconciled bounded evidence are persisted.
- Local research is capped at 1,000 events and 8 MiB. It has no automatic upload and no time-based purge; explicit deletion is always available after initialization.
- A successfully cleaned rehearsal workspace is removed through the reviewed owned-workspace path. Unsafe or ambiguous residuals remain visible rather than being recursively erased.

ADR 0010 deliberately keeps per-lesson, per-skill, per-receipt, and automatic retention targets out of v0.1: independently deleting immutable provenance, append-only receipts/journals, or recovery links would invalidate trusted history. Skill Card maintenance actions are visibly disabled rather than implying erasure. Whole-memory deletion is the only product-state erasure control.

## Export before deletion

The File Apprentice section can export a Companion Capsule containing provider-independent companion identity, validated SkillSpecs, immutable version lineage, and bounded evidence summaries. It contains no physical root, active grant, approval, plan, journal, receipt, Undo authority, credential, raw content, or model transcript. Import is available only over the unique empty first-run identity: preview shows exact bytes/hash without mutation, commit atomically stores every version Stale, and each skill requires a new grant, explicit parent-linked rebind Draft, and rehearsal. Existing state is never merged or overwritten.

Optional research mode has a separate exact JSONL preview and `CreateNew` export. Its internal export remains part of the Research boundary and is truncated by research deletion. A copy the user deliberately places outside Tooltail storage is not removed by Tooltail.

Redacted diagnostics have a separate exact JSON/SHA-256 preview and `CreateNew` export. The closed document contains only typed states, stable reason codes, and counts; it has no raw path/name/title/content/model/user/machine field and no uploader. Diagnostic exports are preserved by whole-memory deletion, so remove external or Tooltail-owned copies explicitly if they are no longer wanted.

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

The verified portable v0.1 ZIP does not need elevation and installs no service, registry entry, startup task, updater, or uninstall executable. Removing its extracted program directory does not remove `%LOCALAPPDATA%\Tooltail`; user data, Capsule exports, and diagnostic exports are retained unless managed explicitly. The marker-bound removal fixture launches the packaged apphost, deletes only its newly extracted program sibling, and proves a separate local-data sentinel remains byte-identical. Exact commands, package contract, and current unsigned limitations are in [`PORTABLE_PACKAGE.md`](PORTABLE_PACKAGE.md).
