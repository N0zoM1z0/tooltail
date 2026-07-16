# Implementation Roadmap

## 1. Delivery principle

Build Tooltail as a sequence of falsifiable product and safety milestones. Each milestone must leave a runnable, testable artifact. Do not accumulate a large animation, chat, or automation surface before the learning loop works.

The required order is:

```text
contracts and safety kernel
  -> headless skill loop
  -> deterministic agent-body simulator
  -> Windows embodiment
  -> integrated teaching and reuse loop
  -> user research gate
  -> bounded public alpha
```

## 2. Milestone overview

| Milestone | Outcome | Primary exit signal |
| --- | --- | --- |
| M0 | Repository and engineering baseline | clean restore/build/test on Windows CI |
| M1 | Safety kernel and versioned contracts | adversarial path and plan tests pass |
| M2 | Headless File Apprentice | fixture lesson compiles, rehearses, executes, verifies, and undoes |
| M3 | Agent Body simulator | all normalized events produce deterministic, visible states |
| M4 | Native Windows body and leases | drag-to-window scope works across DPI/monitor cases |
| M5 | Integrated MVP loop | a user can teach, inspect, approve, reuse, correct, and undo |
| M6 | Instrumented research build | product hypotheses have measurable evidence |
| M7 | Security and public-alpha gate | threat-model checks and recovery drills pass |

No milestone may weaken the v0.1 non-goals to make a demo easier.

## 3. M0 — repository and engineering baseline

### Deliverables

- Create `Tooltail.sln` and the project graph in `ARCHITECTURE.md`.
- Pin the .NET 10 SDK through `global.json`.
- Enable nullable reference types, analyzers, deterministic builds, and warnings as errors for Tooltail source.
- Add Windows and portable CI jobs.
- Add architecture tests for dependency direction.
- Add a deterministic test clock, ID generator, file-system abstraction, and temporary-directory fixture conventions.
- Add structured logging with content-minimized fields and no telemetry sink.
- Record build commands and known gaps in `BUILD_STATUS.md`.

### Exit criteria

- `dotnet restore`, `dotnet build --no-restore`, and `dotnet test --no-build` pass on a clean Windows runner.
- Portable domain, application, contract, and file-skill tests also pass on a non-Windows runner.
- No project has a dependency forbidden by `ARCHITECTURE.md`.
- A developer can run the empty WPF shell and the headless fixture CLI from the README.

## 4. M1 — safety kernel and contracts

### Deliverables

- Implement typed IDs and immutable domain records for grants, leases, lessons, skills, plans, approvals, executions, and receipts.
- Implement canonical-root handling and relative-path value objects.
- Reject absolute paths, UNC paths, device paths, alternate data streams, dot traversal, mixed-root inputs, and reparse/link traversal.
- Implement action allowlists and the `PermissionGateway`.
- Implement immutable plan construction and canonical plan fingerprinting.
- Implement append-only journal semantics and crash-recovery state detection.
- Validate the four JSON schemas in `docs/schemas/` in contract tests.

### Exit criteria

- Property and adversarial tests cannot produce an accepted destination outside the granted root.
- A changed input, destination, skill version, grant, or ordered operation invalidates approval.
- Unknown schema fields that could affect execution, unknown action discriminators, and incompatible contract versions fail closed.
- Recovery can distinguish `not started`, `started but uncommitted`, `committed but unverified`, `verified`, and `rolled back` steps.

## 5. M2 — headless File Apprentice

Implementation status (2026-07-16): complete and verified through the exact `roadmap-m2/1` six-scenario fixture suite. M3 is also complete; the active implementation milestone is M4.

### Deliverables

- Implement baseline/final folder snapshots with bounded hashing.
- Implement `FileSystemWatcher` as a hint source and overflow invalidation.
- Reconcile snapshots into normalized evidence.
- Implement deterministic candidate generation, elimination, ranking, and ambiguity output.
- Support only `ensure_directory`, `rename_file`, `move_file`, and `copy_file`.
- Implement schema and semantic validation, pure planning, dry-run, sandbox rehearsal, journaled execution, postcondition verification, receipts, and undo.
- Implement `Tooltail.SkillFixtureCli` commands for `observe-fixture`, `compile`, `plan`, `rehearse`, `execute-fixture`, `verify`, and `undo-fixture`.
- Persist episodes, skill versions, executions, and receipts in SQLite.

### Required golden scenarios

1. Move PDFs whose names contain `invoice` into `Invoices/`.
2. Rename image files from spaces to lower-case hyphenated stems.
3. Prefix selected files with their last-write year and month.
4. Copy matched files into a constant review directory while preserving originals.
5. Ask for clarification when a date-like constant could be fixed text or metadata-derived.
6. Reject lessons containing deletion, content modification, a reparse point, cross-volume movement, or an unreconciled watcher overflow.

### Exit criteria

- Each golden scenario has deterministic fixtures and exact expected SkillSpec, plan, receipt, and final file tree.
- Replaying an unchanged plan produces the same fingerprint.
- A failed postcondition stops subsequent effects and surfaces recovery choices.
- Undo restores every supported completed fixture without overwriting unrelated changes.
- The headless loop requires no LLM and no interactive desktop.

## 6. M3 — deterministic Agent Body simulator

Implementation status (2026-07-16): complete and verified on Linux and Windows. Fifteen exact simulator traces, generic and optional Codex adapters, the parameterized projector, original vector body, exact inspector, and development playback controls satisfy the M3 exit criteria without a live Codex process.

### Deliverables

