# ADR 0006: Bind Single-Use Approval to a Canonical Plan Fingerprint

- Status: Accepted
- Date: 2026-07-16

## Context

A user must approve the exact file effects that Tooltail later performs. Hashing a serializer's incidental property order, raw SkillSpec JSON, display text, or an incomplete operation list could let semantically different work reuse one approval. Persisted plans may also be stale or corrupt, and a mutable approval row could accidentally authorize more than one execution.

The v0.1 plan language is closed and has a small typed projection, so its canonical form can be defined directly without accepting arbitrary canonical JSON from an external source.

## Decision

Define canonical execution-plan contract `tooltail.execution-plan/1` as compact UTF-8 JSON written in an explicit property order. The encoding uses:

- lowercase `D`-format GUIDs;
- seven-digit UTC timestamps ending in `Z`;
- explicit snake-case names for every enum value;
- grant capabilities sorted with ordinal comparison;
- operations in their contiguous sequence order;
- explicit `null` values where the closed operation shape has no source or content hash;
- SHA-256 rendered as 64 lowercase hexadecimal characters.

The fingerprint input includes the plan ID and lifetime, skill ID/version/specification hash, grant ID/full action set/root identity, ordered primitive list, normalized relative paths, source identity/metadata/content hash when present, destination precondition, and expected source/destination postconditions.

Canonical bytes are always regenerated from validated typed objects. Stored fingerprints and externally supplied JSON are never treated as canonical merely because they contain a hash field.

An approval records the exact plan ID and fingerprint, the explicit UTC decision time, and an expiry no later than the plan expiry. It is active once, then becomes consumed or revoked. `PermissionGateway` recalculates the fingerprint and checks the current skill lifecycle, exact grant/root/action set, grant lifetime/revocation, plan lifetime, and approval immediately before an execution is authorized. A `WindowLease` is deliberately absent from this authority path.

Changing the canonical contract requires a new contract version, golden vector, compatibility decision, and migration behavior. Historical fingerprints continue to use the version stored with their plan.

## Consequences

Positive:

- any material replanning invalidates approval;
- semantically identical capability sets hash identically regardless of input enumeration order;
- corrupted or forged stored fingerprints fail before approval consumption;
- golden JSON and digest vectors make cross-platform drift visible;
- approval cannot be replayed after successful authorization.

Costs:

- adding an executable field requires an intentional canonical-contract revision;
- old canonical encoders must remain available while their plans or receipts are retained;
- a SHA-256 fingerprint proves equality to the typed projection, not that the underlying file system is unchanged, so path and source identities still require immediate revalidation.

## Rejected alternatives

### Hash ordinary reflection-based JSON output

Rejected because serializer settings, property ordering, converters, and framework upgrades are not an authority contract.

### Hash only the SkillSpec

Rejected because a skill does not bind concrete inputs, destinations, grant state, root identity, operation order, or postconditions.

### Reuse approval while a plan looks equivalent in the UI

Rejected because display summaries omit authority-bearing details and are untrusted presentation, not execution identity.
