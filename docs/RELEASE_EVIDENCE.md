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

Actual GitHub workflow execution remains distinct from equivalent local execution. A release evidence packet must record the workflow run URL/commit only after it really runs.

## External blockers

- repository-owner selection of the project code license;
- review of the OSMF test-binary terms;
- independent path/plan/permission/journal/recovery/import/logging security review;
- attended Windows versions, DPI/monitor, high contrast, reduced motion, keyboard, and screen-reader matrix;
- code-signing identity and protected signing credentials;
- public distribution decision and vulnerability-reporting channel.

No signing, installer, repository publication, release upload, or external credential is created by this evidence tooling.
