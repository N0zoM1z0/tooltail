# Core Technical Challenges and Resolution Plans

## 1. Turning a body metaphor into real authority boundaries

### Failure mode

The user believes dragging Tooltail onto a window creates an operating-system sandbox, while the process still has ambient access to other files or windows.

### Resolution

- Separate `WindowLease` from `ResourceGrant` in domain, storage, UI, and tests.
- Treat window placement as context and presentation scope only.
- Require a deliberate folder picker/drop and a canonical resource grant before file observation or mutation.
- Route every effect through a deny-by-default Permission Gateway.
- Show lease and grant independently in the tether/inspector.
- Revoke on window/process identity change, expiry, target close, or user removal.

### Validation

- Permission tests attempt every primitive with missing, expired, mismatched, and narrower grants.
- User tests ask participants to predict accessible resources before execution.
- Security review rejects any code path that mutates through a UI view model or adapter directly.

## 2. Selecting the underlying window beneath a topmost transparent companion

### Failure mode

Hit testing returns Tooltail's own WPF surface, a child control, an invisible window, a shell surface, or a stale/reused HWND.

### Resolution

- Convert pointer coordinates through one Per-Monitor V2 coordinate service.
- Enumerate candidate windows behind the pointer and skip Tooltail's process.
- Normalize candidates to an eligible top-level root owner.
- Reject invisible, cloaked, child-only, tool, secure, and shell windows.
- Bind `{HWND, PID, processStartIdentity}` rather than HWND alone.
- Keep window title display-only.
- Provide a keyboard/window-picker fallback when spatial targeting is ambiguous.

### Validation

Manual matrix:

- File Explorer, terminal, Visual Studio, browser, Settings, WPF sample app;
- maximized, snapped, minimized/restored, virtual desktops;
- mixed 100/125/150/200 percent DPI;
- negative monitor coordinates and portrait monitor;
- target process restart and HWND reuse simulation;
- elevated target, which must be rejected in v0.1.

## 3. Following target movement without polling or focus theft

### Failure mode

The pet drifts away from its window, steals focus, blocks clicks, thrashes during resize, or leaks native callbacks.

### Resolution

- Use out-of-context `SetWinEventHook` for location, foreground, minimize, cloak, and destroy events.
- Keep delegates rooted for the hook lifetime.
- Enqueue minimal callback data and process it on a serialized application queue.
- Guard reentrancy and collapse bursts to the latest target rectangle.
- Maintain a low-frequency reconciliation timer only as a fallback.
- Handle `WM_MOUSEACTIVATE` to avoid activation during ambient interaction.
- Restrict hit-testing to the visible sprite; tether visuals are click-through.

### Validation

- stress resize/move for five minutes;
- repeatedly create/revoke leases;
- verify no retained callbacks after disposal;
- assert the previous foreground window remains foreground after ambient clicks;
- measure idle CPU and event burst behavior.

## 4. Learning from an underdetermined demonstration

### Failure mode

One example supports many incompatible rules. The system guesses, presents the guess as learning, and fails on the next input.

### Resolution

- Define “teach once” as one bounded session with 2–5 examples.
- Limit inference to a small explainable predicate/template language.
- Enumerate all candidates that explain the examples.
- Rank by minimal assumptions and deterministic coverage.
- Expose disagreement as field-localized questions.
- Ask at most two questions, then request another example.
- Keep ambiguous results Draft and non-executable.

### Validation

- property-based tests generate transformations and examples, then measure recovery;
- ambiguity fixtures must never auto-select a materially different rule;
- mutation tests ensure removing one constraint causes validation failure;
- user study checks whether the inferred When/Do/Ask fields match intent.

## 5. Recovering trustworthy effects from noisy file-system events

### Failure mode

`FileSystemWatcher` emits duplicates, decomposes moves into several events, misses events after buffer overflow, or observes antivirus/editor side effects.

### Resolution

- Use watcher events only as hints.
- Reconcile authoritative baseline and final snapshots.
- Keep callbacks short and bounded; process events asynchronously.
- Filter event types and avoid recursive watching unless explicitly needed.
- Treat `InternalBufferOverflowException` as lesson invalidation.
- Correlate likely renames/moves using identity, size, timestamp, and bounded hashes.
- Present unresolved effects and refuse compilation.

### Validation

- fixtures for duplicate/out-of-order/missing events;
- simulated watcher overflow;
- external process modifying a file during the lesson;
- large batch and antivirus-like duplicate events;
- snapshot reconciliation determinism test.

## 6. Preventing path escape and link attacks

### Failure mode

A crafted relative path, junction, symlink, UNC path, device path, alternate data stream, case trick, or race redirects an approved operation outside the granted root.

### Resolution

- Accept only local, fully qualified grant roots selected by the user.
- Store plan paths relative to the immutable root.
- resolve with deterministic `Path.GetFullPath(relative, root)` semantics;
- compare containment with Windows-appropriate normalized path rules and separator boundaries;
- reject UNC, device, and alternate-stream forms;
- reject any entry or ancestor with `ReparsePoint` in v0.1;
- revalidate paths and file fingerprints immediately before each effect;
- do not follow links during enumeration;
- never use a shell or string-concatenated command.

### Validation

- traversal corpus including `..`, mixed separators, trailing dots/spaces, reserved names, case variants, long paths, ADS syntax, UNC, device paths, junctions, symlinks, mount points, and links introduced after approval;
- race test swaps an ancestor for a junction between planning and execution;
- every attack must stop before mutation and create a security event.

## 7. Making file effects reversible

### Failure mode

