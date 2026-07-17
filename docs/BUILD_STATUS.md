# Build Status

## Current state

M0 through M3 are implemented and verified. M4's automated WindowLease/native-shell implementation is verified, while its attended real-application/mixed-monitor/accessibility matrix remains open. M5's safe-lab engineering loop is implemented and automated end to end: teach, clarify, inspect, rehearse, approve, execute, verify, receipt, separately approved Undo, causal correction and successful v2 reuse, safe pause/cancel, durable folder-grant revocation, restart projection, and authority-free Capsule export/import/rebind. M6's engineering research build is implemented with visible local opt-in, a closed content-minimized event contract, exact preview/CreateNew export, one-click disable/delete, non-destructive session/fixture reset, and an evaluator protocol. M7 now has the release/schema/supply-chain gates, two-step crash-recoverable whole-product-memory deletion, and a verified unsigned portable Windows package whose isolated removal test preserves sibling data. Independent security/packaging review, signing/installer/distribution authority, and attended accessibility/monitor evaluation remain open. The independent first-launch evaluator and all participant-study criteria are explicitly **NOT RUN**; automated smoke is not a usability result.

All six `roadmap-m2/1` scenarios run through one exact cross-platform acceptance surface, including persisted receipt reload and separately approved Undo. The Agent Body has the canonical parameterized state projector, bounded generic JSONL adapter, 15-trace deterministic simulator with an exact state golden, an optional privacy-minimizing `codex exec --json` process adapter, and an original accessible vector body with exact inspector and development playback controls. M4 has explicit preview/drop/keyboard issue, strict HWND/process-start identity, expiry/revocation, closed contract validation, target eligibility, bounded out-of-context event hooks on a dedicated message-loop thread, one-second reconciliation, physical/DIP conversion, a standard-user Per-Monitor V2 manifest, non-activating Pet, click-through Tether, exact Inspector, and keyboard-accessible Home. M5 exposes the durable safe-lab file loop plus an exact existing-local-folder picker/confirmation grant through Home, and native Capsule import/rebind remains authority-free. M6 adds no telemetry or uploader: its separate local research sink is absent until opt-in and cannot create authority or alter a product result.

## Verified blueprint checks

Publisher verification on 2026-07-15:

| Check | Result |
| --- | --- |
| all 41 manifest paths exist | PASS |
| four Draft 2020-12 JSON schemas compile in AJV 8 strict mode with format validation | PASS |
| three JSON examples validate against their schemas | PASS |
| all seven JSONL event lines validate against the normalized event schema | PASS |
| four YAML files and shared MSBuild XML parse | PASS |
| all local Markdown links resolve | PASS |
| package contains no CJK ideographs; prose is English | PASS |
| root `AGENTS.md` size is 14,099 bytes, below the default aggregate discovery limit documented for Codex | PASS |
| final ZIP central-directory/integrity test | PASS after packaging |

## Last implementation verification

Verified on 2026-07-16 for the M2 working tree based on commit `21946e4910514dde6cefbaf6bc28890ea3326cdd`.

```text
SDK: .NET SDK 10.0.302; runtime 10.0.10
Primary target: Windows 11 x64

WSL locked restore: PASS — all 19 projects restored
WSL format verification: PASS
WSL Release build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 257 passed, 0 failed, 8 skipped
WSL skipped tests: eight tagged native Windows path/snapshot/observation/execution/rehearsal/undo tests require a Windows host

Windows environment: build 22631 / 23H2, x64
Windows locked restore: PASS — all 19 projects restored
Windows format verification: PASS
Windows forced non-incremental Release build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 263 passed, 0 failed, 2 skipped
Windows native path, snapshot, watcher, execution, rehearsal, and undo fixtures: PASS
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows because separately tagged native coverage passed
Windows WPF smoke: PASS — shell rendered and exited through --smoke-test
Cross-platform M2 golden: PASS — complete normalized-LF output SHA-256 `30ab4fe4e20ce99088820e0ea9a25aa46d971d8e05fa714c385af303d966d75b` on WSL and Windows

Completed milestone: M3 Deterministic Agent Body and simulator
Active milestone: M4 Native Windows body and WindowLease
```

Commands used from the repository root:

```powershell
dotnet restore Tooltail.sln --locked-mode
dotnet format Tooltail.sln --verify-no-changes --no-restore
dotnet build Tooltail.sln -c Release --no-restore
dotnet test Tooltail.sln -c Release --no-build --logger "console;verbosity=normal"
dotnet <path-to-Tooltail.Desktop.dll> --smoke-test
```

The Windows build used `--no-incremental` after syncing the source-only working tree to the dedicated `D:\tmp\coding\tooltail\repo` mirror.

### M3 headless checkpoint

Verified on WSL on 2026-07-16 before the headless M3 phase commit:

```text
WSL locked restore: PASS — all 19 projects up to date
WSL format verification: PASS
WSL forced non-incremental Release build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 293 passed, 0 failed, 8 expected Windows-host skips
Simulator conformance: PASS — all 15 traces matched their exact parameterized state sequences
Optional Codex invocation: NOT RUN — intentionally unnecessary; redacted JSONL and fake owned-process tests passed
```

This checkpoint covers the pure body projector, generic bounded JSONL, simulator CLI/catalog/golden, defensive Codex raw mapping, safe command construction, prompt-over-stdin handling, bounded stderr discard, abnormal exit, cancellation, timeout, and owned-child termination. It is superseded by the cross-platform M3 completion checkpoint below.

### M3 completion checkpoint

Verified on 2026-07-16 for the UI working tree based on headless M3 commit `86a7790b9ec3b31a987f7365ef8fcd0a57e68f14`:

```text
WSL .NET SDK: 10.0.302; runtime: 10.0.10
WSL locked restore: PASS — all 19 projects up to date; no project or package change followed
WSL format verification: PASS
WSL Debug desktop build: PASS — development playback panel compiled, 0 warnings, 0 errors
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 303 passed, 0 failed, 8 expected Windows-host skips
WSL simulator conformance: PASS — all 15 exact traces matched

Windows environment: build 22631 / 23H2, x64
Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 309 passed, 0 failed, 2 expected skips
Windows simulator conformance: PASS — all 15 exact traces matched
Windows Agent Body WPF smoke: PASS — parallel body rendered and exited through --agent-body-smoke-test
Windows baseline and Skill Card WPF smokes: PASS — both rendered and exited
Optional live Codex invocation: NOT RUN — intentionally unnecessary; redacted JSONL and fake owned-process tests passed
```

The first Agent Body smoke exposed a WPF `Run.Text` binding attempting to write to read-only inspector properties. All such inline bindings were explicitly changed to `Mode=OneWay`; a forced non-incremental Windows desktop rebuild and all three render smokes then passed. The Windows test skips remain the same intentional cases: unprivileged symlink creation requires Developer Mode, and the portable reparse-directory fixture is replaced by passing native Windows coverage.

### M4 lease-core checkpoint

Verified on 2026-07-16 for the uncommitted M4 lease-core working tree based on M3 completion commit `18d0497b14781dbefaf8c0251cfe25252be33bdf`:

