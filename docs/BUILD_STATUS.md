# Build Status

## Current state

M0 through M3 are implemented and verified. M4 is active, with its WindowLease/domain/application kernel and native target adapter implemented. The headless File Apprentice includes bounded authoritative snapshots, watcher-hint teaching observation, deterministic reconciliation and compilation, inspectable Skill Cards, pure canonical planning, owned-root rehearsal, the shared production/rehearsal executor, postcondition verification, receipts, approved recovery planning/execution, durable SQLite repositories, and the complete machine-readable Fixture CLI.

This is a verified headless File Apprentice plus the complete M3 Agent Body experiment and the automated M4 Windows shell, not yet the product MVP. All six `roadmap-m2/1` scenarios run through one exact cross-platform acceptance surface, including persisted receipt reload and separately approved Undo. The Agent Body has the canonical parameterized state projector, bounded generic JSONL adapter, 15-trace deterministic simulator with an exact state golden, an optional privacy-minimizing `codex exec --json` process adapter, and an original accessible vector body with exact inspector and development playback controls. M4 now has explicit preview/drop/keyboard issue, strict HWND/process-start identity, expiry/revocation, closed contract validation, target eligibility, bounded out-of-context event hooks on a dedicated message-loop thread, one-second reconciliation, physical/DIP conversion, a standard-user Per-Monitor V2 manifest, non-activating Pet, click-through Tether, exact Inspector, and keyboard-accessible Home. The attended real-application/mixed-monitor/accessibility matrix remains open. Desktop composition does not yet expose the durable file loop as a user workflow.

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
- The inspector shows exact normalized event identity, sequence, UTC time, source, type, severity, allowlisted data, disposition, active tools/questions/subagents, scope, parameterized body, and reason without retaining provider raw content.
- The M4 lease core, native HWND/hook adapter, ambient WPF surfaces, manifest/runtime DPI gate, keyboard alternatives, own-style/focus smoke, and native synthetic-window integration pass. The attended real-application, mixed-monitor/rotation/taskbar/remote-session, click-through, screen-reader, high-contrast, and text-scaling rows remain explicitly NOT RUN in `docs/WINDOW_SHELL_TEST_MATRIX.md`.
- The portable fixture probe intentionally derives deterministic test identities and is not the native production Windows identity source. Full multi-skill capsule export/import, retention maintenance, and the integrated desktop workflow remain later milestones.

Next smallest safe task: begin M5 desktop integration of the already verified File Apprentice loop while keeping the attended M4 matrix open and truthful; do not treat an unrun display/accessibility row as passed.

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
