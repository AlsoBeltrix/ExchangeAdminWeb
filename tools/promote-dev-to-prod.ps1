[CmdletBinding()]
param(
    [string]$DevPath = "D:\inetpub\ExchangeAdminWebDev",
    [string]$ProdPath = "D:\inetpub\ExchangeAdminWeb",
    [string]$ProdAppPoolName = "ExchangeAdminWeb",
    [string]$ProdPathBase = "/ExchangeAdminWeb",
    [string]$ProdPublicBaseUrl,
    [string]$BackupRoot,
    [int]$BackupRetention = 3,
    [switch]$Apply,
    [switch]$IUnderstandThisOverwritesProd,
    [switch]$CopyAppSettings
)

$ErrorActionPreference = "Stop"

# Shared SQLite config-DB backup / integrity helpers (SqliteConfigStore-Plan Phase D). The
# config database itself is SHARED by dev and prod and is never copied by promotion
# (SharedConfigDb-Plan, decision 2026-09-04) - this script backs it up and otherwise leaves it.
Import-Module (Join-Path $PSScriptRoot 'SqliteConfigBackup.psm1') -Force

# Shared warning about active bulk jobs before an app-pool recycle (BulkJobRunner-Plan).
Import-Module (Join-Path $PSScriptRoot 'JobStateWarning.psm1') -Force

