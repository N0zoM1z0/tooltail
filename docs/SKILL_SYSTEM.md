# Skill Learning and Procedural Memory

## 1. Definition

In Tooltail, a skill is:

> a versioned, executable, testable, scope-bound procedural artifact derived from user-approved evidence

A summary, embedding, prompt fragment, or remembered fact is not a skill.

## 2. Memory layers

Tooltail keeps memory types separate because they have different truth and governance requirements.

| Layer | Contents | May change execution? | User controls |
| --- | --- | --- | --- |
| Episodic | bounded lessons, results, corrections | only through an approved skill version | inspect, retain, delete |
| Semantic | explicit names and preferences | only as typed parameters | edit, delete |
| Procedural | SkillSpec and tests | yes, through planner/executor | inspect, version, rehearse, disable, export |
| Trust | verified execution evidence per skill | changes prompting/presentation, not authority | inspect; delegation remains explicit |
| Presentation | tools, body state, workbench projection | no direct authority | accessibility and display settings |

Raw screenshots and global input streams are not memory layers in v0.1.

## 3. Lesson lifecycle

```text
Started
  -> BaselineCaptured
  -> ObservingEffects
  -> Stopped
  -> Reconciled | Invalid
  -> CandidateCompiled
  -> Clarified | NeedsMoreExamples
  -> Validated
  -> Rehearsed
  -> UserApproved
  -> SkillVersionCreated
```

Every transition is explicit and persisted. An interrupted lesson never silently becomes a skill.

## 4. Evidence capture

### Baseline snapshot

For every entry inside the granted local root, capture a bounded representation:

- normalized relative path;
- file/directory kind;
- length;
- creation and last-write times;
- selected attributes;
- reparse/link status;
- content hash when within configured limits;
- a stable file identity when the platform adapter can provide it safely.

### Watcher hints

Capture only file-system events relevant to the granted root. Do not treat watcher order or multiplicity as authoritative.

### Final snapshot

Capture the same representation and reconcile it with the baseline and hints.

### Reconciliation output

Normalized effects:

- `Created(relativePath, fingerprint)`;
- `Renamed(oldRelativePath, newRelativePath, fingerprint)`;
- `Moved(oldRelativePath, newRelativePath, fingerprint)`;
- `Copied(sourceRelativePath, newRelativePath, fingerprint)` when identity evidence is sufficient;
- `Modified(relativePath, before, after)`—recorded for rejection because content modification is unsupported;
- `Deleted(relativePath, fingerprint)`—recorded for rejection because deletion is unsupported.

If a lesson includes unsupported effects, the user sees them and the compiler refuses to produce an executable v0.1 skill.

## 5. Demonstration requirements

- One lesson contains 2–5 representative examples for rules with variable filename behavior.
- A one-example lesson may create only a fully explicit fixed rule after user confirmation.
- Examples must remain inside one local root and one volume.
- Inputs changed by other processes during the lesson are flagged.
- The user may label an example as an exception or exclude it before compilation.
- The compiler cannot infer file-content meaning in v0.1.

## 6. SkillSpec model

The canonical machine-readable contract is `docs/schemas/skill-spec.schema.json`.

Conceptual structure:

```text
metadata
  skillId, version, name, createdAt, compiler
applicability
  rootBinding, invocation, match predicates
variables
  typed values and extraction sources
steps
  closed primitive actions
policy
  collision behavior, ask conditions, prohibited behavior
verification
  step and final postconditions
provenance
  lesson IDs, example IDs, user answers, prior version
compatibility
  schema, compiler, executor versions
```

### Closed primitive set for v0.1

- `ensure_directory`
- `rename_file`
- `move_file`
- `copy_file`

The runtime rejects all other discriminators. A primitive cannot embed a command, script, executable path, URL, plugin call, or arbitrary expression.

### Supported predicates

- relative origin directory equals a constant;
- extension is in an allowlisted set;
- filename has an application-generated prefix, suffix, contains token, or anchored safe regex;
- entry is a regular local file;
- optional file-size bound.

### Supported template variables

- original stem;
- original extension;
- selected safe regex capture groups;
- file creation date components;
- file last-write date components;
- user-provided bounded string parameters.

No environment variables, current working directory, shell interpolation, device path, UNC path, alternate data stream, or absolute target path is accepted.

At the v0.1 application boundary, relative paths must already be Unicode NFC, use only the Windows backslash separator, contain no empty or dot segments, and avoid trailing-dot/space and reserved-device-name aliases. The kernel compares containment with ordinal case-insensitive Windows semantics. A case-only rename is rejected explicitly in v0.1 rather than emulated with an unapproved temporary path.

## 7. Deterministic inference

### 7.1 Mapping examples

For each demonstrated effect, create a pair:

```text
(source relative path, source metadata) -> (destination relative path)
```

### 7.2 Candidate generation

Generate only candidates from the v0.1 predicate and template language. Examples:

- constant destination directory;
- preserve filename;
- prefix/suffix insertion;
- case normalization;
- separator normalization;
- metadata date + original stem;
- regex group substitution from an application-generated pattern.

### 7.3 Candidate elimination

Reject candidates that:

- fail any positive example;
- include a demonstrated exclusion;
- escape the root;
- require unsupported content interpretation;
- collide under current examples;
- use a target that resolves through a link/reparse point;
- rely on locale-ambiguous dates without an explicit format;
- cannot produce deterministic output.

