# Test and Research Plan

## 1. Purpose

Tooltail has two kinds of uncertainty:

1. engineering uncertainty — whether constrained learning and embodied control can be made deterministic, safe, and recoverable;
2. product uncertainty — whether users understand the body language, experience correction as real learning, and value this specific taught companion.

Passing automated tests is necessary but does not prove the product thesis. The research program therefore combines contract tests, adversarial tests, usability studies, and longitudinal reuse.

The attended, reset-safe execution protocol and results ledger are in [`EVALUATOR_CHECKLIST.md`](EVALUATOR_CHECKLIST.md). All participant rows remain **NOT RUN** until a real evaluator completes them; automated evidence is never substituted.

## 2. Product hypotheses

### H1 — placed scope is understandable

When the pet is attached to a window and a resource grant is visible, users can accurately state what Tooltail can observe and change.

Success threshold for the MVP study:

- at least 80% of participants answer all critical scope questions correctly after first use;
- at least 90% correctly revoke scope without assistance;
- no participant believes that merely touching a window silently grants access to unrelated files.

### H2 — body state improves control comprehension

The embodied state vocabulary lets users identify waiting, working, tool use, need for input, completion, failure, and revocation at least as accurately as a conventional status panel, with faster recognition for interruption-relevant states.

Success threshold:

- at least 85% state-identification accuracy;
- median recognition of `needs_input`, `failed`, and `permission_revoked` within three seconds;
- no critical state is distinguishable only by color.

### H3 — correction is perceived as causal learning

After correcting a demonstrated edge case, users can explain what changed in the skill and observe the next plan behaving differently.

Success threshold:

- at least 80% correctly predict the corrected plan;
- at least 70% spontaneously describe the change as learning/teaching rather than a one-off retry;
- the system's semantic diff matches the actual plan change in every session.

### H4 — instance value emerges

After repeated successful reuse, users prefer retaining their taught companion/capsule over replacing it with an identical untaught instance.

This is a directional longitudinal signal, not a launch vanity metric. Measure stated preference together with retained skill reuse, correction behavior, and willingness to export the capsule.

### H5 — a narrow domain is sufficient

A constrained file workflow can create an “aha” moment without chat, decoration, general automation, or a large skill catalog.

Failure is informative. If participants understand the loop but find file work intrinsically low-value, retain the architecture and test the Blender vertical; do not mask the result with cosmetic features.

## 3. Research sequence

### Study A — body-language comprehension

Participants: 5–8 formative participants, followed by 12–16 validation participants.

Method:

- show short silent clips or live simulator states in randomized order;
- ask “What is it doing?”, “Can it act right now?”, and “What would you do next?”;
- include color-reduced and reduced-motion modes;
- compare embodied-only, inspector-only, and combined presentations within subjects with counterbalanced order.

Record confusion pairs. Revise poses and motion semantics before adding visual polish.

### Study B — permission and revocation mental model

Participants: 8–12.

Tasks:

- attach the pet to an eligible application;
- grant one folder;
- inspect current authority;
- predict whether five example actions are permitted;
- revoke the window lease, then the folder grant;
- respond to a changed-plan approval invalidation.

Critical errors include assuming access to all windows, confusing a lease with a grant, or believing a revoked grant still permits execution.

### Study C — teach/correct/reuse lab

Participants: 12–20 target users who regularly manage files or assets.

Tasks:

- teach the canonical invoice workflow with supplied fixtures;
- resolve one controlled ambiguity;
- inspect and approve a plan;
- run it on a fresh set;
- correct a negative example;
- predict and run version 2;
- inspect the receipt and undo;
- export the capsule.

Use think-aloud only in the formative round; it alters timing and should not be used for final completion-time comparisons.

### Study D — short longitudinal pilot

Participants: 6–10 target users for 7–14 days.

Method:

- support one real granted folder per participant;
- require explicit invocation and approval for every production run;
- conduct brief day-1, mid-point, and exit interviews;
- collect local opt-in event summaries and participant-kept diaries;
- ask which skills they would keep, delete, correct, or export.

The key outcome is repeated verified reuse of the same corrected skill, not total time spent with the pet.

## 4. Product metrics

