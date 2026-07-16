# Data Model and Protocols

## 1. Principles

- Domain events and immutable versions preserve causality.
- SQLite is local application state, not an analytics warehouse.
- Contracts are versioned and validated at every trust boundary.
- Authority objects are never exported as companion identity.
- Sensitive payloads are minimized before persistence.
- Unknown major versions and action discriminators fail closed.

## 2. SQLite schema outline

Exact SQL belongs in migrations, but the logical model is fixed here.

### `schema_migrations`

- `version` primary key
- `applied_utc`
- `application_version`
- `checksum`

### `companions`

- `companion_id` primary key
- `display_name`
- `created_utc`
- `identity_schema_version`
- `presentation_json`

v0.1 has one active companion but does not hard-code a singleton key.

### `window_leases`

- `lease_id` primary key
- `companion_id`
- `hwnd_value`
- `process_id`
- `process_start_identity`
- `root_hwnd_value`
- `application_name`
- `issued_utc`
- `expires_utc`
- `revoked_utc`
- `revocation_reason`

Window titles are optional display metadata and are redacted in diagnostics.

### `resource_grants`

- `grant_id` primary key
- `companion_id`
- `resource_type`
- `canonical_root_protected`
- `capabilities_json`
- `issued_utc`
- `expires_utc`
- `revoked_utc`
- `grant_fingerprint`

`canonical_root_protected` means access is restricted to the local user and diagnostics redact it; it does not imply encryption.

### `teaching_episodes`

- `episode_id` primary key
- `companion_id`
- `grant_id`
- `started_utc`
- `stopped_utc`
- `status`
- `baseline_snapshot_ref`
- `final_snapshot_ref`
- `reconciliation_summary_json`
- `invalid_reason`
- `raw_evidence_expiry_utc`

### `demonstration_examples`

- `example_id` primary key
- `episode_id`
- `effect_type`
- `source_relative_path`
- `destination_relative_path`
- `source_fingerprint_json`
- `user_label`

### `skills`

- `skill_id` primary key
- `companion_id`
- `display_name`
- `created_utc`
- `current_version_id`
- `disabled_utc`

### `skill_versions`

- `skill_version_id` primary key
- `skill_id`
- `version_number`
- `parent_version_id`
- `schema_version`
- `skill_spec_json`
- `spec_hash`
- `compiler_id`
- `compiler_version`
- `executor_compatibility`
- `lifecycle_state`
- `created_utc`
- `approved_utc`
- `semantic_diff_json`

Unique constraint on `(skill_id, version_number)` and immutable after insert except lifecycle projection fields handled through explicit events.

### `skill_evidence`

- `evidence_id` primary key
- `skill_version_id`
- `evidence_type`
- `source_id`
- `summary_json`
- `created_utc`

### `execution_plans`

- `plan_id` primary key
- `plan_kind` (`standard` or `recovery`)
- `skill_version_id`
- `grant_id`
- `original_execution_id` nullable for standard plans
- `original_plan_id` nullable for standard plans
- `original_plan_fingerprint` nullable for standard plans
- `plan_contract_version`
- `created_utc`
- `plan_json`
- `plan_fingerprint`
- `status`
- `expires_utc`

### `approvals`

- `approval_id` primary key
- `plan_id`
- `plan_fingerprint`
- `approved_utc`
- `expires_utc`
- `approval_purpose` (`production`, `rehearsal`, or `undo`)
- `consumed_utc`
- `revoked_utc`
- `revocation_reason`

An approval is single-plan, single-purpose, and single-use for mutable execution. A rehearsal approval can authorize only a rehearsal execution against a newly planned Tooltail-owned temporary root; it can never authorize a production-mode request or the original granted root. An undo approval can authorize only the exact canonical recovery plan derived from a verified receipt and current state; production and rehearsal approvals cannot be reused for recovery.

`plan_fingerprint` is SHA-256 over the versioned canonical typed projection defined by ADR 0006, never over display text or incidental serializer output. Before authorization, the application regenerates the canonical bytes and requires exact plan ID/fingerprint equality. Approval expiry cannot exceed plan expiry; consumption and revocation are terminal append-visible decisions.

### `executions`

