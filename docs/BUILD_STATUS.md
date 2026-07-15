# Build Status

## Current state

This repository seed is a specification package, not an implemented application.

As delivered:

- product, UX, architecture, safety, data, test, and research decisions are documented;
- JSON contract schemas and example payloads are included;
- repository policies, CI intent, and Codex instructions are included;
- no `Tooltail.sln`, C# project, compiled binary, installer, generated artwork, or working MVP exists yet;
- the workflow file intentionally becomes useful after Codex completes M0.

The first implementation assignment is `CODEX_MASTER_TASK.md`. `AGENTS.md` contains constraints that apply to every coding task.

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

## Implementation status template

Codex must replace this section as work begins.

```text
Last verified commit: not implemented
SDK: .NET 10.x
Supported runtime: Windows 11 x64

Restore: NOT RUN — solution absent
Build: NOT RUN — solution absent
Portable tests: NOT RUN — projects absent
Windows tests: NOT RUN — projects absent
Interactive desktop tests: NOT RUN — harness absent

Completed milestone: Blueprint only
Active milestone: M0
Known blockers: owner/license confirmation; implementation not started
```

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
