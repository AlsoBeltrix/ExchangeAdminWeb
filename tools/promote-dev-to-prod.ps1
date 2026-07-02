[CmdletBinding()]
param(
    [string]$DevPath = "D:\inetpub\ExchangeAdminWebDev",
    [string]$ProdPath = "D:\inetpub\ExchangeAdminWeb",
    [string]$ProdAppPoolName = "ExchangeAdminWeb",
    [string]$ProdPathBase = "/ExchangeAdminWeb",
    [string]$DevAppPoolName = "ExchangeAdminWebDev",
    [string]$BackupRoot,
    [int]$BackupRetention = 3,
    [switch]$Apply,
    [switch]$IUnderstandThisOverwritesProd,
    [switch]$CopyAppSettings,
    [switch]$SkipConfigFragments,
    [switch]$Refresh
)

$ErrorActionPreference = "Stop"

# Shared SQLite config-DB backup / promote / integrity helpers (SqliteConfigStore-Plan Phase D).
Import-Module (Join-Path $PSScriptRoot 'SqliteConfigBackup.psm1') -Force

# Shared warning about active bulk jobs before an app-pool recycle (BulkJobRunner-Plan).
Import-Module (Join-Path $PSScriptRoot 'JobStateWarning.psm1') -Force

function Write-Step { param([string]$Message) Write-Host ">>> $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host " OK  $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  !  $Message" -ForegroundColor Yellow }
function Write-Plan { param([string]$Message) Write-Host "DRY  $Message" -ForegroundColor DarkGray }

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-ExistingDirectory {
    param([string]$Path, [string]$Name)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Name does not exist or is not a directory: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\', '/')
}

function Assert-SafeDeploymentPath {
    param([string]$Path, [string]$Name)

    $root = [System.IO.Path]::GetPathRoot($Path).TrimEnd('\', '/')
    if ($Path.TrimEnd('\', '/') -eq $root) {
        throw "$Name resolves to a drive root, refusing to continue: $Path"
    }

    if ([string]::IsNullOrWhiteSpace((Split-Path -Leaf $Path))) {
        throw "$Name is not a safe application directory: $Path"
    }
}

function Assert-DevProdPaths {
    param([string]$Dev, [string]$Prod)

    if ($Dev -notmatch '(?i)dev') {
        throw "DevPath must clearly identify a dev deployment path. Refusing path without 'Dev': $Dev"
    }

    if ($Prod -match '(?i)dev') {
        throw "ProdPath appears to be a dev path. Refusing to promote into: $Prod"
    }
}

function Get-DirectorySizeBytes {
    param([string]$Path)

    $total = 0L
    Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
        ForEach-Object { $total += $_.Length }
    return $total
}

function Assert-BackupFreeSpace {
    param([string]$SourcePath, [string]$BackupRootPath)

    $required = Get-DirectorySizeBytes -Path $SourcePath
    $driveRoot = [System.IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $BackupRootPath).Path)
    $drive = Get-PSDrive -Name $driveRoot.TrimEnd('\').TrimEnd(':') -ErrorAction Stop
    $cushion = 500MB
    if ($drive.Free -lt ($required + $cushion)) {
        throw "Insufficient free space for backup. Required about $([math]::Round(($required + $cushion) / 1GB, 2)) GB, available $([math]::Round($drive.Free / 1GB, 2)) GB on $driveRoot"
    }
}

function Remove-OldBackups {
    param([string]$BackupRootPath, [int]$Retention)

    if ($Retention -lt 1) { return }
    if (-not (Test-Path -LiteralPath $BackupRootPath -PathType Container)) {
        if (-not $Apply) { Write-Plan "Skip backup retention cleanup because backup root does not exist yet: $BackupRootPath" }
        return
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $BackupRootPath).Path.TrimEnd('\', '/')
    $driveRoot = [System.IO.Path]::GetPathRoot($resolvedRoot).TrimEnd('\', '/')
    if ($resolvedRoot -eq $driveRoot) {
        throw "BackupRoot resolves to a drive root; refusing retention cleanup: $resolvedRoot"
    }

    $oldBackups = Get-ChildItem -LiteralPath $resolvedRoot -Directory -Filter "ExchangeAdminWeb.backup.*" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $Retention

    foreach ($old in $oldBackups) {
        if (-not $Apply) {
            Write-Plan "Remove-Item '$($old.FullName)' -Recurse -Force"
        } else {
            Remove-Item -LiteralPath $old.FullName -Recurse -Force
            Write-Ok "Removed old backup $($old.Name)"
        }
    }
}

function Invoke-RobocopyChecked {
    param([string[]]$RobocopyArgs, [string]$Description)

    if (-not $Apply) {
        Write-Plan "robocopy $($RobocopyArgs -join ' ')"
        return
    }

    Write-Step $Description
    & robocopy @RobocopyArgs
    $exit = $LASTEXITCODE
    if ($exit -ge 8) {
        throw "robocopy failed with exit code $exit during: $Description"
    }

    Write-Ok "$Description completed (robocopy exit $exit)"
}

function Copy-FileChecked {
    param([string]$Source, [string]$Destination)

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        Write-Warn "Source file not found, skipping: $Source"
        return
    }

    if (-not $Apply) {
        Write-Plan "Copy-Item '$Source' '$Destination' -Force"
        return
    }

    $destinationDir = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $destinationDir -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    Write-Ok "Copied $(Split-Path -Leaf $Source)"
}

function Set-AppsettingsPathBase {
    param([string]$AppSettingsPath, [string]$PathBase)

    if (-not (Test-Path -LiteralPath $AppSettingsPath -PathType Leaf)) {
        throw "appsettings.json was not found: $AppSettingsPath"
    }

    if (-not $Apply) {
        Write-Plan "Set Application:PathBase in $AppSettingsPath to $PathBase"
        return
    }

    $json = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
    if (-not (Get-Member -InputObject $json -Name Application -MemberType NoteProperty -ErrorAction SilentlyContinue)) {
        $json | Add-Member -NotePropertyName Application -NotePropertyValue ([pscustomobject]@{})
    }
    if (Get-Member -InputObject $json.Application -Name PathBase -MemberType NoteProperty -ErrorAction SilentlyContinue) {
        $json.Application.PathBase = $PathBase
    } else {
        $json.Application | Add-Member -NotePropertyName PathBase -NotePropertyValue $PathBase
    }

    $tmp = Join-Path (Split-Path -Parent $AppSettingsPath) ("appsettings.promote.{0}.tmp" -f [guid]::NewGuid().ToString("N"))
    try {
        $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
        Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json | Out-Null
        Move-Item -LiteralPath $tmp -Destination $AppSettingsPath -Force
        Write-Ok "Set Application:PathBase to $PathBase"
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Stop-AppPoolChecked {
    param(
        [string]$Name,
        [string]$ConfigDir
    )

    if (-not $Apply) {
        Write-Plan "Stop-WebAppPool -Name $Name"
        if ($ConfigDir) { Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir -PlanOnly | Out-Null }
        return
    }

    Write-Step "Stopping prod app pool: $Name"
    # Warn (do not block) if a durable bulk job is active — recycling interrupts it.
    if ($ConfigDir) { Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir | Out-Null }
    Stop-WebAppPool -Name $Name -ErrorAction Stop
    Start-Sleep -Seconds 3
}

function Start-AppPoolChecked {
    param([string]$Name)

    if (-not $Apply) {
        Write-Plan "Start-WebAppPool -Name $Name"
        return
    }

    Write-Step "Starting prod app pool: $Name"
    Start-WebAppPool -Name $Name
    Write-Ok "Prod app pool started"
}

$dev = Resolve-ExistingDirectory $DevPath "DevPath"
$prod = Resolve-ExistingDirectory $ProdPath "ProdPath"
Assert-SafeDeploymentPath $dev "DevPath"
Assert-SafeDeploymentPath $prod "ProdPath"
Assert-DevProdPaths -Dev $dev -Prod $prod

if ($dev.Equals($prod, [StringComparison]::OrdinalIgnoreCase)) {
    throw "DevPath and ProdPath resolve to the same directory. Refusing to continue."
}

# Prod-overwrite consent gates PROMOTION (dev->prod) only. -Refresh (prod->dev) never writes to
# prod, so it must NOT require this confirmation - it has its own elevation check below.
if ($Apply -and -not $Refresh -and -not $IUnderstandThisOverwritesProd) {
    throw "Apply mode requires -IUnderstandThisOverwritesProd to confirm this promotion overwrites the prod publish folder."
}

if (-not $BackupRoot) { $BackupRoot = "D:\backups\ExchangeAdminWeb" }
if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) {
    if ($Apply) { New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null }
    else { Write-Plan "New-Item -ItemType Directory '$BackupRoot'" }
}
$backupRootResolved = if (Test-Path -LiteralPath $BackupRoot) { (Resolve-Path -LiteralPath $BackupRoot).Path } else { $BackupRoot }
$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$backup = Join-Path $backupRootResolved ("ExchangeAdminWeb.backup.$timestamp")

# --- -Refresh: pull PROD config DB down into DEV (the reverse of promotion) -----------------
# The owner's "copy prod config to dev" operation (SqliteConfigStore-Plan section Phase D). It is a
# wholesale, verified copy of prod's config DB onto dev so dev reproduces prod's live config.
# It NEVER touches prod, and NEVER touches dev's appsettings.json / PathBase (those are
# per-environment identity). Backup-first, dev pool stopped during the swap.
if ($Refresh) {
    $devConfigDir = Join-Path $dev "config"
    $prodConfigDir = Join-Path $prod "config"

    Write-Host ""
    Write-Host "ExchangeAdminWeb prod-to-dev config refresh" -ForegroundColor Magenta
    Write-Host "  Source (prod): $prodConfigDir" -ForegroundColor DarkGray
    Write-Host "  Target (dev) : $devConfigDir" -ForegroundColor DarkGray
    Write-Host "  Dev app pool : $DevAppPoolName" -ForegroundColor DarkGray
    Write-Host "  Mode         : $(if ($Apply) { 'APPLY' } else { 'DRY RUN' })" -ForegroundColor DarkGray
    Write-Host ""

    if (-not (Test-IsSqliteConfigDbPresent -ConfigDir $prodConfigDir)) {
        throw "Prod has no config DB at $prodConfigDir\exchangeadmin.db - nothing to refresh into dev."
    }

    if (-not $Apply) {
        Write-Plan "Back up dev config DB (verified) into $backup"
        Write-Plan "Stop-WebAppPool -Name $DevAppPoolName"
        Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $devConfigDir -PlanOnly | Out-Null
        Write-Plan "Replace dev config DB with a consistent copy of $prodConfigDir\exchangeadmin.db (wholesale)"
        Write-Plan "Start-WebAppPool -Name $DevAppPoolName"
        Write-Warn "Dry run only. Re-run with -Apply to make changes. (Dev appsettings.json / PathBase are never touched.)"
        return
    }

    if (-not (Test-IsAdministrator)) { throw "Run this script from an elevated PowerShell session when using -Apply." }
    Import-Module WebAdministration -ErrorAction Stop

    # Back up dev's current DB first (verified online backup; throws/aborts on integrity failure).
    $devDbBackup = Backup-SqliteConfigDb -ConfigDir $devConfigDir -DestDir $backup -Timestamp $timestamp
    if ($devDbBackup) { Write-Ok "Dev config DB backed up (verified) to $devDbBackup" }

    Write-Step "Stopping dev app pool: $DevAppPoolName"
    # Warn (do not block) if a durable bulk job is active on dev — recycling interrupts it.
    Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $devConfigDir | Out-Null
    Stop-WebAppPool -Name $DevAppPoolName -ErrorAction Stop
    Start-Sleep -Seconds 3
    try {
        $refreshed = Copy-SqliteConfigDb -SourceConfigDir $prodConfigDir -DestConfigDir $devConfigDir
        Write-Ok "Refreshed dev config DB from prod (verified): $refreshed"
    } finally {
        Write-Step "Starting dev app pool: $DevAppPoolName"
        Start-WebAppPool -Name $DevAppPoolName
        Write-Ok "Dev app pool started"
    }

    Write-Host ""
    Write-Ok "Prod-to-dev config refresh complete. Dev now mirrors prod's config; appsettings.json/PathBase unchanged."
    return
}

Write-Host ""
Write-Host "ExchangeAdminWeb dev-to-prod promotion" -ForegroundColor Magenta
Write-Host "  Dev        : $dev" -ForegroundColor DarkGray
Write-Host "  Prod       : $prod" -ForegroundColor DarkGray
Write-Host "  Backup     : $backup" -ForegroundColor DarkGray
Write-Host "  App pool   : $ProdAppPoolName" -ForegroundColor DarkGray
Write-Host "  PathBase   : $ProdPathBase" -ForegroundColor DarkGray
Write-Host "  Mode       : $(if ($Apply) { 'APPLY' } else { 'DRY RUN' })" -ForegroundColor DarkGray
Write-Host ""

if (-not $Apply) {
    Write-Warn "Dry run only. Re-run with -Apply to make changes."
}

if ($Apply) {
    if (-not (Test-IsAdministrator)) { throw "Run this script from an elevated PowerShell session when using -Apply." }
    Import-Module WebAdministration -ErrorAction Stop
    Assert-BackupFreeSpace -SourcePath $prod -BackupRootPath $backupRootResolved
} else {
    Write-Plan "Import-Module WebAdministration"
    Write-Plan "Check free space in $backupRootResolved for prod backup"
}

$devAppSettings = Join-Path $dev "appsettings.json"
$prodAppSettings = Join-Path $prod "appsettings.json"
if (-not (Test-Path -LiteralPath $devAppSettings -PathType Leaf)) { throw "Dev appsettings.json not found: $devAppSettings" }
if (-not (Test-Path -LiteralPath $prodAppSettings -PathType Leaf) -and -not $CopyAppSettings) {
    throw "Prod appsettings.json not found. Use -CopyAppSettings only if dev appsettings is intended for prod after PathBase patching."
}

$devConfig = Get-Content -LiteralPath $devAppSettings -Raw | ConvertFrom-Json
if (($devConfig.Application.PathBase -as [string]) -ne "/ExchangeAdminWebDev") {
    Write-Warn "Dev Application:PathBase is '$($devConfig.Application.PathBase)', expected /ExchangeAdminWebDev. Continuing because prod will be patched to $ProdPathBase."
}

Invoke-RobocopyChecked -Description "Backing up prod publish folder" -RobocopyArgs @(
    $prod, $backup,
    '/MIR',
    '/XD', 'logs',
    '/NFL', '/NDL', '/NJH', '/NJS', '/R:2', '/W:1'
)

# The publish-folder backup above includes config/ via robocopy, but a robocopy of a LIVE WAL
# database can be torn/inconsistent - so additionally capture a verified online backup of prod's
# config DB (if it has one yet) into the same backup folder. No-op on a pre-SQLite prod (returns
# null). Throws/aborts if prod's live DB fails its integrity check (owner decision 2026-06-18).
if ($Apply) {
    $prodConfigDirForBackup = Join-Path $prod "config"
    $prodDbBackup = Backup-SqliteConfigDb -ConfigDir $prodConfigDirForBackup -DestDir $backup -Timestamp $timestamp
    if ($prodDbBackup) { Write-Ok "Prod config DB backed up (verified) to $prodDbBackup" }
} else {
    Write-Plan "Verified online backup of prod config DB (if present) into $backup"
}

Stop-AppPoolChecked -Name $ProdAppPoolName -ConfigDir (Join-Path $prod "config")

$promotionFailed = $false
$rolledBack = $false
try {
    Invoke-RobocopyChecked -Description "Promoting dev binaries to prod" -RobocopyArgs @(
        $dev, $prod,
        '/MIR',
        '/XF', 'appsettings*.json',
        '/XD', 'logs', 'config',
        '/NFL', '/NDL', '/NJH', '/NJS', '/R:2', '/W:1'
    )

    if ($CopyAppSettings) {
        Copy-FileChecked -Source $devAppSettings -Destination $prodAppSettings
    }

    Set-AppsettingsPathBase -AppSettingsPath $prodAppSettings -PathBase $ProdPathBase

    if (-not $SkipConfigFragments) {
        # Config promotion (SqliteConfigStore-Plan Phase D2): all runtime config now lives in the
        # single SQLite DB config/exchangeadmin.db. dev is staging for prod and both run the same
        # code version after this promotion, so prod's config should MIRROR dev's exactly - a
        # wholesale replace, not a per-key merge (owner decision 2026-06-18: any prod-only key is
        # either dead under the new code or a dev misconfiguration to fix in dev). Copy-SqliteConfigDb
        # writes a consistent, integrity-verified snapshot of dev's DB over prod's. Prod's prior DB
        # was backed up (verified) above; the prod pool is stopped at this point.
        $devConfigDir = Join-Path $dev "config"
        $prodConfigDir = Join-Path $prod "config"
        if (Test-IsSqliteConfigDbPresent -ConfigDir $devConfigDir) {
            if ($Apply) {
                $promoted = Copy-SqliteConfigDb -SourceConfigDir $devConfigDir -DestConfigDir $prodConfigDir
                Write-Ok "Promoted dev config DB to prod (verified): $promoted"
            } else {
                Write-Plan "Replace prod config DB with a consistent copy of $devConfigDir\exchangeadmin.db (wholesale)"
            }
        } elseif ($Apply) {
            # All runtime config lives in the DB now. A missing dev DB means promotion would ship
            # binaries with NO config promoted, leaving prod on stale/missing config - abort
            # rather than report success. (Triggers the rollback in the surrounding catch.)
            throw "Dev has no config DB at $devConfigDir\exchangeadmin.db - cannot promote config. Run -Dev with the current build first."
        } else {
            Write-Warn "Dev has no config DB at $devConfigDir\exchangeadmin.db - nothing to promote (dry run). Run -Dev with the current build first."
        }
    } else {
        Write-Warn "Skipping config promotion by request."
    }
} catch {
    $promotionFailed = $true
    Write-Host ""
    Write-Host "  X  Promotion FAILED: $_" -ForegroundColor Red
    Write-Host ""

    if ($Apply -and (Test-Path -LiteralPath $backup -PathType Container)) {
        Write-Step "Rolling back prod from backup: $backup"
        try {
            & robocopy $backup $prod '/MIR' '/XD' 'logs' '/NFL' '/NDL' '/NJH' '/NJS' '/R:2' '/W:1'
            if ($LASTEXITCODE -ge 8) {
                Write-Host "  X  Rollback robocopy failed with exit code $LASTEXITCODE - prod may be in an inconsistent state" -ForegroundColor Red
            } else {
                # The robocopy above restores the config/ tree, but its copy of the live DB may be
                # torn (WAL). If a VERIFIED DB backup was taken (Backup-SqliteConfigDb wrote
                # $backup\exchangeadmin.<timestamp>.db), overlay it onto prod's config DB and
                # integrity-check, so rollback restores a known-good DB rather than the raw copy.
                $verifiedDb = Join-Path $backup "exchangeadmin.${timestamp}.db"
                if (Test-Path -LiteralPath $verifiedDb -PathType Leaf) {
                    $prodDb = Join-Path $prod "config\exchangeadmin.db"
                    foreach ($suffix in '-wal', '-shm') {
                        $side = "$prodDb$suffix"
                        if (Test-Path -LiteralPath $side -PathType Leaf) { Remove-Item -LiteralPath $side -Force }
                    }
                    Copy-Item -LiteralPath $verifiedDb -Destination $prodDb -Force
                    Test-SqliteConfigDbIntegrity -DbPath $prodDb | Out-Null
                    Write-Ok "Restored verified config DB from $verifiedDb"
                }
                $rolledBack = $true
                Write-Ok "Rolled back prod to pre-promotion state"
            }
        } catch {
            Write-Host "  X  Rollback failed: $_ - restore manually from $backup" -ForegroundColor Red
        }
    } else {
        Write-Warn "No backup available for automatic rollback. Restore manually."
    }
} finally {
    Start-AppPoolChecked -Name $ProdAppPoolName
}

if ($promotionFailed) {
    if ($rolledBack) {
        throw "Promotion failed and was rolled back. Prod has been restored from backup."
    } else {
        throw "Promotion failed and automatic rollback did not complete. Prod may be in an inconsistent state - restore manually from $backup."
    }
}

Remove-OldBackups -BackupRootPath $backupRootResolved -Retention $BackupRetention

Write-Host ""
if ($Apply) {
    Write-Ok "Promotion complete. Backup: $backup"
    Write-Host "Validate: https://<server>$ProdPathBase" -ForegroundColor Cyan
} else {
    Write-Warn "No changes were made. Re-run with -Apply after reviewing the dry-run output."
}
