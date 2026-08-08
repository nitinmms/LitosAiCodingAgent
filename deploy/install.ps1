# Litos.Gui one-line installer.
#
# Usage:
#   irm https://raw.githubusercontent.com/nitinmms/LitosAiCodingAgent/master/deploy/install.ps1 | iex
#
# Downloads the latest win-x64 GitHub Release asset (built by .github/workflows/release-gui.yml),
# verifies its SHA256 checksum, and installs it to %LOCALAPPDATA%\Programs\Litos — no admin
# rights required. Adds that folder to the current user's PATH and creates a Start Menu shortcut.
# Re-running this script upgrades an existing install in place.

$ErrorActionPreference = "Stop"

$repo = "nitinmms/LitosAiCodingAgent"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\Litos"
$exeName = "Litos.Gui.exe"

Write-Host "Litos.Gui installer" -ForegroundColor Cyan

Write-Host "Looking up latest release..."
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ "User-Agent" = "Litos-Installer" }
$zipAsset = $release.assets | Where-Object { $_.name -like "Litos.Gui-*-win-x64.zip" } | Select-Object -First 1
$shaAsset = $release.assets | Where-Object { $_.name -like "Litos.Gui-*-win-x64.zip.sha256" } | Select-Object -First 1

if (-not $zipAsset) {
    throw "No win-x64 release asset found for $repo. Has a version been released yet?"
}

$version = $release.tag_name
Write-Host "Latest version: $version"

$tempDir = Join-Path $env:TEMP "litos-install-$([guid]::NewGuid())"
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

$shortcutPath = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\Litos.lnk"
$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installDir $exeName
$shortcut.WorkingDirectory = $installDir
$shortcut.Save()

Write-Host ""
Write-Host "Litos.Gui $version installed." -ForegroundColor Green
Write-Host "Launch it from the Start Menu, or open a new terminal and run: Litos.Gui"
Write-Host "On first run, if no LLM API key is found, Litos will prompt you to enter one."
