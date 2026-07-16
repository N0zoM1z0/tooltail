# Threat Control and Test Matrix

Status values are `PASS` for committed automated evidence, `NOT RUN` for required attended/external evidence, and `N/A` only when the surface is deliberately absent. No `PASS` row relies only on prose.

| Threat | Automated control evidence | Manual/external evidence | Status |
| --- | --- | --- | --- |
| T1 scope confusion | separate lease/grant domain tests, architecture boundaries, Home/Inspector control tests, durable revoke/restart smoke | Study B and independent first-launch evaluator | automated PASS; human NOT RUN |
| T2 traversal/canonicalization | adversarial Windows path corpus and containment properties; existing-folder preview/confirm identity re-capture; protected-root persistence tests | independent path/grant review | PASS; independent review NOT RUN |
| T3 reparse/link redirection | inserted-link/root/source/destination races; selected-root preview/confirm/restart probes; native handle probe | Developer Mode symlink row on every supported Windows build | PASS with declared host skip; matrix NOT RUN |
| T4 TOCTOU replacement | plan fingerprint drift, source identity/hash drift, destination collision, approval consumption race | independent plan/permission review | PASS; independent review NOT RUN |
| T5 destructive skill | closed schema/compiler/planner/executor tests and architecture mutation scan | none beyond code review | PASS |
| T6 prompt injection | deterministic compiler; untrusted structured inputs; no model authority | optional adapter review | PASS for v0.1 deterministic path |
| T7 malformed agent JSONL | line/stream/count/time/sequence/duplicate/control-text tests and simulator failures | none | PASS |
| T8 private Codex state | static source scan and fake owned-child process tests | none | PASS |
| T9 IPC impersonation | no product IPC/listener exists; architecture/process scan | new review required before any IPC | N/A for v0.1 |
| T10 journal tamper/replay | strict domain replay, SQLite tamper/approval race, crash-prefix and restart tests | independent journal/recovery review | PASS; independent review NOT RUN |
| T11 Undo destroys later work | changed copy, occupied source, non-empty directory, unrelated-change, reverse-proof tests | independent recovery review | PASS; independent review NOT RUN |
| T12 logging/telemetry leakage | allowlisted event contracts, no raw adapter retention, research exclusions, closed no-raw-field diagnostic builder, strict readback/hash preview/CreateNew export, leak/mutation tests, ReleaseAudit secret scan | independent diagnostic/privacy review | automated PASS; independent review NOT RUN |
| T13 capsule import | bounded exact-byte/hash preview, strict schema/content/linear-history parser, pristine-only atomic identity/Stale-history import, no-authority persistence tests, and scope-only Draft rebind | independent import/rebind review and attended comprehension | automated PASS; independent/human NOT RUN |
| T14 elevation | standard-user manifest and elevated-target eligibility policy | packaged standard-user/elevated-target row | automated PASS; packaged row NOT RUN |
| T15 misleading completion | deterministic precedence, durable evidence body integration, mismatch/restart smoke | screen reader/high contrast/reduced motion attended matrix | automated PASS; attended NOT RUN |
| T16 research drift | default-absent sink, consent, strict closed readback, preview/export/delete, no-network architecture tests | participant consent protocol | automated PASS; participant studies NOT RUN |
| T17 local-state deletion | expiring authorization, exact fixed-file/preservation integration, malformed/oversized intent, cancellation, crash-prefix recovery, startup-order, Home two-step tests, and packaged data-retention fixture | independent deletion/recovery review | automated PASS; independent review NOT RUN |
| T18 package/uninstall | locked RID publish, deterministic manifest/hash/readback, contamination/link/path tests, standard-user manifest, packaged apphost and marker-bound program-only removal with retained data sentinel | signed installer/SmartScreen and independent packaging review | portable automated PASS; signed installer/review NOT RUN |

## Independent review ledger

The implementation has received a self-review against path, canonical plan, PermissionGateway, append-only journal, recovery, capsule parser, and research/logging boundaries during milestone work. That is not independent review. Record reviewer, commit, findings, severity, resolution, and re-review in a dated release evidence packet. Until another qualified reviewer completes it, the M7 independent-review gate is **NOT RUN**.

No critical/high issue is currently known from the automated/self-review evidence. This sentence is not a substitute for the independent review gate.
