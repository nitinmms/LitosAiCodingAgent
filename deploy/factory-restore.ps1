<#
.SYNOPSIS
    Restores a software factory backup into a new database and a new data directory.

.DESCRIPTION
    m4-architecture.md §8.3. Checks the backup against its manifest, restores the dump into a
    database that must not exist yet (never over the live one), copies the data files into a new
    data directory, and prints the settings that point a host at the copy.

    The host started on the copy checks the restored files and its key ring at start, and shows the
    result on the Metrics tab's Operations section. It starts with dispatch paused, as the backup
    was taken, and its unfinished runs wait for a person to resume them.

.EXAMPLE
    .\deploy\factory-restore.ps1 -Source D:\factory-backups\2026-10-17 -Database litos_factory_copy -DataDirectory D:\factory-copy
#>
[CmdletBinding()]
param(
    # A folder written by factory-backup.ps1.
    [Parameter(Mandatory)] [string]$Source,
    # A new database, in the same PostgreSQL as the live one (the roles it names exist there).
    [Parameter(Mandatory)] [string]$Database,
    # A new, or empty, data directory.
    [Parameter(Mandatory)] [string]$DataDirectory,
    # The live host's settings file: how PostgreSQL is reached, and factory_admin's password.
    [string]$EnvFile,
    [string]$HostPath,
    # The address the copy's host should listen on, printed with its settings.
    [string]$Url = 'http://127.0.0.1:5181',
    [string]$ComposeFile,
    [string]$Service = 'factory-db',
    [string]$Container,
    [string]$PgBin
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'factory\backup-common.ps1')

if (-not $EnvFile) { $EnvFile = Join-Path $RepoRoot '.env.factory' }
if (-not $ComposeFile) { $ComposeFile = Join-Path $RepoRoot 'compose.factory-state.yml' }
if ($Database -cnotmatch '^[a-z_][a-z0-9_]{0,62}$') { throw "'$Database' is not a database name this script creates: lower-case letters, digits and _." }
if ((Test-Path $DataDirectory) -and (Get-ChildItem $DataDirectory -Force | Select-Object -First 1)) {
    throw "$DataDirectory is not empty. Restore into a new data directory."
}
$Source = (Resolve-Path $Source).Path
$settings = Read-EnvFile $EnvFile
$factoryHost = Resolve-FactoryHost $HostPath
$pg = New-PgAccess $settings $EnvFile $ComposeFile $Service $Container $PgBin
if ($Database -eq $pg.Database) { throw "$Database is the live database. Restore into a new one." }

# 1. The backup is what its manifest says, before anything is created from it.
if ((Invoke-FactoryHost $factoryHost @('--verify-backup', $Source)) -ne 0) { throw 'The backup does not match its manifest; nothing was restored.' }

# 2. The database, new.
$exists = Invoke-Pg $pg 'psql' @('--dbname=postgres', '-tAc', "SELECT 1 FROM pg_database WHERE datname = '$Database'")
if ("$exists".Trim() -eq '1') { throw "The database $Database already exists. Name a new one: a restore never replaces a database." }

$dump = Join-Path $Source 'litos_factory.dump'
$pgDump = Get-PgPath $pg $dump
if ($pg.Container) {
    & $pg.Docker cp $dump "$($pg.Container):$pgDump" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Copying the dump into the container failed (exit code $LASTEXITCODE)." }
}
try {
    Write-Host "Restoring into $Database..."
    Invoke-Pg $pg 'psql' @('--dbname=postgres', '-c', "CREATE DATABASE $Database")
    Invoke-Pg $pg 'pg_restore' @("--dbname=$Database", '--exit-on-error', $pgDump)
} finally {
    if ($pg.Container) { & $pg.Docker exec $pg.Container rm -f $pgDump | Out-Null }
}

# 3. The files, and the manifest the copy's host checks them against.
New-Item -ItemType Directory -Force $DataDirectory | Out-Null
$DataDirectory = (Resolve-Path $DataDirectory).Path
$data = Join-Path $Source 'data'
if (Test-Path $data) { Copy-Item (Join-Path $data '*') $DataDirectory -Recurse }
Copy-Item (Join-Path $Source 'manifest.json') (Join-Path $DataDirectory 'restored-from.json')

# 4. What points a host at the copy: the live settings with these lines changed.
$live = $pg.Database
Write-Host ''
Write-Host "Restored. To start a host on the copy, copy $EnvFile and change these lines:"
Write-Host "  FACTORY_DATA_DIR=$($DataDirectory.Replace('\', '/'))"
Write-Host "  ConnectionStrings__FactoryState: Database=$live -> Database=$Database"
Write-Host "  ConnectionStrings__FactoryMigrations: Database=$live -> Database=$Database"
Write-Host "  ASPNETCORE_URLS=$Url"
Write-Host 'Dispatch is paused in the copy, as it was during the backup. Resume it on the Metrics tab when you mean the copy to run work.'
