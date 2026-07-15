# Contributing to Tooltail

Tooltail is an early safety- and research-driven project. Contributions should reduce a documented uncertainty or complete a roadmap acceptance criterion. Capability breadth and visual polish are not default priorities.

## Before starting

Read root `AGENTS.md`, the relevant specification, threat-model section, schema, and ADR. For a substantial change, open or draft an issue containing:

- user/research outcome;
- in-scope and out-of-scope behavior;
- exact acceptance tests;
- authority, privacy, recovery, and accessibility impact;
- contract/migration impact;
- alternatives considered.

Use an ADR before introducing a new primitive, framework, process, data sink, external service, model dependency, or platform.

## Development setup

Required for the implemented v0.1 project:

- Windows 11 x64 for the full desktop build and native tests;
- the .NET 10 SDK selected by `global.json`;
- Git;
- a .NET 10-compatible Visual Studio release or another compatible editor is optional; command-line builds are authoritative.

From repository root:

```powershell
dotnet restore Tooltail.sln
dotnet format Tooltail.sln --verify-no-changes --no-restore
dotnet build Tooltail.sln -c Release --no-restore
dotnet test Tooltail.sln -c Release --no-build --logger "console;verbosity=normal"
```

The repository begins as a blueprint. Until M0 creates the solution, consult `docs/BUILD_STATUS.md` rather than assuming these commands are available.

## Change design

Prefer one thin end-to-end behavior over many disconnected abstractions. Keep pure policy and state transitions separate from Windows, SQLite, WPF, file-system, and external-event adapters.

Every authority-bearing change must answer:

1. What typed grant permits it?
2. What exact plan does the user inspect and approve?
3. What changes invalidate approval?
4. What is journaled before and after mutation?
5. How is success verified?
6. What happens on cancellation, revocation, crash, and concurrent change?
7. Can it be undone safely, or must it remain unsupported?

If those answers are not testable, the capability is not ready.

## Tests

- Add a failing test before or with the implementation.
- Prefer real isolated temp directories and SQLite databases at integration boundaries.
- Keep portable core tests runnable without WPF or an interactive desktop.
- Tag native interactive tests and record the required harness.
- Add golden fixtures for contracts, plans, receipts, and state traces.
- Add every property-test counterexample as a named regression case.
- Test failure, cancellation, revocation, and recovery—not only the happy path.

Never use a real user's Desktop, Documents, source tree, or credentials as a test fixture.

## Pull requests

Keep changes focused and use a clear title such as:

```text
file-skills: reject a reparse point introduced after approval
body: project input-required above active tool state
contracts: add v1 capsule validation fixtures
```

A pull request description should include:

- observable outcome;
- linked issue/milestone/ADR;
- behavior and non-goals;
- exact commands and results;
- threat/privacy/accessibility notes;
- contract and migration effects;
- screenshots or short clips only for UI changes, using synthetic data;
- skipped checks and known limitations.

Do not include unrelated formatting, generated output, personal databases, research exports, recordings, or real filenames.

## Review bar

Reviewers should prioritize:

- permission and path confinement;
- exact-plan approval invalidation;
- journal/recovery correctness;
- deterministic compiler and state projection;
- schema compatibility;
- privacy-safe logging;
- accessibility and truthful failure states;
- tests that exercise the claimed boundary.

Visual appeal cannot compensate for an unverifiable effect or misleading state.

## Documentation

Update relevant specs, schemas/examples, ADRs, and `docs/BUILD_STATUS.md` in the same change. Do not silently alter an accepted semantic contract in code.

## Licensing and assets

The recommended repository license is Apache-2.0, pending owner confirmation. Do not contribute third-party code, art, animation, fonts, sounds, datasets, or generated assets without recording provenance and license compatibility. VPet code and artwork must not be assumed to have identical reuse terms.
