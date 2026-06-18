<#
.SYNOPSIS
    Consistent backup + integrity verification for the SQLite runtime config DB.

.DESCRIPTION
    Shared by deploy.ps1 and promote-dev-to-prod.ps1 (SqliteConfigStore-Plan Phase D). The
    config DB (config/exchangeadmin.db) is a live SQLite database; a raw file copy of a live
    WAL database can be torn/inconsistent, so a pre-deploy "backup" that just copies the file
    could produce a rollback snapshot that is itself corrupt.

    DEPENDENCY: sqlite3.exe must be on PATH (declared in docs/AdminModuleDeveloperGuide.md /
    deployment docs — install via `winget install SQLite.SQLite`). Backups use a true online
    backup ('VACUUM INTO') followed by 'PRAGMA integrity_check', which works even while the app
    is running. If sqlite3.exe is absent the backup THROWS rather than silently degrading to an
    unverified file copy — a deploy must not proceed without a verifiable rollback snapshot.

    On an integrity-check FAILURE the function THROWS (owner decision 2026-06-18: abort the
    deploy rather than continue over a corrupt store with a worthless rollback snapshot).
#>

Set-StrictMode -Version Latest

function Get-Sqlite3Path {
    $cmd = Get-Command sqlite3 -CommandType Application -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Assert-Sqlite3Available {
    $sqlite3 = Get-Sqlite3Path
    if (-not $sqlite3) {
        throw "sqlite3.exe is not on PATH. It is a required deployment dependency for the SQLite config store (see deployment docs). Install it with: winget install SQLite.SQLite"
    }
    return $sqlite3
}

function Test-IsSqliteConfigDbPresent {
    param([Parameter(Mandatory)][string]$ConfigDir)
    return (Test-Path -LiteralPath (Join-Path $ConfigDir 'exchangeadmin.db') -PathType Leaf)
}

<#
.SYNOPSIS
    Backs up the config DB to $DestDir via a verified online backup.
.PARAMETER ConfigDir
    The runtime config directory containing exchangeadmin.db.
.PARAMETER DestDir
    Directory to write the backup into (created if missing).
.PARAMETER Timestamp
    Caller's deploy timestamp, used in the backup file name.
.OUTPUTS
    The path to the backup .db file, or $null if there was no DB to back up.
#>
function Backup-SqliteConfigDb {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$DestDir,
        [Parameter(Mandatory)][string]$Timestamp
    )

    $dbPath = Join-Path $ConfigDir 'exchangeadmin.db'
    if (-not (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
        return $null
    }

    $sqlite3 = Assert-Sqlite3Available

    if (-not (Test-Path -LiteralPath $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    $backupPath = Join-Path $DestDir "exchangeadmin.${Timestamp}.db"

    # Online backup: VACUUM INTO writes a fresh, consistent, defragmented copy even while the
    # source is in use. Single-quote the path for SQLite and double any embedded quotes.
    $escaped = $backupPath -replace "'", "''"
    & $sqlite3 $dbPath "VACUUM INTO '$escaped'"
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 VACUUM INTO failed (exit $LASTEXITCODE) backing up $dbPath"
    }

    $integrity = (& $sqlite3 $backupPath 'PRAGMA integrity_check;') 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 integrity_check failed to run (exit $LASTEXITCODE) on $backupPath"
    }
    if ("$integrity".Trim() -ne 'ok') {
        throw "Config DB integrity check FAILED: '$integrity'. Aborting before any changes — the live config DB at $dbPath may be corrupt. Investigate before deploying."
    }

    return $backupPath
}

<#
.SYNOPSIS
    Runs PRAGMA integrity_check against a DB; throws on a definitive fail or if sqlite3 missing.
#>
function Test-SqliteConfigDbIntegrity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$DbPath)

    if (-not (Test-Path -LiteralPath $DbPath -PathType Leaf)) {
        throw "Config DB not found at $DbPath"
    }

    $sqlite3 = Assert-Sqlite3Available
    $result = (& $sqlite3 $DbPath 'PRAGMA integrity_check;') 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 integrity_check failed to run (exit $LASTEXITCODE) on $DbPath"
    }
    if ("$result".Trim() -ne 'ok') {
        throw "Config DB integrity check FAILED on ${DbPath}: '$result'"
    }
    return $true
}

Export-ModuleMember -Function Get-Sqlite3Path, Assert-Sqlite3Available, Test-IsSqliteConfigDbPresent, Backup-SqliteConfigDb, Test-SqliteConfigDbIntegrity
