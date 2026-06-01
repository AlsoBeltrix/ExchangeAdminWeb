#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Build and deploy ExchangeAdminWeb.

.DESCRIPTION
    -Dev   Build from source and deploy to the dev IIS site.
    -Prod  Promote the current dev deployment to prod. Does NOT rebuild.
           Backup taken before overwrite. Automatic rollback on failure.

    Workflow: run -Dev first, validate in dev, then run -Prod to promote.

    Config fragments are merged key-by-key during prod promotion:
    new keys from dev are added, existing prod values are preserved.

.EXAMPLE
    .\deploy-pipeline.ps1 -Dev
    # Build and deploy to dev. Validate at https://<server>/ExchangeAdminWebDev

.EXAMPLE
    .\deploy-pipeline.ps1 -Prod
    # Promote current dev to prod. Does not rebuild — uses what's already deployed in dev.
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

    [switch]$CopyAppSettings
)

$ErrorActionPreference = "Stop"

function Write-Step    { param($m) Write-Host ">>> $m" -ForegroundColor Cyan }
function Write-Ok      { param($m) Write-Host " OK  $m" -ForegroundColor Green }
function Write-Warn    { param($m) Write-Host "  !  $m" -ForegroundColor Yellow }

if (-not $Dev -and -not $Prod) { throw "Specify -Dev or -Prod." }
if ($Dev -and $Prod) { throw "Specify -Dev or -Prod, not both." }

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
Import-Module WebAdministration -ErrorAction Stop

if ($Dev) {
    # --- Build from source and deploy to dev ---

    $deployScript = Join-Path $repoRoot "deploy.ps1"
    if (-not (Test-Path -LiteralPath $deployScript -PathType Leaf)) {
        throw "deploy.ps1 not found at: $deployScript"
    }

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

    Write-Host ""
    Write-Ok "Dev deployment complete at $DevPath"
    Write-Host "Validate: https://<server>$DevPathBase" -ForegroundColor Cyan
    Write-Host "When validated, run: .\deploy-pipeline.ps1 -Prod" -ForegroundColor DarkGray
}

if ($Prod) {
    # --- Promote current dev to prod (no rebuild) ---

    if (-not (Test-Path -LiteralPath $DevPath -PathType Container)) {
        throw "Dev deployment not found at $DevPath. Run -Dev first."
    }

    $devDll = Join-Path $DevPath "ExchangeAdminWeb.dll"
    if (-not (Test-Path -LiteralPath $devDll -PathType Leaf)) {
        throw "No ExchangeAdminWeb.dll in $DevPath. Run -Dev first."
    }

    $promoteScript = Join-Path $PSScriptRoot "promote-dev-to-prod.ps1"
    if (-not (Test-Path -LiteralPath $promoteScript -PathType Leaf)) {
        throw "promote-dev-to-prod.ps1 not found at: $promoteScript"
    }

    Write-Host ""
    Write-Host "=== Promote dev to prod ===" -ForegroundColor Magenta
    Write-Host "  Dev build: $devDll" -ForegroundColor DarkGray
    Write-Host "  Dev modified: $((Get-Item $devDll).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor DarkGray
    Write-Host ""

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
}
