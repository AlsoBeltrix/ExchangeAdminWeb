<#
.SYNOPSIS
    Warn (never block) about active bulk jobs before an app-pool recycle.

.DESCRIPTION
    Shared by every script that stops the app pool, each of which imports it and warns before its
    stop: deploy.ps1, tools/promote-dev-to-prod.ps1, tools/Move-ConfigDbToShared.ps1 (the cutover)
    and tools/Install-ExchangeAdminWeb.ps1. tools/deploy-pipeline.ps1 does not import it: it stops a
    pool only through deploy.ps1 and the promotion. The Bulk Job Runner
    (docs/BulkJobRunner-Plan.md) runs durable batches server-side; a recycle INTERRUPTS any
    Running or Queued job (there is no resume - startup flips them to Interrupted). Operators
    should know that before they recycle.

    Policy (owner-requested): WARN, do not hard-block. The warning is loud and lists the jobs,
    but the deploy proceeds - a stuck/wedged job must never be able to prevent the very recycle
    that clears it (that would defeat the anti-brittleness design).

    The jobs database (config/exchangeadmin-jobs.db) is a separate operational SQLite file, NOT
    the config DB, and is environment-local (never promoted). We read it out-of-process through
    the app's own db mode, `<ProgramFiles>\dotnet\dotnet.exe <AppPath>\ExchangeAdminWeb.dll db
    active-jobs` (docs/ConfigDbToolInApp-Plan.md); sqlite3.exe is not used and the host is never
    searched for on PATH. If the tool is missing or too old, or the DB is unreadable, we surface
    that as a soft note rather than failing - an unreadable job DB must not abort a deploy either.

    Honors plan mode: pass -PlanOnly to report what a real run WOULD check - which jobs database,
    read with which build's db tool - as a plan line, without checking it. A plan launches no tool
    and opens no jobs database (cdt-60 (2), Architectural Invariant 4).
#>

Set-StrictMode -Version Latest

function Get-ConfigDbToolForJobs {
    # Private. The same Program Files host and app assembly SqliteConfigBackup resolves, kept
    # local so this module stays importable on its own. $null means "cannot check": a missing
    # file, or an app older than the db mode (2.34.0), which would try to start rather than
    # answer, so it is never run.
    param([Parameter(Mandatory)][string]$AppPath)

    $programFiles = $env:ProgramFiles
    if ([string]::IsNullOrWhiteSpace($programFiles)) { return $null }
    $dotnetHost = Join-Path (Join-Path $programFiles 'dotnet') 'dotnet.exe'
    $assembly = Join-Path $AppPath 'ExchangeAdminWeb.dll'
    if (-not (Test-Path -LiteralPath $dotnetHost -PathType Leaf)) { return $null }
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { return $null }
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
    $version = New-Object System.Version -ArgumentList $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
    if ($version -lt [version]'2.34.0') { return $null }
    return @{ Host = $dotnetHost; Assembly = $assembly }
}

<#
.SYNOPSIS
    Returns the active (Running/Queued) bulk jobs recorded in the jobs DB, or an empty array.
.PARAMETER ConfigDir
    The runtime config directory that contains exchangeadmin-jobs.db.
.PARAMETER AppPath
    The folder holding the ExchangeAdminWeb.dll whose db mode runs the query.
.OUTPUTS
    An array of PSCustomObjects with Status, Kind, SubmittedBy, Ticket, Progress. Empty when
    there are no active jobs, no DB, or the DB cannot be read (a soft note is written for the
    latter two so the caller/operator knows the check could not run).
#>
function Get-ActiveBulkJobs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$AppPath
    )

    $dbPath = Join-Path $ConfigDir 'exchangeadmin-jobs.db'
    if (-not (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
        # No jobs DB yet (e.g. the runner has never persisted a job) - nothing to warn about.
        return @()
    }

    $tool = Get-ConfigDbToolForJobs -AppPath $AppPath
    if (-not $tool) {
        Write-Warning "The app's db tool was not found (the .NET host under Program Files, or an ExchangeAdminWeb.dll of version 2.34.0 or later in $AppPath); cannot check for active bulk jobs before recycle."
        return @()
    }

    # Local to this function, so a stderr line under the callers' "Stop" (Windows PowerShell
    # 5.1 turns it into a terminating error) cannot hide the exit code; any failure stays a note.
    $ErrorActionPreference = 'Continue'
    $raw = @()
    $errors = @()
    try {
        $output = & $tool.Host $tool.Assembly 'db' 'active-jobs' $dbPath 2>&1
        $exitCode = $LASTEXITCODE
    } catch {
        Write-Warning "Could not read the bulk jobs database ($dbPath): $($_.Exception.Message). Skipping the active-job check."
        return @()
    }
    foreach ($item in @($output)) {
        if ($item -is [System.Management.Automation.ErrorRecord]) {
            $errors += [string]$item.Exception.Message
        } elseif ($null -ne $item) {
            $raw += [string]$item
        }
    }
    if ($exitCode -ne 0) {
        $hint = ''
        if ($exitCode -eq 1) { $hint = ' The app in that folder is older than the backup tool; deploy dev first.' }
        Write-Warning "Could not read the bulk jobs database ($dbPath): db tool exit $exitCode. $($errors -join ' ')$hint Skipping the active-job check."
        return @()
    }

    if ($raw.Count -eq 0) { return @() }

    $jobs = @()
    foreach ($line in $raw) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '\|', 5
        if ($parts.Count -lt 5) { continue }
        $jobs += [PSCustomObject]@{
            Status      = $parts[0]
            Kind        = $parts[1]
            SubmittedBy = $parts[2]
            Ticket      = $parts[3]
            Progress    = $parts[4]
        }
    }
    return $jobs
}

<#
.SYNOPSIS
    Warn (loudly, listing them) if any bulk jobs are active before a recycle. Never blocks.
.PARAMETER ConfigDir
    The runtime config directory that contains exchangeadmin-jobs.db.
.PARAMETER AppPath
    The folder holding the ExchangeAdminWeb.dll whose db mode runs the query.
.PARAMETER PlanOnly
    When set, prints the check a real run makes, as a plan line, and makes none: no tool is
    launched and no jobs database is read (cdt-60 (2)).
.OUTPUTS
    The number of active jobs found (0 when none / not checkable, and in a plan, which checks nothing).
#>
function Assert-NoActiveBulkJobsBeforeRecycle {
    # Write-Host is intentional here: this is operator-facing console output during an interactive
    # deploy (the same pattern deploy.ps1 / promote-dev-to-prod.ps1 use for their plan/step lines),
    # not data that should go to the pipeline. Warnings still go through Write-Warning.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
        Justification = 'Operator-facing deploy console output, consistent with the other ops scripts')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$AppPath,
        [switch]$PlanOnly
    )

    # cdt-60 (2). A plan checks nothing (Architectural Invariant 4). The jobs were read first and the plan branch came after
    # the read, so a plan-only cutover or promotion launched the app's db tool against the instances' real jobs databases.
    # The plan says what a real run reads, and with which build; a string, not a path call, so a folder on a drive this
    # host lacks cannot make the plan line throw.
    if ($PlanOnly) {
        Write-Host "PLAN: would check for active bulk jobs before recycle: exchangeadmin-jobs.db in $ConfigDir, read with the db tool of the app in $AppPath (a plan reads no jobs database)." -ForegroundColor DarkGray
        return 0
    }

    $jobs = @(Get-ActiveBulkJobs -ConfigDir $ConfigDir -AppPath $AppPath)

    if ($jobs.Count -gt 0) {
        Write-Warning "=============================================================="
        Write-Warning " $($jobs.Count) bulk job(s) are ACTIVE. Recycling the app pool will"
        Write-Warning " INTERRUPT them (there is no resume - they become 'Interrupted')."
        foreach ($j in $jobs) {
            Write-Warning "   [$($j.Status)] $($j.Kind) by $($j.SubmittedBy) ticket=$($j.Ticket) progress=$($j.Progress)"
        }
        Write-Warning " Proceeding anyway (warn, not block)."
        Write-Warning "=============================================================="
    }

    return $jobs.Count
}

Export-ModuleMember -Function Get-ActiveBulkJobs, Assert-NoActiveBulkJobsBeforeRecycle
