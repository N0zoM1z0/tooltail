# Portable Windows Package

Tooltail v0.1's first distribution shape is an unsigned, self-contained `win-x64` ZIP. It is a bounded engineering artifact, not a public release, conventional installer, auto-updater, or security sandbox.

## Build and verify

From a clean repository checkout on Windows with .NET SDK 10.0.302:

```powershell
./eng/package-portable.ps1
```

The script has no path parameters. It uses only these fixed ignored outputs:

- `artifacts/portable/win-x64/publish`;
- `artifacts/portable/Tooltail-0.1.0-win-x64-portable.zip`;
- `artifacts/portable/Tooltail-0.1.0-win-x64-portable.zip.sha256`;
- `artifacts/portable/uninstall-verification`.

Every output slot must be absent. The script refuses overwrite and has no cleanup/force switch. Use a fresh checkout/output volume for a subsequent release candidate rather than hiding or replacing prior evidence.

The script performs locked generic and `win-x64` restores, a Release self-contained untrimmed publish, deterministic package construction, full manifest/hash readback, packaged apphost Window Shell smoke, and an isolated portable-removal test. It never signs, uploads, installs a service, writes the registry, enables startup, or contacts an update endpoint.

## Package contract

`Tooltail/package-manifest.json` is embedded in the ZIP and uses internal contract `tooltail.portable-package/1`. It binds:

- product/version, `win-x64`, and the exact Windows TFM;
- `selfContained = true` and `isCodeSigned = false`;
- `Tooltail.Desktop.exe` as the entry point;
- `%LOCALAPPDATA%\Tooltail` as the separate data root;
- `program_directory_only` as the uninstall scope;
- an ordered length and SHA-256 record for every payload file.

The packer rejects duplicate, unmanifested, traversal, absolute, link/reparse, oversized, database, log, JSONL, dump, temporary, PDB, nested ZIP, and other prohibited entries. It requires the apphost, dependency/runtime documents, CoreCLR host files, and WPF framework payload. ZIP timestamps are fixed and files are ordered, so the same input payload produces byte-identical archives.

The verified 2026-07-17 engineering package contained 441 payload files and 177,572,227 uncompressed bytes. Its approximately 71 MiB ZIP SHA-256 was `384a088f4859cee8d5e6a9a187159bb728dce9de4b6b311a1de5c80255d89141`. This hash applies only to that exact local working tree; a later source, SDK, dependency, or documentation-neutral binary change must produce and record new evidence.

## Run as a standard user

Extract the `Tooltail` directory into a user-writable program location and run `Tooltail.Desktop.exe` without elevation. The embedded application manifest uses `asInvoker`, `uiAccess=false`, and Per-Monitor V2 DPI awareness. Do not run Tooltail as administrator to bypass an eligibility or file-permission failure.

The package carries the .NET desktop runtime and does not require a machine-wide .NET installation. It does not include credentials, a model, a Codex binary, a user database, lab files, Capsule/research exports, recordings, or telemetry configuration.

## Portable removal and retained data

Portable “uninstall” means:

1. close Tooltail;
2. remove only the directory into which the ZIP's `Tooltail` program folder was extracted;
3. leave `%LOCALAPPDATA%\Tooltail` unchanged.

The verifier extracts into a newly created marker-bound `program` directory beside a synthetic `local-data` sentinel, launches the packaged apphost, validates the entire program tree has no reparse entries, and recursively removes only that exact program directory. It then requires the sibling data sentinel to remain byte-identical and writes `uninstall-evidence.json`. If apphost launch or any boundary check fails, program files and the sentinel remain for inspection; no removal occurs.

To remove Tooltail's durable product memory, use the in-app two-step deletion described in [`DATA_LIFECYCLE.md`](DATA_LIFECYCLE.md) before removing the program directory. Safe labs and exports are intentionally preserved and remain the user's responsibility.

## Release limitations

- The ZIP is explicitly unsigned. Code-signing identity, protected credentials, and owner authorization are absent.
- There is no conventional installer, Start menu registration, file association, startup task, service, update channel, or uninstall executable.
- SmartScreen/reputation behavior and a signed installer prototype are not tested.
- CI is configured to build and verify without uploading the binary, but an actual GitHub workflow run is not yet recorded.
- Independent security review and the attended Windows/accessibility matrix remain NOT RUN.

Do not distribute this artifact as a public alpha until the external blockers in [`RELEASE_EVIDENCE.md`](RELEASE_EVIDENCE.md) are resolved.