- `execution_id` primary key
- `plan_id`
- `approval_id` unique
- `correlation_id`
- `journal_kind` (`standard` or `recovery`)
- `operation_primitives_json`
- `operation_inverse_kinds_json`
- `recovery_primitives_json`
- `recovery_original_steps_json`
- `started_utc`
- `completed_utc`
- `status`
- `verification_summary_json`
- `residual_effects_json`

### `execution_journal_events`

- `journal_event_id` primary key
- `execution_id`
- `event_sequence`
- `step_sequence` nullable for execution-level events
- `event_type`
- `event_version`
- `occurred_utc`
- `primitive_type` nullable
- `recovery_primitive_type` nullable
- `original_step_sequence` nullable
- `precondition_fingerprint` nullable
- `inverse_kind` nullable
- `reason_code` nullable
- `recovery_execution_id` nullable

Unique constraint on `(execution_id, event_sequence)`. Rows are insert-only. Step status and recovery status are projections over the ordered event prefix; they are not mutable journal columns. The v0.1 event vocabulary includes execution-opened, step-intent, mutation-observed, committed, verified, failed, recovery-required, and rolled-back markers.

`inverse_kind` is a closed internal recovery category (`none`, `rename_back`, `move_back`, or `remove_created_entry`), not an executable SkillSpec primitive. Concrete paths remain bound through the immutable plan; journal diagnostics store only safe reason codes. A rolled-back marker must reference a separate, newly planned and approved recovery execution.

A recovery execution uses the same opened/intent/observed/committed/verified/failure event boundaries, but its intent is separately typed and binds the recovery-plan fingerprint, closed recovery primitive, and original step sequence. `remove_created_entry` never appears as a normal file primitive. Original verified steps receive append-only rolled-back markers only after the corresponding recovery step has been verified, and each marker carries the distinct recovery execution ID.

### `receipts`

- `receipt_id` primary key
- `execution_id`
- `receipt_kind` (`standard` or `recovery`)
- `receipt_json`
- `created_utc`
- `undo_available_until_utc`

Normal receipts retain exact verified destination identity/hash evidence. A successful recovery receipt additionally binds the recovery plan and fingerprint, original execution/plan/fingerprint, ordered verified recovery evidence, and the original-journal rollback links. If recovery stops, no successful receipt is emitted and the result exposes reason-coded residual steps requiring inspection.

The implemented repositories treat every row as untrusted input. JSON is byte-bounded and closed-versioned; canonical plan hashes and executable fields must agree; journal event IDs, payload shapes, contiguous order, timestamps, plan fingerprints, inverse kinds, and state transitions are replayed through the domain model. Receipt evidence is checked against the canonical plan and replayed journals before a receipt object is returned. Unknown fields, impossible lifecycle history, mismatched projections, oversized arrays, and tampered append-only identities fail closed.

Approval consumption, execution creation, and the first journal event share one `BEGIN IMMEDIATE` transaction. Event appends and receipt completion each use their own serialized transaction, reflecting the fact that SQLite and the user file system do not share a transaction. Exact retries are idempotent; conflicting retries are rejected. Startup recovery only reports bounded unreceipted journal assessments and never automatically repeats a file effect.

The M5 workspace read model reconstructs one bounded, content-minimized startup projection from SQLite: companion identity, exact folder grants, current immutable skill versions, recent teaching phase/evidence summaries, and recent execution/receipt presence. Companion discovery is bounded to 100 records, active skills and immutable versions to 500, and recent lessons/executions to 100 each. Grant fingerprints and protected-root byte bounds are revalidated; current SkillSpecs and every referenced canonical plan use the existing fail-closed readers. The projection deliberately omits demonstration paths, reconciliation payloads, journal payloads, and receipt evidence. Callers load exact journals/receipts separately and run the recovery scan; no read operation replays a mutation.

Folder snapshots used by Desktop teaching are encoded as `tooltail.folder-snapshot/1` documents with a 4 MiB document bound and 10,000-entry bound. The closed parser rejects unknown fields/enums and then reconstructs every entry plus the aggregate through `FolderSnapshot.Rehydrate`, so UTC ordering, normalized relative paths, identity pairs, content-hash state, and exact hashed-byte totals are revalidated. Reconciliation summaries use `tooltail.reconciliation-summary/1`, retain only normalized effect paths/reason codes/candidate paths, and have the same byte and effect-count bounds. Baseline and final snapshots remain authoritative; watcher data is never serialized as authority.

