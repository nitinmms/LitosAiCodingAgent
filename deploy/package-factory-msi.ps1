# Builds the Windows installer for Litos Software Factory (SoftwareFactoryInstallation.md §14.4
# step 3): publishes the factory (deploy/publish-factory-windows.ps1) unless -SkipPublish, then
# builds installer/windows/LitosSoftwareFactory.wixproj with WiX v5 from NuGet; nothing to install.
# Unsigned until a code-signing certificate exists (§5.4): SmartScreen then says "unknown publisher".
#
# Usage: powershell -File deploy/package-factory-msi.ps1 [-Version 0.1.0] [-SkipPublish] [-SkipWeb]
# Output: deploy/out/factory/LitosSoftwareFactory-<version>-win-x64.msi

param(
    [string]$Version = "0.1.0",
    [switch]$SkipPublish,
    [switch]$SkipWeb
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = "win-x64"
$publishDir = Join-Path $repoRoot "deploy/out/factory/windows/$runtime"
$outDir = Join-Path $repoRoot "deploy/out/factory"
. (Join-Path $PSScriptRoot "msi-common.ps1")
Assert-MsiVersion $Version

if (-not $SkipPublish) {
    $publishArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "publish-factory-windows.ps1"), "-Runtime", $runtime, "-Version", $Version)
    if ($SkipWeb) { $publishArgs += "-SkipWeb" }
    & powershell @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path (Join-Path $publishDir "litos-factory-w.exe"))) { throw "Nothing is published in $publishDir." }

$licenseRtf = Join-Path $outDir "License.rtf"
New-LicenseRtf $repoRoot $licenseRtf

# Always a full build: incrementally, WiX skipped linking when only the version had changed. Into
# its own folder, then copied out: a full build cleans its output folder, which took the MSI of the
# version built before with it (an upgrade test needs both).
$buildDir = Join-Path $outDir "msi-build"
dotnet build (Join-Path $repoRoot "installer/windows/LitosSoftwareFactory.wixproj") -c Release --no-incremental `
    -p:ProductVersion=$Version -p:PublishDir=$publishDir -p:LicenseRtf=$licenseRtf -o $buildDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Building the MSI failed with exit code $LASTEXITCODE" }

$msi = Join-Path $outDir "LitosSoftwareFactory-$Version-$runtime.msi"
Copy-Item (Join-Path $buildDir (Split-Path $msi -Leaf)) $msi -Force
Write-Host ""
Write-Host ("Built {0} ({1:N0} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
Write-Host "Install:            msiexec /i `"$msi`""
Write-Host "Uninstall and purge: msiexec /x `"$msi`" PURGE=1"
