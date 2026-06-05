#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Build and deploy ExchangeAdminWeb.

.DESCRIPTION
    -Dev   Build from source and deploy to the dev IIS site.
    -Prod  Promote the current dev deployment to prod. Does NOT rebuild.
           Verifies dev was built from the current source commit.
           Backup taken before overwrite. Automatic rollback on failure.

    Workflow: run -Dev first, validate in dev, then run -Prod to promote.

    Config promotion: module configs and operational settings are promoted
    from dev (dev values win). appsettings.json is prod-owned and preserved,
    with Application:PathBase patched for the prod path.

.EXAMPLE
    .\deploy-pipeline.ps1 -Dev
    # Build and deploy to dev. Validate at https://<server>/ExchangeAdminWebDev

.EXAMPLE
    .\deploy-pipeline.ps1 -Prod
    # Promote current dev to prod. Does not rebuild - uses what's already deployed in dev.
#>

[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$Prod,
    [switch]$AllowDirty,

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

    # Check that dev is current via deployment manifest
    $manifestPath = Join-Path $DevPath "deployment-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Dev has no deployment manifest. Run -Dev first with the current source."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $currentCommit = try { (git -C $repoRoot rev-parse HEAD 2>$null) } catch { $null }
    $currentDirty = try { [bool](git -C $repoRoot status --porcelain 2>$null) } catch { $false }

    Write-Host ""
    Write-Host "=== Promote dev to prod ===" -ForegroundColor Magenta
    Write-Host "  Dev version : v$($manifest.version)" -ForegroundColor DarkGray
    Write-Host "  Dev commit  : $($manifest.gitCommit)" -ForegroundColor DarkGray
    Write-Host "  Dev branch  : $($manifest.gitBranch)" -ForegroundColor DarkGray
    Write-Host "  Dev built   : $($manifest.buildTime)" -ForegroundColor DarkGray
    Write-Host "  Source HEAD : $currentCommit" -ForegroundColor DarkGray
    Write-Host ""

    if ($currentCommit -and $manifest.gitCommit -and $currentCommit -ne $manifest.gitCommit) {
        throw "Dev is stale: built from commit $($manifest.gitCommit.Substring(0,8)) but source is at $($currentCommit.Substring(0,8)). Run -Dev first."
    }
    Write-Ok "Dev commit matches source HEAD"

    if ($manifest.gitDirty) {
        if (-not $AllowDirty) {
            throw "Dev was built from a dirty source tree (uncommitted changes). Commit your changes and run -Dev again, or use -AllowDirty to override."
        }
        Write-Warn "Dev was built from a dirty source tree - proceeding because -AllowDirty was specified."
    }

    if ($currentDirty -and -not $manifest.gitDirty) {
        Write-Warn "Source tree has uncommitted changes since dev was built. Dev build is clean but source has drifted."
    }

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
    Write-Host "Module configs and operational settings promoted from dev. appsettings.json preserved with PathBase patched." -ForegroundColor DarkGray
}