Desktop compilation preserves the same generated `example_id` in both the persisted `demonstration_examples` row and the in-memory normalized compiler example. This keeps provenance stable from authoritative reconciliation through compilation without deriving identity from mutable display text. The deterministic compiler accepts only complete evidence with at least two supported examples, returns at most its closed typed clarification questions when ambiguity remains, and persists a candidate only after the result is `ready`. That candidate is stored as immutable version 1 in `draft`; compiler output supplies neither approval nor execution authority. Its Skill Card is a presentation projection of the canonical SkillSpec, exact grant capabilities, bounded normalized samples, and a `teaching_complete` evidence marker.

Desktop rehearsal uses normal persisted rows rather than an alternate execution path. It stores a short-lived exact-root temporary grant, canonical plan, and `rehearsal`-purpose approval before the standard journal can open; journal creation atomically consumes that approval. The resulting receipt remains linked to the exact original SkillSpec hash even though planning is rebound to the owned temporary root. Normal completion stores the temporary grant as revoked after bounded identity-checked cleanup. A passing result additionally captures a fresh source snapshot and stores a separate canonical production plan with status `planned`; no production approval row exists at this checkpoint.

The explicit production action reloads that planned document and requires byte-for-byte canonical JSON plus fingerprint equality. It persists the same immutable SkillVersion's legal `draft` to `approved` transition and then one `production`-purpose approval. Journal creation consumes the persisted approval in the same transaction that opens the execution. The executor's authority source reloads the exact skill version and grant from bounded SQLite projections at every effect boundary, so revocation or lifecycle drift is immediately visible. A verified standard receipt retains exact destination identity/hash evidence and a bounded Undo window; after receipt storage the version advances to `practiced`.

### `agent_runs`

- `agent_run_id` primary key
- `adapter_id`
- `external_run_id_hash`
- `started_utc`
- `completed_utc`
- `normalized_status`
- `unknown_event_count`
- `failure_code`

No prompt, reasoning, source, path, or raw payload columns.

### `domain_events`

- `event_sequence` integer primary key autoincrement
- `aggregate_type`
- `aggregate_id`
- `event_type`
- `event_version`
- `occurred_utc`
- `correlation_id`
- `payload_json`

Use for durable causality and body-state projections. Payloads obey the same privacy rules as tables.

## 3. Domain event vocabulary

### Window and grant

- `WindowLeaseProposed`
- `WindowLeaseIssued`
- `WindowLeaseRevoked`
- `ResourceGrantIssued`
- `ResourceGrantRevoked`

### Teaching and skills

- `TeachingStarted`
- `BaselineCaptured`
- `TeachingStopped`
- `TeachingEvidenceReconciled`
- `TeachingInvalidated`
- `SkillCandidateCompiled`
- `SkillClarificationRequested`
- `SkillVersionValidated`
- `SkillRehearsalPassed`
- `SkillVersionApproved`
- `SkillVersionCorrected`
- `SkillMarkedStale`

### Plan and execution

- `PlanCreated`
- `PlanApprovalGranted`
- `PlanApprovalInvalidated`
- `ExecutionStarted`
- `ExecutionStepStarted`
- `ExecutionStepCommitted`
- `ExecutionStepVerified`
- `ExecutionStopped`
- `ExecutionCompleted`
- `ExecutionFailed`
- `RollbackStarted`
- `RollbackCompleted`
- `RollbackIncomplete`

### Agent

- `AgentRunStarted`
- `AgentPhaseChanged`
- `AgentInputRequested`
- `AgentBlocked`
- `AgentArtifactProduced`
- `AgentRunCompleted`
- `AgentRunFailed`
- `AgentRunCancelled`

## 4. Body-state projection

Body state is derived, not persisted as authoritative mutable state.

Precedence:

1. security/verification failure;
2. permission revoked or adapter disconnected;
3. needs input;
4. blocked;
5. rollback;
6. paused;
7. executing;
8. rehearsing;
9. compiling;
10. teaching;
11. observing/window-bound;
12. completed-unopened;
13. idle/quiet.

