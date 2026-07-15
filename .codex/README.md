# Codex Usage

## Primary assignment

Use `CODEX_MASTER_TASK.md` for the end-to-end Tooltail v0.1 build. Root `AGENTS.md` is the durable instruction file for every task in this repository.

Recommended first prompt:

```text
Read AGENTS.md and CODEX_MASTER_TASK.md. Inspect docs/BUILD_STATUS.md and the repository tree. Begin M0, keep a milestone plan, implement through the next verified runnable checkpoint, and update BUILD_STATUS with exact commands and results. Do not create a remote, commit, push, or broaden v0.1 capabilities.
```

For a focused task, cite the milestone, requirement, schema, and acceptance test rather than pasting a replacement architecture.

## Verify instruction discovery

From the repository root, ask Codex to summarize the active instructions before a large run. A non-mutating CLI check can use the installed Codex version's documented `exec` and sandbox options, for example:

```powershell
codex exec --sandbox read-only "Summarize the active AGENTS.md instructions for this repository and list the commands required before handoff. Do not modify files."
```

Confirm the response mentions at least:

- lease versus grant separation;
- exact-plan approval;
- the four allowlisted file primitives;
- no shell/delete/overwrite/reparse/network behavior;
- snapshot truth versus watcher hints;
- deterministic body projection;
- required tests and `BUILD_STATUS.md` update.

Codex instruction discovery is size-bounded. Keep root `AGENTS.md` concise and add nested `AGENTS.md` files only when a subtree genuinely needs different instructions. A nearer file may refine but must not weaken root safety invariants.

## Optional Tooltail adapter

Tooltail's optional Agent Body integration may launch a documented `codex exec --json` process and consume its stdout JSONL through `Tooltail.Adapters.AgentEvents`.

This is product runtime integration, not a repository instruction mechanism. Keep it optional and defensive:

- simulator traces are the CI oracle;
- external line length, queue, time, and process lifetime are bounded;
- events are normalized before projection;
- event text is untrusted presentation data;
- Codex cannot create Tooltail grants or approvals;
- do not read private Codex session files or depend on undocumented protocols.

Consult the current official Codex CLI reference before implementing the adapter because event formats and options may evolve.