function Write-Step { param([string]$Message) Write-Host ">>> $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host " OK  $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  !  $Message" -ForegroundColor Yellow }
function Write-Plan { param([string]$Message) Write-Host "DRY  $Message" -ForegroundColor DarkGray }
function Write-Fail { param([string]$Message) Write-Host "  X  $Message" -ForegroundColor Red; throw $Message }

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
    # Patches the per-environment identity keys under Application. PublicBaseUrl belongs here for
    # the same reason PathBase does: promotion copies dev's appsettings.json, so without an explicit
    # patch prod would inherit dev's URL and email links would send operators to the dev instance.
    # An empty $PublicBaseUrl leaves any existing prod value untouched rather than blanking it.
    param([string]$AppSettingsPath, [string]$PathBase, [string]$PublicBaseUrl)

    if (-not (Test-Path -LiteralPath $AppSettingsPath -PathType Leaf)) {
        throw "appsettings.json was not found: $AppSettingsPath"
    }

    if (-not $Apply) {
        Write-Plan "Set Application:PathBase in $AppSettingsPath to $PathBase"
        if ($PublicBaseUrl) {
            Write-Plan "Set Application:PublicBaseUrl in $AppSettingsPath to $PublicBaseUrl"
        }
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
    if ($PublicBaseUrl) {
        if (Get-Member -InputObject $json.Application -Name PublicBaseUrl -MemberType NoteProperty -ErrorAction SilentlyContinue) {
            $json.Application.PublicBaseUrl = $PublicBaseUrl
        } else {
            $json.Application | Add-Member -NotePropertyName PublicBaseUrl -NotePropertyValue $PublicBaseUrl
        }
    }

    $tmp = Join-Path (Split-Path -Parent $AppSettingsPath) ("appsettings.promote.{0}.tmp" -f [guid]::NewGuid().ToString("N"))
    try {
        $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
        Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json | Out-Null
        Move-Item -LiteralPath $tmp -Destination $AppSettingsPath -Force
        Write-Ok "Set Application:PathBase to $PathBase"
        if ($PublicBaseUrl) {
            Write-Ok "Set Application:PublicBaseUrl to $PublicBaseUrl"
        }
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
    # Warn (do not block) if a durable bulk job is active - recycling interrupts it.
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

if ($Apply -and -not $IUnderstandThisOverwritesProd) {
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

Write-Host ""
Write-Host "ExchangeAdminWeb dev-to-prod promotion" -ForegroundColor Magenta
Write-Host "  Dev        : $dev" -ForegroundColor DarkGray
Write-Host "  Prod       : $prod" -ForegroundColor DarkGray
Write-Host "  Backup     : $backup" -ForegroundColor DarkGray
Write-Host "  App pool   : $ProdAppPoolName" -ForegroundColor DarkGray
Write-Host "  PathBase   : $ProdPathBase" -ForegroundColor DarkGray
Write-Host "  PublicUrl  : $(if ($ProdPublicBaseUrl) { $ProdPublicBaseUrl } else { '(unchanged)' })" -ForegroundColor DarkGray
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
# database can be torn/inconsistent - so additionally capture a verified online backup of the
# config DB prod opens into the same backup folder. That DB is the file named by prod's
# ConfigStore:Path when set (the database SHARED with dev - promotion never copies or replaces
# it, decision 2026-09-04), else prod's own config\exchangeadmin.db. With the key set the file
# MUST exist - a missing shared database stops the promotion here, before anything changes,
# because prod would not start against it (the app never creates a configured database). No-op
# on a pre-SQLite prod without the key (returns null). Throws/aborts if the live DB fails its
# integrity check (owner decision 2026-06-18).
$prodConfigStorePathSetting = Get-ConfigStorePathSetting -PublishPath $prod
$prodConfigDbPath = Resolve-ConfigDbPath -PublishPath $prod
if ($prodConfigStorePathSetting -and -not (Test-IsSqliteConfigDbPresent -DbPath $prodConfigDbPath)) {
    Write-Fail "Prod's ConfigStore:Path names '$prodConfigDbPath' but no database exists there. Prod will not start against a missing shared database (the app never creates one). Fix the key or restore the file before promoting. Nothing was changed."
}
if ($Apply) {
    $prodDbBackup = Backup-SqliteConfigDb -DbPath $prodConfigDbPath -DestDir $backup -Timestamp $timestamp
    if ($prodDbBackup) { Write-Ok "Config DB ($prodConfigDbPath) backed up (verified) to $prodDbBackup" }
} else {
    Write-Plan "Verified online backup of the config DB at $prodConfigDbPath (if present) into $backup"
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

    Set-AppsettingsPathBase -AppSettingsPath $prodAppSettings -PathBase $ProdPathBase -PublicBaseUrl $ProdPublicBaseUrl

    # No config step: the config database is shared by both instances and already holds
    # whatever dev saved (SharedConfigDb-Plan AC7). New tables reach prod through the shared
    # file - whichever build starts first migrates it and the other accepts it.
    if (-not $Apply) {
        Write-Plan "Config database at $prodConfigDbPath is shared with dev and is NOT copied or replaced by promotion"
    }
} catch {
    $promotionFailed = $true
    Write-Host ""
    Write-Host "  X  Promotion FAILED: $_" -ForegroundColor Red
    Write-Host ""

    if ($Apply -and (Test-Path -LiteralPath $backup -PathType Container)) {
        Write-Step "Rolling back prod binaries from backup: $backup"
        try {
            # Binaries only: config\ is excluded so the rollback never touches the per-instance
            # jobs database or a per-instance config DB, and the shared config database (outside
            # the publish folder) was never changed by this promotion. Restoring the verified DB
            # backup would roll back dev's live data too, so it is a deliberate manual act.
            & robocopy $backup $prod '/MIR' '/XD' 'logs' 'config' '/NFL' '/NDL' '/NJH' '/NJS' '/R:2' '/W:1'
            if ($LASTEXITCODE -ge 8) {
                Write-Host "  X  Rollback robocopy failed with exit code $LASTEXITCODE - prod may be in an inconsistent state" -ForegroundColor Red
            } else {
                $verifiedDb = Join-Path $backup "exchangeadmin.${timestamp}.db"
                Write-Warn "The config database ($prodConfigDbPath) was not changed by this promotion and is NOT restored by the rollback (it is shared with dev). A verified pre-promotion backup is at $verifiedDb should you decide to restore it by hand - stop BOTH app pools first."
                $rolledBack = $true
                Write-Ok "Rolled back prod binaries to pre-promotion state"
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
    Write-Host "The config database ($prodConfigDbPath) is shared with dev and was not changed." -ForegroundColor DarkGray
} else {
    Write-Warn "No changes were made. Re-run with -Apply after reviewing the dry-run output."
}
