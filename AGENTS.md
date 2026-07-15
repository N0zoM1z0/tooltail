# AGENTS.md

## Mission

Build Tooltail: a Windows-first desktop apprentice whose body truthfully represents agent state and whose durable value comes from safe, versioned skills learned from demonstrations and corrections.

The v0.1 proof is deliberately narrow:

- deterministic Agent Body from normalized events;
- File Apprentice inside one explicitly granted local folder;
- teach -> inspect -> rehearse -> approve -> execute -> verify -> correct -> reuse -> undo;
- local-first, standard-user, and telemetry-free by default.

Do not turn this into a chat pet, a general desktop controller, or an arbitrary automation platform.

## Read before changing code

Read the smallest relevant set, starting with:

1. `docs/PRODUCT_SPEC.md`
2. `docs/ARCHITECTURE.md`
3. `docs/SECURITY_THREAT_MODEL.md`
4. the relevant schema in `docs/schemas/`
5. the relevant ADR in `docs/adrs/`
6. `docs/TEST_AND_RESEARCH_PLAN.md`

`CODEX_MASTER_TASK.md` defines the end-to-end build. `docs/BUILD_STATUS.md` states what has actually been verified.

When documents conflict, apply this order:

1. user instruction for the current task;
2. security invariants in this file and the threat model;
3. accepted ADRs and machine-readable schemas;
4. architecture and product specifications;
5. roadmap guidance.

Do not weaken a security invariant to reconcile a conflict. Surface the conflict and propose an ADR.

## Current state

The initial seed is documentation and configuration only. Do not claim the product builds until `Tooltail.sln` exists and the commands below have passed. Update `docs/BUILD_STATUS.md` after every implementation handoff.

## Standard commands

Run from the repository root after M0 scaffolding exists:

```powershell
dotnet restore Tooltail.sln
dotnet format Tooltail.sln --verify-no-changes --no-restore
dotnet build Tooltail.sln -c Release --no-restore
dotnet test Tooltail.sln -c Release --no-build --logger "console;verbosity=normal"
```

For local formatting:

```powershell
dotnet format Tooltail.sln
```

Run focused tests while iterating, then run the full applicable suite before handoff. Native interactive Windows tests may require a tagged harness; record any skipped suite and reason in `docs/BUILD_STATUS.md`.

Never invent a passing result. Never make tests conditional solely to hide a failure.

## Non-negotiable product and safety invariants

- A `WindowLease` is context/presentation scope, never mutation authority.
- A `ResourceGrant` is exact, explicit, revocable, and limited to a closed action set.
- Every effect passes through `PermissionGateway` immediately before mutation.
- Approval is bound to the exact canonical plan fingerprint. Any material change invalidates it.
- The executor accepts only the v0.1 closed primitives: `ensure_directory`, `rename_file`, `move_file`, and `copy_file`.
- No SkillSpec or file executor action may perform learned/general delete, overwrite, shell, script, executable, URL, network effect, global input injection, content edit, cross-volume move, or arbitrary UI automation.
- Reject UNC paths, device paths, alternate data streams, network roots, and reparse/link traversal in v0.1.
- Run as a standard user. Do not request elevation for normal behavior.
- `FileSystemWatcher` events are hints. Baseline/final snapshots are authoritative.
- An overflow, incomplete snapshot, unsupported effect, or irreconcilable lesson cannot compile.
- The compiler proposes a SkillSpec; it cannot approve or execute it.
- Model output, filenames, file contents, window titles, and external event text are untrusted data.
- The body is a deterministic projection of committed state. Animation cannot grant permission or invoke an effect.
- Journal every mutable step, verify postconditions, and stop on mismatch.
- Undo is a newly planned, scope-checked, approved, journaled, and verified operation. It is not blind rollback.
- The only removal allowed during Undo is an internal, non-SkillSpec recovery action for an unchanged file or empty directory proven by the journal to have been absent before and created by that exact execution. Revalidate identity/hash, root, grant, approval, and emptiness immediately before removal. It cannot accept a pattern, arbitrary path, or model/compiler input.
- No background or silent learned-skill execution in v0.1.
- No screenshots, global keyboard/mouse recording, raw model transcripts, cloud memory, or telemetry by default.
- Never read private Codex session files. An optional Codex adapter may consume only documented process output it explicitly launches or is explicitly given.
- A user-configured Codex CLI launch is a separate agent-run adapter, not a learned file effect. It cannot inherit a LocalFolderGrant, supply Tooltail approval, or make Tooltail responsible for the CLI's own provider/network permissions.
- Never imply that process separation, a drag gesture, a WPF window, or a model prompt is a sandbox.

