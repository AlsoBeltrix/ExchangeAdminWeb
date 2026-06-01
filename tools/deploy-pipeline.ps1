#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Build, publish, and optionally promote ExchangeAdminWeb through dev to prod.

.DESCRIPTION
    Single deployment script that replaces the three-script workflow:
      publish_to_dev.ps1 → promote-dev-to-prod.ps1 → (manual prod config)

    Modes:
      -Dev        Build from source and deploy to the dev IIS site.
      -Prod       Promote the current dev deployment to prod (backup, copy, patch PathBase).
      -DevToProd  Do both: build to dev, then promote to prod.

    Only one mode flag at a time. Prod always backs up before overwriting.
    Config fragments (sectionaccess.json, module-config.json, etc.) are never
    overwritten in prod by default — use -CopyConfigFragments to include them.

.EXAMPLE
    .\deploy-pipeline.ps1 -Dev
    # Build from source and deploy to dev only.

.EXAMPLE
    .\deploy-pipeline.ps1 -Prod
    # Promote current dev site to prod (no rebuild).

.EXAMPLE
    .\deploy-pipeline.ps1 -DevToProd
    # Build to dev, then promote to prod in one step.
#>

[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$Prod,
    [switch]$DevToProd,

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

# --- Validation ---

$modeCount = @($Dev, $Prod, $DevToProd).Where({ $_ }).Count
if ($modeCount -eq 0) { throw "Specify one mode: -Dev, -Prod, or -DevToProd" }
if ($modeCount -gt 1) { throw "Specify only one mode at a time." }

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$deployScript = Join-Path $repoRoot "deploy.ps1"

if (-not (Test-Path -LiteralPath $deployScript -PathType Leaf)) {
    throw "deploy.ps1 not found at: $deployScript"
}

Import-Module WebAdministration -ErrorAction Stop

# --- Step 1: Build and deploy to dev ---

function Invoke-DeployToDev {
    Write-Host ""
    Write-Host "=== STAGE 1: Build and deploy to dev ===" -ForegroundColor Magenta
    Write-Host ""

    & $deployScript `
        -ParentSite "Default Web Site" `
        -AppAlias "ExchangeAdminWebDev" `
        -AppPoolName $DevAppPool `
        -PublishPath $DevPath `
        -PathBase $DevPathBase `
        -LogRoot $LogRoot `
        -NonInteractive

    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Dev deployment failed with exit code $LASTEXITCODE"
    }

    Write-Ok "Dev deployment complete at $DevPath"
}

# --- Step 2: Promote dev to prod ---

function Invoke-PromoteToProd {
    Write-Host ""
    Write-Host "=== STAGE 2: Promote dev to prod ===" -ForegroundColor Magenta
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
        # default behavior in promote script is to copy; pass nothing
    } else {
        $promoteArgs["SkipConfigFragments"] = $true
    }

    if ($CopyAppSettings) {
        $promoteArgs["CopyAppSettings"] = $true
    }

    & $promoteScript @promoteArgs

    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Prod promotion failed with exit code $LASTEXITCODE"
    }

    Write-Ok "Prod promotion complete at $ProdPath"
}

# --- Execute ---

if ($Dev -or $DevToProd) {
    Invoke-DeployToDev
}

if ($Prod -or $DevToProd) {
    Invoke-PromoteToProd
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
if ($Prod -or $DevToProd) {
    Write-Host "Validate prod: https://<server>$ProdPathBase" -ForegroundColor Cyan
    if (-not $CopyConfigFragments) {
        Write-Warn "Config fragments were NOT copied. Verify module-config.json has correct Delinea secret IDs for prod."
    }
}
