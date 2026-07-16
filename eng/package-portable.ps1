[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src/Tooltail.Desktop/Tooltail.Desktop.csproj"
$portableRoot = Join-Path $repositoryRoot "artifacts/portable"
$publishRoot = Join-Path $portableRoot "win-x64/publish"
$archive = Join-Path $portableRoot "Tooltail-0.1.0-win-x64-portable.zip"
$verificationRoot = Join-Path $portableRoot "uninstall-verification"
$auditProject = Join-Path $repositoryRoot "tools/Tooltail.ReleaseAudit/Tooltail.ReleaseAudit.csproj"

foreach ($path in @($publishRoot, $archive, "$archive.sha256", $verificationRoot)) {
    if (Test-Path -LiteralPath $path) {
        throw "Portable packaging refuses to overwrite existing output: $path"
    }
}

$publishParent = Split-Path -Parent $publishRoot
if (-not (Test-Path -LiteralPath $publishParent)) {
    New-Item -ItemType Directory -Path $publishParent | Out-Null
}

& dotnet restore $project --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet restore $auditProject --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $publishRoot `
    -p:PublishProfile=win-x64 `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet run --project $auditProject --configuration Release --no-restore -- `
    pack-portable --root $repositoryRoot --publish $publishRoot --output $archive
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet run --project $auditProject --configuration Release --no-build -- `
    verify-uninstall --root $repositoryRoot --archive $archive --work $verificationRoot
exit $LASTEXITCODE
