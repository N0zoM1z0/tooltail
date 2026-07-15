# UX and Interaction Specification

## 1. Interaction model

Tooltail uses three layers of disclosure:

1. **Glance:** the body communicates ambient state.
2. **Act:** direct manipulation communicates scope, pause, revocation, and handoff.
3. **Inspect:** structured cards communicate exact plans, permissions, diffs, errors, and receipts.

The body must never be the only source of safety-critical information. Animation can signal “working” or “needs input,” but exact authority and effects live in inspectable text and controls.

## 2. Four physical verbs

### Place

Dragging and dropping Tooltail onto an eligible window proposes a WindowLease. The target window receives a visible outline before the drop is accepted.

### Give

Dropping a file, folder, sample, or supported task object onto Tooltail provides explicit input. A folder grant always requires a precise confirmation showing its canonical path and access mode.

### Grab

Beginning a drag immediately pauses Tooltail-owned execution if pausing is safe. Pulling it away from a bound window revokes the WindowLease. If an atomic file operation is already in progress, the UI shows **Pausing after current atomic step**.

### Open

Clicking Tooltail or an object it carries opens the exact inspector: status, request, plan, Skill Card, result, or failure receipt.

Keyboard and tray-menu equivalents must exist for every physical verb.

## 3. Body-state vocabulary

The placeholder character needs only a readable silhouette, eyes/visor, hands, and a satchel or tool position. It must be original and must not reuse third-party pet assets.

| State | Required visual cue | Inspector headline | Urgency |
| --- | --- | --- | --- |
| Idle | relaxed, no tool | Ready | none |
| Window bound | sits against target edge; tether visible | Bound to `<application>` | low |
| Observing | visor/eye illuminated | Observing granted context | low |
| Teaching | notebook open; persistent recording dot | Teaching session active | medium |
| Compiling | sorting notes/blueprint | Building a Skill Card | low |
| Rehearsing | translucent tool or ghost path | Rehearsing safely | low |
| Executing | carries the skill's tool; step badge | Executing step `n/m` | medium |
| Parallel work | one body with small orbiting helper markers; count badge | Parallel units active | medium |
| Needs input | freezes and raises hand | A decision is required | high |
| Blocked | lowered tool with visible barrier | Cannot continue safely | high |
| Permission revoked | broken tether and tool put away | Permission was revoked; no next step will start | high |
| Adapter disconnected | signal/tool link visibly disconnected | Agent status source disconnected | high |
| Completed | returns with parcel/receipt | Completed and verified | medium until opened |
| Failed | returns with damaged plan, not a distressed character | Failed; no silent recovery | high |
| Paused | sits with tool put down | Paused | medium |
| Quiet | reduced scale at screen edge | Quiet mode | none |

Do not represent failures as illness, pain, guilt, or emotional punishment.

## 4. Window binding

### 4.1 Preview

While dragging:

- eligible underlying windows receive a thin outline;
- Tooltail's own windows and invisible/cloaked/tool windows are ignored;
- the label shows application name and window title, truncated safely;
- no lease is created until drop;
- an ineligible target shows a neutral “Cannot bind here” cue.

### 4.2 Active tether

An active tether must communicate two independent facts:

- **Context:** which HWND/process instance Tooltail is attached to.
- **Capability:** which separately granted resources are readable or writable.

Suggested icon language:

- eye = context/read observation;
- hand = write capability;
- clock = lease expiration;
- broken tether = revoked or stale target.

The inspector states explicitly: “Window position limits Tooltail's context. It is not an operating-system sandbox. Actual actions are restricted by the grants below.”

### 4.3 Revocation

Revocation paths:

- drag Tooltail away;
- choose **Unbind**;
- close the target window;
- target PID or process start identity changes;
- lease expires;
- application restarts;
- the permission gateway detects a mismatch.

Revocation is immediate for observation. Writes already inside one non-interruptible atomic step finish or fail, then no additional step begins.

## 5. Compact action menu

Clicking Tooltail opens a small non-modal menu with no chat box:

- Inspect current activity;
- Teach a workflow;
- Give files or folder;
- Skills;
- Pause/Resume;
- Unbind;
- Quiet mode;
- Home/Settings.

The menu must not steal focus from the target application until the user intentionally selects an item that requires a full inspector.

## 6. Teaching mode

Teaching mode must always show:

- granted root;
- start time;
- the classes of events being captured;
- an explicit statement that screen and global keyboard recording are off;
- an always-available Stop button;
- a live count of relevant file effects, not raw noisy events.

If the file watcher overflows or reconciliation fails, teaching stops and the session is marked unusable. Tooltail must not silently compile from incomplete evidence.

## 7. Skill Card

The default view is task-level and editable without code.

### Required sections