- Implement versioned `AgentEventEnvelope` parsing.
- Normalize simulator, generic JSONL, and optional `codex exec --json` events into the small internal vocabulary.
- Implement the pure `CompanionStateProjector` and precedence table.
- Implement a simulator that can emit normal, parallel, ambiguous, failure, cancellation, malformed, delayed, and out-of-order traces.
- Render a deliberately minimal pet using original geometric/vector assets and eight body states.
- Add an inspector showing exact normalized events, active tools, current scope, and why a state is active.

### Exit criteria

- The same committed event sequence always produces the same state sequence.
- Malformed and unknown external events cannot put the UI into an authoritative state.
- `needs_input`, `failed`, and `permission_revoked` visibly outrank background work.
- A disconnected adapter becomes `disconnected` or `failed`; it never appears to keep working.
- All simulator traces can be run in CI without launching Codex.

## 7. M4 — native Windows body and window leases

### Deliverables

- Implement the non-activating transparent `PetWindow`, click-through `TetherWindow`, and accessible `InspectorWindow`.
- Implement drag target discovery that skips Tooltail, child-only, hidden, cloaked, shell, secure, and ineligible windows.
- Issue a `WindowLease` bound to HWND plus process identity; track move, resize, minimize, destroy, cloak, foreground, and process exit.
- Implement Per-Monitor V2 coordinate conversion and reconciliation.
- Implement immediate revocation when the pet is pulled away, returned home, the target disappears, or the lease expires.
- Make scope visible in the inspector and through the tether; never imply that a lease grants file authority.

### Exit criteria

- Dragging onto an eligible target creates one visible lease; dragging away revokes it immediately.
- HWND reuse and changed process identity invalidate the lease.
- The body tracks targets across monitors at 100%, 125%, 150%, and 200% scale, including negative monitor origins.
- Ambient interactions do not steal focus from the user's active application.
- Keyboard-only users can open the inspector, revoke a lease, pause, cancel, and return home without dragging.

## 8. M5 — integrated MVP loop

### Deliverables

- Connect one user-granted local folder to a teaching session.
- Show teaching status, evidence quality, unsupported effects, and safe stop behavior through the body and inspector.
- Present a plain-language Skill Card alongside the exact match rule, transformation, scope, provenance, and sample outputs.
- Require rehearsal and exact-plan approval.
- Render tool acquisition and lifecycle changes from committed skill events.
- Support correction as a new immutable skill version with a semantic diff.
- Surface task completion as a receipt object the pet brings back.
- Support pause, cancel, revoke, and home as consistent body/control actions.

### Canonical demo

```text
1. User grants C:\Users\Alice\Desktop\TooltailLab.
2. User starts “Teach me.”
3. User moves three invoice PDFs into Invoices and demonstrates a safe rename.
4. Tooltail reconciles the before/after state and asks one targeted question.
5. The Skill Card explains exactly what will match and what will happen.
6. Tooltail rehearses on a temporary copy.
7. User approves the exact plan for a fresh fixture set.
8. The pet visibly uses its learned file tool.
9. Verification succeeds and the pet returns a receipt.
10. User corrects an edge case; version 2 changes visibly and succeeds next time.
11. User invokes undo and sees a verified inverse receipt.
```

### Exit criteria

- A new evaluator can complete the canonical demo from first launch without developer intervention.
- Every visible action corresponds to a persisted state or committed event.
- Removing a grant prevents future planning and execution.
- A correction demonstrably changes the next relevant plan.
- Export produces a provider-independent Companion Capsule with identity, skill versions, provenance, and evidence summaries.

## 9. M6 — instrumented research build

### Deliverables

- Add an explicit opt-in, local research log export with no raw filenames by default.
- Implement event timing for teach, clarification, rehearsal, approval, execution, correction, undo, revocation, and inspection.
- Add a study mode that resets fixtures and produces anonymized session IDs.
- Run the formative and summative protocols in `TEST_AND_RESEARCH_PLAN.md`.
- Record findings and decisions in dated research notes and ADRs.

### Exit criteria

- Participants correctly predict scope and next effects at the defined thresholds.
- At least one supported workflow shows meaningful reuse value after correction.
- The research demonstrates whether embodiment improves control comprehension over a conventional status panel.
- Failure results are documented as product decisions, not hidden by adding unrelated features.

## 10. M7 — public-alpha gate

### Deliverables

- Complete the threat-model checklist and independent code review of path, approval, journal, and recovery code.
- Run crash injection at every mutable step boundary.
- Run a signed-binary and installer prototype as standard user.
- Add data export/delete UI and retention documentation.
- Add dependency and secret scanning, SBOM generation, and release provenance appropriate to the chosen public-release process.
- Freeze schemas and publish migration policy for the alpha line.

### Exit criteria

- No open critical or high-severity finding in the v0.1 threat model.
- Crash recovery never silently repeats a potentially committed mutation.
- The installer requests no administrator privilege for normal operation.
- The product never claims sandboxing, autonomy, or learned behavior beyond what the implementation enforces.
- Public issue templates and security reporting instructions are active.

## 11. Parallelization guidance

After M1 contracts are stable, these workstreams may proceed in parallel:

- File Apprentice domain and fixtures;
- Agent-event adapters and simulator;
- Windows window-tracking prototype;
- WPF body presentation;
- SQLite persistence.

They converge only through reviewed contracts. Never parallelize two incompatible versions of path canonicalization, plan fingerprinting, or the execution journal.

## 12. Deferred expansion

After M7, the preferred first vertical is a bounded Blender asset apprentice implemented through a documented Blender add-on/API rather than pixel-level control. It should reuse lesson, SkillSpec, approval, receipt, correction, and capsule semantics while defining a new closed action vocabulary.

Linux exploration should begin with app-specific adapters on one supported desktop environment. General Wayland observation/control is not a port of the Windows implementation and requires its own capability and portal ADR.
