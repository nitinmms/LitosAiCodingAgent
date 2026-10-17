<#
.SYNOPSIS
    Backs up the software factory: its database and the data directory files it cannot rebuild.

.DESCRIPTION
    m4-architecture.md §8.2. Signs in to the running host as an Admin, pauses dispatch and waits
    until no run is working, then dumps the database (pg_dump, custom format), copies the key ring,
    skill library, run logs and baselines, and writes manifest.json with every file's size and
    SHA-256. Dispatch is resumed whatever happened, unless it was already paused before.

    Working copies, reading copies and virtual environments are not backed up: GitHub holds
    handed-off code, and unpushed work in a working copy is not covered.

.EXAMPLE
    .\deploy\factory-backup.ps1 -Destination D:\factory-backups\2026-10-17
#>
[CmdletBinding()]
param(
    # An empty or new folder for this backup.
    [Parameter(Mandatory)] [string]$Destination,
    [string]$Url = 'http://127.0.0.1:5180',
    # An Admin of the factory; asked for when not given.
    [pscredential]$Credential,
    # The host's settings file: the database, the data directory and factory_admin's password.
    [string]$EnvFile,
    # The host to run the backup commands with; the newest build in the source tree by default.
    [string]$HostPath,
    # How long to wait for working runs to reach their next stop.
    [int]$WaitMinutes = 30,
    # PostgreSQL in its container (the default) ...
    [string]$ComposeFile,
    [string]$Service = 'factory-db',
    [string]$Container,
    # ... or with local client tools, from this folder.
    [string]$PgBin
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'factory\backup-common.ps1')

if (-not $EnvFile) { $EnvFile = Join-Path $RepoRoot '.env.factory' }
if (-not $ComposeFile) { $ComposeFile = Join-Path $RepoRoot 'compose.factory-state.yml' }
$Url = $Url.TrimEnd('/')

if ((Test-Path $Destination) -and (Get-ChildItem $Destination -Force | Select-Object -First 1)) {
    throw "$Destination is not empty. Use a new folder for each backup."
}
$settings = Read-EnvFile $EnvFile
$factoryHost = Resolve-FactoryHost $HostPath
$pg = New-PgAccess $settings $EnvFile $ComposeFile $Service $Container $PgBin
if (-not $Credential) { $Credential = Get-Credential -Message "An Admin of the factory at $Url" }

New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path
$dump = Join-Path $Destination 'litos_factory.dump'

$session = Connect-Factory $Url $Credential
$pausedHere = $false
try {
    # 1. Nothing new starts; what is working carries on to its next stop.
    $state = Invoke-Factory $session $Url Post '/api/admin/dispatch' @{ paused = $true; reason = 'A backup is being taken.' }
    $pausedHere = -not $state.wasPaused
    if (-not $pausedHere) { Write-Host 'Dispatch was already paused; it stays paused after the backup.' }

    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while ($state.active.Count -gt 0) {
        if ((Get-Date) -gt $deadline) {
            $working = ($state.active | ForEach-Object { if ($_.title) { "  - $($_.title) ($($_.kind))" } else { "  - run $($_.runId)" } }) -join "`n"
            throw "Runs were still working after $WaitMinutes minute(s), so no backup was taken:`n$working"
        }
        Write-Host "Waiting for $($state.active.Count) run(s) to stop..."
        Start-Sleep -Seconds 10
        $state = Invoke-Factory $session $Url Get '/api/admin/dispatch' $null
    }

    # 2. The database.
    Write-Host "Dumping $($pg.Database)..."
    $pgDump = Get-PgPath $pg $dump
    Invoke-Pg $pg 'pg_dump' @('--format=custom', "--dbname=$($pg.Database)", "--file=$pgDump")
    if ($pg.Container) {
        try {
            & docker cp "$($pg.Container):$pgDump" $dump | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Copying the dump out of the container failed (exit code $LASTEXITCODE)." }
        } finally {
            & docker exec $pg.Container rm -f $pgDump | Out-Null
        }
    }

    # 3 and 4. The data directory's files, and the manifest.
    if ((Invoke-FactoryHost $factoryHost @('--env-file', $EnvFile, '--backup-data', $Destination)) -ne 0) {
        throw 'The data files could not be backed up; the folder holds an incomplete backup.'
    }
} finally {
    # 5. Resumed whatever happened, unless someone else had paused it.
    if ($pausedHere) {
        try {
            Invoke-Factory $session $Url Post '/api/admin/dispatch' @{ paused = $false } | Out-Null
            Write-Host 'Dispatch resumed.'
        } catch {
            Write-Warning "Dispatch could not be resumed: $($_.Exception.Message) Resume it on the Metrics tab."
        }
    }
}

Write-Host "Backup written to $Destination."
