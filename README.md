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

On Windows, the M0 desktop shell has a self-closing smoke mode:

```powershell
dotnet run --project src/Tooltail.Desktop -c Release --no-build -- --smoke-test
```

## Status

M0, M1, and M2 are implemented: the repository baseline, safety kernel, and complete headless File Apprentice loop are present and verified on Linux and Windows. M3 deterministic Agent Body simulator work is next. See:

- [`docs/PRODUCT_SPEC.md`](docs/PRODUCT_SPEC.md)
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- [`docs/SECURITY_THREAT_MODEL.md`](docs/SECURITY_THREAT_MODEL.md)
- [`docs/IMPLEMENTATION_ROADMAP.md`](docs/IMPLEMENTATION_ROADMAP.md)
- [`CODEX_MASTER_TASK.md`](CODEX_MASTER_TASK.md)

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
