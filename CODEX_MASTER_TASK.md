# Codex Master Task: Build Tooltail v0.1

## Assignment

Implement Tooltail v0.1 from this repository seed.

The completed result is a Windows 11 desktop hypothesis build that proves two connected ideas:

1. a desktop companion can act as an agent's truthful body and control surface;
2. a user can teach that specific companion a small procedural skill, correct it, and see the correction improve the next real execution.

The canonical product loop is:

```text
real task
  -> bounded demonstration
  -> reconciled evidence
  -> explicit executable SkillSpec
  -> visible skill/body change
  -> rehearsal and exact-plan approval
  -> journaled execution and verification
  -> correction as a new version
  -> better next execution
  -> portable companion continuity
```

Do not stop at architecture scaffolding if the environment supports implementation. Work milestone by milestone, keep the repository runnable, and update `docs/BUILD_STATUS.md` at each handoff. Do not publish, push, create a remote repository, or release binaries unless explicitly authorized.

## Governing instructions

Before coding, read:

- `AGENTS.md` in full;
- `docs/PRODUCT_SPEC.md`;
- `docs/UX_INTERACTION_SPEC.md`;
- `docs/ARCHITECTURE.md`;
- `docs/SKILL_SYSTEM.md`;
- `docs/SECURITY_THREAT_MODEL.md`;
- `docs/DATA_AND_PROTOCOLS.md`;
- `docs/IMPLEMENTATION_ROADMAP.md`;
- `docs/TEST_AND_RESEARCH_PLAN.md`;
- every accepted ADR and JSON schema.

Security invariants and accepted contracts are requirements, not suggestions. If two sources conflict, follow `AGENTS.md`, document the conflict, and create/propose an ADR. Never resolve a conflict by broadening authority.

## Required v0.1 outcome

A first-time evaluator on Windows 11 can:

1. launch Tooltail as a standard user;
2. see a minimal original companion and open its accessible inspector;
3. drag it onto an eligible window and understand the resulting temporary WindowLease;
4. remove it and see the lease revoke immediately;
5. grant one local fixture folder through an explicit picker/confirmation;
6. start a bounded teaching session;
7. demonstrate 2–5 supported file moves/renames/copies inside that root;
8. stop teaching and see reconciled evidence or a precise invalid reason;
9. review a deterministic Skill Card and answer at most two targeted questions;
10. rehearse the exact candidate on a Tooltail-owned temporary copy;
11. approve an immutable plan for a fresh fixture set;
12. watch the companion reflect observation, tool use, waiting, work, completion, failure, cancellation, and revocation truthfully;
13. receive an exact verified receipt;
14. undo through a separately validated inverse plan;
15. correct an edge case, inspect the semantic version diff, and observe version 2 change the next plan;
16. export a provider-independent Companion Capsule whose skills require re-binding on import.

The evaluator must also be able to run the full core loop headlessly through fixture tools without WPF or a live Codex process.

## Forbidden shortcuts

Do not implement:

- global keyboard/mouse recording;
- screenshots or screen video;
- `SendInput` or arbitrary desktop automation;
- shell/PowerShell/cmd execution for learned skills;
- learned/general delete, overwrite, content editing, cross-volume move, network roots, or reparse points;
- an LLM deciding permissions, approval, executable effects, or body state;
- opaque prompt memory presented as a learned skill;
- silent triggers or background autonomous runs;
- admin/elevated operation;
- cloud storage, telemetry, chat, voice, marketplace, skins, social, hunger, or economy systems;
- private Codex session-file/protocol coupling;
- copied VPet/OpenPets/Clicky or other third-party art/code without an approved dependency and license review.

Do not fake the demo with hard-coded UI transitions detached from domain state.

The sole removal exception is Undo's internal `remove_created_entry` recovery effect. It is not representable in SkillSpec and may remove only an unchanged file or empty directory that the durable journal proves was absent before and created by the exact execution being undone. It requires a newly previewed/approved inverse plan and immediate grant, path, identity/hash, and emptiness checks. Any mismatch rejects the removal.

