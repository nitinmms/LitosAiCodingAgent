# Litos.Console one-line installer.
#
# Usage:
#   irm https://raw.githubusercontent.com/nitinmms/LitosAiCodingAgent/master/deploy/install-console.ps1 | iex
#
# Downloads the latest win-x64 GitHub Release asset (built by .github/workflows/release-console.yml,
# tagged "console-v*.*.*" — separate from Litos.Gui's "v*.*.*" releases), verifies its SHA256
# checksum, and installs it to %LOCALAPPDATA%\Programs\Litos.Console — no admin rights required.
# Adds that folder to the current user's PATH. Re-running this script upgrades an existing
# install in place.

$ErrorActionPreference = "Stop"

$repo = "nitinmms/LitosAiCodingAgent"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\Litos.Console"
$exeName = "Litos.Console.exe"

Write-Host "Litos.Console installer" -ForegroundColor Cyan

Write-Host "Looking up latest console release..."
$releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases" -Headers @{ "User-Agent" = "Litos-Installer" }
$release = $releases | Where-Object { $_.tag_name -like "console-v*" } | Select-Object -First 1

if (-not $release) {
    throw "No console-v* release found for $repo. Has a Litos.Console version been released yet?"
}

$zipAsset = $release.assets | Where-Object { $_.name -like "Litos.Console-*-win-x64.zip" } | Select-Object -First 1
$shaAsset = $release.assets | Where-Object { $_.name -like "Litos.Console-*-win-x64.zip.sha256" } | Select-Object -First 1

if (-not $zipAsset) {
    throw "No win-x64 release asset found in $($release.tag_name)."
}

$version = $release.tag_name
Write-Host "Latest version: $version"

$tempDir = Join-Path $env:TEMP "litos-console-install-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $zipPath = Join-Path $tempDir $zipAsset.name
    Write-Host "Downloading $($zipAsset.name)..."
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath

    if ($shaAsset) {
        $shaPath = Join-Path $tempDir $shaAsset.name
        Invoke-WebRequest -Uri $shaAsset.browser_download_url -OutFile $shaPath
        $expectedHash = (Get-Content $shaPath -Raw).Trim()
        $actualHash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash
        if ($actualHash -ne $expectedHash) {
            throw "Checksum mismatch for $($zipAsset.name): expected $expectedHash, got $actualHash"
        }
        Write-Host "Checksum verified."
    }

    Write-Host "Installing to $installDir..."
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Expand-Archive -Path $zipPath -DestinationPath $installDir -Force
}
finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}

# User-scope PATH update — no admin rights needed, takes effect in new shells/processes.
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    Write-Host "Adding $installDir to your PATH..."
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$installDir", "User")
}

Write-Host ""
Write-Host "Litos.Console $version installed." -ForegroundColor Green
Write-Host "Open a NEW terminal window (PATH changes need a fresh shell) and run: Litos.Console"
Write-Host "On first run, if no LLM API key is found, Litos.Console will prompt you to enter one."
