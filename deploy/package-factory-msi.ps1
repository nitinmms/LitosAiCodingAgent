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

# An MSI's ProductVersion is three numbers; a major upgrade needs one of them to grow.
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The version must be major.minor.patch, such as 0.1.0." }

if (-not $SkipPublish) {
    $publishArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "publish-factory-windows.ps1"), "-Runtime", $runtime, "-Version", $Version)
    if ($SkipWeb) { $publishArgs += "-SkipWeb" }
    & powershell @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE" }
}
if (-not (Test-Path (Join-Path $publishDir "litos-factory-w.exe"))) { throw "Nothing is published in $publishDir." }

# The licence page shows RTF: LICENSE.txt with \ { } escaped, anything beyond ASCII as \uN, one
# paragraph per line.
function ConvertTo-RtfLine([string]$Line) {
    $rtf = [System.Text.StringBuilder]::new()
    foreach ($c in $Line.ToCharArray()) {
        $code = [int]$c
        if ($c -eq '\' -or $c -eq '{' -or $c -eq '}') { [void]$rtf.Append('\').Append($c) }
        elseif ($code -gt 127) {
            # \uN takes a signed 16-bit number; ? is what a reader without Unicode shows.
            if ($code -gt 32767) { $code -= 65536 }
            [void]$rtf.Append("\u$code?")
        }
        else { [void]$rtf.Append($c) }
    }
    return $rtf.Append('\par').ToString()
}
$licenseRtf = Join-Path $outDir "License.rtf"
$lines = Get-Content (Join-Path $repoRoot "LICENSE.txt") -Encoding UTF8 | ForEach-Object { ConvertTo-RtfLine $_ }
Set-Content $licenseRtf -Encoding ascii -Value ("{\rtf1\ansi\deff0{\fonttbl{\f0 Consolas;}}\f0\fs16`r`n" + ($lines -join "`r`n") + "`r`n}")

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