## Implementation strategy

Use a modular monolith and vertical slices. Establish the safety kernel and headless file loop before native desktop polish. The simulator is required before optional Codex integration.

Maintain one working plan in the coding session. At the start of each milestone:

1. inspect the working tree and `BUILD_STATUS.md`;
2. restate the milestone's observable exit criteria;
3. identify schema/ADR changes before code;
4. implement the smallest end-to-end slice;
5. run focused tests, then full applicable gates;
6. update docs and build status;
7. inspect the diff for authority expansion and privacy leaks.

## M0 — scaffold and prove the project graph

### Create

```text
Tooltail.sln
src/Tooltail.Domain/
src/Tooltail.Application/
src/Tooltail.Contracts/
src/Tooltail.Infrastructure.Sqlite/
src/Tooltail.Platform.Windows/
src/Tooltail.Features.FileSkills/
src/Tooltail.Adapters.AgentEvents/
src/Tooltail.Desktop/
tests/<matching test projects>/
tests/Tooltail.Architecture.Tests/
tools/Tooltail.AgentEventSimulator/
tools/Tooltail.SkillFixtureCli/
```

### Requirements

- Target .NET 10. Use `net10.0` for portable libraries/tools and `net10.0-windows` with WPF only where required.
- Keep Windows-only types out of portable public APIs.
- Add Microsoft.Extensions.Hosting composition in Desktop.
- Add xUnit test projects and one architecture-rule test per boundary.
- Use central package version management if more than a small number of package references emerge.
- Generate and commit package lock files if `Directory.Build.props` enables them.
- Add a minimal WPF shell and headless tool that both resolve the application host.
- Make CI validate JSON syntax immediately and run format/build/test once the solution exists.

### Acceptance

- clean restore, format verification, Release build, and tests pass;
- Desktop starts on Windows and exits cleanly;
- fixture CLI prints version/help with no user-data access;
- architecture tests demonstrate at least one forbidden dependency would fail.

## M1 — implement contracts and the safety kernel

### Domain types

Implement immutable, strongly typed concepts for:

- `CompanionId`, `LeaseId`, `GrantId`, `TeachingEpisodeId`, `ExampleId`, `SkillId`, `SkillVersion`, `PlanId`, `ApprovalId`, `ExecutionId`, and `RunId`;
- `WindowLease` and lifecycle;
- `LocalFolderGrant` and allowed action set;
- `TeachingEpisode` and normalized evidence state;
- `Skill` plus immutable `SkillVersion`;
- `ExecutionPlan`, operations, fingerprint, and approval;
- `ExecutionJournal`, step status, recovery status, verification, and receipt;
- normalized agent events and projected body state.

### Contract implementation

- Generate or hand-write DTOs that exactly reflect `docs/schemas/`.
- Validate schema version before mapping to domain.
- Keep transport enums separate from domain transitions where useful.
- Implement bounded JSON parsing and deterministic canonical JSON for fingerprints/exports.
- Add contract tests that parse every bundled example and reject mutated invalid fixtures.

### Path kernel

Implement an injectable Windows-aware path service that:

- accepts a selected existing local directory and captures its immutable canonical root identity;
- accepts only normalized relative paths afterward;
- rejects rooted, drive-relative, UNC, device, alternate-stream, traversal, and invalid paths;
- walks existing components and rejects any reparse/link component;
- proves source/destination containment using path-boundary-aware comparison;
- revalidates root/source/destination identity immediately before every effect;
- exposes pure validation results with stable reason codes.

Use real temporary directories for integration tests. Where Windows semantics cannot run portably, keep pure parsing/canonicalization tests portable and tag the real Windows suite.

### Plan and approval

Canonical plan fingerprint input must include at least:

- contract version;
- skill ID/version and executable semantics;
- grant ID/action set and canonical root identity;
- ordered operations;
- source relative path and stable/relevant fingerprint;
- destination relative path and expected absence/state;
- expected postconditions.

Approval stores the fingerprint and explicit decision time. Replanning, changed input, changed destination, changed root/grant, changed order, or changed skill invalidates it.