```text
WSL locked restore: PASS — all 19 projects up to date
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 343 passed, 0 failed, 11 expected Windows-host skips

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 352 passed, 0 failed, 2 expected skips
Windows native HWND/hook tests: PASS — 3 passed, 0 failed
Windows complete Platform tests: PASS — 26 passed, 0 failed, 1 expected skip
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

The native tests use a synthetic, no-activate HWND created and closed by the test itself. They verify real PID/start identity, own-process exclusion, display-only title drift, location/destroy hook delivery, and 20 repeated hook registrations/disposals. No test sends input, activates/closes an existing host window, or stops a host process. The WPF focus/accessibility smoke and manual monitor matrix remain for the next M4 surface checkpoint.

### M4 Window Shell checkpoint

Verified on 2026-07-16 for the Window Shell working tree based on M4 core commit `445f495`:

```text
WSL locked restore: PASS — all 19 projects up to date
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 348 passed, 0 failed, 13 expected Windows-host skips

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 359 passed, 0 failed, 2 expected skips
Windows Platform tests: PASS — 29 passed, 0 failed, 1 expected skip
Windows native end-to-end lease: PASS — real synthetic HWND attach, move reconciliation, and destroy revocation
Windows baseline WPF smoke: PASS
Windows Skill Card WPF smoke: PASS
Windows Agent Body WPF smoke: PASS
Windows Window Shell apphost smoke: PASS — Home/Pet/Tether/Inspector rendered; runtime Per-Monitor V2, own-HWND styles, and ambient-not-foreground gates passed
```

All four smokes ran through `Tooltail.Desktop.exe` with the repository-local `DOTNET_ROOT`, so the generated apphost manifest—not `dotnet.exe`—owned the process DPI context. The Windows test skips remain the intentional cases: unprivileged symlink creation requires Developer Mode, and the portable reparse-directory fixture is replaced by passing separately tagged native coverage.

The first Window Shell smoke exposed setting Inspector ownership before Home was shown; ownership is now assigned after Home creates its HWND. An attempted DLL-host smoke correctly failed the Per-Monitor V2 runtime gate because `dotnet.exe` owns that process manifest. The documented shell smoke therefore uses the generated apphost. Ordinary `EVENT_OBJECT_HIDE` is not mislabeled as cloak: persistent visibility drift uses low-frequency reconciliation, real cloak remains immediate, and close uses the precise destroy event.

### M5 correction and continuity-core checkpoint

Verified on 2026-07-16 for the M5 correction/capsule working tree based on M4 shell commit `d6f8945`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 357 passed, 0 failed, 13 expected Windows-host skips
WSL FileSkills focused tests: PASS — 190 passed, 0 failed
WSL Fixture CLI focused tests: PASS — 6 passed, 0 failed

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 368 passed, 0 failed, 2 expected skips
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

The shared capsule path now performs bounded semantic validation before producing bytes, canonicalizes SkillSpecs, checks immutable version lineage and grant-binding consistency, and readbacks the closed contract. Import remains deliberately disabled: its parser returns a preview with no authority and mandatory grant/rebind/rehearsal state. The Fixture CLI uses this same implementation rather than a fixture-only validator.

Correction accepts explicit positive, negative, or typed clarification evidence, retains all parent positive-example IDs, compiles exactly `n + 1` with a parent reference, and emits a deterministic category-level semantic diff. A provenance-only change is rejected as `correction.no_executable_change`; tests prove a negative example and explicit clarification change matching on the target edge case. Accepted output is Draft and explicitly requires rehearsal plus a new exact-plan approval. Desktop orchestration and persistence readback remain the next M5 checkpoint.

### M5 restart read-model checkpoint

Verified on 2026-07-16 for the M5 persistence working tree based on correction/continuity commit `e82b406`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 361 passed, 0 failed, 13 expected Windows-host skips
WSL SQLite focused tests: PASS — 21 passed, 0 failed

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 372 passed, 0 failed, 2 expected skips
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

The bounded workspace projection can discover a first-run or existing companion and reconstruct exact grant revocation, current skills, complete immutable version history for export, lesson phase/evidence, recent executions, and receipt presence after restart. It revalidates grant fingerprints, current SkillSpecs, and each referenced canonical plan. An incomplete execution remains a reason-coded recovery candidate with `not_started`/inspection status and is never replayed. Tests cover restart, revoked grants, v1→v2 current-version movement, incomplete execution discovery, and tampered grant authority.

### M5 persisted first-run shell checkpoint

Verified on 2026-07-16 for the Desktop startup working tree based on restart read-model commit `112b997`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 362 passed, 0 failed, 13 expected Windows-host skips
WSL SQLite focused tests: PASS — 22 passed, 0 failed
WSL architecture focused tests: PASS — 19 passed, 0 failed

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 373 passed, 0 failed, 2 expected skips
Windows persisted Window Shell apphost smoke: PASS — isolated smoke SQLite initialized, one companion was bootstrapped, startup recovery scan completed, and Home/Pet/Tether/Inspector style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

Home now presents content-minimized persisted grant, skill, teaching, execution, receipt-presence, and recovery state. A clean launch creates one local provider-independent companion identity and no grant; later launches restore that identity. Onboarding explicitly requires no login, model key, chat setup, telemetry, or customization and repeats that window context is not folder authority. Smoke state uses a unique Tooltail-owned temporary database rather than normal user state. The safe-lab grant and teaching controls remain the next M5 surface.

### M5 safe-lab grant checkpoint

Verified on 2026-07-16 for the safe-lab Desktop working tree based on persisted shell commit `9f19e68`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL architecture focused tests: PASS — 19 passed, 0 failed

Windows forced non-incremental Desktop Release build: PASS — 0 warnings, 0 errors
Windows safe-lab Window Shell apphost smoke: PASS — first-run companion, isolated application-data root, exact grant, three CreateNew synthetic PDF fixtures, persisted grant reason, and existing style/focus gates all passed
Full solution tests after the immediately preceding Desktop startup commit: PASS — WSL 362 passed/13 expected skips; Windows 373 passed/2 expected skips
```

The explicit Home action creates a fresh grant-ID directory only below Tooltail's captured local application-data root. It rejects reparse or non-fixed roots, binds and revalidates every absent directory/file immediately before creation, uses `CreateNew`, never overwrites or removes an existing entry, and then persists one seven-day closed-action `LocalFolderGrant`. The three small PDF fixtures contain only Tooltail-authored synthetic bytes. This setup is not a SkillSpec effect and grants no shell, delete, content-edit, network, or WindowLease authority.

### M5 teaching observation checkpoint

Verified on 2026-07-16 for the teaching workflow working tree based on safe-lab commit `0a0cbf9`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 365 passed, 0 failed, 13 expected Windows-host skips
WSL FileSkills focused tests: PASS — 193 passed, 0 failed

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 376 passed, 0 failed, 2 expected skips
Windows teaching Window Shell apphost smoke: PASS — isolated first run, safe grant, baseline-before-watcher, two real same-pattern file moves, watcher quiescence, authoritative final snapshot, complete reconciliation, two persisted examples, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

Home now exposes keyboard Start teaching and Stop and reconcile actions. The state service persists every legal episode transition rather than skipping states, stores closed bounded snapshot documents, invalidates a lesson on baseline/storage failure, and records only normalized examples from supported rename/move/copy effects. Stop first quiesces watcher hints, then captures the final snapshot and derives reconciliation; incomplete, overflowed, ambiguous, concurrent, or unsupported evidence cannot become a compilable result. The smoke demonstration mutates only its newly created Tooltail-owned fixture and neither deletes nor overwrites an entry.

### M5 Draft compilation and Skill Card checkpoint

Verified on 2026-07-16 for the clarification/Skill Card working tree based on teaching workflow commit `ca11b73`:

