# Public-Alpha Known Limitations

## Readiness statement

Tooltail is a verified engineering hypothesis build, not a public-alpha-ready release. M0–M6 and the automatable M7 gates have substantial committed evidence, but required human, independent-review, repository-owner, and distribution gates remain open. An automated smoke, simulator trace, synthetic HWND, or isolated file fixture is never counted as usability, assistive-technology, mixed-monitor, independent-security, or participant evidence.

The current distribution candidate is an unsigned self-contained Windows 11 x64 portable ZIP. It must not be publicly distributed, described as signed or reputation-tested, or represented as a conventional installer. Exact package evidence and removal boundaries are in [`PORTABLE_PACKAGE.md`](PORTABLE_PACKAGE.md).

## Final acceptance delta

| Area | Current status | Evidence or missing gate |
| --- | --- | --- |
| clean restore, format, Release build, and automated suites | PASS locally | Exact Linux/Windows results are in [`BUILD_STATUS.md`](BUILD_STATUS.md); the GitHub workflow itself is NOT RUN. |
| headless core, fixture CLI, deterministic body simulator | PASS | No live model or interactive desktop is required. |
| Windows companion, Inspector, and standard-user apphost | automated PASS; attended PARTIAL | Synthetic/native integration and apphost smokes pass on one Windows 11 engineering host. Real-application, focus, mixed-monitor, remote-session, keyboard, screen-reader, high-contrast, reduced-motion, and text-scaling rows remain NOT RUN in [`WINDOW_SHELL_TEST_MATRIX.md`](WINDOW_SHELL_TEST_MATRIX.md). |
| WindowLease and ResourceGrant separation | automated PASS; human comprehension NOT RUN | Domain, UI, revocation, and restart evidence pass. Independent first-launch evaluation and Study B remain NOT RUN. |
| deterministic 2–5-example lesson, Skill Card, rehearsal, exact approval, execution, receipt, correction, reuse, and Undo | PASS in the bounded safe-lab engineering flow | The Desktop creates a Tooltail-owned synthetic safe lab. Arbitrary user-folder selection is not shipped, so the broader local-folder experience has not been evaluated. |
| scope, path, permission, plan, journal, verification, recovery, and crash behavior | automated PASS; independent review NOT RUN | Adversarial and crash matrices pass. A qualified reviewer has not independently reviewed the named security boundaries. |
| Agent Body truth under concurrency, interruption, failure, revocation, cancellation, malformed input, and disconnect | automated PASS; participant comprehension NOT RUN | Simulator/core integration is the oracle. Optional live `codex exec --json` use is NOT RUN and unnecessary for the core proof. |
| Companion Capsule continuity | automated PASS; human comprehension NOT RUN | Authority-free export, exact-byte/hash preview, pristine-only atomic import, Stale histories, explicit scope-only Draft rebind, and normal rehearsal/approval enforcement pass. v0.1 deliberately refuses merge into existing state. |
| data export, deletion, and retention | bounded v0.1 decision implemented | Capsule, research, and closed redacted diagnostic preview/export exist; whole-memory deletion is the sole erasure boundary. ADR 0010 deliberately rejects unsafe per-object deletion/history rewriting and automatic purge in v0.1. |
| dependency, license, secret, schema-freeze, SBOM, and local provenance gates | PASS locally with owner blockers | ReleaseAudit passes. Repository code-license selection and review of the test-only OSMF binary terms still require the owner/legal reviewer. |
| portable package and program-only removal | PASS locally; public distribution blocked | Two final publishes were byte-identical, the packaged apphost passed, and the isolated program directory was removed while sibling data remained unchanged. Signing identity, SmartScreen/reputation evidence, a conventional installer, actual CI provenance, and distribution approval are absent. |
| research claims | NOT RUN | The research sink and evaluator protocol are engineering-complete, but the independent first-launch evaluator and Studies A–D have not occurred. No usability, comprehension, correction, reuse, or instance-value threshold is claimed. |

## Product and platform scope

