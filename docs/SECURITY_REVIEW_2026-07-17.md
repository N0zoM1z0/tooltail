# Security Review Packet: 2026-07-17

## Scope

This packet tracks the P1 security review request for the File Apprentice mutation boundary, including path validation, permission, journal ordering, recovery, and Undo removal. The implementation under review is the current handle-bound Windows mutation work on 2026-07-17.

## Review status

| Review | Reviewer | Status | Notes |
| --- | --- | --- | --- |
| Internal adversarial design/code review | independent Codex subagent | COMPLETED initial pass | Not a qualified external human review. Counts as internal review evidence only. |
| Internal adversarial re-review after fixes | independent Codex subagent | COMPLETED internal pass | Confirmed SR-2026-07-17-06 ABI fix and regression; documentation overclaim on native race coverage was narrowed. This is still not a qualified external human review. |
| Qualified independent security review | external reviewer | NOT RUN | Required before any public-alpha assurance claim. |

## Initial findings

| ID | Area | Severity | Finding | Resolution |
| --- | --- | --- | --- | --- |
| SR-2026-07-17-01 | path/mutation | High | Path-based mutation left a same-user race after final checks. | Added ADR 0012 and a Windows handle-bound two-phase native mutation engine. |
| SR-2026-07-17-02 | Undo removal | High | `ensure_directory` ownership was not precise enough for later internal removal proof. | `ensure_directory` now requires native create-new evidence and `destinationCreatedByThisCall = true`. |
| SR-2026-07-17-03 | identity | High | 64-bit `BY_HANDLE_FILE_INFORMATION` file IDs are not sufficient for ReFS uniqueness. | Windows native identity now uses `FileIdInfo` v2 volume/file IDs. |
| SR-2026-07-17-04 | permission | Medium | The grant-revocation linearization point needed an explicit design statement and regression. | ADR 0012 defines final authority semantics; portable regression proves revocation during preparation prevents the prepared effect. |
| SR-2026-07-17-05 | journal/recovery/Undo | Low | Existing append-only journal, recovery, and Undo proof shape was sound but depended on mutation evidence quality. | Verified evidence now includes native mutation identity and create-new ownership. |
| SR-2026-07-17-06 | native ABI | High | `BY_HANDLE_FILE_INFORMATION` modeled `FILETIME` as aligned `long` values, which corrupts timestamp and length offsets. | Replaced with Pack=4 native layout and DWORD-based `FILETIME`; added Windows-host ABI regression comparing handle evidence with `FileInfo`. |

## Required external-review checklist

- Path parsing, root capture, root restore, and reparse handling.
- Native handle access masks, sharing modes, relative creates, rename/disposition calls, and cleanup behavior.
- PermissionGateway authority reload and revocation linearization.
- Journal intent/observed/commit/verified/failure ordering under crash and cancellation.
- Startup recovery classification and refusal to replay ambiguous effects.
- Undo planning and `remove_created_entry` proof for copied files and empty created directories.
- Capsule import/rebind and protected-root persistence interactions with grants.
- Redacted diagnostics, research sink, and package/uninstall boundaries.

Until the external row is completed and any critical/high findings are resolved and re-reviewed, the independent security-review release gate remains **NOT RUN**.
