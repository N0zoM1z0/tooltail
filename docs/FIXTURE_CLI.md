# Headless File Apprentice Fixture CLI

`Tooltail.SkillFixtureCli` is the bounded, machine-readable acceptance surface for the M2 File Apprentice. It exercises the real deterministic compiler, planner, rehearsal service, permission gateway, allowlisted executor, verifier, SQLite repositories, recovery planner, and Undo executor without an LLM or interactive desktop.

It is a fixture harness, not a general file-management command. Every mutating command requires an explicit marked workspace created by `init-fixture`. The tool never defaults to the current directory, Desktop, Documents, or another user-data location.

## Safety boundary

- `--workspace` must be an absolute path. `init-fixture` accepts only a new path.
- The complete existing ancestor chain, workspace marker, fixed workspace directories, artifacts, and SQLite file slots are rejected if they contain a symbolic link or reparse point.
- File effects remain confined to the workspace's `root/` directory under one fixture-only `LocalFolderGrant`.
- Rehearsal uses a newly created child of the workspace's `temp/` directory and the same executor/verifier path as production-mode fixture execution.
- Only `ensure_directory`, `rename_file`, `move_file`, and `copy_file` can enter a standard plan. There is no shell, script, learned deletion, overwrite, network, URL, or arbitrary command surface.
- Undo uses the separate closed recovery contract, a fresh canonical recovery plan, and a separate single-use approval.
- Each JSON artifact is bounded to 4 MiB and parsed with closed members, enum values, depth, and collection limits.
- `verify` reloads the persisted journal and receipt and compares a fresh authoritative snapshot with the exact post-execution snapshot. A later added, removed, renamed, content-changed, or metadata-changed entry fails verification.

The fixture path probe uses deterministic fixture identities so exact output is portable between Linux development and Windows 11. It is not a replacement for the native Windows handle-based identity probe used by the product.

## Workspace layout

`init-fixture` creates this fixed layout:

```text
<workspace>/
  .tooltail-fixture.json
  root/                         explicit file-effect scope
  artifacts/                    bounded canonical inputs and results
  state/tooltail.db             durable fixture state and journals
  temp/                         Tooltail-owned rehearsal workspaces
```

The marker contract is `tooltail.fixture-workspace/1`. A directory that merely resembles this layout is not accepted.

## Output and exit codes

Workflow commands emit one UTF-8 JSON object followed by a newline:

```json
{
  "contractVersion": "tooltail.fixture-result/1",
  "command": "validate",
  "status": "succeeded",
  "reasonCode": "fixture.skill_valid",
  "data": {}
}
```

`data` is command-specific. Absolute physical paths are not included in the six-scenario golden result. `help` and `version` intentionally remain plain text.

| Exit | Meaning |
| --- | --- |
| `0` | Command completed and its closed postconditions passed. |
| `1` | Evidence, safety, persistence, execution, verification, or recovery failed. |
| `2` | Command syntax is invalid or the requested surface is not implemented. |
| `3` | Compilation is safe but requires a typed user clarification. |

Reason codes are the stable automation interface. Display prose is not an authority or compatibility key.

## Commands