```text
WSL locked restore: PASS — all 19 projects up to date
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 365 passed, 0 failed, 13 expected Windows-host skips
WSL FileSkills focused tests: PASS — 193 passed, 0 failed
WSL architecture focused tests: PASS — 19 passed, 0 failed

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 376 passed, 0 failed, 2 expected skips
Windows clarified-Draft Window Shell apphost smoke: PASS — two real normalized examples, bounded typed questions, selected closed answers, immutable Draft persistence/readback, populated Skill Card render, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

The reconciliation and compiler views now share each exact generated example ID. Home exposes one keyboard compilation action and closed-choice clarification controls; ambiguity persists no skill, while a ready result stores one canonical immutable Draft version and renders its bounded samples, policy, verification, exact grant capabilities, and teaching evidence. The compiler still cannot approve, plan, rehearse, or execute. A populated Skill Card Windows smoke also caught and corrected scalar `Run.Text` bindings that required explicit one-way current-item paths.

### M5 durable rehearsal and unapproved plan checkpoint

Verified on 2026-07-16 for the rehearsal working tree based on Draft compilation commit `3ac33a8`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 366 passed, 0 failed, 13 expected Windows-host skips
WSL rehearsal-focused FileSkills tests: PASS — 5 passed, 0 failed
WSL rehearsal SQLite integration test: PASS — 1 passed, 0 failed
WSL architecture focused tests: PASS — 19 passed, 0 failed

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 377 passed, 0 failed, 2 expected skips
Windows durable-rehearsal Window Shell apphost smoke: PASS — native source snapshot, bounded owned staging, canonical temporary plan, persisted rehearsal-only approval, shared executor/verifier, receipt readback, identity-checked cleanup, persisted temporary-grant revocation, fresh production snapshot, one canonical unapproved production plan, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

Home now exposes keyboard rehearsal and renders the exact production plan ID, SHA-256 fingerprint, grant, expiry, operation list, expected results, verified rehearsal step count, cleanup result, and temporary-grant retirement. The temporary copy uses the production executor/verifier but a purpose-bound rehearsal approval; SQLite consumes that approval with journal open. Rehearsal passes only when its receipt verifies, its owned workspace is safely removed, and its temporary grant is durably revoked. A fresh authoritative source snapshot then creates a separate `planned` production plan with no approval. The source safe-lab tree remains unchanged by rehearsal, and the current Draft still cannot execute there.

### M5 exact approval, production execution, and receipt checkpoint

Verified on 2026-07-16 for the production execution working tree based on durable rehearsal commit `1671c63`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 367 passed, 0 failed, 13 expected Windows-host skips
WSL current-authority SQLite integration test: PASS — 1 passed, 0 failed
WSL architecture focused tests: PASS — 19 passed, 0 failed

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 378 passed, 0 failed, 2 expected skips
Windows production Window Shell apphost smoke: PASS — canonical planned-document readback, deliberate exact-fingerprint decision, Draft-to-Approved transition, production-purpose approval, atomic approval consumption/journal open, current SQLite authority at each boundary, one real Tooltail-owned file move, verified postcondition/evidence, durable receipt readback, Practiced transition, populated receipt render, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

Home now provides one deliberate action beside the exact displayed fingerprint and explains that material replanning requires a new decision. Production first reloads and compares the canonical persisted plan, then persists the legal lifecycle transition and one production-only approval. The SQLite journal consumes that approval once; the executor reloads current persisted grant/skill authority before every effect and verification boundary. Success requires the exact destination identity/hash evidence, journal, receipt, and Practiced lifecycle to agree. The receipt surface exposes its IDs, production fingerprint, completion, bounded Undo window, verified steps, destination identity, and content hash. This checkpoint moved only the remaining Tooltail-authored synthetic safe-lab file and performed no overwrite or deletion.

### M5 separately planned and approved Undo checkpoint

Verified on 2026-07-16 for the Desktop Undo working tree based on production execution commit `b5dea91`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 367 passed, 0 failed, 13 expected Windows-host skips

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 378 passed, 0 failed, 2 expected skips
Windows production-and-Undo Window Shell apphost smoke: PASS — strict production receipt/journal reload, current authoritative snapshot, reverse-ordered canonical recovery preview with no mutation, recovery-plan readback, new undo-purpose approval, distinct recovery journal, immediate identity/precondition revalidation, verified move-back restoration, original rollback link, separate recovery receipt readback, no residuals, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

Home now separates **Plan Undo** from **Approve exact Undo and restore**. Preview shows the recovery plan ID/fingerprint, original execution/fingerprint, expiry, closed recovery primitives, exact source/destination, and expected unchanged identity; the smoke proves preview performs no mutation. Execution reloads the canonical recovery document and both original evidence records before issuing a new undo-only approval. The shared recovery path journals and verifies each inverse, appends the original-step rollback link only after verification, preserves the production receipt, and emits a separate linked recovery receipt. This demo uses `move_back`; it performs no removal. The safe-lab tree after Undo exactly restores the pre-production tree.

### M5 causal correction v2 checkpoint

Verified on 2026-07-16 for the Desktop correction working tree based on Undo commit `3f91777`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 367 passed, 0 failed, 13 expected Windows-host skips

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 378 passed, 0 failed, 2 expected skips
Windows correction Window Shell apphost smoke: PASS — retained v1 evidence lineage, changed closed typed clarification, deterministic v2 compilation, target-edge matcher change, parent-linked immutable Draft persistence, populated semantic diff, no v2 approval/rehearsal/plan, retained Practiced v1 and all three historical receipts, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

The explicit correction action broadens the demonstrated origin and filename scope through two closed typed answers. It is accepted only because the deterministic compiler produces version 2 with `parentVersion = 1`, an executable match diff, and a changed result on a retained destination edge case. SQLite keeps v1 and its production/Undo receipts unchanged while making v2 the current Draft with no `approved_utc`. Home renders the parent semantic diff and re-enables rehearsal; the v2 compiler cannot reuse v1 rehearsal or approvals.

### M5 authority-free Companion Capsule export checkpoint

Verified on 2026-07-16 for the Desktop capsule-export working tree based on correction commit `750dfde`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 367 passed, 0 failed, 13 expected Windows-host skips

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 378 passed, 0 failed, 2 expected skips
Windows capsule-export Window Shell apphost smoke: PASS — complete immutable v1/v2 history export, parent-lineage and semantic parser readback, bounded evidence projection, CreateNew write below Tooltail-owned Exports, no physical lab path in bytes, no imported authority, native import disabled, mandatory rebind/rehearsal preview, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

The explicit **Export Capsule** action reconstructs all immutable versions of each current skill from SQLite, canonicalizes and semantically validates the complete document, and parser-readbacks its authority-free import preview before writing. It exports bounded lifecycle/evidence summaries but no physical root, live grant, approval, plan, journal, receipt, Undo material, credential, content, transcript, or executable authority. The file is created once below Tooltail-owned application storage; the UI truthfully reports that import is disabled and a future import requires a new user grant, explicit rebind, and rehearsal.

### M5 deterministic integrated body-truth checkpoint

Verified on 2026-07-16 for the integrated File Apprentice body working tree based on capsule-export commit `7f6e78d`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 370 passed, 0 failed, 13 expected Windows-host skips

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 381 passed, 0 failed, 2 expected skips
Windows integrated-body Window Shell apphost smoke: PASS — clean persisted home, active file scope, committed observation, reconciled/ambiguous/Draft/unapproved-plan input states, closed File versus Other tool props, verified production and Undo receipt states, corrected-Draft precedence over capsule output, ambient Pet read-only projection, and prior style/focus gates passed
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
```

`CompanionActivityProjector` now projects closed internal activity facts through the same interruptive vocabulary used by normalized agent runs. The File Apprentice supplies typed tool props only after an eligible explicit use-case action begins, and it selects observation, input, failure, cancellation, or receipt poses only from bounded workflow outcomes. Startup reconstructs body truth from the current grant, current skill, latest lesson/execution, and inspect-first recovery scan. The ambient Pet consumes the projection read-only; a WindowLease may add context scope only when no stronger apprentice state exists. Architecture tests prove that the body view models and vector control reference no permission gateway, executor, file mutation, or process boundary.

### M5 canonical reuse and critical-control audit checkpoint

