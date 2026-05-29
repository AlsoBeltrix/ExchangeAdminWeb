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
        $configFiles = @(
            'sectionaccess.json',
            'module-config.json',
            'modules-enabled.json',
            'extended-log-level.txt',
            'protected-principals.json',
            'ad-editable-attributes.json'
        )

        foreach ($name in $configFiles) {
            Copy-FileChecked -Source (Join-Path $dev "config\$name") -Destination (Join-Path $prod "config\$name")
        }
    } else {
        Write-Warn "Skipping config fragment copy by request."
    }
} finally {
    Start-AppPoolChecked -Name $ProdAppPoolName
}

Remove-OldBackups -BackupRootPath $backupRootResolved -Retention $BackupRetention

Write-Host ""
if ($Apply) {
    Write-Ok "Promotion complete. Backup: $backup"
    Write-Host "Validate: https://<server>$ProdPathBase" -ForegroundColor Cyan
} else {
    Write-Warn "No changes were made. Re-run with -Apply after reviewing the dry-run output."
}
