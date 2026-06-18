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
    Replaces the destination config DB with a consistent, integrity-verified copy of the source
    config DB (dev -> prod wholesale promotion; SqliteConfigStore-Plan Phase D2).

.DESCRIPTION
    dev is staging for prod and the two run the same code version after a promotion, so prod's
    config should mirror dev's exactly — a wholesale replace, not a per-table merge (owner
    decision 2026-06-18; any prod-only key is either dead under the new code or a dev
    misconfiguration to fix in dev, so nothing of prod's is worth preserving).

    Uses 'VACUUM INTO' to write a fresh consistent snapshot of the SOURCE directly onto the
    destination path, so a live/WAL source cannot produce a torn copy. The destination's WAL/SHM
    sidecars are removed (the fresh DB has no pending WAL), and the result is integrity-checked.
    The CALLER is responsible for backing up the destination first and for stopping the
    destination app pool before calling this.

.PARAMETER SourceConfigDir
    Config directory containing the source (dev) exchangeadmin.db.
.PARAMETER DestConfigDir
    Config directory whose exchangeadmin.db will be replaced (prod).
.OUTPUTS
    The destination DB path on success. Throws if the source DB is absent or fails integrity.
#>
function Copy-SqliteConfigDb {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$SourceConfigDir,
        [Parameter(Mandatory)][string]$DestConfigDir
    )

    $sourceDb = Join-Path $SourceConfigDir 'exchangeadmin.db'
    if (-not (Test-Path -LiteralPath $sourceDb -PathType Leaf)) {
        throw "Source config DB not found at $sourceDb — cannot promote config to $DestConfigDir."
    }

    $sqlite3 = Assert-Sqlite3Available

    if (-not (Test-Path -LiteralPath $DestConfigDir)) {
        New-Item -ItemType Directory -Path $DestConfigDir -Force | Out-Null
    }
    $destDb = Join-Path $DestConfigDir 'exchangeadmin.db'

    if (-not $PSCmdlet.ShouldProcess($destDb, "Replace with consistent copy of $sourceDb")) {
        return $destDb
    }

    # Write a fresh consistent snapshot of the source onto a temp path, integrity-check it, then
    # atomically swap it into place and drop the destination's stale WAL/SHM sidecars.
    $tmpDb = Join-Path $DestConfigDir ("exchangeadmin.promote.{0}.db" -f ([guid]::NewGuid().ToString('N')))
    try {
        $escaped = $tmpDb -replace "'", "''"
        & $sqlite3 $sourceDb "VACUUM INTO '$escaped'"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 VACUUM INTO failed (exit $LASTEXITCODE) copying $sourceDb"
        }

        $integrity = (& $sqlite3 $tmpDb 'PRAGMA integrity_check;') 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 integrity_check failed to run (exit $LASTEXITCODE) on the promoted copy"
        }
        if ("$integrity".Trim() -ne 'ok') {
            throw "Promoted config DB failed integrity check: '$integrity'. Prod NOT changed."
        }

        # Remove the destination's old WAL/SHM (they belong to the DB being replaced); the fresh
        # VACUUM INTO output is a self-contained DB with no pending WAL.
        foreach ($suffix in '-wal', '-shm') {
            $side = "$destDb$suffix"
            if (Test-Path -LiteralPath $side -PathType Leaf) { Remove-Item -LiteralPath $side -Force }
        }

        Move-Item -LiteralPath $tmpDb -Destination $destDb -Force
        return $destDb
    } finally {
        if (Test-Path -LiteralPath $tmpDb -PathType Leaf) {
            Remove-Item -LiteralPath $tmpDb -Force -ErrorAction SilentlyContinue
        }
    }
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

Export-ModuleMember -Function Get-Sqlite3Path, Assert-Sqlite3Available, Test-IsSqliteConfigDbPresent, Backup-SqliteConfigDb, Copy-SqliteConfigDb, Test-SqliteConfigDbIntegrity
