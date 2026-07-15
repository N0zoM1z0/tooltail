# Repository Setup

## 1. Recommended GitHub identity

- Repository name: `tooltail`
- Product name: `Tooltail`
- Description: `A Windows-first desktop apprentice that learns safe, repeatable workflows from demonstrations and corrections.`
- Website: leave blank until a stable project page exists
- Initial visibility: private
- Topics: `desktop-companion`, `desktop-agent`, `procedural-memory`, `human-computer-interaction`, `windows`, `wpf`, `dotnet`, `local-first`, `agent-ui`
- Default branch: `main`
- Package namespace: `Tooltail.*`
- Suggested license: Apache-2.0, after owner confirmation

Check repository-name availability and conduct an appropriate trademark review immediately before publication. The exploratory search recorded in `RESEARCH_AND_SOURCES.md` is not legal clearance.

## 2. Create the repository

Copy the contents of `repo-seed/` to a clean working directory. Do not copy the outer blueprint wrapper.

After confirming the owner/organization and license:

```powershell
git init -b main
git add .
git commit -m "docs: seed Tooltail product and engineering blueprint"
gh repo create OWNER/tooltail --private --description "A Windows-first desktop apprentice that learns safe, repeatable workflows from demonstrations and corrections." --source . --remote origin --push
```

Do not execute the example commands until `OWNER` and the licensing decision are explicit.

## 3. License decision

Apache-2.0 is recommended because it is permissive and includes an express patent grant. Before adding `LICENSE`:

- confirm all owners/contributors can license their work;
- use only original or compatibly licensed art, fonts, sounds, and code;
- keep third-party notices and source links;
- do not copy VPet artwork or assume its art terms match its code license;
- document model-generated asset provenance if such assets are later used.

If the repository remains closed, record the private proprietary status instead of adding a misleading open-source license.

## 4. GitHub settings

Recommended settings after the first green CI run:

- disable force pushes and branch deletion on `main`;
- require a pull request for changes to `main`;
- require at least one approving review for security-sensitive code;
- require the `build-test` and `portable-test` checks;
- require conversation resolution;
- require branches to be current before merge once CI is stable;
- enable secret scanning, push protection, dependency graph, and Dependabot alerts where available;
- enable private vulnerability reporting before public release;
- use squash merge and auto-delete merged branches;
- disable wiki and projects until there is an owner for them;
- keep Issues enabled for experiments, bugs, and research findings;
- do not enable GitHub Actions write permissions globally unless a workflow requires a narrowly scoped permission.

Add `CODEOWNERS` only after real maintainers and paths are known; do not invent identities in the seed.

## 5. Labels

Create a small taxonomy:

| Label | Purpose |
| --- | --- |
| `area/body` | embodied UI and state projection |
| `area/file-skills` | teaching, inference, execution, undo |
| `area/windows` | HWND, DPI, hooks, platform integration |
| `area/security` | permission, path, journal, threat-model work |
| `area/contracts` | schemas and compatibility |
| `area/research` | hypothesis tests and study findings |
| `kind/bug` | behavior differs from contract |
| `kind/experiment` | time-bounded uncertainty reduction |
| `kind/decision` | ADR or product decision needed |
| `risk/high` | affects authority, mutation, privacy, or recovery |
| `status/blocked` | cannot proceed without an explicit dependency/decision |

Avoid a large priority-label system before a roadmap owner exists.

## 6. Milestones

Mirror the milestones in `IMPLEMENTATION_ROADMAP.md`:

- M0 Repository Baseline
- M1 Safety Kernel
- M2 Headless File Apprentice
- M3 Agent Body Simulator
- M4 Windows Body and Leases
- M5 Integrated MVP
- M6 Research Build
- M7 Public Alpha Gate

Each issue should have one milestone, one area, a testable outcome, and an explicit non-goal when scope could expand.

## 7. First issue set

Create these issues from the master task, in order:

1. Scaffold solution, projects, analyzers, CI, and architecture tests.
2. Implement contract envelopes and validate bundled JSON schemas.
3. Implement canonical local-root and relative-path safety kernel.
4. Implement plan fingerprint, approval, journal, and recovery state machine.
5. Implement snapshot/reconciliation fixtures and watcher overflow behavior.
6. Implement deterministic skill compiler and ambiguity output.
7. Implement planner, rehearsal, executor, verifier, receipt, and undo.
8. Implement agent-event simulator, normalizer, and state projector.
9. Prototype non-activating WPF body and accessible inspector.
10. Implement WindowLease discovery, tracking, and revocation.
11. Integrate the canonical teach/correct/reuse demo.
12. Run adversarial, accessibility, and user-research gates.

Split an issue only when both resulting issues have independent acceptance tests. Keep authority-bearing changes small enough for focused review.

## 8. Secrets and external services

The MVP requires no cloud database, analytics key, model API key, or hosted service. The optional Codex adapter launches a user-configured local CLI process and consumes its public JSONL output; Tooltail must not collect credentials or read private Codex session storage.

If a future adapter needs secrets:

- store them through an OS credential facility, not source, JSON settings, SQLite, logs, or environment dumps;
- document exact purpose and retention;
- use least-privilege tokens;
- provide a revoke/delete path;
- add a threat-model update and ADR.

## 9. Release naming

- Development snapshots: `0.0.0-dev.<date>.<shortsha>`
- Internal hypothesis builds: `0.1.0-alpha.N`
- First public alpha only after M7: continue `0.1.0-alpha.N`
- Do not call any build “beta” until data migration, updater/release provenance, and compatibility policies are proven.

Schemas have their own integer or semantic versions and are not inferred from application version.

## 10. Definition of ready

An implementation issue is ready when it has:

- a linked requirement, ADR, or schema;
- in-scope and out-of-scope behavior;
- observable acceptance criteria;
- tests to add or update;
- security/privacy implications;
- dependencies and platform assumptions;
- a rollback or recovery expectation for stateful changes.

## 11. Definition of done

Work is done when:

- behavior and tests match the documented contract;
- format, build, and relevant test suites pass;
- failure and cancellation paths are covered;
- no new authority or data collection was introduced implicitly;
- docs, schema examples, and `BUILD_STATUS.md` are updated;
- accessibility has been considered for visible behavior;
- a reviewer can reproduce the result from a clean checkout;
- no unrelated refactor or dependency was bundled into the change.
