# Tooltail

**Teach a workflow. Keep the skill.**

Tooltail is a Windows-first desktop apprentice that learns safe, repeatable workflows from demonstrations and corrections.

It is not a general-purpose computer-control agent and it is not a chat character with a task list. Tooltail makes an agent's context, permissions, progress, learned procedures, and uncertainty physically legible through a small desktop companion.

## Product loop

1. Place Tooltail on a window to establish visible context.
2. Grant a specific resource, such as one folder.
3. Demonstrate a bounded workflow in one teaching session.
4. Review the generated Skill Card.
5. Rehearse the proposed actions in a sandbox or dry-run.
6. Execute only after approval.
7. Verify the result and retain an undo receipt.
8. Correct the procedure; the next run uses the new skill version.

The tools the companion carries are not cosmetic unlocks. They represent real, versioned, tested skills.

## v0.1 scope

The first build contains two experiments in one application:

- **Agent Body:** maps structured events from a simulator and launched Codex runs into clear companion states.
- **File Apprentice:** learns constrained directory, rename, move, and copy workflows inside a user-granted folder, with preview, verification, and undo.

The v0.1 build does **not** record global keyboard input, capture continuous screenshots, inject arbitrary mouse input, execute learned shell commands, learn/generalize deletion, or run learned skills silently. Undo may remove only an unchanged file or empty directory that the journal proves was created by that exact execution.

## Platform and stack

- Windows 11 x64
- .NET 10 LTS
- WPF desktop shell
- SQLite local state
- versioned JSON contracts
- Windows named-pipe IPC where process separation is required

## Development quick start

Install the .NET 10.0.302 SDK selected by `global.json`, then run from the repository root:

```powershell
dotnet restore Tooltail.sln --locked-mode
dotnet format Tooltail.sln --verify-no-changes --no-restore
dotnet build Tooltail.sln -c Release --no-restore
dotnet test Tooltail.sln -c Release --no-build --logger "console;verbosity=normal"
```

The complete headless File Apprentice fixture surface can be inspected without opening user data:

```powershell
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release --no-build -- --help
```

Its deterministic six-scenario M2 acceptance suite requires a new explicit disposable path:

```powershell
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release --no-build -- golden-suite --workspace D:\tmp\tooltail-m2-golden
```

See [`docs/FIXTURE_CLI.md`](docs/FIXTURE_CLI.md) for the bounded workspace contract, commands, exit codes, manual loop, and exact golden output.

The deterministic Agent Body simulator runs without Codex or a desktop session:

```powershell
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- verify-all
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- project --trace parallel-two-units
```

See [`docs/AGENT_BODY.md`](docs/AGENT_BODY.md) for canonical state precedence, all scripted traces, bounded generic JSONL, and the optional privacy-minimizing Codex CLI adapter. The M4 lease lifecycle, HWND/process identity boundary, native tracking design, and DPI model are documented in [`docs/WINDOW_LEASES.md`](docs/WINDOW_LEASES.md).

On Windows, the desktop workbench and Agent Body have self-closing smoke modes:

```powershell
dotnet run --project src/Tooltail.Desktop -c Release --no-build -- --smoke-test
dotnet run --project src/Tooltail.Desktop -c Release --no-build -- --agent-body-smoke-test
```

The M4 Window Shell smoke must run the generated apphost so its Per-Monitor V2 manifest owns the process. When using the repository-local runtime:

```powershell
$env:DOTNET_ROOT = 'D:\tmp\coding\tooltail\.dotnet'
src\Tooltail.Desktop\bin\Release\net10.0-windows10.0.22000.0\Tooltail.Desktop.exe --window-shell-smoke-test
```

## Status

M0 through M6 engineering is implemented. The deterministic Agent Body, constrained File Apprentice loop, Window Shell, correction/reuse/Undo/capsule flow, and opt-in local research build pass the recorded Linux and Windows automated matrices. M7 public-alpha hardening is active. Independent first-launch evaluation, participant research, independent security review, and attended real-application/mixed-monitor/accessibility rows remain explicitly NOT RUN. See:

- [`docs/PRODUCT_SPEC.md`](docs/PRODUCT_SPEC.md)
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- [`docs/SECURITY_THREAT_MODEL.md`](docs/SECURITY_THREAT_MODEL.md)
- [`docs/DATA_LIFECYCLE.md`](docs/DATA_LIFECYCLE.md)
- [`docs/IMPLEMENTATION_ROADMAP.md`](docs/IMPLEMENTATION_ROADMAP.md)
- [`CODEX_MASTER_TASK.md`](CODEX_MASTER_TASK.md)

Local M7 release evidence (dependency licenses, frozen schemas, pinned CI actions, tracked-file secret patterns, SPDX, and external blockers) is generated without publishing:

```powershell
dotnet run --project tools/Tooltail.ReleaseAudit -c Release --no-build -- verify --root $PWD --output "$PWD\artifacts\release-audit"
```

See [`docs/RELEASE_EVIDENCE.md`](docs/RELEASE_EVIDENCE.md), [`docs/THREAT_CONTROL_MATRIX.md`](docs/THREAT_CONTROL_MATRIX.md), [`docs/CRASH_RECOVERY_MATRIX.md`](docs/CRASH_RECOVERY_MATRIX.md), and [`docs/SCHEMA_COMPATIBILITY.md`](docs/SCHEMA_COMPATIBILITY.md).

The consolidated readiness delta and unsupported surfaces are maintained in [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md). Tooltail is an engineering hypothesis build, not a public-alpha-ready release.

Home also exposes a two-step, crash-recoverable whole-memory deletion with an exact deleted/preserved preview. It removes only Tooltail's SQLite product state after revoking authority and clearing the separate research sink; safe labs, user files, rehearsal residuals, Capsule exports, and separately copied research exports remain. Exact locations, current retention, recovery, and uninstall boundaries are documented in [`docs/DATA_LIFECYCLE.md`](docs/DATA_LIFECYCLE.md).

Companion Capsule continuity is authority-free: export contains immutable provider-independent history, while import is allowed only over a unique empty first-run identity after an exact-byte/SHA-256 preview. Imported skills are Stale, create no grant or approval, and require a new folder grant, explicit scope-only Draft rebind, rehearsal, and exact-plan approval. Existing state is never merged. See [ADR 0009](docs/adrs/0009-authority-free-capsule-import-and-rebind.md).

The bounded Windows deliverable is an unsigned self-contained portable ZIP. From a clean Windows checkout, `./eng/package-portable.ps1` performs locked RID restore, publish, deterministic manifest/hash packaging, packaged apphost smoke, and a marker-bound removal test that retains sibling local data. It does not upload or sign the binary. See [`docs/PORTABLE_PACKAGE.md`](docs/PORTABLE_PACKAGE.md).

## Core invariants

- A pet position is not treated as an operating-system security boundary.
- Window context and resource authority are separate grants.
- Learned skills are executable specifications with tests and provenance, not hidden prompt memory.
- Every file effect is scope-checked, previewed, journaled, verified, and reversible.
- Unknown events, actions, schema versions, and paths fail closed.
- Private Codex session files are never read.
- The MVP is local-first and telemetry-free by default.

## License

Apache-2.0 is the recommended code license, pending repository-owner confirmation. No VPet, OpenPets, Clicky, or other third-party art or code should be copied into this repository without an explicit dependency and license review.