Verified on 2026-07-16 for the final M5 engineering working tree based on integrated-body commit `f6878ab`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 370 passed, 0 failed, 13 expected Windows-host skips

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 381 passed, 0 failed, 2 expected skips
Windows final-M5 Window Shell apphost smoke: PASS — clean first launch, exact safe-lab grant, teaching/reconciliation/clarification, Draft v1 rehearsal and production, receipt, separately planned/approved Undo, causal v2 correction, authority-free capsule, keyboard safe-pause during v2 rehearsal with no exposed plan and successful explicit retry, distinct v2 production fingerprint, verified v2 execution/receipt, clean persisted restart reconstruction, exact durable folder-grant revocation with unchanged lab tree, and permission-revoked restart with all old-grant planning/execution/Undo controls disabled
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows and separately tagged native coverage passes
Independent first-launch evaluator: NOT RUN — no usability result is inferred from apphost automation
Attended M4 matrix: NOT RUN rows remain open in docs/WINDOW_SHELL_TEST_MATRIX.md
```

Home and Inspector now expose separate **Unbind context** and **Revoke folder grant** controls. Resource revocation reloads the exact current grant, persists its terminal state, requests cooperative cancellation for any active use case, deletes nothing, and disables future compile/rehearse/approve/execute/Undo authority under that grant. Cancel signals the one linked active-operation token. Safe Pause intentionally uses the same fail-safe boundary and never auto-resumes mutable work. The final smoke cancels a real corrected-skill rehearsal before a production plan appears, retries explicitly, obtains a plan whose fingerprint differs from v1, executes v2 successfully, then proves both successful and revoked restart projections.

The mismatch audit remains layered rather than using unsafe smoke hooks: canonical fingerprint tests prove every material plan mutation changes approval identity; `SourceChangedAfterIntentFailsBeforePrimitiveEvenWhenMetadataIsRestored` proves source mismatch stops before a primitive; destination/link/root drift, current grant/skill drift, approval consumption races, and tampered persisted plan/authority tests all pass in the same full suite. No test-only bypass was added to production Desktop composition.

### M6 minimized research-event contract checkpoint

Verified on 2026-07-16 for the research-contract working tree based on final-M5 commit `0aa4773`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL focused contract tests: PASS — 15 passed, 0 failed, 0 skipped

Windows locked restore: PASS — all 19 projects up to date in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows focused contract tests: PASS — 15 passed, 0 failed, 0 skipped
```

ADR 0007 accepts a separate, explicit, local-only research sink. The fifth Draft 2020-12 schema and strict DTO/parser define one bounded JSONL event with random identities, UTC timing, closed event/body enums, stable reason codes, bounded numeric summaries, and an optional 64-hex session-local salted token. The shape contains no field for a raw path, filename, title, content, prompt, transcript, user, machine, credential, or free-form text. Unknown fields/versions/enums, non-UTC time, malformed tokens, and oversized payloads fail closed. This checkpoint creates no sink, consent state, event file, or upload behavior yet.

### M6 local research-store checkpoint

Verified on 2026-07-16 for the local research-store working tree based on contract commit `6531ef6`:

```text
WSL format: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL focused local-research tests: PASS — 8 passed, 0 failed, 0 skipped
WSL focused architecture tests: PASS — 20 passed, 0 failed, 0 skipped
Windows verification: NOT RUN for this intermediate checkpoint
```

The new portable `Infrastructure.LocalResearch` boundary remains absent on a default first launch and creates no research directory until explicit opt-in. Once enabled it accepts only the closed validated research-event contract, bounds the JSONL file to 1,000 events/8 MiB, uses random study/session IDs and an in-memory per-session cryptographic salt, strictly reads back every append, previews exact local JSONL, and exports only with `CreateNew`. Deletion first preflights exact Tooltail-owned research artifacts, durably disables consent, then truncates those fixed files without a learned delete primitive or arbitrary path. Unexpected files fail deletion before either consent or event data changes. Restart, invalid/incomplete data, export round-trip, default-off, reparse-root rejection, and deletion retention behavior are covered with isolated real directories. There is no uploader, network client, authority DTO, raw-path field, or product-workflow integration in this checkpoint.

### M6 final engineering research-build checkpoint

Verified on 2026-07-16 from M6 implementation commit `d12d19d` plus the final lock-file, WPF binding, and native-test reliability working tree:

```text
WSL locked restore: PASS — all 21 projects restored from committed lock files
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL final tests: PASS — 385 passed, 0 failed, 13 expected Windows/interactive skips

Windows locked restore: PASS — all 21 projects restored in the dedicated D: mirror
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows final tests (non-parallel rerun): PASS — 396 passed, 0 failed, 2 expected skips
Windows focused native close/revoke race repeats: PASS — 5/5
Windows expanded M6 Window Shell apphost smoke: PASS — exit 0

Independent first-launch evaluator: NOT RUN
Participant Studies A–D: NOT RUN
Attended M4 real-application/mixed-monitor/accessibility matrix: NOT RUN
```

Home visibly says research is OFF by default and discloses the exact excluded content before opt-in. Opt-in creates a random study/session and ephemeral salt; workflow instrumentation records only closed timing, reason, count, version, body, token, and rating fields. Teaching, clarification, rehearsal, production/Undo approval decisions, execution, Undo, correction, capsule export, window/folder revocation, inspector open, pause, and cancel are connected after or alongside their existing committed outcomes. Research failure is observational and cannot replace the product result, create a grant/lease/approval, invoke the executor, or change body authority.

The store now rejects cross-study data and non-contiguous per-session sequences during every strict readback. Session reset rotates the random session ID and salt without removing event history. The explicit study-fixture reset then uses the normal exact-grant revocation and safe-lab creation paths; it never removes the prior lab. The evaluator checklist requires a new marked absolute workspace for headless reset, keeps qualitative/screen-recording consent outside the product, and leaves every actual participant result NOT RUN.

The first expanded apphost launch failed before product work because WPF attempted a default TwoWay binding to the intentionally read-only JSONL preview. The final XAML declares `Mode=OneWay`; affected WSL/Windows Desktop builds and the complete apphost flow then passed. A high-load parallel cross-OS rerun also showed the synthetic native target close may be observed first as `TargetIneligible` by reconciliation or as `TargetDestroyed` by the hook. Both terminal paths revoke immediately; the dedicated hook test still requires the destroy signal. The lifecycle test now accepts those two closed terminal reasons, passed five focused repetitions, and the final non-parallel Windows suite passed.

The two Windows skips remain explicit: unprivileged symbolic-link creation is unavailable without Developer Mode, and the portable reparse-directory fixture is intentionally non-Windows. The 13 WSL skips require Windows or an interactive Windows host. No Codex process, network service, host application file, unrelated process, arbitrary user folder, or participant data was used.

### M7 crash, schema-freeze, and supply-chain checkpoint

Verified on 2026-07-16 for the M7 hardening working tree based on M6 commit `cfd1055`:

```text
WSL locked restore: PASS — 23 projects
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 397 passed, 0 failed, 13 expected Windows/interactive skips
Focused production/Undo crash-boundary cases: PASS — 17 passed
ReleaseAudit integration: PASS — 2 passed
Local release audit: PASS — 61 dependencies, 10 frozen files, tracked-file secret scan, SPDX/evidence generated
NuGet vulnerability query: PASS — 0 findings
NuGet deprecation query: PASS — 0 findings after xUnit v3 migration
Windows verification for this intermediate M7 checkpoint: NOT RUN
```

Production crash injection now includes the exact pre-primitive boundary. Undo crash injection covers journal open, intent, pre-primitive, post-primitive, observed, committed, verified, original rollback link, and receipt persistence, asserting exact recovery-event counts, mutation state, rollback-link count, and receipt presence. The matrix does not claim the external packaged-process termination campaign has run.

All ten v1 schema/example surfaces have normalized-LF freeze hashes and a documented strict-reader/migration policy. CI's stale four-schema expectation is corrected to five and the portable project list includes LocalResearch and ReleaseAudit. Every action reference is pinned to a full commit. The BCL-only release tool cross-checks every lock-file dependency against restored NuGet license metadata, scans only tracked files for bounded secret patterns, and produces local SPDX 2.3 plus content-minimized evidence. Test projects moved from deprecated xUnit v2 to xUnit v3; custom platform Fact attributes now preserve caller source information. `xUnit1051` is explicitly suppressed only in test projects because cancellation/race tests deliberately inject their own exact tokens.

The 61-package SBOM reports runtime MIT/Apache-2.0 dependencies separately from test-only tooling. JsonSchema.Net, JsonPointer.Net, and Json.More.Net NuGet binaries are recorded as `LicenseRef-OSMFEULA`; owner/legal review before revenue use remains an external release blocker. Microsoft Testing Platform telemetry packages are test-only and CI opts out of both .NET CLI and test-platform telemetry. No runtime dependency, analytics SDK, publish action, signing material, or credential was added.

