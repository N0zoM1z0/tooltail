# Build Status

## Current state

M0, M1, and M2 are implemented and verified. The headless File Apprentice now includes bounded authoritative snapshots, watcher-hint teaching observation, deterministic reconciliation and compilation, inspectable Skill Cards, pure canonical planning, owned-root rehearsal, the shared production/rehearsal executor, postcondition verification, receipts, approved recovery planning/execution, durable SQLite repositories, and the complete machine-readable Fixture CLI.

This is a verified headless File Apprentice, not yet the product MVP. All six `roadmap-m2/1` scenarios run through one exact cross-platform acceptance surface, including persisted receipt reload and separately approved Undo. M3 deterministic Agent Body and simulator work is active. Desktop composition does not yet expose the durable headless loop as a user workflow.

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

Completed milestone: M2 Complete headless File Apprentice
Active milestone: M3 Deterministic Agent Body and simulator
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
- The M4 interactive HWND, focus, DPI, monitor, accessibility, and WindowLease matrix is not applicable yet because those features do not exist.
- The portable fixture probe intentionally derives deterministic test identities and is not the native production Windows identity source. Full multi-skill capsule export/import, retention maintenance, and the integrated desktop workflow remain later milestones.

Next smallest safe task: complete the M3 deterministic Agent Body state vocabulary and exact simulator traces before expanding the desktop embodiment.

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