### Journal

Model append-only intent, mutation-observed/committed, verification, failure, and recovery markers. Add crash injection points. Never assume an operation that started but lacks a commit marker is safe to repeat.

### Acceptance

- adversarial corpus in the test plan passes;
- property tests cannot produce an accepted escape from the root;
- every executable-field mutation changes the fingerprint;
- incompatible/unknown contracts and primitives fail closed;
- crash-state tests produce explicit recovery status.

## M2 — implement the complete headless File Apprentice

### Snapshot service

Capture a bounded tree representation:

- normalized relative path;
- kind;
- length and selected attributes;
- creation/last-write UTC timestamps;
- reparse status;
- bounded SHA-256 content hash where policy permits;
- safe platform file identity where available.

Expose limits for entry count, per-file hash size, total hash bytes, queue depth, and duration. Cancellation must produce an incomplete snapshot, never a partial valid one.

### Teaching observation

- Start only with an active matching LocalFolderGrant.
- Capture baseline before reporting observation active.
- Configure narrow `FileSystemWatcher` filters; callbacks enqueue hints only.
- Mark overflow immediately and continue only enough to explain invalidity safely.
- Capture final snapshot after stopping and quiescing bounded hint processing.
- Reconcile baseline/final truth, aided but not dictated by hints.
- Surface created, moved/renamed, copied, modified, deleted, ambiguous, concurrent, and unsupported evidence.

Deletion and content modification may be recognized for explanation but make a v0.1 lesson non-compilable.

### Deterministic compiler

Implement the candidate language in `SKILL_SYSTEM.md` and the schema. Given 2–5 examples:

- derive source/destination pairs;
- enumerate only supported predicates/templates;
- eliminate candidates that fail any positive/negative example or safety rule;
- rank deterministically by exact coverage, assumptions, explainability, stable semantics, and collision risk;
- return differences between top candidates as typed ambiguity fields;
- ask no more than two targeted questions;
- require more examples if material ambiguity remains;
- persist examples, answers, rejected causes, and compiler version as provenance.

The same evidence must always produce the same ordered candidates.

### Skill Card

Create a view model and reusable WPF surface showing:

- name and plain-language summary;
- exact match predicates;
- exact transformation and sample before/after paths;
- granted root label and allowed actions;
- collision/unsupported behavior;
- lesson/examples and user answers;
- compiler/schema/executor compatibility;
- current lifecycle and evidence;
- semantic diff from the parent version;
- controls for rehearse, approve, disable, correct, export, and delete local history as allowed.

Never show a single opaque confidence percentage. Show concrete ambiguity and evidence instead.

### Planner and rehearsal

- Make planning pure and deterministic.
- Select only regular matching files under the fixed root.
- Render typed templates; reject missing/invalid values.
- canonicalize, detect duplicates/collisions, order directory creation, and emit postconditions;
- support no-write dry-run for every candidate;
- for mutable multi-step Draft skills, copy bounded fixtures to a Tooltail-owned temp root and run the production executor/verifier there;
- preserve a rehearsal result linked to the exact skill version.

### Executor and verifier

For each exact approved step:

1. confirm grant, skill, approval, and plan are current;
2. revalidate root, source, destination, containment, links, identity, and preconditions;
3. append journal intent;
4. call one direct allowlisted primitive;
5. append observed commit state;
6. verify postconditions and unexpected changes;
7. append verification or failure;
8. stop on mismatch, revocation, cancellation, or lost authority.

Do not continue the plan after a material failure.

### Undo

Construct inverse operations only from verified committed steps and current state. Preview and approve the inverse plan. Reject undo if it would overwrite, escape scope, or conflict with unrelated changes. Journal and verify it through the same executor.

For `copy_file` and a directory actually created by `ensure_directory`, the inverse may use the internal `remove_created_entry` recovery effect defined above. Keep it in an internal recovery contract, never the public SkillSpec action union. It must not remove a pre-existing directory, a non-empty directory, a changed copy, or an entry whose exact creation is not proven by the execution journal.

