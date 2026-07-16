# Agent Body, Simulator, and Event Adapters

## Purpose

The Agent Body is a deterministic presentation of normalized run status. It does not execute a learned file skill, grant authority, infer permission from text, or treat animation as proof of success. The same committed event prefix always produces the same body projection.

M3 keeps three boundaries separate:

1. provider-specific input is parsed and minimized;
2. the closed normalized event vocabulary is validated and projected by the domain;
3. desktop presentation reads only the resulting body state, typed parameter, and reason code.

## Canonical body vocabulary

| State | Parameter | Stable reason | Meaning |
| --- | --- | --- | --- |
| `home_idle` | none | `body.home_idle` | no visible scope and no active run |
| `scoped_idle` | none | `body.scoped_idle` | a visible context exists but no run is active |
| `observing` | none | `body.observing` | allowlisted context observation is active |
| `working` | optional closed tool kind | `body.working` or `body.working_tool` | a run or one bounded work unit is active |
| `parallel_work` | count from 2 through 32 | `body.parallel_work` | two or more bounded work units are active |
| `needs_input` | none | `body.needs_input` | an exact pending question requires the user |
| `blocked` | none | `body.blocked` | the run reported an explicit non-terminal block |
| `completed_receipt` | none | `body.completed_receipt` | the run completed and its result card is available |
| `failed` | none | `body.failed` | a tool or run failed |
| `paused_or_cancelled` | none | `body.paused` or `body.cancelled` | active progress is paused or the run was cancelled |
| `permission_revoked` | none | `body.permission_revoked` | a relevant permission was revoked |
| `disconnected` | none | `body.disconnected` | the event adapter is no longer trustworthy or connected |

The projector uses this exact precedence, highest first:

1. failed;
2. permission revoked;
3. disconnected;
4. needs input;
5. blocked;
6. paused or cancelled;
7. parallel work;
8. one concrete active tool or subagent;
9. observation;
10. a generic active run;
11. an unopened completed receipt;
12. scoped idle or home idle.

This ordering prevents a background tool event from hiding failure, revocation, disconnection, a pending question, a block, or cancellation. Opening the completed receipt explicitly dismisses that presentation back to scoped or home idle; it does not mutate run history.

## Bounded normalized JSONL

`NormalizedAgentJsonlAdapter` is the generic normalized-event boundary. Its defaults are:

- one strict UTF-8 JSON object per line;
- 64 KiB maximum line size;
- 16 MiB maximum stream size;
- 4,096 maximum non-empty events;
- 4 KiB fixed read buffer;
- one source and one run per stream;
- strict v1 contract parsing with unknown normalized fields and enum values rejected;
- idempotent exact event IDs, with conflicting reuse rejected;
- monotonic sequence and UTC event time.

The reader does not use an unbounded line API. It accepts LF or CRLF across arbitrary stream chunk boundaries, clears pooled buffers, returns reason-coded cancellation or I/O failure, and never includes input content in an error.

## Deterministic simulator

`Tooltail.AgentEventSimulator` is the CI oracle. It does not launch Codex, read user files, or default to a user folder.

```powershell
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- list
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- emit --trace normal-start-tool-complete
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- project --trace parallel-two-units
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- verify-all
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- golden
```

`list`, `project`, and `verify-all` return bounded machine-readable JSON. `emit` returns the exact fixture JSONL. `golden` returns the compact exact state-sequence format used by the committed acceptance fixture. Unknown commands or trace names exit with code 2; a conformance mismatch exits with code 1.

The fixed catalog contains:

- normal tool completion;
- observation only;
- targeted input and resolution;
- two parallel typed units;
- tool and run failure;
- pause, resume, and cancellation;
- permission revocation during a tool;
- adapter disconnection;
- blocked and resumed work;
- an exact duplicate event;
- malformed JSON;
- regressed event time;
- out-of-order sequence;
- an oversized line;
- the event-count/backpressure limit.

Every trace declares its expected status, reason, accepted and duplicate counts, and complete parameterized body sequence. `tests/Tooltail.Adapters.AgentEvents.Tests/Golden/simulator-state-sequences.golden.txt` pins the whole catalog byte-for-byte across platforms.

## Optional Codex CLI adapter

Codex integration is optional and is not part of simulator or core-demo success. The reviewed adapter follows the public non-interactive CLI surface documented in the [Codex CLI reference](https://developers.openai.com/codex/cli/reference) and [non-interactive mode guide](https://developers.openai.com/codex/noninteractive).

`CodexExecConfiguration.Create` requires:

- an explicit absolute executable path;
- an explicit absolute working directory;
- a non-empty approved prompt bounded to 16 KiB UTF-8;
- a timeout from one second through 30 minutes;
- a bounded stderr discard limit from 1 KiB through 1 MiB.

The runner constructs a `ProcessStartInfo` with `UseShellExecute = false` and `ArgumentList` equivalent to:

```text
exec --json --ephemeral --sandbox read-only --ignore-user-config --cd <workspace> -
```

The approved prompt is written to redirected stdin and is never placed in a command string or argument. Prompt bytes are cleared after the write. Stdout goes directly through the bounded raw adapter. Stderr is counted and discarded without being returned, logged, or persisted. A stderr overflow, malformed stdout, timeout, launch failure, or unexpected EOF becomes a reason-coded visible adapter failure.

Only the `ICodexChildProcess` returned by Tooltail's reviewed launcher can be terminated. Cancellation, timeout, adapter rejection, or stderr overflow never searches for or terminates an unrelated host process.

### Defensive raw mapping

The raw provider object exists only for the duration of one bounded line. The adapter retains only allowlisted status metadata:

| Public JSONL event | Normalized result |
| --- | --- |
| first `thread.started` or `turn.started` | `run_started` |
| `command_execution` item start/end | terminal tool start/completion/failure |
| `file_change` item start/end | file tool start/completion/failure |
| `mcp_tool_call` item start/end | other tool start/completion/failure |
| `web_search` item start/end | browser tool start/completion/failure |
| `turn.completed` | `run_completed`, only with no active item |
| `turn.failed` | `run_failed` |
| `error` | `adapter_disconnected` |

Raw item IDs are SHA-256-derived opaque IDs. Agent messages, reasoning, plans, prompts, commands, file changes, queries, paths, tool output, token usage, and error text are discarded. Known content-only items are counted as ignored; unknown future events are counted and ignored. Malformed known shapes fail only this adapter and cannot create a grant, approval, lease, or file effect.

Tests use a recorded redacted fixture and fake child-process boundary. CI never requires a Codex installation, credential, network call, model response, or private state directory.

## Verification

Portable verification is:

```powershell
dotnet test tests/Tooltail.Domain.Tests -c Release --no-restore
dotnet test tests/Tooltail.Adapters.AgentEvents.Tests -c Release --no-restore
dotnet test tests/Tooltail.Architecture.Tests -c Release --no-restore
dotnet run --project tools/Tooltail.AgentEventSimulator -c Release --no-build -- verify-all
```

Architecture tests confine process launch/termination APIs to the reviewed optional Codex boundary and statically reject references to private Codex session, rollout, or app-server state.
