# File Mutation Crash and Recovery Matrix

## Scope

Production, rehearsal, and Undo use the same journaled executor boundary. Test fault injectors are absent from production composition and stop the process-shaped test at an exact named boundary. Recovery never automatically replays an ambiguous mutation.

| Boundary | Durable standard prefix | Filesystem may have changed | Restart interpretation | Automated evidence |
| --- | --- | --- | --- | --- |
| `journal_opened` | opened event | no | not started | production and Undo crash matrix |
| `step_intent_persisted` | opened + exact intent | no | started, inspect before any retry | production and Undo crash matrix |
| `before_primitive` | same exact intent | no | started, inspect; never assume no external race | production and Undo crash matrix |
| `after_primitive` | intent only | yes | recovery required; never replay | production and Undo crash matrix |
| `mutation_observed_persisted` | intent + observed | yes | committed evidence incomplete | production and Undo crash matrix |
| `step_committed_persisted` | committed marker | yes | verification still required | production and Undo crash matrix |
| `step_verified_persisted` | verified step | yes | preserve verified evidence; do not repeat | production and Undo crash matrix |
| `original_step_rollback_linked` | recovery step verified and original journal linked | yes, inverse verified | retain both journals; do not duplicate link | Undo crash matrix |
| `receipt_persisted` | every step verified; receipt stored | final verified state | completed; approval remains consumed | production and Undo crash matrix |

Failure-only `step_failed_persisted` and `recovery_required_persisted` markers are exercised by identity drift, cancellation, revocation, verification mismatch, and persistence tests. They are terminal evidence writes rather than a new file mutation boundary.

The production theory asserts exact journal event counts and destination existence at every standard mutation boundary. The Undo theory additionally asserts exact recovery-journal counts, original rollback-link count, removal state, and recovery-receipt presence at every inverse boundary. Separate SQLite tests reload incomplete prefixes and report inspection candidates without replay. Native Windows tests repeat the same executor with handle-derived file identity.

Open release work: an external process-termination harness and independent review must still confirm OS-level termination at these boundaries on the packaged binary. The in-process injected matrix is complete engineering evidence, not a claim that the external crash campaign has run.