- Windows 11 x64 is the only supported hypothesis-test platform. Linux/WSL runs portable domain, contract, infrastructure, fixture, and release-audit tests; it is not a supported Desktop platform.
- The visible Desktop File Apprentice flow creates and grants a new Tooltail-owned synthetic safe lab. It does not yet let a user select an arbitrary existing local folder.
- Only `ensure_directory`, `rename_file`, `move_file`, and `copy_file` are learned primitives. Learned/general delete, overwrite, content editing, shell/script execution, URLs/network effects, cross-volume moves, global input injection, and arbitrary UI automation are rejected by design.
- Undo is a newly planned, approved, scope-checked, journaled, and verified operation. Its internal recovery removal is limited to an unchanged file or empty directory proven created by that exact execution; it is not a general deletion surface.
- The deterministic compiler handles the closed file-domain lessons and typed clarifications. Tooltail is not a general desktop automation platform and does not infer authority from model prose, body animation, window placement, or a drag gesture.
- The optional Codex adapter launches only a user-configured CLI and consumes documented output. Tooltail does not read private Codex session files, inherit CLI provider/network authority, or make the CLI part of a learned file effect.

## Data, privacy, and recovery limits

- Telemetry, screenshots, global keyboard/mouse recording, raw model transcripts, cloud memory, and research capture are off by default. The optional research sink is local, separately consented, content-minimized, and has no uploader.
- Whole-product-memory deletion removes only the fixed SQLite database/WAL/SHM/intent slots after revoking authority and clearing the internal research sink. Safe labs, user files, rehearsal residuals, Capsule exports, and separately copied research exports are preserved and must be managed explicitly by the user or study owner.
- There is no automatic retention scheduler or individual erasure for lessons, skills, receipts, or executions. ADR 0010 keeps whole-memory deletion as the only erasure control because partial removal would invalidate immutable provenance, append-only evidence, and recovery links; maintenance actions are visibly disabled rather than misleading.
- Research exports already copied outside Tooltail cannot be recalled. Screen recordings, interview notes, quotations, and any participant identity mapping are outside the product and require a separately governed consent and retention process.
- SQLite is local application state, not a tamper-proof audit boundary. Ambiguous recovery fails closed and requires validated recovery choices; it is not a sandbox against a malicious process running as the same user.
- Capsule files contain no grants or approvals and cannot restore authority. Native import is limited to the unique empty first-run identity; it cannot merge into an existing companion. Every imported version is Stale, and explicit rebind creates a new scope-only Draft that still requires rehearsal and exact-plan approval.

## Packaging, operations, and support limits

- The portable ZIP is unsigned and installs no service, updater, startup task, registry entry, file association, Start menu entry, or uninstall executable. Portable removal means closing Tooltail and removing only the extracted program directory; `%LOCALAPPDATA%\Tooltail` remains unless the user first invokes the explicit in-app deletion.
- No code-signing credential, public download, package upload, auto-update channel, crash-reporting service, or remote support channel exists. The CI packaging job intentionally does not upload binaries.
- The configured GitHub Actions workflow has not run. Local equivalence is recorded, but no workflow URL or hosted provenance may be claimed.
- The repository owner has not confirmed the recommended Apache-2.0 project license. Test-only JsonSchema.Net-family binaries retain an explicit `LicenseRef-OSMFEULA` review blocker.
- Startup time, idle CPU/private memory, long-running rendered-frame behavior, and reference-machine performance budgets have not been recorded for a tagged release. Functional bounds and cancellation tests are not substitutes for that measurement.
- No independent security/packaging review or public vulnerability-reporting channel has been completed. No critical/high issue is currently known from automated and self-review evidence, but that is not independent assurance.

## Required evidence before a public-alpha claim

1. Run the pinned GitHub workflow on the exact candidate commit and retain its URL, commit, SBOM, compatibility report, and golden/crash/path results without publishing the unsigned binary.
2. Complete and record every required row in [`WINDOW_SHELL_TEST_MATRIX.md`](WINDOW_SHELL_TEST_MATRIX.md) on the declared Windows 11 configurations, including the standard-user and elevated-target cases.
3. Complete an independent review of path, plan, permission, journal, recovery, Capsule import/parser, research/logging, local deletion, and package-removal boundaries; resolve and re-review every critical/high finding.
4. Obtain repository-owner decisions for the project license, OSMF test-binary terms, vulnerability-reporting channel, distribution shape, and whether a signed installer is required. Any signing or publishing action requires separate explicit authority and protected credentials.
5. Treat any future granular retention/deletion, Capsule merge, or automatic purge as a new migration/ADR with dependency, crash, recovery, and evidence-integrity review. ADRs 0009 and 0010 close those v0.1 scope decisions without weakening safety invariants.
6. Run the independent first-launch evaluation and Studies A–D under the consented protocol. Record actual outcomes and apply the documented stop/pivot criteria; do not infer them from automation.

Until all applicable items are complete, `BUILD_STATUS.md` must continue to describe Tooltail as an engineering hypothesis build rather than a public alpha.