Late or duplicate events must not move a terminal run back into a nonterminal state.

## 5. Agent event protocol

The JSON Schema is `docs/schemas/agent-event.schema.json`.

Envelope fields:

- `schemaVersion`;
- `eventId`;
- `runId`;
- `sequence` monotonically increasing per run when the adapter can provide it;
- `occurredAt`;
- `source` closed adapter-source enum;
- `type` closed normalized event enum;
- `severity` closed presentation/diagnostic enum;
- `data` containing only allowlisted bounded fields such as tool kind, opaque call/question/subagent ID, reason code, progress, or untrusted display label.

Protocol rules:

- one UTF-8 JSON object per line;
- maximum line size 64 KiB in the generic protocol, lower where possible;
- no ANSI control sequences in displayed values;
- duplicate `eventId` is idempotent;
- invalid UTF-8, malformed JSON, unknown major schema, or oversized line terminates the adapter connection;
- provider-specific unknown events are counted and ignored before normalization; an unknown normalized `type` is rejected;
- the normalized protocol never contains prompt, source code, reasoning, command output, environment variables, or credentials.

## 6. Codex adapter boundary

Codex's documented noninteractive CLI can emit newline-delimited JSON events using `codex exec --json`. Tooltail may wrap only runs it launches explicitly.

Adapter flow:

```text
user approves launch
  -> ProcessStartInfo without shell
  -> codex exec --json --cd <approved workspace> <prompt source>
  -> bounded stdout line reader
  -> raw event mapper
  -> normalized AgentEvent
  -> raw object discarded
```

Rules:

- use `ProcessStartInfo.ArgumentList`, never a concatenated shell command;
- pass prompt through a safe supported channel, not command interpolation;
- do not print or persist raw stdout;
- keep stderr bounded and redacted for process status only;
- never pass dangerous sandbox/approval-bypass flags;
- do not read `$CODEX_HOME` sessions or private rollout files;
- adapter fixture tests must tolerate additional unknown fields;
- an adapter failure does not affect file skills or companion identity.

## 7. Scope contract

The external `WindowLease` schema is `docs/schemas/scope-lease.schema.json`. It intentionally contains no mutation capability.

`WindowLease` and `ResourceGrant` are separate domain records and separate application messages. The v0.1 LocalFolderGrant remains an internal typed application contract until a real external boundary requires a JSON schema. It must not be serialized by extending the lease schema ad hoc.

Window lease authority is limited to:

- associate ambient state with target;
- observe allowlisted window metadata;
- anchor body/tether position.

Local folder grant capabilities are closed:

- `enumerate`;
- `read_metadata`;
- `read_content_hash`;
- `create_directory`;
- `rename`;
- `move_within_root`;
- `copy_within_root`.

Grant expiry/revocation invalidates all unconsumed approvals that reference it.

## 8. Fixture CLI protocol

The M2 headless acceptance surface is documented in [`FIXTURE_CLI.md`](FIXTURE_CLI.md). It uses three closed internal JSON contracts:

- `tooltail.fixture-workspace/1` marks a newly created Tooltail-owned workspace and supplies deterministic fixture identity/time;
- `tooltail.fixture-result/1` wraps every workflow command with command, success/failure status, stable reason code, and command-specific data;
- `tooltail.fixture-golden-suite/1` contains the complete `roadmap-m2/1` cross-platform acceptance evidence.

Snapshot and reconciliation artifacts use their own `tooltail.fixture-snapshot/1` and `tooltail.fixture-reconciliation/1` versions. Readers reject unknown members, enum integers, non-UTC or inconsistent lifecycle values, invalid Windows-relative paths, inconsistent hashes, more than 10,000 retained entries/effects, JSON deeper than 64 levels, and artifacts outside the 2-byte to 4-MiB bound. A complete stored reconciliation must byte-match a fresh canonical reconciliation of its baseline and final snapshots before compilation; this prevents an overflow or unsupported episode from being bypassed by invoking `compile` separately.

The workspace marker, fixed directories, artifact file slots, and SQLite database/sidecar slots are revalidated for reparse/link traversal. Artifacts are replaced through collision-resistant `CreateNew` temporary files in the owned artifact directory. SQLite remains local state rather than a security boundary, but the fixture CLI never opens it through a detected link.