### Learning loop

- lesson start-to-valid-candidate rate;
- invalid lesson reasons;
- examples per compiled skill;
- clarification questions per candidate;
- candidate-to-approved conversion;
- rehearsal failures;
- corrected versions per skill;
- percentage of corrections that change the next relevant plan as predicted.

### Execution quality

- planned, executed, verified, skipped, failed, and undone steps;
- approval invalidation frequency and reason;
- verification mismatch rate;
- recovery state after injected and real interruption;
- unintended-collision prevention count;
- median receipt inspection time.

### Embodiment and control

- state recognition accuracy and latency;
- lease creation/revocation success;
- accidental focus-steal reports;
- inspector-open rate by state;
- pause/cancel/revoke action completion;
- scope prediction correctness.

### Instance value

- weekly verified reuse of an existing skill;
- active skills with at least one correction and later success;
- capsule export intent and completion;
- preference for taught versus reset instance;
- qualitative language about trust, ownership, and replaceability.

Do not optimize message count, animation exposure, pet clicks, or session length. They are not evidence of procedural value.

## 5. Privacy-preserving research data

Research logging is off by default and requires an explicit opt-in screen. Default exported events may include:

- random study/session IDs;
- timestamps and durations;
- event and state discriminators;
- counts and bounded sizes;
- salted, session-local path tokens;
- skill and schema versions;
- success/failure reason enums;
- participant-entered study ratings.

Default exports must not include raw paths, filenames, file contents, screenshots, window titles, prompt text, model output text, usernames, or machine identifiers. A separate consent level is required for screen recording or interviews, managed outside the product repository.

## 6. Automated test taxonomy

### Domain tests

- lifecycle transition tables;
- immutable version rules;
- state-projection precedence;
- approval fingerprint equality and invalidation;
- trust evidence rules that never create authority.

### Contract tests

- valid examples pass every JSON schema;
- unknown versions and action discriminators fail;
- round-trip serialization is canonical where fingerprints depend on it;
- backward-compatible readers have golden fixtures;
- event and capsule size limits are enforced.

### File-skill tests

- baseline/final reconciliation under duplicate, coalesced, delayed, and missing watcher hints;
- hash budget and cancellation;
- deterministic candidate ordering;
- ambiguity and negative-example handling;
- collision policies;
- exact planning and postconditions;
- undo under unrelated file changes;
- authority revocation after mutation preparation and before the prepared effect;
- precise `ensure_directory` create-new ownership evidence for later Undo removal.
- exact cross-platform M2 golden output for all six roadmap scenarios, including persisted receipt reload and complete tree restoration after separately approved Undo;
- post-execution mutation rejection and reparse/link insertion at fixture workspace, artifact, state, source, and destination boundaries.

### Platform tests

- HWND plus process-identity validation;
- event-hook callback lifetime and reentrancy;
- window eligibility;
- coordinate conversion across monitor layouts and DPI;
- lease expiry and revocation;
- non-activation and keyboard accessibility.
- Windows handle-bound file mutation races where competitors act after handles are prepared and after the final permission read, including create-new destination collision, parent replacement attempts, and late children before internal removal.

Native desktop tests should be split into pure adapter tests, fake-window-system tests, and a small tagged set that runs on an interactive Windows test machine.

### Persistence tests

- forward migrations on empty and populated fixture databases;
- transaction rollback;
- append-only journal invariants;
- crash markers and recovery queries;
- export after partial historical migrations;
- deletion/retention behavior.

### UI tests

- view-model transitions from domain events;
- inspector state and scope rendering;
- reduced motion and high contrast;
- keyboard navigation and focus behavior;
- body-state snapshot tests for original minimal assets;
- integrated File Apprentice body tests for closed activity precedence, typed file-tool props, persisted restart reconstruction, unapproved-plan input state, verified receipt completion, and recovery/failure visibility;
- no animation state directly invokes an effect.

## 7. Adversarial path corpus

At minimum, test:

- `..` and dot-segment traversal;
- rooted and drive-relative paths;
- UNC and device paths;
- mixed separators and repeated separators;
- trailing spaces and periods;
- reserved device names;
- case-only renames;
- Unicode normalization and confusable names;
- alternate data stream syntax;
- symbolic links, junctions, mount points, and other reparse points;
- a link introduced after planning but before execution;
- root replacement after approval;
- destination introduced after approval;
- source content changed after approval;
- inaccessible, locked, sparse, compressed, encrypted, and very large files;
- same name under case-insensitive comparison;
- long paths near platform limits;
- cross-volume sources and destinations;
- network and removable roots.

The v0.1 answer to many cases is an explicit rejection. That is a passing result when the reason is stable and user-readable.

## 8. Crash and concurrency injection

Inject cancellation or process termination at every boundary:

```text
before journal intent
after intent, before mutation
during mutation where the API permits observation
after mutation, before commit marker
after commit marker, before verification
during verification
after verification, before receipt projection
```

On restart, Tooltail must never guess that an ambiguous effect is safe to repeat. It must inspect actual state, mark the execution as needing recovery, and offer only validated recovery plans.

Also test:

- file changed by another process after planning;
- competitor-created destination after the final permission check;
- grant revoked during mutation preparation before the prepared effect;
- two Tooltail runs targeting the same input;
- user revocation during each step;
- pause/cancel while watcher reconciliation is active;
- Desktop safe-pause/cancel while a corrected-skill rehearsal is active, proving no production plan is exposed and a fresh explicit retry is required;
- durable exact-folder-grant revocation after verified work, proving the tree is unchanged and restart cannot plan, execute, or Undo under the old grant;
- database busy/locked conditions;
- disk full and permission denied;
- adapter disconnect and malformed JSONL during file execution.

## 9. Property and metamorphic tests

Useful invariants include:

- accepted relative destinations remain under the same canonical immutable root;
- planning is pure: identical skill version and input snapshot yield identical plan and fingerprint;
- adding an unrelated non-matching file does not change existing operations;
- reordering watcher hints does not change snapshot reconciliation;
- executing then successfully undoing a supported fixture restores the original observable tree;
- reducing a grant's action set never increases the accepted plan set;
- changing any executable field changes the canonical plan fingerprint;
- a presentation/body-state change cannot change an execution plan.

Use generated data with bounded path lengths and store every discovered counterexample as a named regression fixture.

## 10. Performance and reliability budgets

Initial engineering budgets, to be measured rather than promised:

- pet rendering remains responsive at 60 Hz on a typical Windows 11 laptop while no task is active;
- UI thread work from native callbacks stays below one frame budget;
- target tracking visibly settles within 100 ms after a normal window move event;
- snapshot and plan operations remain cancellation-aware and keep the UI responsive for 10,000-entry lab folders;
- idle CPU use is below 1% on the reference machine after reconciliation settles;
- idle private memory and startup time are recorded in every tagged release;
- JSONL adapters bound line size, queue depth, and retained history.

Budgets are gates only after a reference machine and measurement script are committed.

## 11. Accessibility matrix

Test every critical state with:

- keyboard-only navigation;
- Windows high-contrast themes;
- 200% text scaling where applicable;
- reduced motion;
- color-vision simulation;
- screen-reader labels in inspector and home surfaces;
- pet hidden while controls remain available through the tray/home surface.

Body language is a redundant channel, never the only way to inspect state or stop work.

## 12. Release evidence packet

Every alpha candidate should attach:

- CI result and exact commit;
- contract/schema compatibility report;
- golden fixture diff;
- adversarial path suite result;
- crash-injection matrix;
- Windows DPI/monitor test matrix;
- threat-model delta;
- known limitations and unsupported cases;
- research-build version and any user-study protocol changes.

## 13. Stop or pivot criteria

Pause expansion and revisit the thesis if any of these persist after two focused design iterations:

- users cannot distinguish lease context from resource authority;
- safe rules cannot be explained well enough for users to predict effects;
- corrections regularly require hidden manual programming by the team;
- verified reuse is rare even among participants with recurring file tasks;
- embodiment adds distraction without improving state or control comprehension;
- recovery cannot be made reliable under the closed primitive set.

A file-domain failure does not automatically invalidate procedural companionship. Separate “the loop is not compelling” from “this domain is not valuable enough.”
