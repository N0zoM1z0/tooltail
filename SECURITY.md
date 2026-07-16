# Security Policy

## Project stage

Tooltail begins as a private hypothesis build. No version is security-supported until the repository owner publishes a support table here. The current design deliberately limits effects, but it must not be represented as a security sandbox.

## Reporting a vulnerability

When private vulnerability reporting is enabled for the GitHub repository, use GitHub's **Report a vulnerability** flow.

Until then, contact the repository owner through an established private channel and include “Tooltail security” in the subject. Do not open a public issue containing exploit details, private filenames, credentials, personal data, or a working path-escape/crash-replay proof.

Include when possible:

- affected commit/version and Windows/.NET version;
- the violated invariant or expected behavior;
- minimal synthetic reproduction steps;
- whether user interaction or an existing grant/approval is required;
- observable impact;
- logs with sensitive data removed;
- a suggested mitigation, if known.

The project should acknowledge a report after an owner and private channel are established. No response-time promise is made in this seed.

## Highest-priority issues

Treat these as security-sensitive:

- any accepted path or operation outside the exact granted root;
- traversal through a symbolic link, junction, mount point, or other reparse point;
- learned/general delete, overwrite, shell, script, network, cross-volume, global-input, or arbitrary-UI behavior reachable through a SkillSpec/file execution, or any removal outside the exact Undo exception below;
- executing a plan after its input, destination, grant, skill version, root identity, or ordered effects changed;
- repeating an ambiguous mutation after crash/restart;
- UI/body indicating revoked, stopped, or waiting while effects continue;
- a WindowLease creating or retaining mutation authority;
- model/external text granting permission or selecting an unvalidated effect;
- imported capsule creating a live grant or approval;
- private Codex/session data access;
- credentials, raw paths/content, screenshots, transcripts, or identifiers leaking through logs, exports, telemetry, or crash reports;
- local IPC accepting another user's/client's commands;
- elevation or insecure installer behavior.

## Security boundaries

### WindowLease

A temporary verified HWND/process association for context and presentation only. It is not a file or application capability.

### ResourceGrant

A user-issued, revocable capability for a closed action set over one immutable canonical local root.

### Plan approval

Consent for one exact canonical plan fingerprint. It is invalid after any executable material changes.

### PermissionGateway and executor

The only route to a side effect. They revalidate path, identity, grant, plan, approval, and preconditions immediately before each direct primitive call.

### Model and event adapters

Untrusted proposers/status sources. They have no permission authority and cannot bypass schemas, validation, planning, approval, execution, or verification.

### Desktop process

Runs as the current standard user. The modular monolith and any future subprocess boundary reduce coupling; neither is a sandbox against the same user account.

See `docs/SECURITY_THREAT_MODEL.md` for the full assets, trust boundaries, threats, controls, and test mapping.

## v0.1 effect boundary

Only these learned SkillSpec primitives are valid:

- create an absent directory under the grant;
- rename a regular local file under the grant;
- move a regular local file within the same root and volume;
- copy a bounded regular local file within the root.

All collisions reject. Learned/general deletion, overwrite, content editing, shell/script execution, network paths, reparse points, cross-volume moves, global input, and arbitrary desktop automation are unsupported by design.

Undo has one separate internal effect: remove an unchanged file or empty directory proven by the journal to have been absent before and created by the exact execution being undone. This effect is not representable in SkillSpec, accepts no pattern or model/compiler path, requires a newly approved inverse plan, and revalidates root, grant, canonical path, identity/hash, and emptiness immediately before removal. Any mismatch refuses removal and reports residual state.

Explicit whole-product-memory deletion is another separate application-maintenance boundary, not learned/general file deletion. It accepts no caller path or pattern, uses only the fixed local SQLite/WAL/SHM/intent slots after two-step confirmation, and recovers a valid partial request before opening SQLite. An invalid marker or layout stops without replacing state. Safe labs, user files, rehearsal residuals, Capsule exports, and external research copies are outside this deletion boundary; see `docs/DATA_LIFECYCLE.md`.

Companion Capsule import is authority-free and pristine-only. The Desktop previews one bounded local non-reparse file and its exact SHA-256 before a separate commit; SQLite atomically replaces only the unique empty first-run identity and forces every imported version Stale. It never imports a grant, approval, plan, receipt, or trusted evidence. A later explicit rebind creates a new parent-linked Draft changing only the current exact grant binding, then requires the normal rehearsal and approval path. Existing product state is never merged or overwritten.

## Safe testing

- Reproduce only in an isolated temp/fixture root with synthetic files.
- Do not test path escapes against other users or machines.
- Do not attach secrets, real companion databases, recordings, or private model/session data.
- Do not publish a working exploit before a fix and disclosure plan exist.
- Crash and concurrency testing must make recovery state explicit and must not target real user resources.

## Dependency and supply-chain policy

- Prefer the .NET BCL and a minimal reviewed package set.
- Pin the SDK family and commit package lock files.
- Review license, maintenance, transitive/native code, install scripts, network behavior, and telemetry before adding a package.
- CI and release workflows should use least-privilege permissions and pinned action revisions before public alpha.
- Public-alpha evidence must include dependency/secret scanning, an SBOM, and appropriate release provenance.

The local/CI `Tooltail.ReleaseAudit` gate cross-checks lock files with reviewed `.nuspec` license metadata, freezes v1 contract hashes, requires commit-pinned workflow actions, scans tracked files for bounded secret patterns, and emits an SPDX 2.3 SBOM. Current NuGet vulnerability and deprecation queries must be clean. Test-only JsonSchema.Net-family binaries retain an explicit `LicenseRef-OSMFEULA` owner-review blocker; they are not product runtime dependencies or silently concluded as MIT.

The user-previewed diagnostic export is minimized by type rather than best-effort string redaction. Its internal DTO has only UTC/product version, closed state/tool enums, stable reason codes, and bounded counts; it has no path/name/title/content/model/user/machine input field. Strict readback and exact SHA-256 preview precede a Tooltail-owned `CreateNew` write. There is no uploader or automatic diagnostic collection. Ordinary SQLite/database copies are not safe diagnostic exports.

An existing-folder picker supplies read intent, not authority. Tooltail previews the exact local fixed-drive/non-reparse root and closed action set, then re-captures stable root identity on a separate confirmation before persisting one grant. The canonical path is stored only as bounded Windows current-user DPAPI ciphertext. Failed decryption or identity restoration disables every file workflow but leaves the exact grant revocable; DPAPI is privacy minimization, not a same-user sandbox.

The portable package is unsigned and is not a public release. Its verifier binds every file hash, rejects debug/state/link/traversal contamination, confirms the standard-user apphost, and removes only a new marker-bound fixture program directory while preserving sibling data. It is not an installer and adds no updater, service, startup task, registry mutation, or production uninstall code. A signed installer and its uninstall behavior require separate owner authority and review.

## Secrets

The MVP requires no product API key or hosted service. Never store secrets in source, settings JSON, SQLite, logs, environment dumps, capsule files, or issue attachments. A future secret-bearing adapter requires an ADR, OS credential storage, least privilege, revocation, redaction, and threat-model tests.

## Disclosure and fixes

Security fixes should include:

- a regression test that fails before the fix;
- affected versions and impact assessment;
- threat-model and known-limitations updates;
- contract/migration implications;
- recovery guidance if user state may be affected;
- a coordinated disclosure decision by the repository owner.

Never hide a security behavior change behind a visual or wording-only fix.