### Persistence

Implement SQLite migrations and repositories for the data model. Use transactions where domain atomicity requires them without pretending SQLite and the file system share a transaction. Recovery queries must bridge that gap explicitly.

### Fixture CLI

Provide commands with machine-readable output and nonzero failure codes:

```text
snapshot
reconcile
compile
validate
plan
rehearse
execute-fixture
verify
undo-fixture
export-capsule
```

Commands operate only on explicit fixture/temp paths and never default to a user's Desktop/Documents.

### Acceptance

- all six roadmap golden scenarios pass with exact golden outputs;
- execution plus valid undo restores the original fixture tree;
- watcher event reordering/duplication does not change reconciliation;
- overflow and unsupported effects never compile;
- no test or implementation invokes a shell;
- the whole loop runs headlessly without an LLM.

## M3 — implement Agent Body and simulator

### Canonical body states

Implement a small deterministic vocabulary, at minimum:

- `home_idle`;
- `scoped_idle`;
- `observing`;
- `working(toolKind)`;
- `parallel_work(count)`;
- `needs_input`;
- `completed_receipt`;
- `failed`;
- `paused_or_cancelled`;
- `permission_revoked`;
- `disconnected`.

Define and test precedence. Interruptive states must not be hidden by a background tool event.

### Simulator

Build deterministic scripted traces for:

- normal start/tool/complete;
- observation only;
- targeted input request and resolution;
- two parallel units;
- tool failure and run failure;
- pause/cancel;
- permission revoked mid-tool;
- adapter disconnect;
- malformed, duplicate, delayed, and out-of-order JSONL;
- event stream backpressure/limit.

Provide playback speed and single-step controls in a development-only simulator panel. CI tests state sequences without rendering.

### Original minimal body

Use simple original XAML vector geometry or programmatic shapes. Implement only enough visual language to distinguish states:

- gaze/visor for observation;
- held tool icon for active adapter/primitive;
- raised paw/turn toward user for input;
- compact edge posture for quiet/home;
- receipt card for completion;
- separated small orbiting helper markers for parallel work;
- unmistakable stop/revocation/failure poses.

Do not invest in a sprite marketplace, elaborate customization, or copied assets. Support high contrast, reduced motion, and non-color cues.

### External adapters

Implement:

1. simulator adapter;
2. generic bounded JSONL adapter;
3. optional Codex CLI adapter behind explicit configuration.

For Codex, launch only a user-configured `codex exec --json` process, read stdout/stderr with limits, map documented events defensively, and show adapter incompatibility visibly. Do not parse private state or let text grant tools/permissions.

### Acceptance

- every simulator trace has an exact state golden test;
- malformed/external drift cannot crash the UI or create authority;
- inspector explains the active state from normalized events;
- optional Codex absence does not degrade the core demo.

## M4 — implement the Windows body and WindowLease

### Windows surfaces

- `PetWindow`: transparent, topmost, absent from taskbar, ambient non-activating behavior.
- `TetherWindow`: click-through scope outline/tether; never intercept target input.
- `InspectorWindow`: normal accessible window opened explicitly.
- `HomeWindow`: normal workbench/settings/grants/skills/recovery surface if needed.

Keep commands accessible through inspector/home/tray even when dragging or animation is unavailable.

### Target discovery

During a user drag:

- use physical pointer coordinates;
- enumerate the underlying candidate rather than selecting Tooltail's own window;
- normalize to an eligible top-level root owner;
- reject Tooltail, invisible, cloaked, child-only, shell, secure, transient tool, and otherwise ineligible windows;
- capture HWND, root HWND, process ID, process start identity, application display name, and display-only title;
- show an eligible preview before lease issue.

### Lease lifecycle

- issue only on an explicit successful drop;
- expire/revoke on user removal, home, explicit revoke, target destroy, identity mismatch, process exit, ineligibility, or timeout;
- subscribe narrowly with out-of-context `SetWinEventHook` and a reconciliation timer;
- root callbacks, marshal safely, prevent reentrancy, and unsubscribe deterministically;
- expose lease state through the inspector and schema;
- never convert lease state into a ResourceGrant.