Execution persists the standard journal and receipt in SQLite plus a complete authoritative final snapshot. The independent `verify` command requires the reloaded journal to project every step as `verified`, the receipt to pass repository/domain rehydration, and a fresh snapshot to equal the persisted final entries. Undo persists a distinct canonical recovery plan, approval, journal, and recovery receipt.

The golden suite output contains only relative file-tree data and provider-independent typed contracts; it never emits the physical fixture path. Its exact normalized-LF digest and committed expected document are listed in [`FIXTURE_CLI.md`](FIXTURE_CLI.md).

## 9. Capsule protocol

The manifest schema is `docs/schemas/companion-capsule.schema.json`.

The v1 capsule is one bounded UTF-8 JSON document. Requirements:

- versioned manifest;
- bounded skill count and total document size;
- no permissions, approvals, credentials, undo material, or active leases;
- no raw paths, filenames, file contents, or model transcripts;
- every source grant binding imports as `require_user_rebind`;
- imported skills remain unbound/stale until the user grants, rebinds, and rehearses them;
- unknown major manifest version rejected.

Export can ship before import. Import remains feature-flagged until size, schema, duplicate-ID, compatibility, and no-authority tests pass. If a future capsule becomes a multi-file archive, add a new schema/ADR and defend against traversal, duplicate entries, decompression bombs, links, and hash confusion.

The shared capsule service performs semantic validation before producing export bytes. It rejects unsafe content-policy flags, empty or duplicate identities, invalid or unsupported SkillSpecs, source-grant bindings that disagree with the SkillSpec, invalid evidence counters/timestamps, and correction versions whose declared parent is absent from the capsule. It canonicalizes and orders immutable SkillSpecs before a bounded parser readback. The M5 parser currently returns a non-mutating preview only: `createsAuthority` and `canImport` are always false, and every displayed skill requires a new user grant, explicit rebind, and rehearsal. Native import remains disabled truthfully.

Correction compiles version `n + 1` through the same deterministic compiler with an explicit parent reference and the complete retained positive-evidence lineage. A positive example must add a new example ID, a negative correction must include at least one bounded exclusion, and an explicit clarification must change the typed answer set. A result is accepted as a correction only when match, transformation, policy, verification, or scope-binding semantics change; provenance-only or timestamp-only changes are rejected. Accepted correction versions restart in `draft` and require a new rehearsal plus exact-plan approval. Their deterministic semantic-diff document is persisted with the immutable version; earlier version receipts remain bound to their original version.

## 10. Migration strategy

- One ordered migration per schema change.
- Migration checksum stored and verified.
- Backup database before nontrivial migration.
- Apply in a transaction when SQLite semantics allow.
- On failure, preserve original and enter read-only recovery UI.
- Test migration from every released fixture to current.
- Never discard unknown user data to “make migration pass.”
- SkillSpec schema migration creates a new compatible projection or marks a skill Stale; it does not rewrite signed historical receipts.

The implemented v1 store embeds normalized-LF SQL migrations and records their SHA-256 checksums. Startup opens the existing file in place, enables foreign keys and full synchronous durability, requests WAL mode, runs `quick_check`, validates the complete contiguous migration history, and verifies required schema objects plus `foreign_key_check` before granting write access. Tables use SQLite `STRICT` mode, JSON and closed-enum checks, and append-only triggers for migration history, execution journals, receipts, and domain events. On Unix the newly created database is restricted to the current user.

An unrecognized database, future migration, checksum mismatch, integrity error, missing required object, or foreign-key violation returns a reason-coded read-only recovery state. The initializer neither deletes the file nor substitutes a fresh database. The migration catalog marks nontrivial future migrations as backup-required; a collision-free SQLite online backup must complete before such a migration can take the writer lock.

## 11. Retention and deletion

Deletion is user-initiated application-state deletion, distinct from learned file actions.

- Deleting a lesson removes raw evidence after confirming no approved skill requires it; compact provenance can remain only with user consent.
- Deleting a skill disables it, revokes pending approvals, and removes versions/evidence after a recovery window.
- Deleting a companion requires capsule export offer, explicit confirmation, and database backup policy.
- Undo backups expire visibly and are removed by Tooltail's own maintenance service, never by a learned skill.
