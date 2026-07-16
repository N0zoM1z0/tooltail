# ADR 0007: Use an Explicit Local-Only Research Event Sink

- Status: Accepted
- Date: 2026-07-16

## Context

M6 must measure the product hypotheses without turning Tooltail into telemetry or collecting the sensitive desktop material that the v0.1 privacy model excludes. Research timing and outcome events are a new data sink, so they require an explicit architecture decision rather than reuse of diagnostics, SQLite authority state, or an analytics SDK.

Study exports also need a stable reviewable format. Free-form messages, raw paths, filenames, window titles, prompts, content, user names, and machine identifiers would make accidental disclosure difficult to bound or test.

## Decision

Research mode is off by default and may start only through a visible local opt-in action. Opt-in creates random study and session IDs plus an in-memory random session salt. It does not create an account, network client, upload job, background service, or mutation authority.

Store research events separately below Tooltail-owned local application storage as bounded JSONL conforming to `research-event.schema.json`. The contract is closed and contains only:

- random event, study, and session IDs;
- monotonically bounded sequence and UTC time;
- closed event and body-state discriminators;
- stable reason codes, success flags, durations, counts, skill versions, and 1–7 ratings;
- optional SHA-256 tokens derived with the session-local salt.

No contract field can hold raw path/name/title/content/model/user/machine text. The session salt is not exported, so a path token is useful only for equality within that session.

Provide an exact local preview before any export, write exports only with `CreateNew` below Tooltail-owned storage, and perform no automatic upload. One-click deletion is application-state deletion, never a learned file action: it may truncate/remove only identity-checked Tooltail research artifacts and disables collection. It cannot target a supplied path or pattern.

Unknown schema versions, properties, enum values, invalid UTC times, unbounded numbers, and malformed tokens fail closed. v1 readers reject later versions until a documented compatibility decision exists.

The pre-handoff v1 vocabulary was completed with `clarification_completed` and `approval_decided` because the accepted M6 protocol measures those intervals separately. This changes no field or executable meaning and all earlier v1 events remain readable, but a strict pre-handoff reader will reject the new values. No research build or external export had been released; after M6 handoff, further discriminator additions require a new contract version.

## Consequences

Positive:

- consent and the existence of local research data are visible;
- export contents are structurally minimized and previewable;
- research data cannot authorize an effect or alter product state;
- studies can be reproduced without cloud infrastructure or telemetry.

Costs:

- event vocabulary changes require schema/example/parser tests and protocol documentation;
- local deletion and bounded retention need dedicated owned-storage code;
- raw qualitative interviews and screen recordings remain outside Tooltail and require separate consent.

## Rejected alternatives

### Reuse normal diagnostic logs

Rejected because diagnostic retention and operator intent differ from explicit research consent, and log messages are not a sufficiently closed export contract.

### Add an analytics or crash-reporting SDK

Rejected because it introduces network behavior, third-party processing, identifiers, and telemetry that v0.1 explicitly excludes.

### Store research fields in the authority database

Rejected because research data is not execution authority and should remain separately inspectable and deletable without changing grant, plan, journal, receipt, or skill history.