### DPI, input, and accessibility

- declare Per-Monitor V2 awareness;
- centralize physical/DIP conversions;
- test mixed scaling, negative origins, rotation, taskbar edges, maximize/minimize, monitor removal, and remote-session changes;
- use `WM_MOUSEACTIVATE`/appropriate interop so ambient clicks do not steal focus;
- ensure transparent pixels do not block the target;
- provide keyboard alternatives for attach selection, inspect, revoke, pause, cancel, and home.

### Acceptance

- the manual matrix in the roadmap passes on an interactive Windows harness;
- HWND reuse invalidates rather than transfers a lease;
- the target remains usable and focused during ambient pet behavior;
- lease creation/revocation is visible and immediate;
- no lease grants a file action in tests or code paths.

## M5 — integrate the canonical MVP

### First-run experience

Keep onboarding functional and short:

1. explain that placement is context and folder grant is authority;
2. offer simulator demo or create a safe lab folder under app-local/user-selected storage;
3. teach the three critical controls: inspector, revoke, home/cancel;
4. do not require login, model key, chat setup, or customization.

### Integrated state flow

Connect domain events to body projection for:

- lease preview/issue/revoke;
- grant issue/revoke;
- baseline capture and observation;
- lesson invalid/compiled/ambiguous;
- rehearsal;
- approval wait;
- plan execution and verification;
- completion receipt;
- failure/recovery;
- correction and tool/skill lifecycle.

The UI must render persisted truth after restart, including interrupted executions that need recovery.

### Correction

- let the user add a positive example, negative example, or explicit clarification to an existing skill;
- compile a new immutable version with parent reference;
- show semantic changes to match, transformation, policy, or scope binding;
- reset lifecycle evidence appropriately;
- require new rehearsal and exact-plan approval;
- retain old receipts with their original version;
- prove the corrected plan differs on the target edge case.

### Companion continuity

Implement export against `companion-capsule.schema.json`:

- companion ID/name/minimal presentation;
- immutable SkillSpecs and parent/provenance links;
- bounded verification/correction summaries;
- no raw paths/names, contents, transcripts, credentials, active grants, approvals, or live execution journals.

Validate before writing. On import, validate and show contents, create no authority, mark skills unbound/stale until the user creates a new grant and explicitly rebinds/rehearses. If full import is not ready for M5, complete safe export plus parser/validation and keep import disabled with a truthful explanation.

### Acceptance

- the canonical demo in `IMPLEMENTATION_ROADMAP.md` works from a clean first launch;
- every animation/state is explainable from committed state;
- correction causally changes the next plan;
- revoke, cancellation, mismatch, restart recovery, receipt, and undo all work;
- no optional Codex/model integration is needed for success.

## M6 — create the research build

Implement only local, explicit study instrumentation described in `TEST_AND_RESEARCH_PLAN.md`:

- opt-in and visible study mode;
- random study/session IDs;
- event timings, enum reason codes, and counts;
- session-local/salted tokens instead of raw names;
- previewable JSON export;
- one-click deletion;
- no automatic upload.

Create reproducible study fixtures and reset flow. Add an evaluator checklist for body comprehension, scope mental model, teaching, correction, reuse, and capsule preference. Do not fabricate user-study results; provide the instrumentation and protocol, then record only actual studies.

## M7 — harden for a bounded public alpha

Before calling the project public-alpha ready:

- complete every security control/test mapped in the threat model;
- run crash injection at every mutation boundary;
- review path, plan, permission, journal, recovery, import, and logging code independently;
- resolve all critical/high findings;
- test standard-user packaging and uninstall without touching user data unexpectedly;
- add explicit data export/delete and retention UI;
- add dependency/license/secret scanning and SBOM/release provenance appropriate to the selected workflow;
- freeze v1 schemas and document compatibility/migration policy;
- test on the declared Windows versions, DPI layouts, high contrast, reduced motion, keyboard, and screen reader;
- update SECURITY, README, BUILD_STATUS, known limitations, and release evidence.

