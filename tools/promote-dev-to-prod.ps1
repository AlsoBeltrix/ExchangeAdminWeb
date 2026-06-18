[CmdletBinding()]
param(
    [string]$DevPath = "D:\inetpub\ExchangeAdminWebDev",
    [string]$ProdPath = "D:\inetpub\ExchangeAdminWeb",
    [string]$ProdAppPoolName = "ExchangeAdminWeb",
    [string]$ProdPathBase = "/ExchangeAdminWeb",
    [string]$BackupRoot,
    [int]$BackupRetention = 3,
    [switch]$Apply,
    [switch]$IUnderstandThisOverwritesProd,
    [switch]$CopyAppSettings,
    [switch]$SkipConfigFragments
)

$ErrorActionPreference = "Stop"

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

function Merge-JsonConfig {
    param([string]$DevFile, [string]$ProdFile, [string]$Name, [switch]$DevWins)

    $devExists = Test-Path -LiteralPath $DevFile -PathType Leaf
    $prodExists = Test-Path -LiteralPath $ProdFile -PathType Leaf

    if (-not $devExists -and -not $prodExists) { return }

    if (-not $devExists) {
        if (-not $Apply) { Write-Plan "No dev $Name - prod keeps its version" }
        else { Write-Ok "$Name - no dev version, prod unchanged" }
        return
    }

    if (-not $prodExists) {
        Copy-FileChecked -Source $DevFile -Destination $ProdFile
        return
    }

    $mergeLabel = if ($DevWins) { "dev values promoted to prod" } else { "prod values preserved, new dev keys added" }

    if (-not $Apply) {
        Write-Plan "Merge $Name ($mergeLabel)"
        try {
            $devJson = Get-Content -LiteralPath $DevFile -Raw | ConvertFrom-Json
            $prodJson = Get-Content -LiteralPath $ProdFile -Raw | ConvertFrom-Json
            $changes = Compare-JsonKeys -Dev $devJson -Prod $prodJson -DevWins:$DevWins
            foreach ($change in $changes) {
                Write-Plan "  $change"
            }
        } catch {
            throw "Failed to preview merge $Name - dry run cannot verify config changes. Error: $_"
        }
        return
    }

    try {
        $devJson = Get-Content -LiteralPath $DevFile -Raw | ConvertFrom-Json
        $prodJson = Get-Content -LiteralPath $ProdFile -Raw | ConvertFrom-Json

        $merged = if ($DevWins) {
            Merge-Object -Base $devJson -Overlay $prodJson -OverlayWins $false
        } else {
            Merge-Object -Base $prodJson -Overlay $devJson -OverlayWins $false
        }
        $prodDir = Split-Path -Parent $ProdFile
        if (-not (Test-Path -LiteralPath $prodDir -PathType Container)) {
            New-Item -ItemType Directory -Path $prodDir -Force | Out-Null
        }
        $tmp = Join-Path $prodDir ("$Name.merge.{0}.tmp" -f [guid]::NewGuid().ToString("N"))
        try {
            $merged | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
            Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json | Out-Null
            Move-Item -LiteralPath $tmp -Destination $ProdFile -Force
            Write-Ok "Merged $Name ($mergeLabel)"
        } finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    } catch {
        throw "Failed to merge $Name - promotion cannot continue. Error: $_"
    }
}

function Merge-Object {
    param($Base, $Overlay, [bool]$OverlayWins = $false)

    if ($null -eq $Base) { return $Overlay }
    if ($null -eq $Overlay) { return $Base }

    if ($Base -is [PSCustomObject] -and $Overlay -is [PSCustomObject]) {
        $result = $Base.PSObject.Copy()
        foreach ($prop in $Overlay.PSObject.Properties) {
            $existing = $result.PSObject.Properties[$prop.Name]
            if ($null -eq $existing) {
                $result | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value
            } elseif ($existing.Value -is [PSCustomObject] -and $prop.Value -is [PSCustomObject]) {
                $existing.Value = Merge-Object -Base $existing.Value -Overlay $prop.Value -OverlayWins $OverlayWins
            } elseif ($OverlayWins) {
                $existing.Value = $prop.Value
            }
        }
        return $result
    }

    return $Base
}

function Format-ValueForDiff {
    param($Value)
    if ($null -eq $Value) { return "(null)" }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @($Value)
        if ($items.Count -eq 0) { return "[]" }

        $objectLabels = @()
        foreach ($item in $items) {
            if ($item -isnot [PSCustomObject]) {
                $objectLabels = @()
                break
            }

            $label = $null
            foreach ($propertyName in @('name', 'Name', 'id', 'Id', 'moduleId', 'ModuleId', 'policyAlias', 'PolicyAlias')) {
                $property = $item.PSObject.Properties[$propertyName]
                if ($property -and $property.Value) {
                    $label = "$($property.Value)"
                    break
                }
            }

            if (-not $label) {
                $objectLabels = @()
                break
            }
            $objectLabels += $label
        }

        if ($objectLabels.Count -eq $items.Count -and $items.Count -gt 3) {
            $preview = @($objectLabels | Select-Object -First 8)
            $suffix = if ($objectLabels.Count -gt 8) { ", ..." } else { "" }
            return "[$($items.Count) objects: $($preview -join ', ')$suffix]"
        }

        return (ConvertTo-Json -InputObject $items -Depth 8 -Compress)
    }
    if ($Value -is [PSCustomObject]) {
        return (ConvertTo-Json -InputObject $Value -Depth 8 -Compress)
    }
    return "$Value"
}

