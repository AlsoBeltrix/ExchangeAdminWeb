#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Build and deploy ExchangeAdminWeb.

.DESCRIPTION
    -Dev   Build from source and deploy to the dev IIS site.
    -Prod  Build from source, deploy to dev, then promote to prod.
           Prod always goes through dev first. Backup taken before overwrite.

    Config fragments (module-config.json, sectionaccess.json, etc.) are
    never overwritten in prod unless you pass -CopyConfigFragments.

.EXAMPLE
    .\deploy-pipeline.ps1 -Dev

.EXAMPLE
    .\deploy-pipeline.ps1 -Prod
#>

[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$Prod,

    [string]$DevPath       = "D:\inetpub\ExchangeAdminWebDev",
    [string]$ProdPath      = "D:\inetpub\ExchangeAdminWeb",
    [string]$DevAppPool    = "ExchangeAdminWebDev",
    [string]$ProdAppPool   = "ExchangeAdminWeb",
    [string]$DevPathBase   = "/ExchangeAdminWebDev",
    [string]$ProdPathBase  = "/ExchangeAdminWeb",
    [string]$LogRoot       = "E:\WWWOutput",
    [string]$BackupRoot    = "D:\backups\ExchangeAdminWeb",
    [int]$BackupRetention  = 3,

    [switch]$CopyConfigFragments,
    [switch]$CopyAppSettings
)

$ErrorActionPreference = "Stop"

function Write-Step    { param($m) Write-Host ">>> $m" -ForegroundColor Cyan }
function Write-Ok      { param($m) Write-Host " OK  $m" -ForegroundColor Green }
function Write-Warn    { param($m) Write-Host "  !  $m" -ForegroundColor Yellow }

if (-not $Dev -and -not $Prod) { throw "Specify -Dev or -Prod." }
if ($Dev -and $Prod) { throw "Specify -Dev or -Prod, not both." }

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$deployScript = Join-Path $repoRoot "deploy.ps1"
if (-not (Test-Path -LiteralPath $deployScript -PathType Leaf)) {
    throw "deploy.ps1 not found at: $deployScript"
}
Import-Module WebAdministration -ErrorAction Stop

# --- Build and deploy to dev (always runs) ---

Write-Host ""
Write-Host "=== Build and deploy to dev ===" -ForegroundColor Magenta
Write-Host ""

& $deployScript `
    -ParentSite "Default Web Site" `
    -AppAlias "ExchangeAdminWebDev" `
    -AppPoolName $DevAppPool `
    -PublishPath $DevPath `
    -PathBase $DevPathBase `
    -LogRoot $LogRoot `
    -NonInteractive

if ($LASTEXITCODE -ge 8) {
    throw "Dev deployment failed with exit code $LASTEXITCODE"
}
Write-Ok "Dev deployment complete at $DevPath"

if (-not $Prod) {
    Write-Host ""
    Write-Host "Done. Validate: https://<server>$DevPathBase" -ForegroundColor Green
    return
}

# --- Promote dev to prod ---

Write-Host ""
Write-Host "=== Promote dev to prod ===" -ForegroundColor Magenta
Write-Host ""

$promoteScript = Join-Path $PSScriptRoot "promote-dev-to-prod.ps1"
if (-not (Test-Path -LiteralPath $promoteScript -PathType Leaf)) {
    throw "promote-dev-to-prod.ps1 not found at: $promoteScript"
}

$promoteArgs = @{
    DevPath              = $DevPath
    ProdPath             = $ProdPath
    ProdAppPoolName      = $ProdAppPool
    ProdPathBase         = $ProdPathBase
    BackupRoot           = $BackupRoot
    BackupRetention      = $BackupRetention
    Apply                = $true
    IUnderstandThisOverwritesProd = $true
}

if ($CopyConfigFragments) {
    # Config fragments now merge by default (new dev keys added,
    # existing prod values preserved). This flag is kept for
    # backward compatibility but the default behavior is safe.
}
if ($CopyAppSettings) {
    $promoteArgs["CopyAppSettings"] = $true
}

& $promoteScript @promoteArgs

if ($LASTEXITCODE -ge 8) {
    throw "Prod promotion failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Ok "Prod deployment complete at $ProdPath"
Write-Host "Validate: https://<server>$ProdPathBase" -ForegroundColor Cyan
Write-Host "Config fragments were merged: new dev keys added, existing prod values preserved." -ForegroundColor DarkGray