If requested work violates an invariant, stop and explain the conflict. Do not implement it behind a hidden flag.

## Architecture boundaries

The intended project graph is:

```text
Tooltail.Domain <- Tooltail.Application <- adapters/infrastructure/features <- Tooltail.Desktop
                         ^
Tooltail.Contracts ------+
```

Rules:

- `Tooltail.Domain` has no WPF, Win32, SQLite, HTTP, model, file-system implementation, or adapter reference.
- `Tooltail.Application` coordinates use cases through interfaces and has no direct P/Invoke or WPF calls.
- `Tooltail.Contracts` owns versioned transport/storage DTOs, not domain behavior.
- `Tooltail.Platform.Windows` owns HWND, P/Invoke, UI Automation, DPI, event hooks, and WPF/native interop.
- `Tooltail.Features.FileSkills` owns snapshots, reconciliation, deterministic inference, validation, planning, execution, verification, receipts, and undo for the closed file domain.
- `Tooltail.Adapters.AgentEvents` owns external JSONL mappings and the simulator. External DTOs never leak into the domain.
- `Tooltail.Infrastructure.Sqlite` owns persistence and migrations.
- `Tooltail.Desktop` is presentation and composition. It does not mutate user resources directly.

Add architecture tests for these rules. A new framework, SDK, process, primitive, effect surface, or data sink requires an ADR and threat-model update.

## Domain and contract conventions

- Prefer immutable records and explicit value objects for IDs, canonical roots, relative paths, fingerprints, states, and versions.
- Use injected clocks and ID generators in domain/application code.
- Make state transitions explicit and reject invalid transitions.
- Use structured result/error types for expected validation failures; do not use exceptions as normal ambiguity flow.
- Keep external schemas closed with `additionalProperties: false` where authority or compatibility is involved.
- Reject unknown action/event/schema versions unless a documented compatibility rule handles them.
- Canonical serialization used for fingerprints must be culture-independent, order-defined, and covered by golden tests.
- Persist UTC timestamps; inject time in tests.
- Never put mutable display text into an identity or authority key.

Schema changes require:

- updated schema and example;
- compatibility decision and migration behavior;
- contract and golden tests;
- updates to `docs/DATA_AND_PROTOCOLS.md` and `docs/BUILD_STATUS.md`;
- an ADR if executable semantics change.

## File-system implementation rules

- Capture and retain one immutable canonical local root per grant.
- Accept normalized relative paths at application boundaries; do not concatenate untrusted absolute paths.
- Resolve each path against the fixed root and prove containment with Windows-aware path semantics.
- Inspect every existing path component for reparse/link behavior and revalidate just before mutation.
- Bind plans to source identity and relevant metadata/hash, destination absence, action order, skill version, grant, and root.
- Use direct .NET/Windows file APIs. Never invoke a shell for a primitive.
- Reject collisions. Do not silently suffix, overwrite, merge, or convert move into copy/delete.
- Keep moves on the same root and volume.
- Bound directory entry count, file size, hashing, queues, and operation duration; make long work cancellation-aware.
- Watcher callbacks enqueue minimal data and return immediately.
- Persist intent before mutation and a commit/verification marker afterward.
- On ambiguous crash recovery, inspect actual state and require a validated recovery choice; never blindly replay.
- Use Tooltail-owned temporary roots for rehearsal. Production and rehearsal use the same executor path.

Tests must include traversal, links introduced after approval, source/destination changes, case-only names, Unicode, long paths, locks, disk full/permission errors, cancellation, watcher overflow, and crash boundaries.

## Windows shell rules