function Get-ValueFingerprint {
    param($Value)
    if ($null -eq $Value) { return "(null)" }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return (ConvertTo-Json -InputObject @($Value) -Depth 20 -Compress)
    }
    if ($Value -is [PSCustomObject]) {
        return (ConvertTo-Json -InputObject $Value -Depth 20 -Compress)
    }
    return "$Value"
}

function Compare-JsonKeys {
    param($Dev, $Prod, [switch]$DevWins, [string]$Prefix = "")

    $changes = @()
    if ($Dev -is [PSCustomObject] -and $Prod -is [PSCustomObject]) {
        foreach ($prop in $Dev.PSObject.Properties) {
            $key = if ($Prefix) { "$Prefix.$($prop.Name)" } else { $prop.Name }
            $prodProp = $Prod.PSObject.Properties[$prop.Name]
            if ($null -eq $prodProp) {
                $devDisplay = Format-ValueForDiff $prop.Value
                $changes += "+ $key = $devDisplay (new from dev)"
            } elseif ($prop.Value -is [PSCustomObject] -and $prodProp.Value -is [PSCustomObject]) {
                $changes += Compare-JsonKeys -Dev $prop.Value -Prod $prodProp.Value -DevWins:$DevWins -Prefix $key
            } elseif ($DevWins) {
                $devFingerprint = Get-ValueFingerprint $prop.Value
                $prodFingerprint = Get-ValueFingerprint $prodProp.Value
                $devDisplay = Format-ValueForDiff $prop.Value
                $prodDisplay = Format-ValueForDiff $prodProp.Value
                if ($devFingerprint -ne $prodFingerprint) {
                    $suffix = if ($devDisplay -eq $prodDisplay) { " (content changed)" } else { "" }
                    $changes += "~ $key : $prodDisplay -> $devDisplay$suffix"
                }
            }
        }
        foreach ($prop in $Prod.PSObject.Properties) {
            $key = if ($Prefix) { "$Prefix.$($prop.Name)" } else { $prop.Name }
            if ($null -eq $Dev.PSObject.Properties[$prop.Name]) {
                $changes += "  $key (prod-only, preserved)"
            }
        }
    }
    return $changes
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
    param([string]$Name)

    if (-not $Apply) {
        Write-Plan "Stop-WebAppPool -Name $Name"
        return
    }

    Write-Step "Stopping prod app pool: $Name"
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

Stop-AppPoolChecked -Name $ProdAppPoolName

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
        # TRANSITIONAL (SQLite Phase B): these fragments are being moved into the SQLite config
        # DB one at a time. modules-enabled.json moved in B.4 (app 2.3.16); after cutover it no
        # longer exists at runtime, so its merge below silently no-ops and enablement does NOT
        # promote until Phase D replaces these with module_enablement/section_access/etc. table
        # merges. sectionaccess/protected-principals/ad-editable-attributes follow in B.5-B.7.
        # See docs/SqliteConfigStore-Plan.md Phase D "Phase B promotion debt".
        $jsonConfigFiles = @(
            'sectionaccess.json',
            'modules-enabled.json',
            'protected-principals.json',
            'ad-editable-attributes.json',
            'ad-editable-attributes-legend.json'
        )

        # Promote per-module config files (dev values win - this is tested config)
        # TRANSITIONAL (SQLite Phase B.3): module-config-*.json (and the older single
        # module-config.json) were moved into the SQLite module_config table (app 2.3.15).
        # After cutover these files no longer exist at runtime, so this loop matches nothing and
        # per-module config does NOT promote until Phase D replaces it with a module_config
        # table merge. See docs/SqliteConfigStore-Plan.md Phase D "Phase B promotion debt".
        $devConfigDir = Join-Path $dev "config"
        $prodConfigDir = Join-Path $prod "config"
        foreach ($moduleFile in Get-ChildItem -Path $devConfigDir -Filter "module-config-*.json" -ErrorAction SilentlyContinue) {
            Merge-JsonConfig `
                -DevFile $moduleFile.FullName `
                -ProdFile (Join-Path $prodConfigDir $moduleFile.Name) `
                -Name $moduleFile.Name `
                -DevWins
        }

        # Promote operational config files (dev values win)
        foreach ($name in $jsonConfigFiles) {
            Merge-JsonConfig `
                -DevFile (Join-Path $dev "config\$name") `
                -ProdFile (Join-Path $prod "config\$name") `
                -Name $name `
                -DevWins
        }

        # TRANSITIONAL (SQLite Phase B.1): extended-log-level.txt was moved into the SQLite
        # app_setting table (app 2.3.13). After cutover this file no longer exists at runtime,
        # so this copy safely no-ops (Copy-FileChecked warns and skips a missing source). The
        # log level therefore does NOT promote until Phase D replaces this with a DB-row merge.
        # See docs/SqliteConfigStore-Plan.md Phase D "Phase B promotion debt".
        Copy-FileChecked -Source (Join-Path $dev "config\extended-log-level.txt") -Destination (Join-Path $prod "config\extended-log-level.txt")
    } else {
        Write-Warn "Skipping config fragment copy by request."
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
