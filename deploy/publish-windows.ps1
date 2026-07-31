# Publishes Litos.Gui as a self-contained, single-file Windows executable.
# Usage: pwsh deploy/publish-windows.ps1 [-Runtime win-x64]

param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/Litos.Gui/Litos.Gui.csproj"
$outDir = Join-Path $repoRoot "deploy/out/windows/$Runtime"

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
Write-Host "Run: $outDir/Litos.Gui.exe"
