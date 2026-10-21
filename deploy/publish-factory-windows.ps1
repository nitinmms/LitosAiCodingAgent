# Publishes the installed software factory for Windows into one folder (SoftwareFactoryInstallation.md
# §14.4 step 1, G3): the host, the worker, the PTC kernel, litos-factory and the tray app
# (litos-factory-tray, ReadMe_LitosSoftwareFactory_V1.md §26.3), each self-contained
# and single-file, beside the web app (wwwroot/) and the database's compose file (db/). This folder
# is what the MSI installs (deploy/package-factory-msi.ps1).
#
# Usage: powershell -File deploy/publish-factory-windows.ps1 [-Runtime win-x64] [-Version 0.1.0] [-SkipWeb]
# (Windows PowerShell 5.1 or pwsh.)
#
# Each program is published into its own staging folder and only its executable is copied out:
# the host and the worker reference other executable projects (the worker the kernel), and
# publishing them into one folder would let their outputs overwrite each other's files.

param(
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    # The web app as already built into the host's wwwroot (npm run build), instead of building it here.
    [switch]$SkipWeb
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $repoRoot "deploy/out/factory/staging/$Runtime"
$outDir = Join-Path $repoRoot "deploy/out/factory/windows/$Runtime"

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}

function Publish-Program([string]$Project, [string]$Name, [string[]]$Extra = @()) {
    $target = Join-Path $staging $Name
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    # Shown, not returned: the caller gets only the folder.
    Invoke-Checked "Publishing $Name" {
        dotnet publish (Join-Path $repoRoot $Project) -c Release -r $Runtime -o $target `
            --self-contained true -p:PublishSingleFile=true -p:Version=$Version @Extra | Out-Host
    }
    return $target
}

# The Windows subsystem in an executable's PE header: 3 runs in a console, 2 runs without one.
# litos-factory-w.exe is litos-factory.exe with this one field changed, as the SDK itself sets it
# for a WinExe, so the Scheduled Task and the Start-menu shortcut never flash a console window.
function Set-WindowsSubsystem([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $peHeader = [BitConverter]::ToInt32($bytes, 0x3C)
    if ([BitConverter]::ToUInt32($bytes, $peHeader) -ne 0x4550) { throw "$Path is not a PE file." }
    $subsystem = $peHeader + 24 + 68
    if ([BitConverter]::ToUInt16($bytes, $subsystem) -ne 3) { throw "$Path is not a console program." }
    $bytes[$subsystem] = 2
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

if (-not $SkipWeb) {
    Push-Location (Join-Path $repoRoot "src/Litos.SoftwareFactory.Web")
    try {
        Invoke-Checked "npm ci" { npm ci }
        Invoke-Checked "npm run build" { npm run build }
    }
    finally { Pop-Location }
}
$webApp = Join-Path $repoRoot "src/Litos.SoftwareFactory.Host/wwwroot"
if (-not (Test-Path (Join-Path $webApp "index.html"))) { throw "The web app is not built: $webApp has no index.html." }

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force $outDir | Out-Null

# Native libraries (MarkItDown's, SkiaSharp, pdfium) inside the host's single file (G8).
$hostDir = Publish-Program "src/Litos.SoftwareFactory.Host/Litos.SoftwareFactory.Host.csproj" "host" @("-p:IncludeNativeLibrariesForSelfExtract=true")
$workerDir = Publish-Program "src/Litos.SoftwareFactory.Worker/Litos.SoftwareFactory.Worker.csproj" "worker" @("-p:IncludeNativeLibrariesForSelfExtract=true")
$kernelDir = Publish-Program "src/Litos.Kernel.Host/Litos.Kernel.Host.csproj" "kernel"
$setupDir = Publish-Program "src/Litos.SoftwareFactory.Setup/Litos.SoftwareFactory.Setup.csproj" "setup"
$trayDir = Publish-Program "src/Litos.SoftwareFactory.Tray.Windows/Litos.SoftwareFactory.Tray.Windows.csproj" "tray"

Copy-Item (Join-Path $hostDir "Litos.SoftwareFactory.Host.exe") $outDir
Copy-Item (Join-Path $workerDir "Litos.SoftwareFactory.Worker.exe") $outDir
Copy-Item (Join-Path $kernelDir "Litos.Kernel.Host.exe") $outDir
Copy-Item (Join-Path $setupDir "litos-factory.exe") $outDir
Copy-Item (Join-Path $setupDir "litos-factory.exe") (Join-Path $outDir "litos-factory-w.exe")
Set-WindowsSubsystem (Join-Path $outDir "litos-factory-w.exe")
Copy-Item (Join-Path $trayDir "litos-factory-tray.exe") $outDir

# Anything else a single-file publish left beside an executable would be a native library it could
# not bundle: fail here, not at runtime on someone else's machine.
foreach ($folder in @($hostDir, $workerDir, $kernelDir, $setupDir, $trayDir)) {
    $extra = Get-ChildItem $folder -File | Where-Object { $_.Extension -notin '.exe', '.pdb', '.json', '.config', '.xml' }
    if ($extra) { throw "Publishing left files beside the executable in ${folder}: $($extra.Name -join ', ')" }
}

Copy-Item $webApp (Join-Path $outDir "wwwroot") -Recurse
$db = New-Item -ItemType Directory -Force (Join-Path $outDir "db")
Copy-Item (Join-Path $repoRoot "deploy/factory/compose.yml") $db
# LF endings, whatever the checkout did: the script runs in the Linux container.
$init = (Get-Content (Join-Path $repoRoot "deploy/factory/init-roles.sh") -Raw) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText((Join-Path $db "init-roles.sh"), $init, [System.Text.UTF8Encoding]::new($false))

$size = (Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host "Published Litos Software Factory $Version ($Runtime) to: $outDir"
Get-ChildItem $outDir -File | ForEach-Object { Write-Host ("  {0,-36} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ("  Total: {0:N0} MB" -f ($size / 1MB))
