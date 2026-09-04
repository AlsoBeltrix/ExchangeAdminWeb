<#
.SYNOPSIS
    One-time cutover: make the dev and prod instances on this server share ONE config database.

.DESCRIPTION
    Implements the owner ruling of 2026-09-04 (docs/SharedConfigDb-Plan.md, .agents/decisions.md):
    both instances open the same exchangeadmin.db, promotion never copies it, and dev's current
    database becomes the shared one (prod's has only ever received copies of it).

    Plan mode is the DEFAULT: without -Apply the script prints every step and changes nothing.
    Run it elevated (it stops and starts IIS app pools) and with -PlanOnly first, then -Apply.

    THE ORDER MATTERS: both instances must already run a build that accepts a database newer
    than itself (app version 2.19.0 or later, SharedConfigDb-Plan S1). Deploy that build to dev
    and promote it to prod BEFORE running this with -Apply; the script refuses otherwise, by
    reading each instance's ExchangeAdminWeb.dll file version.

    Steps (each honours -PlanOnly):
      1. Assert both builds are at or above -MinimumAppVersion.
      2. Stop both app pools (warning about any active bulk job first).
      3. Take verified backups of BOTH current databases into -BackupRoot.
      4. VACUUM INTO dev's database to -SharedDbPath and integrity-check the result.
      5. Grant both app pool identities (OI)(CI)M on the shared directory.
      6. Write ConfigStore:Path into both appsettings.json files (atomic temp-file write).
      7. Start both pools.
      8. Verify each instance logged "Config store schema ready" once since the restart.

    Any failure after step 3 prints the exact commands that return the instances to two
    databases. A successful run prints them too (that is the rollback story from now on).

    Windows PowerShell 5.1 compatible (WebAdministration provider); pure ASCII.

.PARAMETER DevPublishPath
    Dev instance publish folder (holds appsettings.json, config\, ExchangeAdminWeb.dll).
.PARAMETER ProdPublishPath
    Prod instance publish folder.
.PARAMETER SharedDbPath
    Where the shared database will live. Must be an absolute LOCAL path (no UNC share).
.PARAMETER DevAppPoolName
    IIS app pool of the dev instance.
.PARAMETER ProdAppPoolName
    IIS app pool of the prod instance.
.PARAMETER BackupRoot
    Directory for the two pre-cutover verified backups.
.PARAMETER MinimumAppVersion
    The lowest ExchangeAdminWeb.dll file version that tolerates a newer database.
.PARAMETER PlanOnly
    Print every step; change nothing. Default unless -Apply is given.
.PARAMETER Apply
    Make the changes.

.EXAMPLE
    .\tools\Move-ConfigDbToShared.ps1 -PlanOnly

.EXAMPLE
    .\tools\Move-ConfigDbToShared.ps1 -Apply
#>

[CmdletBinding()]
param(
    [string]$DevPublishPath = "D:\inetpub\ExchangeAdminWebDev",
    [string]$ProdPublishPath = "D:\inetpub\ExchangeAdminWeb",
    [string]$SharedDbPath = "D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db",
    [string]$DevAppPoolName = "ExchangeAdminWebDev",
    [string]$ProdAppPoolName = "ExchangeAdminWeb",
    [string]$BackupRoot = "D:\backups\ExchangeAdminWeb\SharedConfigCutover",
    [version]$MinimumAppVersion = "2.19.0",
    [switch]$PlanOnly,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

# Plan mode is the default; -Apply is the only way to act. -PlanOnly is accepted explicitly so
# the invocation reads the same way as the other ops scripts.
if (-not $Apply) { $PlanOnly = $true }
if ($Apply -and $PSBoundParameters.ContainsKey('PlanOnly') -and $PlanOnly) {
    throw "Specify -PlanOnly or -Apply, not both."
}

Import-Module (Join-Path $PSScriptRoot 'SqliteConfigBackup.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'JobStateWarning.psm1') -Force

function Write-Step { param([string]$Message) Write-Host ">>> $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host " OK  $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  !  $Message" -ForegroundColor Yellow }
function Write-Plan { param([string]$Message) Write-Host "PLAN $Message" -ForegroundColor DarkGray }
function Write-Fail { param([string]$Message) Write-Host "  X  $Message" -ForegroundColor Red; throw $Message }

function Invoke-PlanOrAction {
    param([string]$Description, [scriptblock]$Action)

    if ($PlanOnly) {
        Write-Plan $Description
        return
    }

    Write-Step $Description
    & $Action
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-LocalAbsolutePath {
    param([string]$Path, [string]$Name)

    if ([string]::IsNullOrWhiteSpace($Path)) { Write-Fail "$Name is required." }
    if ($Path.StartsWith('\\') -or $Path.StartsWith('//')) {
        Write-Fail "$Name must be a local path, not a network share: $Path. SQLite locking is not reliable over a share and both instances run on this server."
    }
    # Fully qualified, not merely rooted: "D:file.db" is rooted (it names a drive) but
    # drive-relative, resolving against that drive's current directory (review finding scdi-2).
    # [IO.Path]::IsPathFullyQualified does not exist on .NET Framework (Windows PowerShell 5.1).
    if ($Path -notmatch '^[A-Za-z]:\\') {
        Write-Fail "$Name must be an absolute local path (drive letter, colon, backslash - for example D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db): $Path"
    }
}

function Get-AppFileVersion {
    param([string]$PublishPath)

    $dll = Join-Path $PublishPath 'ExchangeAdminWeb.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        Write-Fail "No ExchangeAdminWeb.dll in $PublishPath - is this an instance publish folder?"
    }
    $raw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-Fail "ExchangeAdminWeb.dll in $PublishPath carries no file version."
    }
    return [version]($raw.Split(' ')[0])
}

function Assert-BuildTolerates {
    # Step 1. A build older than -MinimumAppVersion refuses a database migrated by a newer one
    # (the pre-S1 migrator). Sharing the file before both builds tolerate it would stop the
    # older instance on the first migration.
    param([string]$Label, [string]$PublishPath)

    $actual = Get-AppFileVersion -PublishPath $PublishPath
    if ($actual -lt $MinimumAppVersion) {
        Write-Fail "$Label build is $actual, below the minimum $MinimumAppVersion that tolerates a newer database. Deploy the current build to dev, promote it to prod, then run this script again. Nothing was changed."
    }
    Write-Ok "$Label build $actual is at or above $MinimumAppVersion"
}

function Get-AppPoolIdentity {
    param([string]$Name)

    $pool = Get-Item "IIS:\AppPools\$Name"
    switch ([int]$pool.processModel.identityType) {
        3 { return $pool.processModel.userName }
        default { return "IIS AppPool\$Name" }
    }
}

function Stop-AppPoolChecked {
    param([string]$Name, [string]$ConfigDir)

    Invoke-PlanOrAction "Stop app pool $Name (warns about active bulk jobs first)" {
        Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir | Out-Null
        $state = (Get-ItemProperty "IIS:\AppPools\$Name").State
        if ($state -ne 'Stopped') {
            Stop-WebAppPool -Name $Name -ErrorAction Stop
            Start-Sleep -Seconds 3
        }
        Write-Ok "App pool $Name stopped"
    }
    if ($PlanOnly) { Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir -PlanOnly | Out-Null }
}

function Start-AppPoolChecked {
    param([string]$Name)

    Invoke-PlanOrAction "Start app pool $Name" {
        $started = $false
        for ($i = 1; $i -le 5; $i++) {
            try {
                $state = (Get-ItemProperty "IIS:\AppPools\$Name").State
                if ($state -eq 'Started') { $started = $true; break }
                Start-WebAppPool -Name $Name -ErrorAction Stop
                $started = $true
                break
            } catch {
                if ($i -eq 5) { throw }
                Start-Sleep -Seconds 3
            }
        }
        if (-not $started) { Write-Fail "App pool $Name did not start." }
        Write-Ok "App pool $Name started"
    }
}

function Set-ConfigStorePathInAppsettings {
    # Step 6. Same atomic temp-file pattern as promote-dev-to-prod.ps1 Set-AppsettingsPathBase:
    # serialize, re-parse the temp file, then move it into place. A backup of the original is
    # kept next to it so the key can be reverted by hand.
    param([string]$AppSettingsPath, [string]$Value, [string]$BackupSuffix)

    if (-not (Test-Path -LiteralPath $AppSettingsPath -PathType Leaf)) {
        Write-Fail "appsettings.json was not found: $AppSettingsPath"
    }

    Invoke-PlanOrAction "Set ConfigStore:Path = $Value in $AppSettingsPath (backup: appsettings.json.$BackupSuffix)" {
        Copy-Item -LiteralPath $AppSettingsPath -Destination "$AppSettingsPath.$BackupSuffix" -Force

        $json = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
        if (-not (Get-Member -InputObject $json -Name ConfigStore -MemberType NoteProperty -ErrorAction SilentlyContinue)) {
            $json | Add-Member -NotePropertyName ConfigStore -NotePropertyValue ([pscustomobject]@{})
        }
        if (Get-Member -InputObject $json.ConfigStore -Name Path -MemberType NoteProperty -ErrorAction SilentlyContinue) {
            $json.ConfigStore.Path = $Value
        } else {
            $json.ConfigStore | Add-Member -NotePropertyName Path -NotePropertyValue $Value
        }

        $tmp = Join-Path (Split-Path -Parent $AppSettingsPath) ("appsettings.cutover.{0}.tmp" -f [guid]::NewGuid().ToString("N"))
        try {
            $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
            $check = Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json
            if ($check.ConfigStore.Path -ne $Value) { Write-Fail "Re-read of $tmp did not carry ConfigStore:Path = $Value" }
            Move-Item -LiteralPath $tmp -Destination $AppSettingsPath -Force
            Write-Ok "ConfigStore:Path set in $AppSettingsPath"
        } finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-LatestSerilogLog {
    param([string]$PublishPath)

    $logDir = Join-Path $PublishPath 'logs'
    if (-not (Test-Path -LiteralPath $logDir -PathType Container)) { return $null }
    return Get-ChildItem -LiteralPath $logDir -Filter 'app-*.log' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Assert-SchemaReadyLogged {
    # Step 8. Each instance logs "Config store schema ready at version N" once per start
    # (Program.cs). One line since the restart proves the instance opened the shared file.
    param([string]$Label, [string]$PublishPath, [datetime]$Since)

    Invoke-PlanOrAction "Verify $Label logged 'Config store schema ready' once since restart (logs\app-*.log under $PublishPath)" {
        $deadline = (Get-Date).AddSeconds(90)
        $count = 0
        do {
            $log = Get-LatestSerilogLog -PublishPath $PublishPath
            if ($log -and $log.LastWriteTime -ge $Since) {
                $lines = Get-Content -LiteralPath $log.FullName -ErrorAction SilentlyContinue |
                    Where-Object { $_ -match 'Config store schema ready' }
                $count = @($lines | Where-Object {
                    $stamp = $null
                    if ($_ -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                        $stamp = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
                    }
                    ($null -ne $stamp) -and ($stamp -ge $Since.AddSeconds(-5))
                }).Count
            }
            if ($count -ge 1) { break }
            Start-Sleep -Seconds 5
        } while ((Get-Date) -lt $deadline)

        if ($count -lt 1) {
            Write-Fail "$Label did not log 'Config store schema ready' within 90 s of starting. Check $PublishPath\logs for the startup error (a missing or unreadable shared database is fatal by design)."
        }
        if ($count -gt 1) {
            Write-Warn "$Label logged 'Config store schema ready' $count times since the restart - more than one start happened; check the log."
        } else {
            Write-Ok "$Label logged 'Config store schema ready' once"
        }
    }
}

function Write-RestoreInstructions {
    param([string]$DevDb, [string]$ProdDb, [string]$DevBackup, [string]$ProdBackup, [string]$DevAppSettings, [string]$ProdAppSettings, [string]$Suffix)

    Write-Host ""
    Write-Host "To return to two databases (run elevated, both pools stopped):" -ForegroundColor Yellow
    Write-Host "  Stop-WebAppPool -Name $DevAppPoolName; Stop-WebAppPool -Name $ProdAppPoolName" -ForegroundColor DarkGray
    Write-Host "  Import-Module '$PSScriptRoot\SqliteConfigBackup.psm1'" -ForegroundColor DarkGray
    Write-Host "  Copy-SqliteDbFile -SourceDbPath '$SharedDbPath' -DestDbPath '$DevDb'    # or -SourceDbPath '$DevBackup'" -ForegroundColor DarkGray
    Write-Host "  Copy-SqliteDbFile -SourceDbPath '$SharedDbPath' -DestDbPath '$ProdDb'   # or -SourceDbPath '$ProdBackup'" -ForegroundColor DarkGray
    Write-Host "  Copy-Item '$DevAppSettings.$Suffix' '$DevAppSettings' -Force    # removes ConfigStore:Path" -ForegroundColor DarkGray
    Write-Host "  Copy-Item '$ProdAppSettings.$Suffix' '$ProdAppSettings' -Force  # removes ConfigStore:Path" -ForegroundColor DarkGray
    Write-Host "  Start-WebAppPool -Name $DevAppPoolName; Start-WebAppPool -Name $ProdAppPoolName" -ForegroundColor DarkGray
    Write-Host ""
}

function Assert-SharedDbUsable {
    # Review finding scdi-3. When both appsettings already name the shared path, "nothing to do"
    # is only true if the file they are configured to open exists and is sound: the app opens a
    # configured path WITHOUT create, so a deleted or never-created shared file stops BOTH pools
    # at their next start. That is the failure to report - in plan mode too - never a success.
    # Prints the same return-to-two-databases commands the cutover itself prints, pointing at
    # the newest backups this script left behind (placeholders when none are found).
    param([string]$Path, [string]$DevConfigDir, [string]$ProdConfigDir, [string]$DevAppSettings, [string]$ProdAppSettings)

    $problem = $null
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $problem = "no file exists there"
    } else {
        try {
            Test-SqliteConfigDbIntegrity -DbPath $Path | Out-Null
        } catch {
            $problem = "it failed PRAGMA integrity_check: $($_.Exception.Message)"
        }
    }
    if (-not $problem) { return }

    $devBackup = Get-ChildItem -LiteralPath $BackupRoot -Recurse -File -Filter 'dev.exchangeadmin.*.db' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $prodBackup = Get-ChildItem -LiteralPath $BackupRoot -Recurse -File -Filter 'prod.exchangeadmin.*.db' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $appsettingsBackup = Get-ChildItem -LiteralPath (Split-Path -Parent $DevAppSettings) -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'appsettings.json.pre-shared.*.bak' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $suffix = if ($appsettingsBackup) { $appsettingsBackup.Name.Substring('appsettings.json.'.Length) } else { 'pre-shared.<timestamp>.bak' }

    Write-Host ""
    Write-Host "  X  Both instances name $Path in ConfigStore:Path but $problem. Both app pools will refuse to start on it (the configured path is opened without create)." -ForegroundColor Red
    Write-Host "     Restore the shared file from the newest verified backup under $BackupRoot (Copy-SqliteDbFile -SourceDbPath <backup> -DestDbPath '$Path'), or return both instances to two databases:" -ForegroundColor Yellow
    Write-RestoreInstructions -DevDb (Join-Path $DevConfigDir 'exchangeadmin.db') -ProdDb (Join-Path $ProdConfigDir 'exchangeadmin.db') `
        -DevBackup $(if ($devBackup) { $devBackup.FullName } else { Join-Path $BackupRoot 'cutover.<timestamp>\dev.exchangeadmin.<timestamp>.db' }) `
        -ProdBackup $(if ($prodBackup) { $prodBackup.FullName } else { Join-Path $BackupRoot 'cutover.<timestamp>\prod.exchangeadmin.<timestamp>.db' }) `
        -DevAppSettings $DevAppSettings -ProdAppSettings $ProdAppSettings -Suffix $suffix
    Write-Fail "Both instances name $Path in ConfigStore:Path but $problem. Nothing was changed; see the restore commands above."
}

# --- Preconditions (read-only) --------------------------------------------------------------

Assert-LocalAbsolutePath -Path $SharedDbPath -Name 'SharedDbPath'
foreach ($p in @(@{ N = 'DevPublishPath'; V = $DevPublishPath }, @{ N = 'ProdPublishPath'; V = $ProdPublishPath })) {
    if (-not (Test-Path -LiteralPath $p.V -PathType Container)) { Write-Fail "$($p.N) does not exist or is not a directory: $($p.V)" }
}
if ($DevPublishPath.TrimEnd('\', '/') -ieq $ProdPublishPath.TrimEnd('\', '/')) {
    Write-Fail "DevPublishPath and ProdPublishPath are the same directory."
}

$devAppSettings = Join-Path $DevPublishPath 'appsettings.json'
$prodAppSettings = Join-Path $ProdPublishPath 'appsettings.json'
$devConfigDir = Join-Path $DevPublishPath 'config'
$prodConfigDir = Join-Path $ProdPublishPath 'config'
$devDb = Resolve-ConfigDbPath -PublishPath $DevPublishPath
$prodDb = Resolve-ConfigDbPath -PublishPath $ProdPublishPath
$sharedDir = Split-Path -Parent $SharedDbPath
$timestamp = Get-Date -Format 'yyyyMMddHHmmss'
$backupDir = Join-Path $BackupRoot "cutover.$timestamp"
$devBackupFile = Join-Path $backupDir "dev.exchangeadmin.$timestamp.db"
$prodBackupFile = Join-Path $backupDir "prod.exchangeadmin.$timestamp.db"
$appsettingsBackupSuffix = "pre-shared.$timestamp.bak"

Write-Host ""
Write-Host "ExchangeAdminWeb shared config database cutover" -ForegroundColor Magenta
Write-Host "  Dev       : $DevPublishPath ($DevAppPoolName)  DB now: $devDb" -ForegroundColor DarkGray
Write-Host "  Prod      : $ProdPublishPath ($ProdAppPoolName)  DB now: $prodDb" -ForegroundColor DarkGray
Write-Host "  Shared DB : $SharedDbPath" -ForegroundColor DarkGray
Write-Host "  Backups   : $backupDir" -ForegroundColor DarkGray
Write-Host "  Min build : $MinimumAppVersion" -ForegroundColor DarkGray
Write-Host "  Mode      : $(if ($PlanOnly) { 'PLAN ONLY' } else { 'APPLY' })" -ForegroundColor DarkGray
Write-Host ""

if ($devDb -ieq $SharedDbPath -and $prodDb -ieq $SharedDbPath) {
    # The no-op shortcut sits BEHIND the invariant it claims (scdi-3): a missing or corrupt
    # configured file is a failure to report, in plan mode too, never "nothing to do".
    Assert-SharedDbUsable -Path $SharedDbPath -DevConfigDir $devConfigDir -ProdConfigDir $prodConfigDir -DevAppSettings $devAppSettings -ProdAppSettings $prodAppSettings
    Write-Ok "Both instances already name $SharedDbPath in ConfigStore:Path and the file passes PRAGMA integrity_check - nothing to do."
    return
}
if ($devDb -ieq $SharedDbPath -or $prodDb -ieq $SharedDbPath) {
    Write-Fail "One instance already points at $SharedDbPath and the other does not (split state). Finish or revert that by hand before running this script; see the restore commands in the script help."
}
if (-not (Test-Path -LiteralPath $devDb -PathType Leaf)) {
    Write-Fail "Dev has no config database at $devDb - there is nothing to make shared. Start dev once with the current build first."
}
if (Test-Path -LiteralPath $SharedDbPath -PathType Leaf) {
    Write-Fail "A file already exists at $SharedDbPath. This script only creates the shared database; remove or rename the existing file deliberately first."
}

# Step 1 - both builds tolerate a newer database (read-only, runs in plan mode too).
Assert-BuildTolerates -Label 'Dev' -PublishPath $DevPublishPath
Assert-BuildTolerates -Label 'Prod' -PublishPath $ProdPublishPath

if ($PlanOnly) {
    Write-Plan "Import-Module WebAdministration"
} else {
    if (-not (Test-IsAdministrator)) { Write-Fail "Run this script from an elevated PowerShell session when using -Apply." }
    Import-Module WebAdministration -ErrorAction Stop
    Assert-Sqlite3Available | Out-Null
}

# --- Steps 2-8 -------------------------------------------------------------------------------

$sharedWritten = $false
$appsettingsTouched = @()
$startedAt = Get-Date
try {
    # Step 2 - stop both pools.
    Stop-AppPoolChecked -Name $DevAppPoolName -ConfigDir $devConfigDir
    Stop-AppPoolChecked -Name $ProdAppPoolName -ConfigDir $prodConfigDir

    # Step 3 - verified backups of BOTH current databases.
    Invoke-PlanOrAction "Verified backup of dev DB $devDb to $devBackupFile" {
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $b = Backup-SqliteConfigDb -DbPath $devDb -DestDir $backupDir -Timestamp "dev.$timestamp"
        if (-not $b) { Write-Fail "Dev database vanished at $devDb" }
        Move-Item -LiteralPath $b -Destination $devBackupFile -Force
        Write-Ok "Dev DB backed up (verified) to $devBackupFile"
    }
    Invoke-PlanOrAction "Verified backup of prod DB $prodDb to $prodBackupFile (skipped if prod has no DB yet)" {
        if (Test-Path -LiteralPath $prodDb -PathType Leaf) {
            $b = Backup-SqliteConfigDb -DbPath $prodDb -DestDir $backupDir -Timestamp "prod.$timestamp"
            Move-Item -LiteralPath $b -Destination $prodBackupFile -Force
            Write-Ok "Prod DB backed up (verified) to $prodBackupFile"
        } else {
            Write-Warn "Prod has no config DB at $prodDb - nothing to back up there"
        }
    }

    # Step 4 - dev's database becomes the shared one.
    Invoke-PlanOrAction "VACUUM INTO dev DB $devDb -> $SharedDbPath, then PRAGMA integrity_check" {
        Copy-SqliteDbFile -SourceDbPath $devDb -DestDbPath $SharedDbPath | Out-Null
        Test-SqliteConfigDbIntegrity -DbPath $SharedDbPath | Out-Null
        Write-Ok "Shared database written and verified at $SharedDbPath"
    }
    if (-not $PlanOnly) { $sharedWritten = $true }

    # Step 5 - both pool identities get Modify on the shared directory (WAL/SHM need write).
    Invoke-PlanOrAction "Grant both app pool identities (OI)(CI)M on $sharedDir" {
        foreach ($pool in @($DevAppPoolName, $ProdAppPoolName)) {
            $identity = Get-AppPoolIdentity -Name $pool
            & icacls $sharedDir /grant "${identity}:(OI)(CI)M" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Fail "icacls failed (exit $LASTEXITCODE) granting $identity (OI)(CI)M on $sharedDir"
            }
            Write-Ok "Granted $identity (OI)(CI)M on $sharedDir"
        }
    }

    # Step 6 - both appsettings.json files name the shared file.
    Set-ConfigStorePathInAppsettings -AppSettingsPath $devAppSettings -Value $SharedDbPath -BackupSuffix $appsettingsBackupSuffix
    if (-not $PlanOnly) { $appsettingsTouched += 'dev' }
    Set-ConfigStorePathInAppsettings -AppSettingsPath $prodAppSettings -Value $SharedDbPath -BackupSuffix $appsettingsBackupSuffix
    if (-not $PlanOnly) { $appsettingsTouched += 'prod' }

    # Step 7 - start both pools.
    Start-AppPoolChecked -Name $DevAppPoolName
    Start-AppPoolChecked -Name $ProdAppPoolName

    # Step 8 - each instance opened the shared database.
    Assert-SchemaReadyLogged -Label 'Dev' -PublishPath $DevPublishPath -Since $startedAt
    Assert-SchemaReadyLogged -Label 'Prod' -PublishPath $ProdPublishPath -Since $startedAt
} catch {
    Write-Host ""
    Write-Host "  X  Cutover FAILED: $_" -ForegroundColor Red
    if ($sharedWritten -or $appsettingsTouched.Count -gt 0) {
        if ($appsettingsTouched.Count -eq 1) {
            Write-Warn "SPLIT STATE: the $($appsettingsTouched[0]) instance now names $SharedDbPath and the other still uses its own database. Revert with the commands below (the appsettings backups carry the '.$appsettingsBackupSuffix' suffix)."
        }
        Write-RestoreInstructions -DevDb $devDb -ProdDb $prodDb -DevBackup $devBackupFile -ProdBackup $prodBackupFile -DevAppSettings $devAppSettings -ProdAppSettings $prodAppSettings -Suffix $appsettingsBackupSuffix
    } else {
        Write-Warn "Nothing was changed: the original databases and appsettings files are untouched. App pools may be stopped - start them with Start-WebAppPool if so."
    }
    throw
}

Write-Host ""
if ($PlanOnly) {
    Write-Warn "Plan only - nothing was changed. Re-run with -Apply after reviewing the steps above."
} else {
    Write-Ok "Cutover complete. Both instances open $SharedDbPath. Pre-cutover backups: $backupDir"
    Write-Host "The per-instance databases at $devDb and $prodDb are no longer opened by the app; keep them until you are satisfied, then delete them so nobody mistakes them for live." -ForegroundColor DarkGray
}
Write-RestoreInstructions -DevDb $devDb -ProdDb $prodDb -DevBackup $devBackupFile -ProdBackup $prodBackupFile -DevAppSettings $devAppSettings -ProdAppSettings $prodAppSettings -Suffix $appsettingsBackupSuffix
