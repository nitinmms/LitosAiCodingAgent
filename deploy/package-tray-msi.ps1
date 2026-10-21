# Builds the tray app's own Windows installer (ReadMe_LitosSoftwareFactory_V1.md §26.3), for people
# whose factory runs on a server: publishes litos-factory-tray self-contained and single-file, then
# builds installer/windows-tray/LitosFactoryTray.wixproj with WiX v5 from NuGet. Unsigned until a
# code-signing certificate exists: SmartScreen then says "unknown publisher".
# (The factory's own MSI, deploy/package-factory-msi.ps1, installs the same program beside the factory.)
#
# Usage: powershell -File deploy/package-tray-msi.ps1 [-Version 0.1.0]
# Output: deploy/out/tray/LitosFactoryTray-<version>-win-x64.msi

param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = "win-x64"
$outDir = Join-Path $repoRoot "deploy/out/tray"
$publishDir = Join-Path $outDir "publish/$runtime"
. (Join-Path $PSScriptRoot "msi-common.ps1")
Assert-MsiVersion $Version

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $repoRoot "src/Litos.SoftwareFactory.Tray.Windows/Litos.SoftwareFactory.Tray.Windows.csproj") `
    -c Release -r $runtime -o $publishDir --self-contained true -p:PublishSingleFile=true -p:Version=$Version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publishing the tray app failed with exit code $LASTEXITCODE" }
$extra = Get-ChildItem $publishDir -File | Where-Object { $_.Extension -notin '.exe', '.pdb', '.json', '.config', '.xml' }
if ($extra) { throw "Publishing left files beside the executable: $($extra.Name -join ', ')" }

$licenseRtf = Join-Path $outDir "License.rtf"
New-LicenseRtf $repoRoot $licenseRtf

# A full build into its own folder, then copied out, as for the factory's MSI.
$buildDir = Join-Path $outDir "msi-build"
dotnet build (Join-Path $repoRoot "installer/windows-tray/LitosFactoryTray.wixproj") -c Release --no-incremental `
    -p:ProductVersion=$Version -p:PublishDir=$publishDir -p:LicenseRtf=$licenseRtf -o $buildDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Building the MSI failed with exit code $LASTEXITCODE" }

$msi = Join-Path $outDir "LitosFactoryTray-$Version-$runtime.msi"
Copy-Item (Join-Path $buildDir (Split-Path $msi -Leaf)) $msi -Force
Write-Host ""
Write-Host ("Built {0} ({1:N0} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
Write-Host "Install:   msiexec /i `"$msi`""
Write-Host "Uninstall: msiexec /x `"$msi`""
