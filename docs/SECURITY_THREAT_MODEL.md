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

**Controls:** separate WindowLease/ResourceGrant; inspector disclosure; mandatory Permission Gateway; effect-level grant checks; user study comprehension gate.

### T2. Path traversal and canonicalization bypass

**Threat:** `..`, device paths, UNC, alternate streams, odd separators, trailing dot/space semantics, or case behavior escapes a grant.

**Controls:** relative paths only; deterministic full-path resolution against fixed root; boundary-aware containment; reject UNC/device/ADS; reserved-name validation; adversarial path corpus.

### T3. Reparse point and link redirection

**Threat:** a junction/symlink redirects an in-scope path out of scope, including after approval.

**Controls:** reject reparse points on every path component; do not follow links during enumeration; revalidate immediately before effect; reject changes after plan fingerprinting.

### T4. TOCTOU file replacement

**Threat:** source or destination changes after plan approval.

**Controls:** file identity/metadata/hash fingerprints; per-step revalidation; approval invalidation; no overwrite; stop on mismatch.

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

**Controls:** unique IDs; immutable records; database transactions; plan/grant/skill fingerprints; purpose-bound production versus rehearsal approvals; status transition validation; startup reconciliation; undo revalidation; no replay of completed approval.

### T11. Undo destroys later user work

**Threat:** user modifies a Tooltail-created target before Undo.

**Controls:** derive a reverse-ordered canonical recovery plan only from a complete verified receipt and its exact journal; require a fresh purpose-bound approval and current grant; verify canonical containment plus exact entry identity, retained metadata, and file hash before and immediately prior to each effect; require created directories to be empty; never overwrite; use non-recursive removal; journal and verify recovery separately; append the original rollback link only after verification; expose residuals and refuse automatic replay on any mismatch.

### T12. Sensitive logging and telemetry

**Threat:** prompts, source, paths, file contents, or credentials enter logs or analytics.

**Controls:** telemetry off/default absent; structured allowlist logging; relative/redacted paths; no raw payloads; diagnostic export preview; automated redaction tests.

### T13. Capsule import attack

**Threat:** oversized JSON, duplicate/conflicting IDs, malicious SkillSpec, compatibility confusion, sensitive content, or imported authority.

**Controls:** import deferred until hardened; bounded UTF-8 JSON parser; document/skill-count limits; strict schema validation; duplicate-ID rejection; closed actions; content-policy validation; imported skills unbound/stale; permissions and approvals never imported; new grants, rebinding, and rehearsal required. A future multi-file archive requires a separate traversal/decompression threat review.

### T14. Elevated target interaction

**Threat:** Tooltail attempts to automate a higher-integrity process or users run Tooltail as administrator to “fix” it.

**Controls:** standard-user manifest; do not request `uiAccess`; reject unsupported elevated targets; never recommend running as administrator; v0.1 file execution uses granted filesystem resources, not input injection.

### T15. Misleading completion

**Threat:** body shows success while verification is incomplete or rollback left residue.

**Controls:** state projection from committed/verified events; explicit partial/failed states; receipt lists residual effects; no optimistic success animation.

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
