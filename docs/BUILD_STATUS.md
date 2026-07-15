# Build Status

## Current state

M0 is implemented and verified. The repository now contains the complete solution/project graph, locked dependencies, replaceable clock and ID seams, a read-only file-system metadata boundary, Generic Host composition, safe headless tool shells, a minimal WPF engineering shell, shared isolated test fixtures, architecture rules, and strict CI baseline checks.

This is still an engineering baseline, not a working File Apprentice or product MVP. No folder grant, teaching, planning, execution, agent-event projection, native WindowLease, persistence, or Companion Capsule behavior exists yet. M1 safety-kernel work is the active milestone.

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

Verified on 2026-07-16 against commit `4951fd9`.

```text
SDK: .NET SDK 10.0.302; runtime 10.0.10
Primary target: Windows 11 x64

WSL restore: PASS — all 18 projects restored
WSL format verification: PASS
WSL Release build: PASS — 0 warnings, 0 errors
WSL tests: PASS — 15 passed, 0 failed, 0 skipped
Fixture CLI help: PASS — no user-data access
Agent simulator help: PASS — no external process or user-data access

Windows environment: build 22631 / 23H2, x64
Windows locked restore: PASS — all 18 projects restored
Windows format verification: PASS
Windows Release build: PASS — 0 warnings, 0 errors
Windows tests: PASS — 15 passed, 0 failed, 0 skipped
Windows WPF smoke: PASS — shell rendered and exited through --smoke-test

Completed milestone: M0 Repository and engineering baseline
Active milestone: M1 Safety kernel and versioned contracts
```

Commands used from the repository root:

```powershell
dotnet restore Tooltail.sln --locked-mode
dotnet format Tooltail.sln --verify-no-changes --no-restore
dotnet build Tooltail.sln -c Release --no-restore
dotnet test Tooltail.sln -c Release --no-build --logger "console;verbosity=normal"
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release --no-build -- --help
dotnet run --project src/Tooltail.Desktop -c Release --no-build -- --smoke-test
```

Skipped or incomplete checks:

- The M4 interactive HWND, focus, DPI, monitor, accessibility, and WindowLease matrix is not applicable yet because those features do not exist.
- CI workflow execution on GitHub has not run; equivalent restore/format/build/test commands passed locally on Linux and Windows.
- License/visibility remains an owner decision and does not block M1 implementation.

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
