# Build Status

## Current state

M0 and M1 are implemented and verified. M2's headless services now include bounded authoritative snapshots, watcher-hint teaching observation, deterministic reconciliation and compilation, inspectable Skill Cards, pure canonical planning, owned-root rehearsal, the shared production/rehearsal executor, postcondition verification, receipts, approved recovery planning/execution, and durable SQLite repositories.

This is a verified headless File Apprentice core, not yet the product MVP. M2 remains active because the fixture CLI still exposes only help/version and the six roadmap golden scenarios have not yet been wired into one machine-readable end-to-end acceptance surface. Desktop composition also does not yet expose the durable headless loop as a user workflow.

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

Verified on 2026-07-16 for the persistence/recovery working tree based on commit `0f2b3d0`.

```text
SDK: .NET SDK 10.0.302; runtime 10.0.10
Primary target: Windows 11 x64

WSL locked restore: PASS — all 18 projects restored
WSL format verification: PASS
WSL Release build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 247 passed, 0 failed, 8 skipped
WSL skipped tests: eight tagged native Windows path/snapshot/observation/execution/rehearsal/undo tests require a Windows host

Windows environment: build 22631 / 23H2, x64
Windows locked restore: PASS — all 18 projects restored
Windows format verification: PASS
Windows forced non-incremental Release build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 253 passed, 0 failed, 2 skipped
Windows native path, snapshot, watcher, execution, rehearsal, and undo fixtures: PASS
Windows skips: unprivileged symlink creation requires Developer Mode; the portable reparse-directory fixture is intentionally non-Windows because separately tagged native coverage passed
Windows WPF smoke: PASS — shell rendered and exited through --smoke-test

Completed milestone: M1 Safety kernel and versioned contracts
Active milestone: M2 Complete headless File Apprentice
```

Commands used from the repository root:

```powershell
dotnet restore Tooltail.sln --locked-mode
dotnet format Tooltail.sln --verify-no-changes --no-restore
dotnet build Tooltail.sln -c Release --no-restore
dotnet test Tooltail.sln -c Release --no-build --logger "console;verbosity=normal"
dotnet <path-to-Tooltail.Desktop.dll> --smoke-test
```

Current evidence and known limitations:

- All four bundled JSON examples validate against Draft 2020-12 schemas and strict DTO parsers; incompatible versions, unknown fields/actions, and oversized payloads fail closed. `JsonSchema.Net` is test-only.
- The portable adversarial corpus covers rooted/drive/UNC/device/ADS/traversal/mixed/repeated separators, reserved names, trailing aliases, NFC, case-only changes, long bounds, root/source/destination identity drift, and a link introduced after planning.
- The real Windows probe opens entries with `FILE_FLAG_OPEN_REPARSE_POINT` and records canonical handle path, volume serial, and file identity. This host could not create an unprivileged symlink, so that one native creation test is visibly skipped; injected reparse/race tests pass portably.
- Production, rehearsal, and recovery use the same direct allowlisted executor path. Authority and root/source/destination state are revalidated at each durable boundary; no implementation invokes a shell for a learned effect.
- Canonical plan JSON and SHA-256 have a fixed golden vector that passes on WSL and Windows. Material plan, skill, grant, root, input, destination, ordering, precondition, or postcondition changes invalidate approval.
- Crash-prefix tests distinguish not-started, started-uncommitted, committed-unverified, verified, recovery-required, and rolled-back states. Started-without-commit never permits automatic replay.
- SQLite v1 uses 17 strict tables, foreign keys, WAL/full synchronization, checksummed migrations, append-only journal/receipt triggers, serialized writers, and read-only recovery on unknown or damaged state. Repository tests cover restart replay, exact retry, approval races, tampered rows, missing receipts, and standard-plus-Undo receipt round trips.
- SQLite rows are treated as untrusted projections: skill lifecycles and journals replay domain transitions, canonical plans must match every executable field, and receipt evidence must agree with its plan and linked journals.
- CI workflow execution on GitHub has not run; equivalent locked restore, format, Release build, and test commands passed locally on Linux and Windows.
- The M4 interactive HWND, focus, DPI, monitor, accessibility, and WindowLease matrix is not applicable yet because those features do not exist.
- The Fixture CLI and six roadmap golden scenarios remain the blocking M2 acceptance work. Capsule export/import, retention maintenance, and the complete desktop workflow remain later milestones.

Next smallest safe task: implement the bounded machine-readable M2 Fixture CLI over explicit Tooltail-owned fixture/temp paths, then lock the six end-to-end golden scenarios including execute-plus-Undo tree restoration.

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