Current evidence and known limitations:

- All four bundled JSON examples validate against Draft 2020-12 schemas and strict DTO parsers; incompatible versions, unknown fields/actions, and oversized payloads fail closed. `JsonSchema.Net` is test-only.
- The portable adversarial corpus covers rooted/drive/UNC/device/ADS/traversal/mixed/repeated separators, reserved names, trailing aliases, NFC, case-only changes, long bounds, root/source/destination identity drift, and a link introduced after planning.
- The real Windows probe opens entries with `FILE_FLAG_OPEN_REPARSE_POINT` and records canonical handle path, volume serial, and file identity. This host could not create an unprivileged symlink, so that one native creation test is visibly skipped; injected reparse/race tests pass portably.
- Production, rehearsal, and recovery use the same direct allowlisted executor path. Authority and root/source/destination state are revalidated at each durable boundary; no implementation invokes a shell for a learned effect.
- Canonical plan JSON and SHA-256 have a fixed golden vector that passes on WSL and Windows. Material plan, skill, grant, root, input, destination, ordering, precondition, or postcondition changes invalidate approval.
- Crash-prefix tests distinguish not-started, started-uncommitted, committed-unverified, verified, recovery-required, and rolled-back states. Started-without-commit never permits automatic replay.
- SQLite v1 uses 17 strict tables, foreign keys, WAL/full synchronization, checksummed migrations, append-only journal/receipt triggers, serialized writers, and read-only recovery on unknown or damaged state. Repository tests cover restart replay, exact retry, approval races, tampered rows, missing receipts, and standard-plus-Undo receipt round trips.
- SQLite rows are treated as untrusted projections: skill lifecycles and journals replay domain transitions, canonical plans must match every executable field, and receipt evidence must agree with its plan and linked journals.
- The Fixture CLI requires a new or marked absolute workspace and fixed owned `root`, `artifacts`, `state`, and `temp` children. It never defaults to current/user folders, bounds artifacts to 4 MiB, rejects reparse/link ancestry and file slots, and never adds a primitive or shell surface.
- The exact six-scenario result includes complete SkillSpecs, canonical plans, rehearsal/execution receipts, original/final trees, canonical recovery plans, Undo receipts, and restored trees. Reordered/duplicated watcher hints are byte-invariant; overflow and unsupported evidence are proven unable to compile.
- The compiler now emits closed lower-case/hyphen transforms and typed last-write year/month variables, and asks a targeted fixed-text-versus-metadata question when both explain the same date-looking examples.
- Independent fixture verification reloads the durable journal and receipt and compares a fresh authoritative snapshot with the exact post-execution snapshot. A later tree or metadata change fails verification.
- Fixture capsule export writes one validated current SkillSpec and bounded evidence summary to the owned artifact directory. It exports no grant, approval, lease, journal, receipt, Undo authority, physical path, content, transcript, or credential, and mandates `require_user_rebind`.
- CI workflow execution on GitHub has not run; equivalent locked restore, format, Release build, and test commands passed locally on Linux and Windows.
- The M3 simulator is the provider-independent CI oracle: 15 exact traces cover normal, parallel, input, block, failure, cancellation, revocation, disconnect, malformed, duplicate, delayed, out-of-order, oversized, and event-limit behavior without Codex or an LLM.
- Generic normalized JSONL is bounded by line, total byte, event count, and fixed read-buffer limits. The optional Codex adapter launches only an absolute user-configured executable, passes the prompt over stdin, requests JSON/ephemeral/read-only/ignored-user-config operation, hashes provider item IDs, discards raw content and stderr, and fails visibly on drift.
- Codex adapter tests use a redacted public-JSONL fixture and fake owned child processes; no test launches Codex, reads private session/rollout state, uses credentials, or requires network access.
- The M3 body uses only original XAML vector geometry, dynamic system brushes, static non-color cues, and explicit labels. It has no image/media asset or continuous animation; high contrast and reduced-motion paths preserve every state and control.
- The integrated File Apprentice body uses closed accepted activity facts, exact File/Other tool props, persisted restart reconstruction, and deterministic precedence. Working never implies success, unapproved plans and Drafts remain input-visible, and only verified durable results select a receipt pose; the body owns no effect boundary.
- Desktop critical controls now cooperatively stop the single active use case and durably revoke the exact folder grant. Safe Pause is deliberately cancellation-with-no-auto-resume, not suspended mutable execution. The Windows smoke proves a cancelled v2 rehearsal exposes no production plan, retry is explicit, v2 succeeds, revocation preserves the tree, and restart cannot reuse old authority.
- The inspector shows exact normalized event identity, sequence, UTC time, source, type, severity, allowlisted data, disposition, active tools/questions/subagents, scope, parameterized body, and reason without retaining provider raw content.
- The M4 lease core, native HWND/hook adapter, ambient WPF surfaces, manifest/runtime DPI gate, keyboard alternatives, own-style/focus smoke, and native synthetic-window integration pass. The attended real-application, mixed-monitor/rotation/taskbar/remote-session, click-through, screen-reader, high-contrast, and text-scaling rows remain explicitly NOT RUN in `docs/WINDOW_SHELL_TEST_MATRIX.md`.
- The portable fixture probe intentionally derives deterministic test identities and is not the native production Windows identity source. Native capsule import and retention maintenance remain later milestones; native import is intentionally disabled.

### M7 explicit local-data lifecycle checkpoint

Verified on 2026-07-16 for the staged working tree based on M7 release-gate commit `13a83e0`:

```text
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 23 projects, 0 warnings, 0 errors
WSL full tests: PASS — 413 passed, 0 failed, 13 expected Windows/interactive skips
WSL focused local-state deletion: PASS — 13 passed
WSL focused lifecycle UI/architecture: PASS — 3 passed
Local ReleaseAudit: PASS — 61 reviewed dependencies, 10 frozen schema/example files, 376 tracked files

Windows locked restore: PASS — 23 projects
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows full serial test run: PASS — 423 passed, 0 failed, 3 expected skips
Windows focused local-state deletion: PASS — 12 passed, 1 expected non-Windows symlink-fixture skip
Windows focused lifecycle UI/architecture: PASS — 3 passed
Windows expanded Window Shell apphost smoke: PASS — exit 0 / WINDOW_LIFECYCLE_SMOKE_PASS
```

ADR 0008 accepts explicit whole-product-memory deletion as a fixed application-maintenance boundary, never a learned file primitive. Home exposes the exact deleted and preserved category lists, a five-minute single-use request, and the case-sensitive `DELETE LOCAL STATE` phrase. The controller refuses an active use case or teaching observation, durably revokes the current folder grant, and clears the separate research sink before the SQLite boundary can start.

The deletion service accepts no caller path, pattern, model/SkillSpec/plan input, or recursive directory operation. It requires the exact local fixed-volume `state/tooltail.db` layout, rejects reparse ancestry, writes a bounded `CreateNew`/write-through intent containing a root fingerprint rather than a raw path, and removes only the database, WAL, SHM, and intent. Cancellation is accepted before intent and not after. Startup validates and completes every incomplete prefix before SQLite initialization; invalid, oversized, wrong-root, or unsafe intent state stops without opening or replacing the database.

The real Windows apphost smoke performs the UI preview, proves a lower-case phrase cannot enable deletion, uses the exact phrase, and then verifies database/WAL/SHM/intent absence together with continued existence of the safe-lab result and authority-free Capsule. Unit tests additionally cover authorization mismatch/expiry, pre-intent cancellation, every incomplete prefix, the return gap after intent removal, malformed/oversized/wrong-root intent, a directory substituted into a fixed file slot, unrelated-state preservation, and portable linked ancestry.

