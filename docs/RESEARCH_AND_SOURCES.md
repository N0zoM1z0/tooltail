# Research and Sources

## 1. Scope and method

This research pass was completed on 2026-07-15. It combined:

- review of existing virtual-pet and desktop-agent projects;
- primary Windows and .NET platform documentation;
- primary Codex documentation for repository instructions and process integration;
- XDG portal documentation to assess Linux/Wayland tradeoffs;
- recent research on learning executable workflows from demonstrations;
- threat-model literature for tool-using agents.

Sources informed design decisions but are not dependencies unless an implementation ADR later says otherwise. Repository and name searches are point-in-time observations, not legal or security guarantees.

## 2. Product and interaction precedents

### VPet

Source: [VPet English README](https://github.com/LorisYounger/VPet/blob/main/README_en.md)

Useful evidence:

- a transparent WPF desktop-pet surface is technically and socially legible on Windows;
- a character can expose extensibility and many animation states without a conventional application frame;
- code and art may carry different reuse conditions.

Decision:

- use WPF for the Windows hypothesis build;
- do not fork VPet or copy its artwork/animation system;
- create a minimal original eight-state visual vocabulary first;
- review every third-party asset independently of the source-code license.

### OpenPets

Sources: [OpenPets repository](https://github.com/alvinunreal/openpets), [OpenPets code map](https://github.com/alvinunreal/openpets/blob/main/CODEMAP.md)

Useful evidence:

- desktop-pet agent integrations benefit from a process boundary, line-delimited event messages, leases, and replaceable adapters;
- Windows named pipes are a practical local IPC option.

Decision:

- adopt versioned event envelopes and lease semantics;
- keep v0.1 a modular monolith unless a real isolation/lifecycle need justifies IPC;
- if IPC is introduced, use current-user named pipes with a launch capability and strict limits;
- do not make OpenPets a runtime dependency.

### Clicky

Source: [Clicky repository instructions](https://github.com/farzaa/clicky/blob/main/AGENTS.md)

Useful evidence:

- a full-desktop transparent overlay must explicitly handle multi-monitor coordinates, click-through behavior, and app-window state;
- visual embodiment and desktop observation rapidly create platform-specific complexity.

Decision:

- use small separate WPF windows instead of one giant overlay;
- centralize coordinate conversion;
- test negative origins and mixed DPI from the first native milestone.

## 3. Windows and .NET platform research

### UI Automation

Source: [Microsoft UI Automation overview](https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32)

Finding and decision:

- UI Automation is the supported accessibility automation framework for programmatic access to many desktop UI elements. Tooltail v0.1 does not need arbitrary UI control; keep any future use behind `Tooltail.Platform.Windows` and an app-specific, user-approved capability.

### Window events

Sources: [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook), [event constants](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants)

Findings:

- out-of-context hooks deliver events asynchronously and require a message loop;
- event ordering is not guaranteed;
- callbacks can create reentrancy risks;
- callback delegates must remain alive for native use.

Decision:

- enqueue minimal callback data, project state off the callback, root native delegates, and reconcile periodically;
- validate HWND and process identity on every authority-relevant use;
- subscribe only to the event set needed for a current lease.

### Screen capture

Source: [Windows.Graphics.Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)

Finding and decision:

- Windows exposes user-mediated capture APIs, but screen capture adds sensitive data and prompt-injection surface. It is excluded from v0.1. A future capture adapter requires a separate grant, visible observation state, redaction/retention design, and threat-model update.

### Synthetic input

Source: [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

Finding and decision:

- synthetic input is constrained by Windows integrity boundaries and can fail without identifying UIPI as the cause. Tooltail does not use `SendInput` in v0.1. App-specific APIs are preferred for later verticals.

### File observation

Source: [.NET FileSystemWatcher documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0)

Findings:

- one file-system operation can produce multiple events;
- buffer overflow can lose changes;
- ordering and event counts are unsuitable as a transaction record.

Decision:

- use baseline/final snapshots as truth and watcher events only as hints;
- invalidate a lesson on overflow or irreconcilable evidence;
- keep callback work minimal and bound buffers/filters.

### Canonical paths and links

Sources: [Path.GetFullPath](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.getfullpath?view=net-10.0), [FileAttributes.ReparsePoint](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileattributes?view=net-10.0), [FileSystemInfo.ResolveLinkTarget](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.resolvelinktarget?view=net-10.0), [Windows reparse points](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points)

Decision:

- canonicalize against an immutable local root, compare with Windows-aware semantics, reject links/reparse points throughout the path, and revalidate immediately before mutation;
- v0.1 rejects network, device, alternate-stream, and cross-volume behavior.

### Runtime choice

Source: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

Decision:

- target the current even-numbered LTS line, .NET 10, and pin a feature band in `global.json` when implementation begins;
- keep contracts independent of WPF so portable tests can run outside Windows.

## 4. Codex integration and repository guidance

### Repository instructions

Source: [Codex AGENTS.md guide](https://developers.openai.com/codex/guides/agents-md)

Findings and decision:

- Codex discovers `AGENTS.md` instructions from repository root toward the working directory, with nearer files able to refine guidance;
- instruction discovery has a configured aggregate byte limit.

This seed includes one concise root `AGENTS.md`. Add nested files only when a subtree genuinely requires different commands or constraints. Verify discovery before relying on it.

### Non-interactive runs

Source: [Codex CLI reference](https://developers.openai.com/codex/cli/reference)

Finding and decision:

- `codex exec --json` exposes a documented JSONL event stream suitable for automation.

Tooltail may optionally launch an explicitly configured Codex CLI process and parse its stdout through a versioned adapter. The deterministic simulator remains the CI oracle. Tooltail must not inspect private session storage, infer permissions from text, or make undocumented app-server protocols a core dependency.

## 5. Linux/Wayland assessment

Sources: [XDG ScreenCast portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html), [XDG RemoteDesktop portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html)

Findings:

- Wayland screen observation and remote input are mediated through portal sessions and compositor-specific implementations;
- screen-cast streams and input capabilities have explicit user-consent and session semantics;
- a Windows HWND-style global window-control model does not transfer directly.

Decision:

- prove the product loop on Windows 11 first;
- later support Linux through app-specific adapters on an explicitly tested desktop environment;
- treat general Wayland scope, positioning, observation, and control as a new architecture/security project, not feature parity work.

## 6. Learning from demonstrations research

### ShowUI-Aloha

Source: [ShowUI-Aloha paper](https://arxiv.org/html/2601.07181v1)

Relevant idea:

- recording a demonstration, structuring it into a trace, learning a reusable representation, and executing through a bounded layer are separable concerns.

Tooltail adaptation:

- retain the recorder/learner/actor/executor separation, but begin with file snapshots and a closed deterministic DSL rather than visual desktop trajectories.

### Alloy

Source: [Alloy paper](https://arxiv.org/html/2510.10049v1)

Relevant idea:

- demonstrations can communicate procedural preferences, and generated workflows become more usable when users can inspect and directly edit them.

Tooltail adaptation:

- the Skill Card is a first-class editable/inspectable artifact;
- correction creates an explicit semantic version diff rather than hidden prompt memory.

### Agent Skill Induction

Source: [Agent Skill Induction paper](https://arxiv.org/html/2504.06821v2)

Relevant idea:

- executable procedural skills with tests can provide more reliable reuse than prose-only memory.

Tooltail adaptation:

- define a schema, closed primitive vocabulary, test fixtures, rehearsal, postconditions, and versioned provenance;
- do not call conversation summaries “skills.”

### GPA

Source: [GPA paper](https://arxiv.org/abs/2604.01676)

Relevant idea:

- deterministic local replay can be learned from a small number of demonstrations in constrained settings.

Tooltail adaptation:

- seek a small-domain proof with 2–5 representative examples;
- prefer deterministic matching and transformations before adding a general model compiler.

These papers motivate architectural separation; they do not establish that Tooltail's user-value hypotheses are true. Those require the studies in `TEST_AND_RESEARCH_PLAN.md`.

## 7. Agent security research

Sources: [AgentDojo](https://arxiv.org/abs/2406.13352), [InjecAgent](https://arxiv.org/abs/2403.02691)

Relevant evidence:

- tool-using agents can be manipulated by untrusted content and indirect prompt injection;
- broad tool access and implicit authorization make failures materially worse.

Decision:

- filenames, window titles, file content, model output, and external event text are untrusted data;
- a model may propose a SkillSpec only through the same validator;
- authorization comes only from typed grants and exact-plan approvals;
- the v0.1 SkillSpec accepts no shell, script, network, delete, arbitrary UI, or content-derived instruction; Undo's exact-created-artifact removal is a separate non-compiler recovery contract.

## 8. Name search

A general web and GitHub search on 2026-07-15 did not reveal an obvious exact-name software repository that should disqualify `Tooltail` for this exploration. This result can change and is not a trademark, domain, package-registry, or corporate-name clearance.

Before publication:

- search GitHub, package registries, major app stores, domains, and relevant trademark databases;
- verify the desired GitHub owner/name pair is available;
- select a different name if counsel or distribution partners identify confusion risk.

## 9. Resulting design synthesis

The research supports five decisions:

1. Build the companion as a truthful projection of authority and runtime state, not as a decorative chat overlay.
2. Treat demonstrations as evidence compiled into explicit executable skills, not as raw recordings to replay blindly.
3. Start with a closed file-action domain where every plan can be previewed, verified, journaled, and undone.
4. Separate context leases, resource grants, model proposals, user approval, and actual effects.
5. Use Windows to test embodiment now; preserve contracts so future app-specific and Linux adapters can be added without weakening the safety kernel.
