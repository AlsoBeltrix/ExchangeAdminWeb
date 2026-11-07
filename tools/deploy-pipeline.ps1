#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Build and deploy ExchangeAdminWeb.

.DESCRIPTION
    -Dev   Build from source and deploy to the dev IIS site.
    -Prod  Promote the current dev deployment to prod. Does NOT rebuild.
           Verifies dev was built from the current source: the same commit, or one
           whose build inputs are the same - a commit that touches only .agents/,
           docs/, Markdown files or tests/ps/ does not make dev stale (sweep item 35).
           A stale dev refuses -Prod; with -PlanOnly it is a warning and the dry run
           still prints.
           Backup taken before overwrite. Automatic rollback on failure.

    Workflow: run -Dev first, validate in dev, then run -Prod to promote.

    Config: dev and prod share ONE config database (the file named by
    ConfigStore:Path in each instance's appsettings.json; decision 2026-09-04).
    Promotion never copies, replaces or merges it - a setting saved on dev is
    already live on prod. Every deploy of either instance takes a verified
    backup of that file first. appsettings.json is prod-owned and preserved,
    with Application:PathBase patched for the prod path.

.EXAMPLE
    .\deploy-pipeline.ps1 -Dev
    # Build and deploy to dev. Validate at https://<server>/ExchangeAdminWebDev

.EXAMPLE
    .\deploy-pipeline.ps1 -Prod
    # Promote current dev to prod. Does not rebuild - uses what's already deployed in dev.

.EXAMPLE
    .\deploy-pipeline.ps1 -Prod -PlanOnly
    # DRY RUN of the prod promotion: prints what promote-dev-to-prod.ps1 would do
    # without touching prod. Always available before a real -Prod run.
#>

[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$Prod,
    [switch]$AllowDirty,
    # A provenance check that cannot run is not a provenance check that passed. This is the
    # explicit way to promote anyway, modelled on -AllowDirty above.
    [switch]$AllowUnverifiedProvenance,
    [switch]$PlanOnly,

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

# Sweep item 35. The tracked paths whose changes never reach the build, in this one place: the
# agent records, the docs, every Markdown file, and the PowerShell test suite. A commit that
# touches only these leaves dev's build current, so -Prod is not refused for it. Everything else
# counts as a build input - tools\ and the C# test project included - so the list errs toward
# "stale". tests/ps/DeployInvariants.Tests.ps1 pins it.
$script:NonBuildInputPrefixes = @('.agents/', 'docs/', 'tests/ps/')
$script:NonBuildInputSuffixes = @('.md')

function Test-IsNonBuildInputPath {
    # True for a repository path (forward slashes, as git prints it) that the list above leaves out.
    param([string]$Path)

    foreach ($prefix in $script:NonBuildInputPrefixes) {
        if ($Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    foreach ($suffix in $script:NonBuildInputSuffixes) {
        if ($Path.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-BuildInputTreeId {
    # The build inputs of one commit as one hash: the mode, blob id and path of every tracked file
    # the list above does not leave out. git is native and does not throw, so its exit code is
    # read; a commit git cannot list throws.
    param([string]$RepoRoot, [string]$Commit)

    $listing = $null
    try {
        $listing = & git -C $RepoRoot ls-tree -r --full-tree $Commit 2>$null
    } catch {
        throw "git could not list commit ${Commit}: $($_.Exception.Message)"
    }
    if ($LASTEXITCODE -ne 0) { throw "git could not list commit $Commit (exit $LASTEXITCODE); it may not be in this repository" }

    $kept = foreach ($line in @($listing)) {
        $tab = $line.IndexOf("`t")
        if ($tab -lt 0) { continue }
        if (-not (Test-IsNonBuildInputPath $line.Substring($tab + 1))) { $line }
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes((@($kept) -join "`n")))
    } finally {
        $sha.Dispose()
    }
    return (-join ($bytes | ForEach-Object { $_.ToString('x2') }))
}

function Assert-DevBuildCurrent {
    # Sweep item 35. Dev was built from another commit than HEAD. It is current when the two commits
    # hold the same build inputs; otherwise it is stale, and so is a pair git cannot compare. A stale
    # dev refuses a real -Prod; a plan (-PlanOnly) warns and goes on, so it can still print.
    param(
        [string]$RepoRoot,
        [string]$DevCommit,
        [string]$SourceCommit,
        [string]$DevShort,
        [string]$SourceShort,
        [switch]$PlanOnly
    )

    $excluded = (@($script:NonBuildInputPrefixes) + @($script:NonBuildInputSuffixes | ForEach-Object { "*$_" })) -join ', '
    try {
        $devInputs = Get-BuildInputTreeId -RepoRoot $RepoRoot -Commit $DevCommit
        $sourceInputs = Get-BuildInputTreeId -RepoRoot $RepoRoot -Commit $SourceCommit
        if ($devInputs -eq $sourceInputs) {
            Write-Ok "Dev was built from $DevShort and source is at $SourceShort, but the build inputs are the same at both (the commits between touch only $excluded)"
            return
        }
        $reason = "Dev is stale: built from commit $DevShort but source is at $SourceShort, and the build inputs differ between them (everything but $excluded)"
    } catch {
        $reason = "Dev is stale: built from commit $DevShort but source is at $SourceShort, and the two cannot be compared ($($_.Exception.Message))"
    }
    if ($PlanOnly) {
        Write-Warn "$reason. A real -Prod refuses here: run -Dev first. This plan goes on."
        return
    }
    throw "$reason. Run -Dev first."
}

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

    if ($PlanOnly) {
        Write-Host "PLAN  Would run: deploy.ps1 -ParentSite 'Default Web Site' -AppAlias ExchangeAdminWebDev -AppPoolName $DevAppPool -PublishPath $DevPath -PathBase $DevPathBase -LogRoot $LogRoot -NonInteractive" -ForegroundColor DarkGray
        Write-Warn "deploy.ps1 has no native plan mode yet; -PlanOnly skips the dev deploy entirely."
    }
    else {
        # deploy.ps1 signals failure by throwing (Write-Fail). Do not gate on
        # $LASTEXITCODE here: after a successful run it holds the last native
        # command's exit code (robocopy returns 1-3 on success), so any
        # exit-code comparison misclassifies script-to-script invocation.
        & $deployScript `
            -ParentSite "Default Web Site" `
            -AppAlias "ExchangeAdminWebDev" `
            -AppPoolName $DevAppPool `
            -PublishPath $DevPath `
            -PathBase $DevPathBase `
            -LogRoot $LogRoot `
            -NonInteractive

        Write-Host ""
        Write-Ok "Dev deployment complete at $DevPath"
        Write-Host "Validate: https://<server>$DevPathBase" -ForegroundColor Cyan
        Write-Host "When validated, run: .\deploy-pipeline.ps1 -Prod" -ForegroundColor DarkGray
    }
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
    # Provenance probe. `git` is a native executable: it does not throw on failure, so the
    # old try/catch caught nothing and a failed probe simply produced an empty string. The
    # exit code is what says whether the answer is trustworthy.
    # Review: the exit-code check is right, but `git` ABSENT is a different failure -
    # PowerShell raises CommandNotFoundException before any exit code exists, and with
    # $ErrorActionPreference = "Stop" that terminated the run instead of reporting
    # unverifiable provenance, so -AllowUnverifiedProvenance could not be used on a host
    # without git. Catch the missing command; keep the exit code for the rest.
    $commitProbe = $null
    $commitProbeFailed = $false
    try { $commitProbe = git -C $repoRoot rev-parse HEAD 2>$null } catch { $commitProbeFailed = $true }
    $commitProbeOk = -not $commitProbeFailed -and ($LASTEXITCODE -eq 0) -and -not [string]::IsNullOrWhiteSpace($commitProbe)
    $currentCommit = if ($commitProbeOk) { "$commitProbe".Trim() } else { $null }

    $statusProbe = $null
    $statusProbeFailed = $false
    try { $statusProbe = git -C $repoRoot status --porcelain 2>$null } catch { $statusProbeFailed = $true }
    $statusProbeOk = -not $statusProbeFailed -and ($LASTEXITCODE -eq 0)
    $currentDirty = $statusProbeOk -and -not [string]::IsNullOrWhiteSpace(($statusProbe | Out-String))

    Write-Host ""
    Write-Host "=== Promote dev to prod ===" -ForegroundColor Magenta
    Write-Host "  Dev version : v$($manifest.version)" -ForegroundColor DarkGray
    Write-Host "  Dev commit  : $($manifest.gitCommit)" -ForegroundColor DarkGray
    Write-Host "  Dev branch  : $($manifest.gitBranch)" -ForegroundColor DarkGray
    Write-Host "  Dev built   : $($manifest.buildTime)" -ForegroundColor DarkGray
    Write-Host "  Source HEAD : $currentCommit" -ForegroundColor DarkGray
    Write-Host ""

    # The old shape skipped the comparison when EITHER side was missing and then printed
    # "Dev commit matches source HEAD" regardless - a positive claim with no evidence, on
    # the one check standing between a stale build and production. Each way of not knowing
    # is now its own branch, and none of them reports a match.
    $provenanceUnverifiable = $null
    if (-not $currentCommit) {
        $provenanceUnverifiable = "git could not report HEAD in $repoRoot (is it a repository, and is git on PATH?)"
    } elseif ([string]::IsNullOrWhiteSpace($manifest.gitCommit) -or $manifest.gitCommit -eq 'unknown') {
        # Review: "unknown" is what the manifest writer records when it cannot resolve HEAD.
        # Treating it as a real commit compared it against the live SHA and reported the
        # build as stale, which is the wrong diagnosis for "we never knew".
        $provenanceUnverifiable = "the dev deployment manifest carries no usable gitCommit, so what dev was built from is unknown"
    }

    if ($provenanceUnverifiable) {
        if (-not $AllowUnverifiedProvenance) {
            throw "Cannot verify that dev was built from the current source: $provenanceUnverifiable. Refusing to promote an unverified build to prod - re-run -Dev, or pass -AllowUnverifiedProvenance to promote anyway."
        }
        Write-Warn "Promoting WITHOUT verified provenance: $provenanceUnverifiable. -AllowUnverifiedProvenance was specified."
    } elseif ($currentCommit -ne $manifest.gitCommit) {
        # Review: a manifest carrying a short or placeholder gitCommit - "unknown" is the
        # one actually written when a build cannot resolve HEAD - made Substring(0,8) throw
        # ArgumentOutOfRangeException, replacing a clear "dev is stale" message with a
        # crash. Shorten only what is long enough to shorten.
        $devShort = if ($manifest.gitCommit.Length -ge 8) { $manifest.gitCommit.Substring(0, 8) } else { $manifest.gitCommit }
        $srcShort = if ($currentCommit.Length -ge 8) { $currentCommit.Substring(0, 8) } else { $currentCommit }
        # Sweep item 35: a different commit is not a different build. Compare what feeds the
        # build at the two commits; a plan warns where the real run refuses.
        Assert-DevBuildCurrent -RepoRoot $repoRoot -DevCommit $manifest.gitCommit -SourceCommit $currentCommit -DevShort $devShort -SourceShort $srcShort -PlanOnly:$PlanOnly
    } else {
        Write-Ok "Dev commit matches source HEAD"
    }

    if ($manifest.gitDirty) {
        if (-not $AllowDirty) {
            throw "Dev was built from a dirty source tree (uncommitted changes). Commit your changes and run -Dev again, or use -AllowDirty to override."
        }
        Write-Warn "Dev was built from a dirty source tree - proceeding because -AllowDirty was specified."
    }

    if (-not $statusProbeOk) {
        # Absence of a warning must not read as "the tree is clean".
        Write-Warn "Could not read the source tree status (git failed in $repoRoot), so drift since the dev build has NOT been checked."
    } elseif ($currentDirty -and -not $manifest.gitDirty) {
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
    }

    # Auto-assert the apply/consent switches only for a real promotion. With
    # -PlanOnly, promote-dev-to-prod.ps1 runs its native DRY RUN and prints the
    # plan without touching prod.
    if (-not $PlanOnly) {
        $promoteArgs["Apply"] = $true
        $promoteArgs["IUnderstandThisOverwritesProd"] = $true
    }

    if ($CopyAppSettings) {
        $promoteArgs["CopyAppSettings"] = $true
    }

    # promote-dev-to-prod.ps1 signals failure by throwing; no exit-code check
    # (see the -Dev branch note on $LASTEXITCODE residue).
    & $promoteScript @promoteArgs

    Write-Host ""
    if ($PlanOnly) {
        Write-Ok "Prod promotion DRY RUN complete. Re-run with -Prod (without -PlanOnly) to apply."
    }
    else {
        Write-Ok "Prod deployment complete at $ProdPath"
        Write-Host "Validate: https://<server>$ProdPathBase" -ForegroundColor Cyan
        Write-Host "Prod now runs the dev build. appsettings.json preserved with PathBase patched. The shared config database was not touched (backup taken first)." -ForegroundColor DarkGray
    }
}
