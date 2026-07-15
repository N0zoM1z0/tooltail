# Build Status

## Current state

M0 and M1 are implemented and verified. In addition to the engineering baseline, the repository now has strict v1 transport DTOs and bounded parsers, typed authority and lifecycle records, a Windows-aware canonical-root/relative-path kernel, handle-derived Windows volume and file identities, adversarial path validation, canonical execution-plan fingerprints, exact single-use approval, `PermissionGateway`, append-only recovery journals, verified receipts, normalized teaching evidence state, and deterministic normalized-agent body projection.

This is a verified safety kernel, not yet a working File Apprentice or product MVP. M2 is active: authoritative bounded folder snapshots, watcher-hint reconciliation, deterministic teaching/compiler behavior, real journaled file execution, postcondition verification, receipts, and approved undo do not exist yet. No M1 code path mutates user files.

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

Verified on 2026-07-16 against commit `e47668d` (the following status update is documentation-only).

```text
SDK: .NET SDK 10.0.302; runtime 10.0.10
Primary target: Windows 11 x64

WSL restore: PASS — all 18 projects restored
WSL format verification: PASS
WSL Release build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 106 passed, 0 failed, 3 skipped
WSL skipped tests: the three tagged native path-probe tests require a Windows host

Windows environment: build 22631 / 23H2, x64
Windows locked restore: PASS — all 18 projects restored
Windows format verification: PASS
Windows Release build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 108 passed, 0 failed, 1 skipped
Windows native path probe: PASS — stable handle-derived identity and missing-path behavior
Windows symlink probe: SKIPPED — standard-user host lacks Developer Mode/symlink creation privilege
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

M1 evidence and known limitations:

- All four bundled JSON examples validate against Draft 2020-12 schemas and strict DTO parsers; incompatible versions, unknown fields/actions, and oversized payloads fail closed. `JsonSchema.Net` is test-only.
- The portable adversarial corpus covers rooted/drive/UNC/device/ADS/traversal/mixed/repeated separators, reserved names, trailing aliases, NFC, case-only changes, long bounds, root/source/destination identity drift, and a link introduced after planning.
- The real Windows probe opens entries with `FILE_FLAG_OPEN_REPARSE_POINT` and records canonical handle path, volume serial, and file identity. This host could not create an unprivileged symlink, so that one native creation test is visibly skipped; injected reparse/race tests pass portably.
- No executor exists in M1. M2 must call root/source/destination revalidation immediately before each effect and retain identity-bearing handles where practical to minimize the remaining same-user time-of-check/time-of-use window.
- Canonical plan JSON and SHA-256 have a fixed golden vector that passes on WSL and Windows. Material plan, skill, grant, root, input, destination, ordering, precondition, or postcondition changes invalidate approval.
- Crash-prefix tests distinguish not-started, started-uncommitted, committed-unverified, verified, recovery-required, and rolled-back states. Started-without-commit never permits automatic replay.
- CI workflow execution on GitHub has not run; equivalent locked restore, format, Release build, and test commands passed locally on Linux and Windows.
- The M4 interactive HWND, focus, DPI, monitor, accessibility, and WindowLease matrix is not applicable yet because those features do not exist.

Next smallest safe task: implement M2 bounded snapshots and authoritative baseline/final reconciliation over Tooltail-owned temporary roots, with watcher events treated only as hints.

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