The first Windows build attempt correctly stopped because the source-only mirror had no restored assets for the two M7 ReleaseAudit projects and still held pre-xUnit-v3 assets; a locked restore corrected that cache state. The first full Windows test attempt later reached one environment failure because the WSL-launched `cmd.exe` PATH had no `git.exe`, which ReleaseAudit uses only for `git ls-files`. Official Git for Windows MinGit 2.55.0.windows.3 was downloaded into the dedicated D: tooling directory, verified against release SHA-256 `f48e2d2dc74a24454adc6d8fd0ac25bf9c2386f19cfb06202b9465aaad4f9f05`, and added only to that test command's PATH. A fresh Git index was initialized only in the source mirror; `preparation/` and the unrelated untracked `droid.resume.txt` were not added. The final complete Windows run above then passed.

Current retention is disclosed rather than implied: SQLite product records remain until whole-memory deletion; Desktop Undo eligibility is one day but durable evidence is not silently purged; research is capped at 1,000 events/8 MiB and explicitly deletable; safe rehearsal cleanup remains identity checked. Per-lesson, per-skill, and per-receipt deletion/retention maintenance are not implemented and remain a declared limitation. `DATA_LIFECYCLE.md` documents exact locations, export boundaries, recovery behavior, and the rule that uninstall must preserve `%LOCALAPPDATA%\Tooltail` unless the user explicitly deletes state in-app.

The three Windows skips are explicit: unprivileged symbolic-link creation is unavailable without Developer Mode, the portable reparse-directory fixture is intentionally non-Windows, and the local-state deletion symlink-ancestry fixture is likewise portable/non-Windows. Native fixed-drive layout and the end-to-end deletion boundary pass in the Windows apphost.

### M7 portable package and uninstall checkpoint

Verified on 2026-07-17 for the packaging working tree based on local-data lifecycle commit `6390247`:

```text
WSL generic locked restore: PASS — 23 projects
WSL Desktop win-x64 locked restore: PASS — all 9 production projects
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL full tests: PASS — 420 passed, 0 failed, 13 expected Windows/interactive skips
WSL focused portable-package tests: PASS — 5 passed
WSL focused packaging architecture tests: PASS — 2 passed
WSL staged-tree ReleaseAudit: PASS — 61 dependencies, 10 frozen contracts, 383 tracked files

Windows generic locked restore: PASS — 23 projects
Windows Desktop win-x64 locked restore: PASS — all 9 production projects
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows full serial test run: PASS — 430 passed, 0 failed, 3 expected skips
Windows package-portable.ps1: PASS — self-contained publish, deterministic pack/readback, packaged apphost, and uninstall fixture
Packaged Window Shell apphost: PASS — exit 0
Portable removal: PASS — exact marker-bound program directory absent; sibling local-data sentinel byte-identical
```

`src/Directory.Build.props` declares `win-x64` as the sole v0.1 distribution RID for production restore graphs while every portable library retains its `net10.0` TargetFramework. Generic and RID-specific locked restores both pass. The Desktop publish profile is self-contained, untrimmed, multi-file, non-ReadyToRun, and symbol-free; it retains the standard-user `asInvoker`, `uiAccess=false`, Per-Monitor V2 apphost and adds no installer/updater behavior.

The BCL-only ReleaseAudit extension captures a bounded non-reparse publish tree, rejects debug/state/archive material, and requires the complete apphost/CoreCLR/WPF payload. It creates a sorted fixed-timestamp ZIP with a closed manifest binding every path/length/SHA-256, then strictly reads every entry back before creating the archive sidecar. Synthetic tests prove byte-identical packaging, unmanifested/prohibited entry rejection, fixed CLI output paths, failed-launch retention, and exact program-only removal with sibling data preservation.

Two independent real publishes of the same final input produced the same archive SHA-256 `384a088f4859cee8d5e6a9a187159bb728dce9de4b6b311a1de5c80255d89141`. Each package contained 441 payload files and 177,572,227 uncompressed bytes; the ZIP was approximately 71 MiB. The manifest truthfully records version `0.1.0`, `win-x64`, `selfContained = true`, `isCodeSigned = false`, `%LOCALAPPDATA%\Tooltail` as the separate data root, and `program_directory_only` removal.

The uninstall verifier extracts only below a new fixed repository-artifact fixture, writes its own marker and sibling local-data sentinel with `CreateNew`, launches only that packaged Tooltail apphost, checks the full program tree has no reparse entries, and then recursively removes only the exact marker-bound `program` directory. If launch or validation fails, no removal occurs. The success evidence proved the sibling sentinel remained byte-identical. No normal user profile, installed program, registry entry, service, startup task, unrelated process/file, or host application was changed or removed.

The first real packaging attempt correctly stopped at RID locked-restore mismatch; adding a production-only RID lock graph made both restore modes pass. A later publish was correctly rejected because referenced-project PDBs were present and a name heuristic misclassified the legitimate LocalResearch assembly. Global symbol suppression and extension-based contamination policy fixed those findings; failed outputs were retained by directory rename rather than deleted. The final hardening rerun retained prior evidence and reproduced the same archive hash.

CI now defines a pinned Windows `portable-package` job that builds/verifies locally and checks uninstall evidence without uploading the ZIP. Actual GitHub workflow execution is **NOT RUN**. The artifact is unsigned and has no installer, SmartScreen/reputation evidence, code-signing identity, protected credential, auto-update, or publication authority. It must not be represented or distributed as a public alpha.

The three Windows skips remain the same explicit link-creation cases recorded in the lifecycle checkpoint. Independent M4 accessibility/monitor checks, M6 participant studies, independent security/packaging review, per-object retention, signed installer work, and distribution remain NOT RUN/external blockers.

Next smallest safe engineering task: run the final M7 traceability/known-limitations audit and close every remaining automatable documentation/test gate without claiming the attended, independent, signing, installer, or participant work that requires external authority.

### M7 final automatable traceability checkpoint