- **Name:** user-editable skill name.
- **When:** trigger or explicit invocation pattern.
- **Where:** granted root and relative scope.
- **Matches:** extension, filename pattern, or other supported predicate.
- **Variables:** values that change between runs.
- **Do:** ordered, human-readable operations.
- **Always:** invariants and preconditions.
- **Never:** prohibited effects.
- **Ask me when:** ambiguity and conflict rules.
- **Success means:** postconditions.
- **Learned from:** demonstrations, corrections, and verified runs.
- **Version:** immutable identifier and semantic diff link.

### Uncertainty treatment

Inference uncertainty must be localized to a field. Do not display a single confidence percentage.

Examples:

- “Is `2026-07` a fixed prefix or the file date?”
- “Should this apply to all PDFs or only filenames containing `invoice`?”
- “Should the destination be created when absent?”

Ask at most two questions after one lesson. If material ambiguity remains, keep the skill Draft and request another example.

## 8. Dry-run and rehearsal

### Dry-run

Shows a deterministic table:

| Input | Proposed effect | Destination | Conflict state | Reversible |
| --- | --- | --- | --- | --- |

No writes occur.

### Sandbox rehearsal

Copies a bounded sample into Tooltail-managed temporary storage, executes the plan there, and verifies postconditions. The UI clearly states that rehearsal results are not the user's real files.

Approval applies to the exact plan fingerprint. If inputs, hashes, skill version, grant, or operation list change, approval is invalidated.

## 9. Execution UI

The ambient body shows only the current state and step count. The inspector provides:

- exact skill version;
- exact granted root;
- current operation;
- completed and remaining operations;
- Pause after current step;
- Cancel remaining steps;
- open journal;
- any verification failure.

Execution never fabricates progress. It is driven by committed domain events from the executor.

## 10. Completion and receipts

The returned parcel opens a receipt containing:

- execution ID and timestamp;
- skill ID/version;
- grant and plan fingerprints;
- source-to-target effects;
- per-step verification;
- skipped items and reasons;
- final status;
- Undo availability and expiry;
- link to correct the skill.

“Completed” is used only if all required postconditions passed. Otherwise use “Partially completed” or “Failed and rolled back,” with explicit residual effects.

## 11. Correction interaction

v0.1 supports correction through structured editing:

1. Open receipt or Skill Card.
2. Choose the incorrect condition, template, destination, or conflict rule.
3. Edit it using constrained controls.
4. Review the semantic diff.
5. Re-run validation and dry-run.
6. Approve SkillSpec version `n+1`.

Live “do this instead” recapture is a later experiment. It must not be simulated by hiding manual edits behind conversational language.

## 12. Skill embodiment

Each skill has a stable tool identity independent of cosmetic art.

| Skill lifecycle | Tool representation |
| --- | --- |
| Draft | paper blueprint |
| Approved | plain tool with a confirmation tag |
| Practiced | tool stored in visible satchel/rack |
| Reliable | maintained/polished tool |
| Delegated | tool receives a narrow automation badge |
| Stale | warning label; Tooltail refuses to use it |

The user can always open the tool to see its exact Skill Card.

## 13. Home/workbench

The Home view is not a game room in v0.1. It is a compact workbench with:

- active and recently completed work;
- the skill rack;
- pending drafts;
- grants and revocation controls;
- receipts and undo state;
- capsule export;
- privacy and retention settings.

No hunger, currency, store, or daily streak is present.

## 14. Quiet and proactive behavior

Tooltail has four escalation levels:

1. body-only state change;
2. quiet visual cue;
3. asks whether help is wanted;
4. performs an approved action.

v0.1 stops at level 3 unless the user explicitly opens and approves a plan. No learned skill auto-runs.

Quiet mode moves Tooltail to the screen edge, suppresses noncritical motion, and retains only needs-input, failure, and user-invoked completion cues.

## 15. Accessibility

- Every drag interaction has a keyboard and tray-menu equivalent.
- State is never conveyed by color or motion alone.
- Reduced-motion mode replaces travel and loops with crossfades/state icons.
- High-contrast assets are provided.
- Screen-reader labels expose state, target, grant, and action.
- Inspector controls follow standard keyboard navigation.
- Animation pauses when the system requests reduced motion or when battery policy requires it.
- Hit targets are at least 32 device-independent pixels in the prototype and should reach 44 where practical.

## 16. Content and tone

Tooltail is concise and literal around permissions, uncertainty, and failures.

Good:

- “I can see the window identity and changes inside the folder you granted.”
- “Two rename rules fit your example. Which one did you mean?”
- “The file changed after approval, so I stopped before moving it.”

Avoid:

- “Trust me.”
- “I secretly learned your habits.”
- “I am sad that you revoked access.”
- “Everything is done!” when verification is incomplete.
