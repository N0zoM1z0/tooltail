# Security and Privacy Threat Model

## 1. Security objective

Tooltail is a local desktop application that observes user-authorized context and performs reversible file effects. Its core security promise is:

> No learned procedure can gain more authority than the user explicitly granted, and no approved plan can silently become a materially different execution.

The body/tether is a legibility mechanism. Enforcement comes from capability checks, closed action vocabularies, path validation, approval binding, journaling, and verification.

## 2. Assumptions

- Tooltail runs as a standard Windows user and does not elevate.
- The local user account and Windows kernel are trusted.
- Other same-user processes may be buggy or malicious and can race file operations.
- File names, metadata, file contents, agent event payloads, imported capsules, and model output are untrusted.
- Model output is nondeterministic and can be prompt-injected.
- GitHub-hosted CI is not an interactive desktop security test environment.
- v0.1 supports only local fixed-drive folder grants and a closed file action set.

## 3. Protected assets

- user files inside and outside granted roots;
- file names, paths, metadata, and contents;
- learned SkillSpecs and provenance;
- execution approvals and journals;
- companion identity and capsule;
- Codex prompts, code, command output, and credentials;
- local database integrity;
- user understanding of active authority;
- undo/recovery material.

## 4. Trust boundaries

1. User <-> WPF UI.
2. WPF UI <-> Application services.
3. Application services <-> Permission Gateway.
4. Permission Gateway <-> Windows/file APIs.
5. Raw demonstration/agent data <-> normalized contracts.
6. Compiler output <-> Skill Validator.
7. Planner output <-> Approval.
8. Approved plan <-> Safe Executor.
9. Desktop process <-> child agent process/JSONL.
10. Local state <-> exported/imported capsule.

## 5. Threats and controls

### T1. Scope-confusion attack

**Threat:** the UI suggests a window-bound scope while effects use ambient process authority.

**Controls:** separate WindowLease/ResourceGrant; inspector disclosure; mandatory Permission Gateway; effect-level grant checks; explicit durable folder-grant revocation in both Home and Inspector; user study comprehension gate. Revocation reloads and terminally persists the exact current grant before requesting cooperative cancellation, deletes no resource, disables future plan/rehearsal/execution/Undo actions, and remains interruptively visible after restart.

### T2. Path traversal and canonicalization bypass

**Threat:** `..`, device paths, UNC, alternate streams, odd separators, trailing dot/space semantics, or case behavior escapes a grant.

**Controls:** relative paths only; deterministic full-path resolution against fixed root; boundary-aware containment; reject UNC/device/ADS; reserved-name validation; adversarial path corpus. First-run safe labs are newly named grant-ID directories below the already captured Tooltail application-data root; setup uses only fixed relative fixture names, `CreateNew`, and no cleanup or overwrite.

### T3. Reparse point and link redirection

**Threat:** a junction/symlink redirects an in-scope path out of scope, including after approval.

**Controls:** reject reparse points on every path component; do not follow links during enumeration; revalidate immediately before effect; reject changes after plan fingerprinting. Safe-lab application/Labs/session roots are captured separately, and every absent directory/file binding is revalidated immediately before its one-time creation.

### T4. TOCTOU file replacement

**Threat:** source or destination changes after plan approval.

**Controls:** file identity/metadata/hash fingerprints; canonical persisted-plan readback before the decision; single-use production approval; current persisted skill/grant reads at every execution and verification boundary; approval invalidation; no overwrite; stop on mismatch.

### T5. Destructive or irreversible skill

**Threat:** compiler/model produces delete, overwrite, shell, execution, or external action.

**Controls:** closed JSON discriminator set; no arbitrary expressions; no delete in SkillSpec; no shell path in executor; schema and architecture tests; fail unknown actions closed. Undo's internal `remove_created_entry` is separately typed and can target only an unchanged file or empty directory proven by the journal to have been created by the exact execution, after a fresh approved inverse plan and immediate revalidation.

### T6. Prompt injection through observed data

**Threat:** a file name, document, or agent payload instructs an LLM compiler to expand scope or perform actions.

**Controls:** deterministic compiler in v0.1; later model sees structured untrusted data and has no tools; fixed schema; output validator; model cannot grant, approve, or execute; content-derived instructions are never policy.

### T7. Malicious/malformed agent JSONL

**Threat:** oversized lines, terminal escapes, schema confusion, secret-rich payloads, or crafted event order causes leakage or incorrect body state.

**Controls:** bounded line size; strict parser; conservative mapping; content discard; escape/sanitize display strings; correlation/ordering rules; unknown events ignored and counted; adapter isolation.

### T8. Private Codex state access

**Threat:** integration reads undocumented sessions, prompts, or credentials.

