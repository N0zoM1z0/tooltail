# ADR 0011: Preview and Confirm Existing-Folder Grants with a Protected Root

- Status: Accepted
- Date: 2026-07-17

## Context

The v0.1 outcome requires a Windows evaluator to grant one local fixture folder through an explicit picker and confirmation. The shipped safe-lab path proved the closed `ResourceGrant` and complete apprentice loop, but it created a new Tooltail-owned directory and therefore did not satisfy the existing-folder selection experience.

An arbitrary selected path is untrusted input. Treating a picker result as authority would skip review, while storing the canonical physical root as ordinary SQLite text would expose a sensitive path unnecessarily. A restart must still reconstruct the exact root and compare its stable volume/entry identity with the immutable grant. If decryption or identity reconstruction fails, the active grant must not become usable or impossible to revoke.

## Decision

Add a keyboard-accessible Windows `OpenFolderDialog` action that supplies read intent only. Selection captures only the root's canonical path metadata, fixed-volume status, non-reparse ancestry, and stable root identity. It does not enumerate contents, start teaching, create a grant, approve a plan, or infer authority from a `WindowLease`.

Show a separate five-minute in-memory preview containing the exact canonical root, expiry, and closed capabilities: enumerate, read metadata/content hash, ensure directory, rename, same-root move, and copy. The second explicit confirmation re-captures the same canonical root immediately, compares root identity and canonical spelling with Windows-aware semantics, refuses another live grant, and only then issues one seven-day `LocalFolderGrant`. No delete, overwrite, content edit, shell, script, network, cross-volume move, global input, plan approval, or background execution is added.

Persist the canonical root only as Windows current-user DPAPI ciphertext in the existing bounded `protected_canonical_root` blob. DPAPI uses product/version entropy and UI-forbidden mode; machine scope is not used. Ciphertext is bounded to 64 KiB and clear/native buffers are zeroed. DPAPI is privacy-at-rest minimization, not a sandbox or defense against another process already running as the same user.

On restart, decrypt, re-run the complete path/root probe, and require the immutable root identity to match. A malformed ciphertext, different user, moved/replaced root, reparse ancestry, missing directory, or probe failure makes all teaching/planning/rehearsal/execution/Undo controls unavailable. The exact persisted grant remains visibly active and can still be revoked by matching its grant ID, companion, root identity, and protected-root bytes; revocation reads or mutates no selected file.

The original Tooltail-owned safe lab remains the deterministic evaluator/default path. It needs no stored physical root because its location is derived from the fixed application root plus grant ID. Existing-folder confirmation creates no fixture files and makes no promise that arbitrary contents form a compilable lesson; normal snapshot bounds and safe failure reasons still apply.

## Consequences

- The required picker/confirmation authority experience exists without broadening the executor or SkillSpec language.
- Selection and preview are visibly non-authoritative; confirmation is bound to a fresh exact root identity.
- Selected physical roots are not stored as plaintext application rows, but are recoverable only for the same Windows user profile.
- Copying state to a different user/profile leaves grants unusable but revocable; it never silently rebinds authority.
- Existing-folder usability and comprehension still require the attended first-launch and participant evidence gates.

## Rejected alternatives

### Treat picker acceptance as the grant

Rejected because a picker expresses selection intent, not approval of the exact capability set and expiry.

### Store the canonical root as plaintext

Rejected because the existing protected blob was designed to avoid unnecessary raw-path storage and a Windows current-user protection boundary is available without a new package.

### Restore by path without stable identity

Rejected because a replaced folder or introduced link could inherit old authority after restart.

### Automatically revoke when restoration fails

Rejected because startup should not silently mutate authority history. File work fails closed while the user retains an explicit exact revoke action.
