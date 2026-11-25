# Publishes Litos.Gui as a self-contained, single-file Windows executable, plus the Programmatic
# Tool Calling kernel (Litos.Kernel.Host.exe) beside it — KernelHostLocator finds the kernel by
# looking next to the launched exe, so the two must land in the same directory. Without the
# kernel, Litos.Gui still runs; /ptc just reports itself unavailable.
# Usage: pwsh deploy/publish-windows.ps1 [-Runtime win-x64]

param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot "deploy/out/windows/$Runtime"

foreach ($project in @("src/Litos.Gui/Litos.Gui.csproj", "src/Litos.Kernel.Host/Litos.Kernel.Host.csproj")) {
    dotnet publish (Join-Path $repoRoot $project) `
        -c Release `
        -r $Runtime `
        -o $outDir `
        --self-contained true `
        -p:PublishSingleFile=true

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish of $project failed with exit code $LASTEXITCODE"
    }
}

Write-Host ""
Write-Host "Published to: $outDir"
Write-Host "Run: $outDir/Litos.Gui.exe"
