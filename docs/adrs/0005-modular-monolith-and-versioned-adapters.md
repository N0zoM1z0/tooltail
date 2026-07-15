# ADR 0005: Use a Modular Monolith with Versioned Edge Adapters

- Status: Accepted
- Date: 2026-07-15

## Context

Tooltail combines UI, local persistence, native window events, file observation, deterministic compilation, and execution. Splitting the prototype into many processes would add IPC, startup, deployment, and failure-mode work. Keeping everything in an undifferentiated UI project would make later hardening and testing difficult.

The Agent Body also needs to consume event sources that may change independently, including a deterministic simulator and optional public CLI JSONL output.

## Decision

Build v0.1 as one standard-user desktop process with strict project and interface boundaries:

- pure domain and application core;
- versioned serializable contracts;
- separate Windows, SQLite, file-skill, and agent-event adapters;
- WPF as composition root and presentation only.

Use an in-process event dispatcher whose committed domain events drive presentation projections. External agent events enter through bounded, versioned adapters and are normalized before projection.

The deterministic simulator is the conformance oracle. Optional Codex integration may launch a user-configured `codex exec --json` process and parse stdout. It may not read private session storage or make an undocumented app-server protocol a required dependency.

If a component later moves out of process, use Windows named pipes with current-user ACLs, a random per-launch capability token, strict message/queue/time limits, and protocol version negotiation. Process separation alone must never be described as a security sandbox.

## Consequences

Positive:

- simple local deployment and lifecycle for the hypothesis build;
- transactional coordination is easier;
- core logic remains headless and testable;
- effect boundaries are ready for later isolation;
- external event churn is contained in adapters.

Costs:

- a crash can affect UI and execution together;
- an in-process bug is not an isolation boundary;
- interfaces and architecture tests require discipline;
- a later executor split needs deliberate migration and recovery design.

## Rejected alternatives

### Microservices or multiple local daemons from day one

Rejected because they add operational complexity without proving isolation or user value.

### Directly bind UI to external CLI event JSON

Rejected because external schemas and text must not control authoritative presentation or effects without normalization.

### Depend on an undocumented agent session database/protocol

Rejected because it is unstable, privacy-sensitive, and unnecessary when a public JSONL interface and simulator exist.
