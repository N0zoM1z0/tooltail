# Bounded Public-Alpha Release Evidence

## Local release audit

The BCL-only `Tooltail.ReleaseAudit` tool performs a read-only repository audit and writes only to the ignored fixed `artifacts/release-audit` directory:

```powershell
dotnet run --project tools/Tooltail.ReleaseAudit -c Release --no-build -- verify --root $PWD --output "$PWD\artifacts\release-audit"
```

It verifies:

- every resolved package in committed lock files matches `eng/dependency-review.json` and its restored `.nuspec` license metadata;
- the ten v1 schema/example files match normalized-LF hashes in `eng/schema-freeze-v1.json`;
- every GitHub workflow action uses a full 40-hex commit pin;
- only `git ls-files` tracked files are scanned for bounded private-key and common provider-token patterns;
- SPDX 2.3 dependency SBOM and a content-minimized release-evidence JSON are generated locally.

NuGet vulnerability and deprecation gates run separately because they query current NuGet advisory metadata. They returned zero findings on 2026-07-16 after migrating test projects to xUnit v3. Product binaries contain no analytics SDK; xUnit v3/Microsoft Testing Platform telemetry dependencies are test-only, and both .NET CLI and test-platform telemetry are opted out in CI.

The JsonSchema.Net, JsonPointer.Net, and Json.More.Net NuGet binaries are test-only and declare `OSMFEULA.txt`. The SPDX records `LicenseRef-OSMFEULA`; repository-owner/legal review is required before revenue-generating use. This is not mislabeled as plain MIT even though the agreement describes the source as MIT.

## CI authority and provenance

CI has read-only repository permission, pinned action commits, locked restore, frozen SDK selection, contract syntax, Windows build/test, portable tests, vulnerability/deprecation checks, secret patterns, license evidence, schema freeze, and artifact-only SBOM output. CI does not publish a release, sign a binary, contact participants, or receive production credentials.

GitHub Actions CI run 29595949677 passed on 2026-07-17 UTC for commit `2912e2967973a4a0df372b6ce7cdf38fa93e6f20`: `contract-syntax`, `supply-chain`, `portable-test`, Windows `build-test`, and `portable-package` all succeeded. Run URL: <https://github.com/N0zoM1z0/tooltail/actions/runs/29595949677>. Future candidates must retain their own exact-commit hosted run rather than inheriting this evidence.

## External blockers

- repository-owner selection of the project code license;
- review of the OSMF test-binary terms;
- independent path/plan/permission/journal/recovery/import/logging security review;
- attended Windows versions, DPI/monitor, high contrast, reduced motion, keyboard, and screen-reader matrix;
- code-signing identity and protected signing credentials;
- public distribution decision and vulnerability-reporting channel.

No signing, installer, repository publication, release upload, or external credential is created by this evidence tooling.

The complete product/platform limitations and final-acceptance delta are maintained in [`KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md). A local PASS below must not override a PARTIAL or NOT RUN external gate in that ledger.

## Local data lifecycle gate

ADR 0008 and `DATA_LIFECYCLE.md` define the implemented whole-memory deletion boundary. Automated evidence covers its expiring single-use authorization, exact fixed-file removal, preserved labs/exports/unrelated state, cancellation-before-intent, every incomplete prefix, malformed/oversized intent, startup-before-SQLite ordering, and two-step Home controls. The expanded Windows apphost smoke must additionally prove the SQLite slots disappear while the safe-lab result and Capsule remain.

ADR 0010 records the bounded v0.1 retention decision: per-object deletion/history rewriting is unsafe for immutable provenance and append-only recovery evidence, so whole-memory deletion remains the sole erasure boundary and granular maintenance controls are visibly disabled. The closed diagnostic export now has exact preview/hash/CreateNew and no-raw-field tests. Independent review remains open. Packaged portable-removal retention never infers that deleting a program directory authorizes deletion of `%LOCALAPPDATA%\Tooltail`.

## Portable package and uninstall evidence

The M7 packaging surface produces an unsigned self-contained `win-x64` ZIP only under ignored local artifacts. The packer requires a bounded non-reparse publish tree, excludes PDB/state/export/archive material, embeds a closed per-file hash manifest, fixes entry order/time, strictly reads back every entry, and writes a `CreateNew` SHA-256 sidecar. Its standard-user source manifest is checked before packaging.

On the Windows 11 engineering host, two independent packages from commit `782d35f` were byte-identical at 441 files and 177,718,659 payload bytes; the 74,428,195-byte ZIP hash was `62d8054b4f1b11b07afc4af70adacebaf4ccfe305476c3bea51f785a080f14eb`. Each extracted self-contained apphost completed the full Window Shell smoke—including existing-folder protected-root restore/failure/revoke—with exit 0. Each verifier then removed only its new marker-bound `program` directory and proved the sibling local-data sentinel was unchanged. No normal user profile, installed application, service, registry entry, startup task, or unrelated process/file was touched.

The CI package job reproduces the build and uninstall evidence without uploading the ZIP; run 29595949677 completed that job successfully. The artifact explicitly reports `isCodeSigned = false`; code signing, SmartScreen/reputation, a conventional installer prototype, protected credentials, and public distribution remain external blockers. See `PORTABLE_PACKAGE.md`.