| Command | Required options | Result |
| --- | --- | --- |
| `init-fixture` | `--workspace`; optional `--name`, `--description` | Creates a new marked workspace and initializes SQLite. |
| `snapshot` | `--workspace --phase baseline\|final\|planning` | Captures and persists one bounded authoritative snapshot. |
| `observe-fixture` | same as `snapshot` | Alias retained for the roadmap terminology. |
| `reconcile` | `--workspace`; optional `--overflow` | Reconciles baseline/final truth. `--overflow` records a closed compile barrier. |
| `compile` | `--workspace`; repeatable `--answer code=value` | Enumerates deterministic candidates and writes a canonical SkillSpec only when ambiguity is resolved. |
| `validate` | `--workspace` | Parses and semantically validates the canonical SkillSpec artifact. |
| `plan` | `--workspace` | Captures a fresh planning snapshot and writes the exact canonical plan. |
| `rehearse` | `--workspace` | Executes a bounded staged fixture with the shared executor and verifier, then approves the exact skill version on success. |
| `execute-fixture` | `--workspace` | Issues a single-use production approval, executes the exact plan, verifies it, and persists journal, receipt, and final snapshot evidence. |
| `verify` | `--workspace` | Reloads durable evidence and proves both journal completion and an unchanged final tree. |
| `undo-fixture` | `--workspace` | Builds, persists, separately approves, executes, and verifies a canonical recovery plan. |
| `golden-suite` | a new `--workspace` | Runs all six M2 acceptance scenarios and emits one exact result document. |
| `export-capsule` | `--workspace` | Validates and exports the fixture's current skill to fixed `artifacts/companion-capsule.json` without grant or approval authority. |

The compiler accepts no more than 16 answer arguments. Current targeted answer codes are emitted in the compiler's question objects; callers should not guess them. The golden invoice example uses `match.origin_scope=same_directory` and `match.filename_scope=contains_token`.

Fixture capsule export contains the one current immutable SkillSpec, its historical display lifecycle, bounded verification counts, source grant ID as provenance only, and mandatory `require_user_rebind`. It excludes grants, approvals, journals, receipts, Undo evidence, physical paths, file contents, model text, and credentials. The command serializes, re-parses, revalidates the nested SkillSpec and no-authority policy, enforces the fixture artifact limit, and only then replaces the owned artifact. Full multi-skill application export, import, rebinding UI, and retention controls remain M5 work.

## Manual fixture sequence

Use a disposable absolute path that does not already exist:

```powershell
$fixture = 'D:\tmp\tooltail-invoice-fixture'
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- init-fixture --workspace $fixture --name 'File invoice PDFs'
```

Populate only `$fixture\root`, capture `baseline`, make two representative changes manually inside that root, and capture `final`. Then run:

```powershell
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- snapshot --workspace $fixture --phase baseline
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- snapshot --workspace $fixture --phase final
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- reconcile --workspace $fixture
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- compile --workspace $fixture --answer match.origin_scope=same_directory --answer match.filename_scope=contains_token
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- validate --workspace $fixture
```

Replace the demonstration contents under `root/` with a fresh production fixture, then continue in this order:

```powershell
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- plan --workspace $fixture
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- rehearse --workspace $fixture
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- execute-fixture --workspace $fixture
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- verify --workspace $fixture
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- undo-fixture --workspace $fixture
```

Do not edit `root/` between `plan` and `execute-fixture`; the source identity, metadata, destination absence, root, grant, operation order, and SkillSpec version are bound into the approval fingerprint.

## Exact M2 golden suite

Run the acceptance dataset in a new disposable location:

```powershell
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- golden-suite --workspace D:\tmp\tooltail-m2-golden
```

The `roadmap-m2/1` dataset covers:

1. move invoice PDFs into `Invoices/`;
2. lower-case and hyphenate image stems;
3. prefix names from each file's last-write year and month;
4. copy matching files into `Review/` while preserving sources;
5. request typed clarification between fixed date text and last-write metadata;
6. reject deletion, content modification, reparse evidence, cross-volume movement, and watcher overflow.

The four positive scenarios include the complete canonical SkillSpec, canonical plan, plan projection, rehearsal receipt, execution receipt, original/final trees, canonical recovery plan, Undo receipt, and restored tree. Watcher hints are also reconciled with no hints, ordered hints, and reordered duplicated hints; all three encodings must be byte-identical.

The normalized-LF SHA-256 of the committed complete result is:

```text
30ab4fe4e20ce99088820e0ea9a25aa46d971d8e05fa714c385af303d966d75b
```

The expected document is committed at `tests/Tooltail.SkillFixtureCli.Tests/Fixtures/golden-suite.expected.json` and is compared structurally in addition to the digest.
