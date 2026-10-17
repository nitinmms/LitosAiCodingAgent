# Shared by deploy/factory-backup.ps1 and deploy/factory-restore.ps1 (m4-architecture.md §8).
# Dot-sourced; not run on its own.

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# docker.exe itself: a stray file named "docker" earlier on the PATH would otherwise be "run" as a document.
function Resolve-Docker {
    $app = Get-Command docker -CommandType Application -All -ErrorAction SilentlyContinue | Where-Object { $_.Source -like '*.exe' } | Select-Object -First 1
    if ($app) { return $app.Source }
    $desktop = Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'
    if (Test-Path $desktop) { return $desktop }
    throw 'docker was not found. Start Docker Desktop, or name local PostgreSQL client tools with -PgBin.'
}

# KEY=value lines, as the host reads them (blank lines and # comments skipped).
function Read-EnvFile([string]$Path) {
    if (-not (Test-Path $Path)) { throw "Settings file '$Path' was not found." }
    $values = @{}
    foreach ($line in Get-Content $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $at = $trimmed.IndexOf('=')
        if ($at -gt 0) { $values[$trimmed.Substring(0, $at).Trim()] = $trimmed.Substring($at + 1).Trim() }
    }
    return $values
}

# One part of an Npgsql connection string ("Database", "Host", ...), or $null.
function Get-ConnectionPart([string]$ConnectionString, [string]$Name) {
    foreach ($part in $ConnectionString.Split(';')) {
        $at = $part.IndexOf('=')
        if ($at -gt 0 -and $part.Substring(0, $at).Trim() -ieq $Name) { return $part.Substring($at + 1).Trim() }
    }
    return $null
}

# The host to run the backup commands with: the one named, or the newest build in the source tree.
function Resolve-FactoryHost([string]$HostPath) {
    if ($HostPath) {
        if (-not (Test-Path $HostPath)) { throw "The host '$HostPath' was not found." }
        return (Resolve-Path $HostPath).Path
    }
    $built = Get-ChildItem (Join-Path $RepoRoot 'src\Litos.SoftwareFactory.Host\bin') -Recurse -Filter 'Litos.SoftwareFactory.Host.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $built) { throw 'No built factory host was found: build src/Litos.SoftwareFactory.Host, or name the host with -HostPath.' }
    return $built.FullName
}

# Runs the host with these arguments; returns its exit code.
function Invoke-FactoryHost([string]$HostPath, [string[]]$Arguments) {
    # Shown, not returned: the caller gets only the exit code.
    if ($HostPath.EndsWith('.dll')) { & dotnet $HostPath @Arguments | Out-Host } else { & $HostPath @Arguments | Out-Host }
    return $LASTEXITCODE
}

# How PostgreSQL is reached: inside the database's container (the default), or with local client
# tools when -PgBin names them.
function New-PgAccess([hashtable]$Env, [string]$EnvFile, [string]$ComposeFile, [string]$Service, [string]$Container, [string]$PgBin) {
    $connection = $Env['ConnectionStrings__FactoryState']
    if (-not $connection) { throw "ConnectionStrings__FactoryState is not set in $EnvFile." }
    $access = @{
        Database  = Get-ConnectionPart $connection 'Database'
        User      = 'factory_admin'
        PgBin     = $PgBin
        Container = $null
        Host      = Get-ConnectionPart $connection 'Host'
        Port      = Get-ConnectionPart $connection 'Port'
        Password  = $Env['FACTORY_DB_ADMIN_PASSWORD']
    }
    if ($PgBin) {
        if (-not (Test-Path (Join-Path $PgBin 'pg_dump*'))) { throw "pg_dump was not found in '$PgBin'." }
        if (-not $access.Password) { throw "FACTORY_DB_ADMIN_PASSWORD is not set in $EnvFile; local tools sign in as factory_admin." }
        return $access
    }
    if (-not $Container) {
        # All of its output, then the first line: Select-Object -First would stop docker early and fail it.
        $Container = @(& (Resolve-Docker) compose -f $ComposeFile --env-file $EnvFile ps -q $Service)[0]
        if ($LASTEXITCODE -ne 0 -or -not $Container) { throw "The '$Service' container of $ComposeFile is not running. Start it, or name local tools with -PgBin." }
    }
    $access.Container = $Container.Trim()
    $access.Docker = Resolve-Docker
    return $access
}

# Runs a PostgreSQL client tool (pg_dump, pg_restore, psql) as factory_admin; throws when it fails.
function Invoke-Pg([hashtable]$Access, [string]$Tool, [string[]]$Arguments) {
    if ($Access.Container) {
        & $Access.Docker exec $Access.Container $Tool -U $Access.User @Arguments
    } else {
        $previous = $env:PGPASSWORD
        $env:PGPASSWORD = $Access.Password
        try { & (Join-Path $Access.PgBin $Tool) -h $Access.Host -p $Access.Port -U $Access.User @Arguments }
        finally { $env:PGPASSWORD = $previous }
    }
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed (exit code $LASTEXITCODE)." }
}

# A path a PostgreSQL tool can read or write: inside the container, or the local path itself.
function Get-PgPath([hashtable]$Access, [string]$LocalPath) {
    if ($Access.Container) { return '/tmp/' + [IO.Path]::GetFileName($LocalPath) }
    return $LocalPath
}

# Signs in to the factory host as an Admin; returns the web session for later calls.
function Connect-Factory([string]$Url, [pscredential]$Credential) {
    $body = @{ userName = $Credential.UserName; password = $Credential.GetNetworkCredential().Password } | ConvertTo-Json
    $me = Invoke-RestMethod "$Url/api/auth/login" -Method Post -Body $body -ContentType 'application/json' `
        -Headers @{ 'X-Factory-Request' = '1' } -SessionVariable session
    if ($me.roles -notcontains 'Admin') { throw "$($Credential.UserName) is not an Admin." }
    return $session
}

function Invoke-Factory($Session, [string]$Url, [string]$Method, [string]$Path, $Body) {
    $call = @{ Uri = "$Url$Path"; Method = $Method; WebSession = $Session; Headers = @{ 'X-Factory-Request' = '1' } }
    if ($null -ne $Body) { $call.Body = ($Body | ConvertTo-Json); $call.ContentType = 'application/json' }
    return Invoke-RestMethod @call
}