The consolidated [`KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md) ledger now maps the master-task final acceptance delta and separates committed automated evidence from PARTIAL, NOT RUN, deliberately unsupported, and owner-authority surfaces. It records the most important incomplete product acceptance items—native authority-free Capsule import/rebind, arbitrary existing-folder selection, and granular retention/deletion/export controls—alongside the external GitHub CI, attended Windows/accessibility, independent security review, licensing, signing/installer/distribution, tagged performance, first-launch evaluator, and participant-study gates.

This checkpoint makes no public-alpha readiness claim and changes no executable surface. Final verification results for the documentation-only commit are recorded below after the complete applicable local gates run.

Verified on 2026-07-17 against the staged documentation tree based on portable-package commit `d5f57f8`:

```text
WSL locked solution restore: PASS — 23 projects up to date
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL full serial test run: PASS — 420 passed, 0 failed, 13 expected Windows-only skips
WSL staged-tree ReleaseAudit: PASS — 61 dependencies, 10 frozen contracts, 384 tracked files
```

An initial parallel solution test attempt was not accepted as evidence: VSTest reported invalid test-assembly arguments while concurrently starting `Tooltail.Platform.Windows.Tests` and `Tooltail.SkillFixtureCli.Tests`, so the chained ReleaseAudit did not run. The complete `-m:1` solution run then executed both projects and passed with the totals above. No test was made conditional or omitted to hide that runner failure.

Windows binaries, tests, publish payload, package hash, and removal behavior are unchanged from `d5f57f8`; Windows was therefore not rerun for this documentation-only checkpoint. The immediately preceding Windows evidence remains 430 passed/3 expected skips plus packaged apphost/removal PASS. GitHub CI, attended/participant rows, independent review, signing/installer, and distribution remain NOT RUN or owner-blocked exactly as listed in `KNOWN_LIMITATIONS.md`.

The native Capsule import/rebind gap identified by this ledger is closed by the checkpoint below. Granular data-lifecycle/diagnostic-export work and all named owner/external gates remain open.

### M7 authority-free Capsule import and rebind checkpoint

Verified on 2026-07-17 for the implementation working tree based on readiness-ledger commit `48c7659`:

```text
WSL locked solution restore: PASS — 23 projects
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL full serial test run: PASS — 431 passed, 0 failed, 14 expected Windows-only skips
WSL focused Capsule feature tests: PASS — 9 passed
WSL focused Capsule SQLite tests: PASS — 4 passed
WSL focused Capsule architecture tests: PASS — 3 passed
WSL staged-tree ReleaseAudit: PASS — 61 dependencies, 10 frozen contracts, 393 tracked files

Windows locked solution restore: PASS — 23 projects
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows full serial test run: PASS — 442 passed, 0 failed, 3 expected link-fixture skips
Windows native bounded Capsule file reader: PASS
Windows Window Shell apphost smoke: PASS — exit 0 / WINDOW_SHELL_IMPORT_SMOKE_PASS
```

ADR 0009 accepts only one bounded v1 JSON file and no merge. The user first previews exact captured bytes, SHA-256, companion name, and version count; preview mutates nothing. The second explicit action can replace only the unique pristine first-run companion, after SQLite proves every authority/history table is empty. Identity and all validated linear histories commit in one immediate transaction; every imported version is forced Stale with no approval, grant, plan, execution, receipt, or trusted evidence.

After a new exact safe-lab grant, each explicit rebind creates parent-linked version `n + 1` as Draft. The semantic diff must contain only `scope_binding`; the imported parent remains immutable/Stale, and the existing shared-executor rehearsal plus new exact-plan approval path remains mandatory. Restart projects unrebound Stale histories as `needs_input`. Existing/non-pristine product state, nonlinear histories, old/same grants, changed grant records, invalid time, malformed content, cancellation, and file/path/link failures stop without merge or authority.

The first local compile exposed a missing namespace and then the xUnit v3 `Assert.Single` analyzer; both were corrected before tests ran. The first SQLite rebind test exposed reference equality on reconstructed capability collections; the boundary now compares every immutable grant field plus the ordered closed capability set. On Windows, an orchestration mistake started tests before a long-running format/build chain completed, producing missing-assembly errors and seven build/file-lock warnings. Those runs were rejected, no host process was terminated, the owned testhosts exited naturally, and the isolated format, clean build, complete serial tests, and apphost smoke above then passed.

The previously recorded portable ZIP/hash belongs to commit `d5f57f8` and is not evidence for this changed production binary. Portable publish/hash/apphost/removal verification must be regenerated after this checkpoint before any final candidate claim. Hosted CI, attended matrices, participant studies, independent review, licensing, signing/installer, and distribution remain NOT RUN or owner-blocked.

### M7 redacted diagnostics and retention-boundary checkpoint

Verified on 2026-07-17 for the implementation working tree based on Capsule import/rebind commit `56d5799`:

```text
WSL locked solution restore: PASS — 23 projects
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL full serial test run: PASS — 437 passed, 0 failed, 14 expected Windows-only/interactive skips
WSL focused diagnostic Application tests: PASS — 2 passed
WSL focused diagnostic architecture tests: PASS — 3 passed
WSL staged-tree ReleaseAudit: PASS — 61 dependencies, 10 frozen contracts, 400 tracked files

Windows locked solution restore: PASS — 23 projects
Windows format verification: PASS
Windows forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows full serial test run: PASS — 448 passed, 0 failed, 3 expected link-fixture skips
Windows Window Shell apphost smoke: PASS — exit 0 / WINDOW_SHELL_DIAGNOSTIC_SMOKE_PASS
```

ADR 0010 keeps whole-product-memory deletion as the only truthful v0.1 erasure boundary. Per-lesson, per-skill, per-execution, and per-receipt physical deletion would break immutable SkillSpec provenance, parent lineage, canonical plans, append-only journals/receipts, or recovery links; rewriting those records would make trusted history unverifiable. Skill Card granular maintenance actions are therefore visibly disabled with precise reasons rather than silently doing nothing or implying that disable/revocation erases history. A future granular implementation requires a dependency-safe migration and a new crash/recovery/evidence-integrity ADR.

The new internal `tooltail.diagnostic-export/1` builder accepts only typed body/tool states, stable bounded reason codes, UTC/product version, and bounded aggregate counts. Its DTO has no field for paths, filenames, titles, contents, model text, credentials, companion identity/name, user identity, or machine identity; every sensitive-content flag is fixed false. Output is capped at 64 KiB, integer/unknown enum and unknown property inputs fail closed, nested nulls and invalid counts/reasons/times are rejected, and the builder serialize/parses/value-compares its own exact bytes.

Home shows the exact diagnostic JSON, byte count, and SHA-256 before a separate export action. Export rehashes and strictly reads the same document, revalidates the Tooltail-owned non-reparse `Diagnostics` root, and writes one new GUID-named file with `CreateNew` and write-through. It has no upload, network client, overwrite, delete, automatic collection, or authority object. Successful export consumes the pending exact preview while leaving the reviewed JSON visible. Whole-memory deletion preserves diagnostic exports, and both the deletion integration test and Windows apphost smoke prove that boundary.

The first focused diagnostic assertion was overly broad because it rejected the literal policy field name `containsFileNames`; it was narrowed to the injected private filename and reran cleanly. The first architecture assertion similarly rejected the policy name `containsWindowTitles`; it now rejects raw-input declarations instead. Neither test was disabled. A full normal-verbosity WSL test output exceeded the orchestration return limit and was not accepted as final evidence; the complete serial minimal-output rerun produced the explicit totals above.

The first Windows apphost launch inherited only the system .NET 8 framework location and correctly reported that .NET 10 was unavailable. That run was not accepted and no process was terminated. The successful rerun scoped `DOTNET_ROOT` and `DOTNET_MULTILEVEL_LOOKUP` only to the command and used the same private Windows .NET 10 SDK/runtime as restore, build, and tests. The three Windows skips remain the documented unprivileged/reparse link fixtures; the native apphost exercises exact diagnostic preview/export and preserved local-state deletion.

The portable ZIP/hash at `d5f57f8` is still stale for this production binary. The next safe task is to commit this bounded diagnostic/lifecycle checkpoint, then regenerate the unsigned portable package twice and repeat packaged apphost plus marker-bound program-only removal verification. Hosted CI, attended accessibility/monitor tests, participant studies, independent security/privacy review, repository license decisions, signing/installer, and distribution remain external NOT RUN or owner-blocked gates.

### M7 current-binary portable package regeneration checkpoint

Verified on 2026-07-17 from diagnostic/lifecycle commit `1725eea` in the isolated Windows D: workspace:

```text
WSL locked solution restore: PASS — 23 projects
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL full serial test run: PASS — 437 passed, 0 failed, 14 expected Windows-only/interactive skips
WSL staged-tree ReleaseAudit: PASS — 61 dependencies, 10 frozen contracts, 400 tracked files

Windows package-portable.ps1 run 1: PASS — locked RID/audit restore, self-contained publish, pack/readback, apphost, removal fixture
Windows package-portable.ps1 run 2: PASS — locked RID/audit restore, self-contained publish, pack/readback, apphost, removal fixture
Independent ZIP comparison: PASS — byte-identical
Payload: 441 files, 177,679,747 bytes
ZIP SHA-256: ec46c5add6bc084b23a9c545b9e68a55e8c5b564b8e202c644b8cbf8639400a5
Packaged apphost: PASS — exit 0 on both runs
Marker-bound removal: PASS — program directory removed; sibling local-data sentinel byte-identical on both runs
Manifest: version 0.1.0; win-x64; self-contained; unsigned; program_directory_only; data root %LOCALAPPDATA%\Tooltail
```

The previous `d5f57f8` package tree was retained by directory rename before regeneration. The first `1725eea` run was likewise retained by rename before the second run; no prior artifact was overwritten or deleted. The final package remains only in the dedicated ignored D: artifact workspace and is not added to Git. Both runs used the same reviewed package path: bounded non-reparse publish capture, closed sorted manifest, fixed timestamps, strict archive readback, self-contained packaged apphost, and a newly created marker-bound uninstall fixture.

The current artifact now includes authority-free Capsule import/rebind and the closed diagnostic/lifecycle changes. Its apphost smoke proves exact diagnostic preview/export, diagnostic survival across whole-memory deletion, and the prior File Apprentice, body, lease, research, Capsule, interruption, revocation, recovery, and local-data boundaries. The removal verifier touches only its newly created fixture `program` directory and preserves its sibling local-data sentinel; it does not remove `%LOCALAPPDATA%\Tooltail`, user files, an installed program, or a host process.

This is still an unsigned engineering artifact, not a public alpha or conventional installer. GitHub-hosted workflow provenance, code signing/SmartScreen evidence, installer/uninstaller decisions, attended Windows/accessibility matrices, independent security/privacy/packaging review, participant studies, owner license decisions, and distribution approval remain NOT RUN or externally blocked.

### M5 explicit existing-folder grant checkpoint

Verified on 2026-07-17 for the implementation working tree based on package-evidence commit `bfd1404`:

```text
WSL locked solution restore: PASS — 23 projects
WSL format verification: PASS
WSL forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
WSL full serial test run: PASS — 443 passed, 0 failed, 15 expected Windows-only/interactive skips
WSL focused existing-folder SQLite tests: PASS — 2 passed
WSL focused existing-folder architecture tests: PASS — 3 passed
WSL staged-tree ReleaseAudit: PASS — 61 dependencies, 10 frozen contracts, 408 tracked files

Windows locked solution restore: PASS — 23 projects
Windows format verification: PASS
Windows isolated forced non-incremental Release solution build: PASS — 0 warnings, 0 errors
Windows full serial test run from isolated output: PASS — 455 passed, 0 failed, 3 expected link-fixture skips
Windows current-user DPAPI round-trip/tamper tests: PASS — 2 passed
Windows final isolated Desktop build: PASS — 0 warnings, 0 errors
Windows expanded apphost smoke: PASS — exit 0 / WINDOW_SHELL_EXISTING_FOLDER_FAILURE_REVOKE_PASS
```

ADR 0011 closes the remaining local required-outcome gap without expanding executable authority. Home uses the native WPF folder picker for intent, then displays the exact canonical root, five-minute preview expiry, and seven closed capabilities. Selection reads only root/ancestry metadata and creates no grant. A separate confirmation re-captures fixed-volume, non-reparse root identity, refuses another current grant, checks expiry, and persists one seven-day `LocalFolderGrant`; it creates, enumerates, edits, or approves no selected file.

User-selected canonical roots use the existing bounded SQLite protected-root blob and a new Application abstraction implemented only in `Platform.Windows` with current-user DPAPI, product entropy, UI-forbidden mode, strict UTF-8, bounded native buffers, and zeroing. No package, machine-scope protection, plaintext root column, network client, IPC, shell, new primitive, or schema migration was introduced. Safe-lab roots remain derived from Tooltail-owned application storage and grant ID.

Restart decrypts and re-runs the complete root probe before any workflow can use the grant. Active authority and usable-root state are separate in the view model. A changed/missing/reparse root or unreadable ciphertext disables teaching, compilation, rehearsal, approval/execution, correction, rebind, and Undo while preserving an exact grant-ID/root-identity/protected-bytes revoke action. The Windows apphost smoke proves preview-before-authority, protected persistence, successful exact restore, tampered-ciphertext failure, and revocation without reading the unavailable root.

The first native-test build used xUnit v3 Fact properties as writable; the repository's constructor-based source-location pattern fixed the compile error. A later test compared an unordered capability set directly with an ordered policy list, and then compared reconstructed grant records by collection reference; stable per-field/set comparisons fixed both tests. No assertion was removed. The first standard Windows output build for this checkpoint was rejected because the earlier intentionally un-terminated missing-framework apphost process (PID 179328) still locks only its old `Tooltail.Desktop.exe`, producing MSB3027/MSB3021 after retries. In accordance with the host-process safety instruction, no process was stopped. A fresh, previously absent Tooltail artifact output then built the whole solution serially with 0 warnings/errors and supplied the complete tests and apphost evidence above.

The `bfd1404` portable ZIP predates this production change and is no longer current-binary evidence. After committing this checkpoint, regenerate the portable artifact twice from the new commit, compare bytes, and repeat packaged apphost plus marker-bound removal. The actual native picker dialog, keyboard/screen-reader comprehension, broad existing-folder contents, GitHub CI, independent review, licensing, signing/installer, participant studies, and distribution remain attended/external gates and are not inferred from automation.

### M7 final current-binary portable package checkpoint

Verified on 2026-07-17 from explicit existing-folder grant commit `f69c1d6`:

```text
Windows package-portable.ps1 run 1: PASS — locked RID/audit restore, self-contained publish, pack/readback, expanded apphost, removal fixture
Windows package-portable.ps1 run 2: PASS — locked RID/audit restore, self-contained publish, pack/readback, expanded apphost, removal fixture
Independent ZIP comparison: PASS — byte-identical
Payload: 441 files, 177,712,515 bytes
ZIP SHA-256: 69c9a39a284e2118094354827a6f0ce39ebedb6e85348c1e25c4bae8c870fb98
Packaged apphost: PASS — exit 0 on both runs
Marker-bound removal: PASS — program directory removed; sibling local-data sentinel byte-identical on both runs
Manifest: version 0.1.0; win-x64; self-contained; unsigned; program_directory_only; data root %LOCALAPPDATA%\Tooltail
```

The prior `bfd1404` package and the first `f69c1d6` run were each retained by directory rename; neither was overwritten or deleted. The final ignored D: artifact includes the explicit existing-folder picker/confirmation implementation and current-user protected-root boundary. Its self-contained apphost proves preview-before-authority, exact protected grant issuance, successful identity-bound restore, unreadable-ciphertext failure, exact unavailable-root revocation, and the earlier full File Apprentice/body/lease/research/Capsule/diagnostic/deletion smoke. Both independent removals touched only their newly generated marker-bound `program` directory and preserved sibling data.

No binary is committed, uploaded, signed, installed, registered, published, or described as a public alpha. Hosted CI, actual attended picker/assistive-technology/mixed-monitor rows, broad-folder evaluator use, participant research, independent security/privacy/packaging review, repository license decisions, signing/installer, SmartScreen evidence, vulnerability-reporting channel, and distribution approval remain external gates.

### M7 atomic existing-folder grant issuance checkpoint

Verified on 2026-07-17 for the working tree after current-binary package-evidence commit `21fc22a`:

```text
WSL locked solution restore: PASS — 23 projects up to date
WSL format verification: PASS
WSL forced serial Release solution build: PASS — 0 warnings, 0 errors
WSL focused existing-folder SQLite tests: PASS — 44 passed, 0 failed
WSL focused architecture tests: PASS — 36 passed, 0 failed
WSL full serial test run: PASS — 444 passed, 0 failed, 15 expected Windows-only/interactive skips
WSL ReleaseAudit: PASS — 61 dependencies, 10 frozen contracts, 408 tracked files
```

Existing-folder confirmation formerly performed a read-time live-grant check and then a later generic grant write. The Home controller serializes normal user actions, but that split did not make the persisted authority invariant atomic. `IFileSkillStateStore.TryIssueExclusiveLocalFolderGrantAsync` now validates an active protected-root grant and executes one `INSERT ... SELECT ... WHERE NOT EXISTS` inside the store's serialized `BEGIN IMMEDIATE` transaction. The condition is scoped to the exact companion's unrevoked, unexpired `local_folder` grants. A second contender receives the stable closed failure `folder_grant.active_grant_exists`; no second grant row or authority is created.

The regression test constructs two independent grant services and confirms two valid previews concurrently against the real temporary SQLite database. It requires exactly one issued active grant and one closed conflict result. This is persistence enforcement, not a replacement for the existing root identity re-capture, DPAPI protection, execution-time permission checks, or the attended picker/accessibility evaluation. The final `f69c1d6` portable ZIP predates this source correction and must be regenerated twice, byte-compared, and subjected to the packaged apphost/removal checks after the implementation commit.

## Update rule

Every implementation handoff must update this file with:

- exact commit or working-tree state tested;
- exact commands run and result;
- completed and active milestone;
- skipped tests and why;
- supported Windows/.NET versions actually tested;
- material known defects or unsupported cases;
- the next smallest safe task.

Never replace an unrun check with an assumption.
