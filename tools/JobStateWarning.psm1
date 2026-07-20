<#
.SYNOPSIS
    Warn (never block) about active bulk jobs before an app-pool recycle.

.DESCRIPTION
    Shared by every deploy/promote script that stops or recycles the app pool (deploy.ps1,
    tools/deploy-pipeline.ps1, tools/promote-dev-to-prod.ps1). The Bulk Job Runner
    (docs/BulkJobRunner-Plan.md) runs durable batches server-side; a recycle INTERRUPTS any
    Running or Queued job (there is no resume - startup flips them to Interrupted). Operators
    should know that before they recycle.

    Policy (owner-requested): WARN, do not hard-block. The warning is loud and lists the jobs,
    but the deploy proceeds - a stuck/wedged job must never be able to prevent the very recycle
    that clears it (that would defeat the anti-brittleness design).

    The jobs database (config/exchangeadmin-jobs.db) is a separate operational SQLite file, NOT
    the config DB, and is environment-local (never promoted). We read it out-of-process with
    sqlite3.exe (already a declared deploy dependency for SqliteConfigBackup). If sqlite3.exe is
    missing or the DB is unreadable we surface that as a soft note rather than failing - an
    unreadable job DB must not abort a deploy either.

    Honors plan mode: pass -PlanOnly to report what WOULD be checked without side effects (there
    are no writes here anyway; the flag keeps the calling scripts' plan/act model consistent and
    the output phrased as a plan line).
#>

Set-StrictMode -Version Latest

function Get-Sqlite3PathForJobs {
    $cmd = Get-Command sqlite3 -CommandType Application -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

<#
.SYNOPSIS
    Returns the active (Running/Queued) bulk jobs recorded in the jobs DB, or an empty array.
.PARAMETER ConfigDir
    The runtime config directory that contains exchangeadmin-jobs.db.
.OUTPUTS
    An array of PSCustomObjects with Status, Kind, SubmittedBy, Ticket, Progress. Empty when
    there are no active jobs, no DB, or the DB cannot be read (a soft note is written for the
    latter two so the caller/operator knows the check could not run).
#>
function Get-ActiveBulkJobs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir
    )

    $dbPath = Join-Path $ConfigDir 'exchangeadmin-jobs.db'
    if (-not (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
        # No jobs DB yet (e.g. the runner has never persisted a job) - nothing to warn about.
        return @()
    }

    $sqlite3 = Get-Sqlite3PathForJobs
    if (-not $sqlite3) {
        Write-Warning "sqlite3.exe is not on PATH; cannot check for active bulk jobs before recycle. (Install: winget install SQLite.SQLite)"
        return @()
    }

    # Read-only query. -readonly avoids taking a write lock on a live DB; a pipe-delimited,
    # header-less result is trivial to parse. Any failure (locked, corrupt) becomes a soft note.
    $query = "SELECT status, job_type, submitted_by, COALESCE(ticket,''), processed_rows || '/' || total_rows FROM bulk_job WHERE status IN ('Running','Queued') ORDER BY submitted_at;"
    try {
        $raw = & $sqlite3 -readonly -separator '|' "$dbPath" "$query" 2>$null
    } catch {
        Write-Warning "Could not read the bulk jobs database ($dbPath): $($_.Exception.Message). Skipping the active-job check."
        return @()
    }

    if (-not $raw) { return @() }

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
.PARAMETER PlanOnly
    When set, reports as a plan line without implying an action was taken (there are no writes).
.OUTPUTS
    The number of active jobs found (0 when none / not checkable).
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
        [switch]$PlanOnly
    )

    $jobs = @(Get-ActiveBulkJobs -ConfigDir $ConfigDir)

    if ($PlanOnly) {
        if ($jobs.Count -eq 0) {
            Write-Host "PLAN: would check for active bulk jobs before recycle (none currently active)." -ForegroundColor DarkGray
        } else {
            Write-Host "PLAN: recycle WOULD interrupt $($jobs.Count) active bulk job(s):" -ForegroundColor Yellow
            foreach ($j in $jobs) {
                Write-Host "        [$($j.Status)] $($j.Kind) by $($j.SubmittedBy) ticket=$($j.Ticket) progress=$($j.Progress)" -ForegroundColor Yellow
            }
        }
        return $jobs.Count
    }

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