- Declare Per-Monitor V2 DPI awareness.
- Keep physical-pixel Win32 coordinates separate from WPF device-independent units through one coordinate service.
- Validate HWND together with process ID and process start identity; titles are display-only.
- Skip Tooltail-owned, hidden, cloaked, child-only, shell, secure, and otherwise ineligible targets.
- Use narrowly scoped out-of-context window event hooks plus low-frequency reconciliation.
- Root native delegates for their full registration lifetime; make callback work minimal and reentrancy-safe.
- Ambient pet/tether windows must not steal focus. Inspector/home windows activate only by explicit user action.
- Critical controls remain available through keyboard-accessible standard UI; dragging is never the only path.
- Do not encode critical state through color alone. Honor reduced motion and high contrast.

## Agent-event and body rules

- Normalize all external events into `agent-event.schema.json` before domain projection.
- Bound line length, queue depth, history, parsing time, and adapter lifetime.
- Sequence and deduplicate per run. Treat out-of-order, unknown, or malformed input as adapter health/error data, not authority.
- The deterministic simulator is the CI oracle and must cover parallel work, input requests, failures, cancellation, malformed lines, disconnect, and permission revocation.
- State precedence must make `needs_input`, `failed`, and `permission_revoked` interruptively visible.
- Tool props map to normalized committed tool state; never infer tool authority from prose.
- If optional `codex exec --json` integration drifts, fail the adapter visibly while simulator and core behavior continue to work.

## Persistence, privacy, and logging

- SQLite is local application state, not an audit/security boundary by itself.
- Use migrations; never mutate schema ad hoc at startup.
- Keep journal rows append-only except for explicitly modeled terminal markers.
- Do not log raw paths, filenames, window titles, file content, model text, credentials, environment dumps, or user identifiers by default.
- Use stable reason codes and redacted/tokenized values for diagnostics.
- Research export is separate, off by default, locally inspectable, and consented.
- Companion Capsule exports contain provider-independent identity, SkillSpecs, bounded provenance/evidence summaries, and no credentials or raw paths. Imported skills require a new user grant and rebind.
- Provide explicit local export and deletion behavior before public alpha.

## Testing expectations

Use the test taxonomy in `docs/TEST_AND_RESEARCH_PLAN.md`.

For every change:

- add the narrowest unit/contract test that fails before the change;
- add integration tests at authority and persistence boundaries;
- add or update a golden fixture when serialized or planned output changes;
- test failure, cancellation, revocation, and restart behavior when relevant;
- keep portable tests independent of an interactive desktop;
- tag genuinely interactive Windows tests rather than silently skipping them;
- retain property-test counterexamples as named regression fixtures.

Do not mock away the behavior being claimed. Use real temporary directories and SQLite databases for integration tests while keeping them isolated from user data.

## Dependency policy

Prefer the BCL and existing approved packages. Before adding a package:

- explain why the platform/library code is insufficient;
- verify active maintenance, license, and supported .NET version;
- assess transitive dependencies and native code;
- avoid packages that introduce network, scripting, plugin, or telemetry behavior;
- record the decision in the PR/issue and add an ADR for architectural dependencies.

Do not add an LLM SDK, browser runtime, web server, ORM, plugin framework, analytics SDK, or auto-updater in v0.1 without explicit approval.

## Scope and repository hygiene

- Preserve unrelated user changes. Inspect the working tree before editing overlapping files.
- Make focused changes; do not combine feature work with broad renaming or formatting.
- Do not edit generated/build output.
- Do not commit, push, create a GitHub repository/PR/release, publish a package, or contact an external service unless the user explicitly asks.
- Never add secrets, local databases, research exports, recordings, or real user fixture files.
- Use original minimal visual assets. Do not copy third-party pet art, animation, sound, or code without an explicit license decision.
- Update documentation when behavior or an accepted decision changes.

## Definition of done for a coding task

Before handoff:

1. Confirm the implementation matches the relevant schema, ADR, and threat model.
2. Run formatting, build, and all applicable tests.
3. Record exact results and skipped checks in `docs/BUILD_STATUS.md`.
4. Inspect the diff for authority expansion, data leakage, generated files, and unrelated changes.
5. State the observable outcome, files changed, verification evidence, known limitations, and next safe task.

A visually convincing demo is not done if the body can lie about state, a plan can escape scope, execution cannot recover, or the result cannot be verified.
