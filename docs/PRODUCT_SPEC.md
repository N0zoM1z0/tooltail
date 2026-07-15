# Product Specification

## 1. Executive definition

Tooltail is a Windows-first desktop apprentice. A user can place it on a window, grant a narrowly defined resource, teach a repeatable workflow through one bounded demonstration session, inspect the inferred procedure, rehearse it, and correct it. The resulting skill belongs to that companion and changes how future tasks are performed.

The product claim is not “an AI can control your computer.” The claim is:

> A digital companion becomes more valuable and more personally specific by safely learning how its user works.

## 2. Product thesis

Most desktop companions have embodiment without causal utility. Most agents have utility without embodiment or identity continuity. Most memory systems preserve facts or text while leaving future execution unchanged.

Tooltail joins these into one loop:

```text
real work
  -> bounded shared experience
  -> inspectable procedural skill
  -> visible change in the companion
  -> better execution next time
  -> calibrated trust in this specific instance
```

The emotional effect is expected to emerge from accumulated, user-shaped capability rather than simulated neediness, streaks, or fictional health mechanics.

## 3. Product principles

### 3.1 Body means system state

Animation is an ambient observability and control surface. It communicates scope, activity, uncertainty, approvals, results, and revocation.

### 3.2 Growth must be causal

A visual tool or behavioral change is unlocked only by a real capability transition: draft, practiced, reliable, explicitly delegated, or stale.

### 3.3 Teaching produces an artifact

A lesson results in a versioned SkillSpec with triggers, scope, parameters, actions, invariants, postconditions, provenance, tests, and rollback behavior.

### 3.4 The model is replaceable

Companion identity, skills, evidence, and user decisions are independent of any model provider or agent runtime.

### 3.5 Safety is a visible interaction

Permissions, previews, pauses, failures, and receipts are part of the core experience, not hidden settings.

### 3.6 Absence is not punished

Tooltail does not become sick, sad, jealous, or degraded when the user leaves. Retention must come from compounding value.

## 4. Target user for the hypothesis build

The initial research cohort is not the broad consumer desktop-pet market. It is Windows power users who:

- already run coding agents or repetitive desktop workflows;
- organize downloaded files, assets, research material, or project handoffs;
- can describe a correction when automation behaves incorrectly;
- care about local control and inspectability;
- will use the prototype for at least seven days.

Suggested cohort size is 6–10 users for the short longitudinal pilot, with 4–6 internal dogfood users before external testing. Larger comprehension/validation studies are specified separately in `TEST_AND_RESEARCH_PLAN.md`.

## 5. Jobs to be done

### Functional

- Show me what a background agent is doing without requiring me to read a log.
- Let me delimit what an assistant can see and change.
- Learn a small procedure that is easier to demonstrate than to describe.
- Apply my correction on the next similar task.
- Let me inspect, test, pause, undo, export, or forget what was learned.

### Emotional

- Help me feel in control while an agent works.
- Make accumulated automation feel like something I shaped rather than a hidden account setting.
- Give me a reason to preserve this companion instance even when the underlying model changes.

## 6. Three required aha moments

### 6.1 Placed context

After placing Tooltail on a window, the user can correctly state which window is bound, which resource is granted, whether observation is active, and how to revoke it.

### 6.2 Causal correction

After correcting one inferred rule, the user observes the next execution choose the corrected behavior without repeating the instruction.

### 6.3 Instance value

After one week, the user can name at least three procedures or defaults that belong to their Tooltail instance and expresses a preference to migrate those skills rather than start from an empty companion.

## 7. v0.1 experiments

### 7.1 Experiment A: Agent Body

Purpose: determine whether embodiment improves ambient comprehension and intervention.

Inputs:

- a deterministic event simulator;
- optionally, a Tooltail-launched `codex exec --json` process;
- normalized events only, with user content discarded by default.

Required states:

- idle;
- understanding;
- reading context;
- editing;
- testing;
- needs input;
- blocked;
- completed;
- failed;
- paused.

Required controls:

- click to inspect exact status;
- grab or choose Pause to halt a Tooltail-owned run when supported;
- open a returned result card;
- dismiss or enter quiet mode.

The Codex adapter is not allowed to inspect private rollout/session storage. It only consumes documented stdout JSONL from processes Tooltail launches.

### 7.2 Experiment B: File Apprentice

Purpose: prove that one teaching session can create useful, inspectable procedural memory.

Initial action vocabulary:

- match files inside one granted root;
- create a directory inside that root;
- rename a file without overwriting;
- move a file inside that root;
- copy a file inside that root;
- write a receipt or manifest into Tooltail application data;
- ask the user when a collision or ambiguity occurs.

Excluded actions:

- learned or generalized delete; Undo has one internal exact-created-artifact removal described below;
- execute a file;
- invoke a shell;
- modify file contents;
- traverse a link or reparse point;
- access a path outside the granted root;
- operate on a network path;
- run without an approved plan.

Teaching session:

1. The user binds Tooltail to a window for visible context.
2. The user grants one local folder through a deliberate picker or file/folder drop.
3. Tooltail captures a baseline directory snapshot.
4. The user performs a small rename/move organization workflow on 2–5 examples.
5. Tooltail captures normalized file-system events and a final snapshot.
6. The deterministic compiler proposes a SkillSpec.
7. If inference is underdetermined, Tooltail asks at most two focused questions.
8. The user edits or approves the Skill Card.
9. Tooltail rehearses in a temporary copy or produces a no-write dry-run.
10. The user approves a real run on new files.
11. Tooltail journals actions, revalidates inputs, executes, verifies, and returns a receipt with Undo.
12. A correction creates a new immutable skill version.

