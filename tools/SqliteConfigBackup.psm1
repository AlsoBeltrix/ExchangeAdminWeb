<#
.SYNOPSIS
    Consistent backup + integrity verification for the SQLite runtime config DB.

.DESCRIPTION
    Shared by deploy.ps1, promote-dev-to-prod.ps1 and Move-ConfigDbToShared.ps1
    (SqliteConfigStore-Plan Phase D; SharedConfigDb-Plan). The config DB is a live SQLite
    database; a raw file copy of a live WAL database can be torn/inconsistent, so a pre-deploy
    "backup" that just copies the file could produce a rollback snapshot that is itself corrupt.

    WHERE THE DATABASE LIVES (SharedConfigDb-Plan AC5, decision 2026-09-04): when an instance's
    appsettings.json names ConfigStore:Path, that is the database - one file shared by the dev
    and prod instances on this server. Without the key, the database is the instance's own
    <PublishPath>\config\exchangeadmin.db. Resolve-ConfigDbPath does that lookup; every
    function here takes an explicit -DbPath so callers never re-derive it.

    DEPENDENCY: sqlite3.exe must be on PATH (declared in docs/AdminModuleDeveloperGuide.md /
    deployment docs - install via `winget install SQLite.SQLite`). Backups use a true online
    backup ('VACUUM INTO') followed by 'PRAGMA integrity_check', which works even while the app
    is running. If sqlite3.exe is absent the backup THROWS rather than silently degrading to an
    unverified file copy - a deploy must not proceed without a verifiable rollback snapshot.

    On an integrity-check FAILURE the function THROWS (owner decision 2026-06-18: abort the
    deploy rather than continue over a corrupt store with a worthless rollback snapshot).

    This module is imported by deploy.ps1 under Windows PowerShell 5.1: keep it pure ASCII and
    5.1-compatible (no ternary, no null-coalescing, no PS7-only cmdlet parameters).
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

<#
.SYNOPSIS
    Returns the ConfigStore:Path value from <PublishPath>\appsettings.json, or $null when the
    file or the key is absent or blank.
.DESCRIPTION
    Callers use this to tell "the key is set" (the named file MUST exist - a missing shared
    database is a deploy-stopping error, never something to create or skip) from "no key" (the
    per-instance default, where a missing database just means a first start has not happened).
    Strict-mode safe: never dereferences a property that is not there.
#>
function Get-ConfigStorePathSetting {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublishPath)

    $appsettings = Join-Path $PublishPath 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appsettings -PathType Leaf)) {
        return $null
    }

    $json = Get-Content -LiteralPath $appsettings -Raw | ConvertFrom-Json
    if ($null -eq $json) { return $null }

    $section = $json.PSObject.Properties['ConfigStore']
    if ($null -eq $section -or $null -eq $section.Value) { return $null }

    $pathProperty = $section.Value.PSObject.Properties['Path']
    if ($null -eq $pathProperty) { return $null }

    $value = [string]$pathProperty.Value
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

<#
.SYNOPSIS
    The config database an instance opens: ConfigStore:Path from its appsettings.json when set,
    else <PublishPath>\config\exchangeadmin.db.
#>
function Resolve-ConfigDbPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublishPath)

    $configured = Get-ConfigStorePathSetting -PublishPath $PublishPath
    if ($configured) { return $configured }
    return (Join-Path (Join-Path $PublishPath 'config') 'exchangeadmin.db')
}

function Resolve-DbPathArgument {
    # Shared by the functions that accept either -DbPath or the older -ConfigDir form.
    param([string]$DbPath, [string]$ConfigDir)

    if ($DbPath) { return $DbPath }
    if ($ConfigDir) { return (Join-Path $ConfigDir 'exchangeadmin.db') }
    throw "Specify -DbPath (the database file) or -ConfigDir (a directory containing exchangeadmin.db)."
}

function Test-IsSqliteConfigDbPresent {
    param(
        [string]$ConfigDir,
        [string]$DbPath
    )
    $path = Resolve-DbPathArgument -DbPath $DbPath -ConfigDir $ConfigDir
    return (Test-Path -LiteralPath $path -PathType Leaf)
}