**Controls:** documented stdout JSONL only; processes Tooltail launches; source scan/architecture test forbids known private-state access; no environment dump; `--ephemeral` considered only when it does not break user expectations.

### T9. Local IPC impersonation

**Threat:** another process connects to an IPC endpoint and sends commands/events.

**Controls:** no IPC unless required; named pipe with current-user ACL; random per-launch capability token; protocol/version validation; local-only; message/time limits; no TCP listener.

### T10. Journal tampering or replay

**Threat:** corrupted state causes an old approval or inverse plan to execute incorrectly.

**Controls:** unique IDs; immutable records; database transactions; plan/grant/skill fingerprints; purpose-bound production versus rehearsal approvals; durable rehearsal preparation before mutation; exact approval consumption with journal open; temporary-grant revocation after owned cleanup; status transition validation; startup reconciliation; undo revalidation; no replay of completed approval. A passing Desktop rehearsal requires a verified receipt, identity-checked cleanup, and persisted retirement of its temporary grant; its separately persisted production plan remains unapproved.

Correction never edits an approved version or rebinds historical authority. It must retain parent evidence, produce a causal executable diff on a target edge case, persist an immutable parent-linked `draft`, and restart rehearsal and exact-plan approval. Earlier receipts remain bound to their original version.

### T11. Undo destroys later user work

**Threat:** user modifies a Tooltail-created target before Undo.

**Controls:** derive a reverse-ordered canonical recovery plan only from a complete verified receipt, its exact journal, and a current authoritative snapshot; persist and display the recovery fingerprint without mutation; require a fresh purpose-bound approval and current grant; reload the recovery plan and both original evidence records before approval; verify canonical containment plus exact entry identity, retained metadata, and file hash before and immediately prior to each effect; require created directories to be empty; never overwrite; use non-recursive removal; journal and verify recovery separately; append the original rollback link only after verification; retain the original receipt; expose residuals and refuse automatic replay on any mismatch.

### T12. Sensitive logging and telemetry

**Threat:** prompts, source, paths, file contents, or credentials enter logs or analytics.

**Controls:** telemetry off/default absent; structured allowlist logging; relative/redacted paths; no raw payloads; diagnostic export preview; automated redaction tests.

### T13. Capsule import attack

**Threat:** oversized JSON, duplicate/conflicting IDs, malicious SkillSpec, compatibility confusion, sensitive content, or imported authority.

**Controls:** bounded single-file UTF-8 reader and strict parser; local fixed-volume/non-reparse file binding; exact-byte SHA-256 preview; document/skill-count limits; strict schema validation; duplicate-ID and nonlinear-history rejection; closed actions; content-policy validation; atomic import only over the exact unique pristine first-run companion; every imported version forced Stale with no approval; permissions, plans, receipts, and evidence trust never imported; new grant plus an explicit parent-linked Draft changing only `scope_binding`; normal rehearsal and exact-plan approval required. A future multi-file archive or merge requires a separate traversal/decompression/conflict threat review.

Desktop export validates and parser-readbacks the complete document before a `CreateNew` write under an identity-checked Tooltail-owned root. It exports no physical root, live grant, approval, plan, journal, receipt, Undo material, credential, or raw content. Native import preview creates no state; commit atomically replaces only an otherwise empty first-run identity and persists all versions Stale. Rebind creates a new Draft against a newly issued grant and cannot reuse imported approval or evidence.

### T14. Elevated target interaction

**Threat:** Tooltail attempts to automate a higher-integrity process or users run Tooltail as administrator to “fix” it.

**Controls:** standard-user manifest; do not request `uiAccess`; reject unsupported elevated targets; never recommend running as administrator; v0.1 file execution uses granted filesystem resources, not input injection.

### T15. Misleading completion

**Threat:** body shows success while verification is incomplete or rollback left residue.

**Controls:** state projection from committed/verified events; explicit partial/failed states; receipt lists residual effects; no optimistic success animation.

The integrated Desktop body receives only closed activity facts and typed tool kinds. Accepted work may show `working`, but verified completion is selected only after the workflow returns durable verified evidence; an unapproved plan or corrected Draft selects `needs_input`, and restart recovery selects failure/inspection rather than replay or success. File Apprentice presentation code and the vector body contain no permission or executor boundary, so a pose or tool prop cannot invoke an effect.

### T16. Research-mode privacy or telemetry drift

**Threat:** study instrumentation silently collects desktop content or identifiers, persists while consent is off, or becomes an automatic upload/analytics channel.

**Controls:** research mode absent/off by default; visible local opt-in; closed versioned event schema with no free-form/raw data fields; random IDs; unexported session salt; bounded session-local tokens; separate Tooltail-owned storage; exact local preview; no network/uploader/analytics SDK; explicit deletion; consent and retention tests; research data never creates authority or changes product behavior. Screen recording and interviews remain outside the product under separate consent.

