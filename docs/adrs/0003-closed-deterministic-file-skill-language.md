# ADR 0003: Start with a Closed Deterministic File-Skill Language

- Status: Accepted
- Date: 2026-07-15

## Context

The product must prove that a user can teach a companion something that later executes correctly. A general desktop agent, arbitrary script generator, or unconstrained model plan would make it difficult to distinguish genuine procedural learning from one-off reasoning and would create unacceptable authority and verification problems.

File organization is narrow enough to model exactly and common enough to test repeated value. Even within this domain, deletion, overwriting, links, network roots, cross-volume moves, and arbitrary content interpretation complicate recovery.

## Decision

The v0.1 skill compiler is deterministic and can emit only a versioned `SkillSpec` composed from:

- allowlisted path/name/metadata predicates;
- typed variables;
- application-generated safe templates;
- `ensure_directory`;
- `rename_file`;
- `move_file`;
- `copy_file`;
- explicit postconditions and reject-on-collision policy.

No SkillSpec may contain shell text, a script, executable path, URL, arbitrary expression, plugin call, model prompt, delete, overwrite, content edit, cross-volume move, network path, or reparse-point traversal.

The compiler enumerates candidates that explain the evidence, eliminates unsafe or inconsistent candidates, ranks remaining candidates by exact coverage and simplicity, and surfaces field-specific ambiguity. It asks at most two clarification questions before requiring another example.

An LLM compiler is a future optional proposer behind the same schema and validator. It cannot approve or execute a skill.

## Consequences

Positive:

- every learned skill is inspectable, testable, and reproducible;
- examples can produce causal semantic diffs;
- planning and fingerprinting are pure;
- adversarial behavior has a closed test surface;
- rehearsal and undo share the production executor.

Costs:

- many natural demonstrations are explicitly unsupported;
- users may need representative examples or a targeted clarification;
- file-content semantics and fuzzy classification are deferred;
- a deterministic compiler requires careful candidate-language design.

## Revisit when

- the deterministic teach/correct/reuse loop passes M5 and research gates;
- unsupported but valuable workflows form a clear repeated cluster;
- a new primitive has defined preconditions, postconditions, journal semantics, inverse/recovery behavior, and adversarial tests;
- model-based proposals can be evaluated without weakening the safety kernel.

## Rejected alternatives

### Generate Python or PowerShell from demonstrations

Rejected because arbitrary code cannot be meaningfully confined or semantically previewed by the v0.1 permission model.

### Record and replay mouse/keyboard input

Rejected because replay is brittle, sensitive, difficult to verify, and unsafe across changed UI state.

### Natural-language memory only

Rejected because prose lacks executable semantics, compatibility, tests, exact effects, and reliable correction behavior.