Do not add auto-update, signing infrastructure, or public distribution credentials without explicit owner authorization. You may prepare scripts/docs and report what external authority is still needed.

## Required test suites

At minimum, deliver:

- domain transition tables;
- contract/schema examples and mutation tests;
- canonical serialization/fingerprint goldens;
- adversarial path corpus and generated containment properties;
- snapshot/reconciliation permutations;
- deterministic compiler goldens and ambiguity cases;
- planner collision and changed-input cases;
- rehearsal/production executor equivalence;
- journal crash/recovery matrix;
- verification and undo under unrelated changes;
- SQLite migration/transaction/recovery tests;
- agent event parsing, sequencing, limits, and state precedence;
- WindowLease identity/lifecycle tests with fakes plus tagged native tests;
- WPF view-model/accessibility/state snapshot tests;
- capsule export/import validation and no-authority tests;
- canonical end-to-end fixture scenario.

Store every regression counterexample as a named fixture. Tests that mutate the real user profile or require broad desktop authority are forbidden.

## UI quality bar

The visual goal is clarity, not decoration.

- Keep the companion small and unobtrusive.
- Use pose, silhouette, prop, motion, and inspector text redundantly.
- Do not communicate critical state through color alone.
- Respect reduced motion and high contrast.
- Keep ambient surfaces non-activating; use normal accessible windows for decisions.
- Make the exact target, grant, skill version, plan, approval, and receipt inspectable.
- Show precise unsupported/invalid reasons and a safe next step.
- Never anthropomorphize uncertainty in a way that hides what the system knows or can do.

## Documentation deliverables

As implementation evolves, keep these accurate:

- README quick start and limitations;
- `docs/BUILD_STATUS.md` with exact evidence;
- ADRs for changed architectural decisions;
- schema and protocol documentation;
- fixture CLI reference;
- Windows manual-test instructions;
- data locations, retention, export, deletion, and recovery guide;
- threat-model control/test mapping;
- public-alpha known limitations.

Do not rewrite the product thesis merely to match an incomplete implementation. Mark gaps honestly.

## Final acceptance checklist

The project is complete for v0.1 only when all are true:

- [ ] Clean restore, format, Release build, and applicable tests pass.
- [ ] Core and fixture tooling work without a live model or interactive desktop.
- [ ] Windows companion and inspector work as standard user.
- [ ] WindowLease context and ResourceGrant authority are visibly and technically separate.
- [ ] A 2–5 example lesson compiles deterministically or fails with a precise safe reason.
- [ ] Skill Card, provenance, compatibility, semantic diff, and lifecycle are inspectable.
- [ ] Rehearsal and exact-plan approval precede every production mutation.
- [ ] Every effect is scope-checked, journaled, verified, receipted, and safely recoverable.
- [ ] Supported executions can be undone through a validated inverse plan.
- [ ] A correction creates a new version and changes the next relevant plan.
- [ ] Body states are deterministic and truthful under success, failure, ambiguity, revocation, cancellation, parallelism, and disconnect.
- [ ] Capsule export is provider-independent and imports/rebinds without authority.
- [ ] Adversarial path and crash matrices pass.
- [ ] Accessibility and Windows DPI/monitor matrices are recorded.
- [ ] Privacy defaults contain no raw content capture or telemetry.
- [ ] No forbidden v0.1 capability or undeclared dependency was introduced.
- [ ] `docs/BUILD_STATUS.md` reports exact verification and remaining external release blockers.

## Handoff format

At each milestone and final completion, report:

1. the user-visible outcome;
2. milestone/requirements completed;
3. key files and decisions changed;
4. exact commands and test results;
5. security/privacy review notes;
6. skipped checks, known limitations, and why;
7. the next smallest safe milestone or any authority required from the owner.

The strongest success signal is not that Tooltail can do many things. It is that an evaluator can say, accurately: “I taught this one a procedure, I can see exactly what it learned and may do, and it performs the corrected procedure better next time.”