<#
.SYNOPSIS
    Backs up the config DB to $DestDir via a verified online backup.
.PARAMETER DbPath
    The database file to back up (the resolved shared or per-instance path). Preferred.
.PARAMETER ConfigDir
    Older form: the runtime config directory containing exchangeadmin.db.
.PARAMETER DestDir
    Directory to write the backup into (created if missing).
.PARAMETER Timestamp
    Caller's deploy timestamp, used in the backup file name.
.OUTPUTS
    The path to the backup .db file, or $null if there was no DB to back up. Whether a missing
    file is acceptable is the CALLER's decision (no key: nothing to back up yet; key set: stop).
#>
function Backup-SqliteConfigDb {
    [CmdletBinding()]
    param(
        [string]$ConfigDir,
        [string]$DbPath,
        [Parameter(Mandatory)][string]$DestDir,
        [Parameter(Mandatory)][string]$Timestamp
    )

    $dbPath = Resolve-DbPathArgument -DbPath $DbPath -ConfigDir $ConfigDir
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
        throw "Config DB integrity check FAILED: '$integrity'. Aborting before any changes - the live config DB at $dbPath may be corrupt. Investigate before deploying."
    }

    return $backupPath
}

<#
.SYNOPSIS
    Writes a consistent, integrity-verified copy of one SQLite database file to another path.

.DESCRIPTION
    Used by the one-time cutover (Move-ConfigDbToShared.ps1) to seed the shared database from
    dev's, and by an operator returning to per-instance databases. Promotion no longer copies
    the config database (decision 2026-09-04).

    Uses 'VACUUM INTO' to write a fresh consistent snapshot of the SOURCE onto a temp file next
    to the destination, integrity-checks it, then moves it into place. Any WAL/SHM sidecars of a
    destination being replaced are removed (the fresh DB has no pending WAL). The CALLER is
    responsible for backing up an existing destination first and for stopping any process that
    has it open.

.PARAMETER SourceDbPath
    The database file to copy.
.PARAMETER DestDbPath
    The file to write. Its directory is created if missing.
.OUTPUTS
    The destination path on success. Throws if the source is absent or the copy fails integrity.
#>
function Copy-SqliteDbFile {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$SourceDbPath,
        [Parameter(Mandatory)][string]$DestDbPath
    )

    if (-not (Test-Path -LiteralPath $SourceDbPath -PathType Leaf)) {
        throw "Source database not found at $SourceDbPath - cannot copy it to $DestDbPath."
    }

    $sqlite3 = Assert-Sqlite3Available

    $destDir = Split-Path -Parent $DestDbPath
    if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    if (-not $PSCmdlet.ShouldProcess($DestDbPath, "Replace with consistent copy of $SourceDbPath")) {
        return $DestDbPath
    }

    $tmpDb = Join-Path $destDir ("exchangeadmin.copy.{0}.db" -f ([guid]::NewGuid().ToString('N')))
    try {
        $escaped = $tmpDb -replace "'", "''"
        & $sqlite3 $SourceDbPath "VACUUM INTO '$escaped'"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 VACUUM INTO failed (exit $LASTEXITCODE) copying $SourceDbPath"
        }

        $integrity = (& $sqlite3 $tmpDb 'PRAGMA integrity_check;') 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 integrity_check failed to run (exit $LASTEXITCODE) on the copy"
        }
        if ("$integrity".Trim() -ne 'ok') {
            throw "Copied database failed integrity check: '$integrity'. Destination NOT changed."
        }

        foreach ($suffix in '-wal', '-shm') {
            $side = "$DestDbPath$suffix"
            if (Test-Path -LiteralPath $side -PathType Leaf) { Remove-Item -LiteralPath $side -Force }
        }

        Move-Item -LiteralPath $tmpDb -Destination $DestDbPath -Force
        return $DestDbPath
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

Export-ModuleMember -Function Get-Sqlite3Path, Assert-Sqlite3Available, Get-ConfigStorePathSetting, Resolve-ConfigDbPath, Test-IsSqliteConfigDbPresent, Backup-SqliteConfigDb, Copy-SqliteDbFile, Test-SqliteConfigDbIntegrity