Undo is not a learned delete capability. Its only removal operation applies to an unchanged file or empty directory proven by the journal to have been absent before and created by the exact execution being undone. It requires a fresh inverse plan, approval, scope/identity validation, journaling, and verification; otherwise Undo refuses and reports residual state.

One teaching session may include several examples. “Teach it once” means one bounded lesson, not zero confirmation from an ambiguous single file.

## 8. Primary user journeys

### 8.1 Bind and grant

1. User drags the companion over a File Explorer window.
2. Tooltail identifies an eligible underlying top-level window.
3. The target receives a visible outline and a temporary tether preview.
4. On drop, Tooltail creates a short-lived WindowLease.
5. Tooltail explains that the window binding is context, not file authority.
6. User grants a folder explicitly.
7. The tether changes to show read or read/write capability.
8. Pulling Tooltail away revokes the lease; resource grants may be revoked separately or automatically according to policy.

### 8.2 Teach a workflow

1. User selects **Teach** from Tooltail's compact action menu.
2. Tooltail displays a notebook state and an always-visible Stop control.
3. User organizes multiple files.
4. Tooltail never captures global keystrokes or screenshots.
5. User stops teaching.
6. Tooltail returns a blueprint object that opens the candidate Skill Card.

### 8.3 Review and rehearse

1. User sees human-readable When, Where, Variables, Always, Never, Ask, and Success fields.
2. Uncertain inferred fields are highlighted.
3. Dry-run shows the exact source-to-target operation list.
4. Sandbox rehearsal runs on copies when behavior requires stronger validation.
5. User approves or edits the skill.

### 8.4 Execute and undo

1. Tooltail detects or is given a compatible new batch.
2. It quietly exposes the relevant tool; it does not auto-run in v0.1.
3. User opens the plan and approves.
4. Tooltail executes one journaled step at a time.
5. Postconditions are checked after each step and at workflow completion.
6. Tooltail returns a parcel/receipt.
7. Undo is available while the journal and backup material remain valid.

### 8.5 Correct

1. User opens the receipt or Skill Card.
2. User edits the incorrect condition, destination, naming template, or ask rule.
3. Tooltail displays the semantic diff from the previous version.
4. The revised version must pass schema validation and dry-run again.
5. The next compatible task uses the new version.

## 9. Skill mastery model

Mastery is per skill and rule-based, not a global opaque score.

| State | Entry condition | Companion behavior |
| --- | --- | --- |
| Draft | Inferred but not user-approved | Carries a blueprint; cannot execute on real inputs |
| Approved | User approved and dry-run passed | May execute only after per-run confirmation |
| Practiced | At least one verified real success | Asks at every ambiguous branch |
| Reliable | At least three verified successes across two input signatures, with no unresolved recent failure | Uses a polished tool and may reduce explanatory interruptions |
| Delegated | User explicitly enables a narrowly scoped routine after Reliable | Eligible for later allowlisted automation; not used in v0.1 |
| Stale | Adapter/compiler version changed materially, scope changed, or recent verification failed | Stops using the tool and requests rehearsal |

No mastery state removes confirmation for destructive or external side effects. Those actions are outside v0.1 in any case.

## 10. Product metrics

### Agent Body metrics

- time to notice a needs-input event;
- percentage of users who correctly identify current state;
- percentage who correctly identify active scope;
- number of unnecessary inspector opens;
- interruption/annoyance rating;
- preference versus a standard task-list control condition.

### File Apprentice metrics

- teaching completion rate without facilitator intervention;
- median time to first approved skill;
- dry-run comprehension accuracy;
- second-run task success;
- correction retention on the next compatible run;
- intervention rate across successive executions;
- seven-day skill reuse;
- successful undo rate;
- number and severity of scope violations, with a required value of zero.

### Instance-value metric

At day seven, offer a hypothetical stronger but empty companion. Record whether users prefer replacement, migration, or retaining their current Tooltail, and why.

## 11. MVP exit criteria

The MVP is complete only when all are true:

- Windows 11 x64 build launches without administrator rights.
- The companion is non-activating during ambient operation and remains usable across mixed-DPI monitors.
- Window binding and revocation work against the manual test matrix.
- Window leases and file grants are represented separately in both data and UI.
- A user can teach a constrained rename/move workflow using 2–5 examples.
- A valid SkillSpec is generated, editable, versioned, and schema-validated.
- Dry-run is exact and deterministic.
- Real execution never escapes its grant and never overwrites by default.
- Every execution has a receipt and every successful mutable operation has a tested undo path.
- One correction changes the next plan.
- Unknown schemas, events, actions, paths, and adapter states fail closed.
- Codex JSONL integration ignores unknown events without crashing or leaking payload content.
- Core, file-operation, schema, migration, and adapter tests pass in CI.
- Manual security and multi-monitor test checklists are complete.

## 12. Kill or rethink criteria

Pause the product direction if, after the seven-day study:

- users enjoy the animation but do not reuse a learned skill;
- correction requires engineers or JSON editing;
- users cannot predict what the companion is allowed to access;
- the embodied condition is not measurably easier to monitor than the control UI;
- users prefer an empty stronger model and show no desire to migrate learned state;
- safe execution cannot achieve near-deterministic success in the constrained file domain;
- the permission UI produces false confidence about operating-system isolation.

## 13. After v0.1

If both hypotheses pass, the recommended first public vertical is a bounded Blender apprentice using a Blender add-on and Python/scene APIs rather than raw pixel replay. Candidate skills are import normalization, scene organization, naming, packaging, and export. Linux support should return through such app-specific adapters before general Wayland desktop automation.