The app crashes mid-workflow, a copy is incomplete, an undo overwrites later user work, or a partially rolled-back run is reported as clean.

### Resolution

- Journal intent before each mutation and flush durable state.
- Revalidate before each step.
- Allow no overwrite by default.
- Verify after every step, not only at the end.
- Keep inverse metadata and exact fingerprints.
- Undo only if the current destination still matches what Tooltail created.
- Keep learned SkillSpec deletion-free. For an unchanged copy or empty directory proven to have been created by the exact run, use a separate internal `remove_created_entry` inverse that requires fresh approval and full revalidation.
- Move other recovery material to Tooltail-managed local application data only when doing so does not imply an unsafe cross-volume conversion.
- Report residual effects explicitly when rollback is incomplete.
- Exclude learned/general delete and cross-volume move in v0.1.

### Validation

- injected crash/failure after each journal boundary;
- power-loss simulation through process termination and recovery scan;
- user modifies destination before Undo;
- disk-full and access-denied cases;
- idempotent recovery test for replayed startup reconciliation.

## 8. Avoiding time-of-check/time-of-use drift

### Failure mode

The user approves one plan, but inputs change before execution and the same approval is applied to different effects.

### Resolution

- Fingerprint skill version, grant, root, input identity, metadata/hash, operation list, and postconditions.
- Bind Approval to the complete plan fingerprint.
- Recompute/revalidate immediately before execution and each mutable step.
- Invalidate approval on any difference.
- Show a concise “What changed since approval” diff.

### Validation

- rename, modify, replace, and link-swap inputs after approval;
- modify the skill or grant after approval;
- introduce destination collision after approval;
- all cases must require a new plan and approval.

## 9. Mapping agent runtime events without coupling to private internals

### Failure mode

Tooltail depends on undocumented Codex session storage or a brittle raw event schema, exposes code/prompt content, or breaks whenever Codex changes.

### Resolution

- Make Tooltail's normalized `AgentEvent` the stable boundary.
- Build the body against a deterministic simulator first.
- Use documented `codex exec --json` stdout JSONL only for processes Tooltail launches.
- Never read private session files.
- Map a conservative subset of known events.
- Ignore and count unknown types; fail the adapter, not the whole application, on malformed input.
- Discard raw payload content and retain only normalized status metadata by default.
- Store redacted fixtures for compatibility tests.
- Do not make the changing Codex app-server interface a v0.1 dependency.

### Validation

- unknown fields and event types;
- malformed, truncated, oversized, and slow JSONL lines;
- stdout/stderr interleaving;
- process cancellation and abnormal exit;
- payloads containing secrets, source, ANSI escapes, and prompt-injection text;
- assert no raw payload reaches UI, logs, or database.

## 10. Keeping body state truthful under concurrency

### Failure mode

Animations claim completion before verification, two runs fight over the body, or UI state reflects transient callbacks rather than committed state.

### Resolution

- Project body state from committed domain events only.
- Give each run a correlation ID and explicit priority.
- v0.1 permits one foreground embodied run; additional runs appear in the inspector queue rather than cloned pets.
- Define one deterministic precedence table: security/verification failure > permission revoked/disconnected > needs input > blocked > rollback > paused > executing > observing > completed-unopened > idle.
- Completion is emitted only after verification.
- UI animation is a pure projection; it cannot mutate runtime state.

### Validation

- randomized event-order tests;
- duplicate events and late completion;
- simultaneous agent and file run;
- pause during verification;
- state-machine transition coverage.

## 11. Preserving identity through schema evolution

### Failure mode

A database migration or capsule import loses skills, silently resets the companion, imports authority, or executes stale semantics.

### Resolution

- Explicit database and contract schema versions.
- Forward-only migrations with backup and recovery mode.
- Immutable skill versions and provenance.
- Capsule export excludes active authority and secrets.
- Imported skills are disabled, rebound, validated, and rehearsed.
- Unknown major schemas fail closed; supported minor additions are preserved when safe.
- Never create a fresh empty identity after a migration failure without user confirmation.

### Validation

- migration from every released schema fixture;
- interrupted migration recovery;
- round-trip capsule tests;
- oversized JSON, duplicate/conflicting ID, incompatible schema, malicious SkillSpec, and imported-authority tests when import is enabled;
- permission/grant absence after import.

## 12. Testing a desktop UI in noninteractive CI

### Failure mode

CI gives false confidence because GitHub-hosted runners do not provide reliable interactive desktop automation, while all logic lives in WPF code-behind.

### Resolution

- Keep domain, inference, permission, planning, execution, and state projection independent of WPF.
- Put Win32 behind `IWindowSystem` and `ICoordinateSpace`.
- Use fake platform adapters for most tests.
- Run compile/unit/integration/contract tests in Windows CI.
- Maintain a versioned manual real-desktop matrix for focus, DPI, monitors, and window tracking.
- Build a diagnostic window-target harness for repeatable local testing.
- Do not claim UI automation coverage from headless CI.

### Validation

- architecture tests prohibit domain logic in WPF assemblies;
- view models run without a dispatcher where practical;
- real-Windows smoke report is required for release candidates.

## 13. Containing scope creep

### Failure mode

Chat, voice, skins, general computer use, plugins, and proactive automation arrive before causal learning is proven.

### Resolution

- Treat v0.1 non-goals as repository invariants.
- Require an ADR for new observation channels, action primitives, external side effects, runtimes, or platforms.
- Measure the two hypotheses independently.
- Keep the public vertical decision behind the MVP exit gate.

### Validation

Review every PR against the Product Spec and non-goals. A feature that does not improve body comprehension, skill learning, safe execution, correction, or research validity is deferred.
