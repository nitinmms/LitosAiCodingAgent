# Shared by the Windows installer scripts (package-factory-msi.ps1, package-tray-msi.ps1).
# Dot-source it: . (Join-Path $PSScriptRoot "msi-common.ps1")

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

# Writes LICENSE.txt as RTF to $Path, for WixUILicenseRtf.
function New-LicenseRtf([string]$RepoRoot, [string]$Path) {
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $lines = Get-Content (Join-Path $RepoRoot "LICENSE.txt") -Encoding UTF8 | ForEach-Object { ConvertTo-RtfLine $_ }
    Set-Content $Path -Encoding ascii -Value ("{\rtf1\ansi\deff0{\fonttbl{\f0 Consolas;}}\f0\fs16`r`n" + ($lines -join "`r`n") + "`r`n}")
}

# An MSI's ProductVersion is three numbers; a major upgrade needs one of them to grow.
function Assert-MsiVersion([string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The version must be major.minor.patch, such as 0.1.0." }
}
