# ADR 0004: Make Snapshots Authoritative and Watcher Events Hints

- Status: Accepted
- Date: 2026-07-15

## Context

`FileSystemWatcher` is useful for responsive observation but does not provide a complete ordered transaction log. Operations can produce duplicate or coalesced events, buffers can overflow, and other processes can modify the same tree during teaching.

Compiling a durable skill from raw event order would teach implementation accidents and could silently omit effects.

## Decision

A teaching episode uses three evidence sources:

1. a bounded baseline snapshot of the granted root;
2. filtered watcher hints captured while the lesson is active;
3. a bounded final snapshot using the same representation.

Reconciliation compares snapshots and uses watcher data only to disambiguate supported identity/move/copy cases. The normalized evidence, not raw events, feeds the compiler.

The episode becomes invalid and cannot compile when:

- the watcher reports buffer overflow;
- either snapshot is incomplete or cancelled;
- evidence cannot distinguish a supported effect safely;
- an entry changes concurrently in a way that breaks identity assumptions;
- unsupported effects such as delete or content modification occur;
- a network path or reparse point is encountered.

Snapshot and hash budgets are explicit. Watcher callbacks enqueue minimal immutable hints and return immediately.

## Consequences

Positive:

- lesson semantics do not depend on watcher ordering or multiplicity;
- tests can construct exact before/after fixtures;
- missed hints do not automatically lose truth;
- unsupported and concurrent changes fail visibly.

Costs:

- snapshots add I/O and hashing cost;
- move/copy inference sometimes remains ambiguous;
- large trees require budgets and may be unsupported;
- the user must repeat an invalid lesson.

## Rejected alternatives

### Raw watcher stream as demonstration

Rejected because it cannot guarantee completeness, uniqueness, order, or transaction boundaries.

### Continuous screen and input recording

Rejected because it captures sensitive unrelated data and still does not establish authoritative file outcomes.

### Periodic snapshots without watcher hints

Rejected as the only source because hints can improve identity/reconciliation and responsive UI at low additional authority. Snapshots still remain authoritative.
