# Publishes Litos.VsCodeHost as a self-contained, single-file Windows executable — the local
# no-auth agent host the Litos VS Code extension (src/Litos.VsCode) spawns as a child process and
# bundles per-RID under src/Litos.VsCode/bin/<rid>/ (see ReadMe_VsCodeExtension.md §6).
# Usage: pwsh deploy/publish-vscodehost-windows.ps1 [-Runtime win-x64]

param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/Litos.VsCodeHost/Litos.VsCodeHost.csproj"
$outDir = Join-Path $repoRoot "deploy/out/vscodehost/windows/$Runtime"

dotnet publish $project `
    -c Release `
    -r $Runtime `
    -o $outDir `
    --self-contained true `
    -p:PublishSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Published to: $outDir"
Write-Host "Copy into the extension bundle with:"
Write-Host "  Copy-Item `"$outDir/Litos.VsCodeHost.exe`" `"src/Litos.VsCode/bin/$Runtime/`" -Force"