### T17. Local-state deletion confused deputy or partial reset

**Threat:** a path, model, skill, grant, link, race, malformed intent, or crash expands an explicit “forget Tooltail” action into deletion of user/lab/export data, or leaves startup silently replacing ambiguous state.

**Controls:** deletion is a separately accepted application-maintenance surface with no caller path/pattern and no SkillSpec/plan/model input; visible preview plus exact phrase and five-minute single-use request; active work/teaching refusal; current grant revocation; separate research deletion; fixed local `state/tooltail.db`/WAL/SHM/intent slots only; fixed-volume and non-reparse ancestry validation; no enumeration/recursive deletion; per-file revalidation; bounded `CreateNew` write-through intent with root fingerprint and no raw path; cancellation only before intent; deterministic crash-prefix recovery before SQLite open; invalid intent/layout fails closed without database replacement; labs, user files, rehearsal residuals, Capsule exports, and external research copies are preserved.

### T18. Package contamination, elevation, or overbroad uninstall

**Threat:** a release archive contains debug/private state, a traversal/link, an unreviewed binary, an elevation request, or removal logic that erases `%LOCALAPPDATA%\Tooltail` or another host directory.

**Controls:** fixed self-contained `win-x64` untrimmed publish profile; locked generic/RID dependency graphs; standard-user `asInvoker`/`uiAccess=false` manifest; bounded sorted payload; forbidden state/debug/archive extensions; no link/reparse or duplicate/traversal entries; closed per-file length/SHA-256 manifest; deterministic ZIP and readback before sidecar; explicit unsigned flag; no installer/updater/service/startup/registry action; removal verifier accepts only its newly created fixed marker-bound program fixture, launches only that owned apphost, rejects links before recursive removal, and requires a sibling data sentinel to remain byte-identical. The package is not uploaded by CI.

## 6. Privacy model

### Default data posture

- local-first;
- no account required;
- no cloud sync;
- no telemetry unless a later opt-in design is separately reviewed;
- no screen recording;
- no global keyboard/mouse logging;
- no raw Codex event persistence;
- no file-content upload in v0.1.

### Data minimization

- Persist reconciled lesson effects and SkillSpec provenance, not noisy watcher streams after the configured retention window.
- Store relative paths where possible.
- Hash only when needed for safety/verification and within resource limits.
- Do not index arbitrary file content.
- Expose retention settings and deletion per lesson, receipt, and skill.

### Suggested retention

| Data | Default |
| --- | --- |
| Raw watcher hints | delete after successful reconciliation or within 24 hours |
| Baseline/final snapshot evidence | retain with lesson until skill approved, then compact |
| Skill versions/provenance | retain until user deletes skill |
| Execution receipts | 30 days, configurable |
| Undo recovery material | 7 days or until invalidated, visible to user |
| Agent normalized status | 7 days or user-selected; raw payload never stored |
| Local logs | rolling 7 days, size bounded and redacted |

## 7. Security invariants

The following are release-blocking:

- no effect without a current grant;
- no effect outside the canonical root;
- no effect through a reparse point;
- no overwrite;
- no unknown action execution;
- no approval reuse after plan drift;
- no success before verification;
- no raw agent payload in storage/log/UI;
- no administrator requirement;
- no imported authority;
- no silent empty reset after state corruption.

## 8. Security tests

Required suites:

- malicious path corpus;
- link/reparse race suite;
- plan-drift/TOCTOU suite;
- unknown action/schema suite;
- crash-at-every-journal-boundary suite;
- undo-after-user-modification suite;
- malformed/oversized JSONL suite;
- logging redaction snapshot tests;
- capsule size/count, duplicate-ID, schema/compatibility, content-policy, malicious SkillSpec, and no-imported-authority tests before import ships;
- static scan for shell invocation and private Codex state references;
- manual standard-user/elevated-target test.
- local-state deletion authorization, fixed-boundary, preservation, malformed/oversized-intent, cancellation, crash-prefix, startup-order, and WPF smoke tests.
- portable package deterministic/hash/manifest contamination tests, standard-user profile inspection, packaged apphost smoke, and isolated program-only removal with retained local-data sentinel.

## 9. Incident behavior

When a security invariant fails:

1. stop before the next effect;
2. revoke the implicated approval and optionally the grant;
3. persist a redacted security event;
4. show exact completed/residual effects;
5. offer safe rollback only after revalidation;
6. do not retry through a broader mechanism;
7. include the event in a user-previewed diagnostic export.

## 10. Deferred security decisions

- database-at-rest encryption;
- signed capsule manifests;
- out-of-process executor sandbox;
- code-signed installer and update channel;
- plugin signing and review;
- cloud sync and account recovery;
- visual computer-use adapters;
- delegated/background automatic execution.

Each requires a dedicated threat-model update and ADR.