### 7.4 Ranking

Rank remaining candidates by:

1. exact coverage;
2. fewest assumptions;
3. shortest explainable template;
4. greatest use of stable semantics instead of observed constants;
5. lowest collision risk.

The rank is not shown as a single confidence score. Differences between top candidates become field-specific questions.

## 8. Clarification

Examples of useful questions:

- “Should this match every PDF, or only PDFs whose names contain `invoice`?”
- “Is `2026-07` the current month, the file's modified month, or fixed text?”
- “Should files already in the destination be ignored?”

The answer becomes explicit provenance and a typed SkillSpec field. It is not appended to an opaque conversational memory.

After two questions, unresolved material ambiguity moves the lesson to `NeedsMoreExamples`.

## 9. Validation pipeline

Validation is layered:

1. JSON/schema validation;
2. discriminator/action allowlist;
3. template parser validation;
4. canonical root containment;
5. link/reparse rejection;
6. collision-policy validation;
7. variable binding completeness;
8. postcondition coverage;
9. provenance presence;
10. compatibility version check.

The validator returns structured field errors. It never silently repairs an unsafe SkillSpec.

## 10. Planning

Planning binds a skill version to a concrete input snapshot.

The planner:

- selects matching regular files;
- extracts variables;
- renders relative destinations;
- canonicalizes source and destination against the immutable grant root;
- detects duplicates and collisions;
- orders `ensure_directory` before dependent operations;
- computes expected postconditions;
- creates an exact plan fingerprint.

Planning is pure and repeatable for identical inputs.

## 11. Rehearsal

### Dry-run

Required for every new or corrected version. Produces exact operations and conflicts without writes.

### Sandbox rehearsal

Required before a Draft becomes Approved when the skill contains more than one mutable step. Copies bounded fixtures to a Tooltail temp root, executes with the same executor and verifier, and removes the temp root after retention policy allows.

Production execution cannot use a different code path from rehearsal.

## 12. Execution and verification

Every effect has:

- preconditions;
- a permission check;
- a journal entry before mutation;
- a mutation implementation without shell execution;
- immediate postconditions;
- inverse metadata for undo;
- a user-readable projection.

Examples:

### Rename

Preconditions: exact source fingerprint exists; destination absent; both paths inside root; neither is a link; same volume.

Postconditions: destination exists with expected identity/content; source path absent.

### Copy

Preconditions: source fingerprint exists; destination absent; bounded size; enough storage.

Postconditions: destination hash equals source; source remains unchanged.

### Move

v0.1 allows same-volume moves inside the root only. Cross-volume behavior is rejected rather than converted silently into copy/delete.

## 13. Correction and versioning

Skill versions are immutable.

A correction creates:

- a new version number;
- parent version reference;
- semantic field diff;
- correction reason;
- new or negative example reference;
- new validation and rehearsal evidence;
- lifecycle reset to Draft or Approved according to policy.

Prior receipts retain the exact version they used.

### Conflict and branching

If a correction applies only to a project or subset, create a narrower applicability branch rather than modifying the general rule. Overlapping skills must produce an explicit choice until precedence is user-defined.

## 14. Trust and mastery

Trust is evidence, not permission.

Evidence dimensions:

- verified success count;
- distinct input signatures;
- recent unresolved failures;
- user corrections since last rehearsal;
- compiler/executor compatibility;
- age since last successful use.

Lifecycle transitions are rule-based and inspectable. Delegation is always a separate explicit user action and is deferred beyond v0.1.

## 15. Staleness

A skill becomes Stale when:

- its schema is unsupported;
- the compiler or executor declares a breaking semantic version change;
- the bound resource grant no longer exists;
- relevant path assumptions no longer validate;
- repeated inputs produce no matches unexpectedly;
- verification fails;
- import loses required provenance or test fixtures.

Stale skills cannot run until reviewed and rehearsed.

## 16. Companion Capsule

The capsule preserves identity without preserving authority.

The v1 capsule is one bounded UTF-8 JSON document conforming to `docs/schemas/companion-capsule.schema.json`. It contains companion identity/minimal presentation, immutable SkillSpecs, source grant IDs only for provenance, mandatory `require_user_rebind` behavior, and bounded verification/correction summaries.

Export excludes:

- active WindowLeases;
- ResourceGrants and approval tokens;
- raw/absolute paths and filenames;
- file contents and model transcripts;
- secrets and credentials;
- raw Codex events;
- raw demonstration events after retention expiry;
- execution journals, receipts containing paths, and undo backups.

Import creates unbound/Stale skills that require schema validation, compatibility checks, a new ResourceGrant, explicit path rebinding, and rehearsal. Import creates no lease, grant, approval, or execution. A future multi-file archive requires a new schema/ADR and archive-specific threat controls.

## 17. Later model-assisted compiler

An LLM compiler can broaden inference only after the deterministic MVP passes.

Required controls:

- observed file names/content are marked untrusted data;
- the system prompt and schema are fixed and versioned;
- structured output only;
- no tools available to the compiler;
- no direct file access from the compiler;
- output constrained to the same closed SkillSpec;
- deterministic validator and planner remain authoritative;
- model/provider metadata recorded in provenance;
- a non-model fallback remains available.

The model may suggest a skill. It may never approve or execute one.
