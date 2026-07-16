# ADR 0008: Use a Recoverable Fixed-Boundary Local-State Deletion

- Status: Accepted
- Date: 2026-07-16

## Context

M7 requires a user-visible way to forget Tooltail's durable local product memory. This is not a File Apprentice skill: the learned action vocabulary deliberately contains no delete primitive, and a model, SkillSpec, plan, folder grant, or user-supplied path must never select application-maintenance targets.

Deleting a live SQLite database can also cross a crash boundary. Removing a database or sidecar without durable intent could leave the next launch unable to distinguish an intentional reset from damage. Conversely, silently replacing an invalid database with empty state would destroy evidence and hide corruption.

Safe labs, user files, incomplete rehearsal work, Capsule exports, and separately copied research exports are outside the product-memory deletion boundary. Internal research data has separate consent and deletion semantics under ADR 0007.

## Decision

Provide a visible two-step Home action. The first step issues an in-memory random request ID valid for five minutes and displays fixed deleted/preserved category lists. The final button is enabled only after the user types the exact case-sensitive phrase `DELETE LOCAL STATE`. The request is single-use.

Refuse deletion while a Tooltail-owned operation or teaching observation is active. Before product-state removal, durably revoke the current folder grant if one exists and invoke the separate research-store deletion. A failure in either precondition retains the product database.

The deletion service accepts no target path, pattern, model output, SkillSpec, plan, or grant. It derives only these fixed slots from the configured `%LOCALAPPDATA%\Tooltail\state\tooltail.db` layout:

- `tooltail.db`;
- `tooltail.db-wal`;
- `tooltail.db-shm`;
- `local-state-deletion.intent.json`.

Require an absolute local fixed-volume layout, exact `state/tooltail.db` names, existing Tooltail application/state directories, and non-reparse ancestry. Revalidate each existing file immediately before `File.Delete`. Never enumerate deletion targets, recursively remove a directory, follow a link, or remove a safe lab/export/rehearsal path.

Before removing SQLite, write a bounded closed-version intent with `CreateNew`, write-through, and flush. It contains only schema version, random request ID, UTC request time, and a SHA-256 application-root fingerprint—not the raw root. Cancellation is honored before intent creation and not afterward.

At startup, check the intent before SQLite initialization. A valid intent completes the remaining idempotent exact-file removals, removes the intent last, and then permits creation of fresh state. Missing application directories are a normal first launch. An invalid, oversized, linked, misplaced, or unreadable intent/layout fails closed: Tooltail does not open, replace, or automatically repair the database.

## Consequences

Positive:

- durable product memory can be explicitly forgotten without adding learned/general delete;
- the UI states exactly what is deleted and preserved;
- every deletion prefix has deterministic startup handling;
- a corrupt or attacker-modified intent cannot silently reset state;
- safe labs and portable exports remain available after identity/skill memory is removed.

Costs:

- deletion closes the running app and may require a subsequent launch after an interrupted attempt;
- per-lesson, per-skill, and per-receipt deletion are not yet implemented;
- files intentionally preserved outside SQLite need separate user review/removal;
- this is application maintenance under the current user account, not a security boundary against that account.

## Rejected alternatives

### Reuse File Apprentice delete or Undo

Rejected because a general learned delete violates the closed v0.1 action language, while Undo can remove only exact unchanged artifacts proven created by one execution.

### Delete the entire Tooltail application directory

Rejected because recursive deletion would erase safe labs, exports, and ambiguous recovery residuals and would make link/race review substantially harder.

### Delete SQLite without an intent marker

Rejected because a crash could leave an ambiguous partial reset and no safe startup decision.

### Rename or replace corrupt state with an empty database

Rejected because it silently discards evidence and conflicts with the existing fail-closed migration policy.
