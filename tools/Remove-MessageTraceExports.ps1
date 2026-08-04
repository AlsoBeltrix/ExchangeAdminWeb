#Requires -Version 7.0
<#
.SYNOPSIS
    Deletes Message Analysis export files older than the retention window.

.DESCRIPTION
    The app NEVER deletes export files. Services/MessageTraceExportStore.cs states that a scheduled
    task on the host removes them, README.md repeats it to operators, and the reports page prints an
    "Available Until" date computed from that promise.

    That task did not exist. Measured on the ADI host 2026-08-04: schtasks reported 266 tasks, none
    belonging to this application. Exports therefore accumulated indefinitely, and past day 30 the
    reports page would show "Expired" for a file still sitting on disk - the status wrong in the
    direction that matters, claiming data is gone when it is not. This script is the missing half.

    Scope is deliberately narrow. The export directory lives INSIDE the audit log root, so this
    deletes only files matching the exact export filename pattern, never a wildcard sweep: a *.csv
    sweep one directory up would take audit data with it.

    Install it with tools/Install-MessageTraceExportRetention.ps1.

.PARAMETER LogRoot
    The configured Audit:LogRoot. REQUIRED and deliberately without a default - a cleanup script
    that guesses its own target is one wrong guess away from deleting the wrong tree.

.PARAMETER RetentionDays
    Files older than this many days are deleted. Default 30, matching
    MessageTraceExportStore.RetentionDays. If one changes, change the other in the same commit -
    the app's "Available Until" column is computed from that constant, so a mismatch makes the app
    lie about when a file goes away.

.EXAMPLE
    ./Remove-MessageTraceExports.ps1 -LogRoot 'E:\WWWOutput' -WhatIf
    Reports what would be deleted without deleting anything.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $LogRoot,

    [ValidateRange(1, 3650)]
    [int] $RetentionDays = 30
)

$ErrorActionPreference = 'Stop'

# Mirrors MessageTraceExportStore.FileNameFor: MessageTraceDetail_<32 hex>_<yyyyMMdd-HHmmss>.csv.
# Anchored at both ends. Anything not written by this app's export path is left alone, which is
# what makes running inside the audit log root safe.
$exportPattern = '^MessageTraceDetail_[0-9a-fA-F]{32}_\d{8}-\d{6}\.csv$'

if (-not (Test-Path -LiteralPath $LogRoot)) {
    # A missing ROOT means this is pointed somewhere wrong. Loud: silently doing nothing is how a
    # retention task appears healthy for months while deleting nothing.
    Write-Error "LogRoot not found: $LogRoot"
    exit 1
}

$exportDir = Join-Path $LogRoot 'ExchangeAdminWeb' 'MessageTraceExports'

if (-not (Test-Path -LiteralPath $exportDir)) {
    # A missing export DIRECTORY is the ordinary state before the first export is written. Not an
    # error, and not something to create.
    Write-Host "No export directory at $exportDir - nothing to do."
    exit 0
}

$cutoff = (Get-Date).AddDays(-$RetentionDays)

$candidates = Get-ChildItem -LiteralPath $exportDir -File |
    Where-Object { $_.Name -match $exportPattern -and $_.LastWriteTime -lt $cutoff }

$deleted = 0
$failed = 0

foreach ($file in $candidates) {
    if ($PSCmdlet.ShouldProcess($file.FullName, 'Delete expired Message Analysis export')) {
        try {
            Remove-Item -LiteralPath $file.FullName -Force
            $deleted++
        }
        catch {
            # Aggregate per-file failures rather than aborting: one locked file must not stop the
            # rest of the sweep, and the run must not report blanket success either.
            $failed++
            Write-Warning "Failed to delete $($file.FullName): $($_.Exception.Message)"
        }
    }
}

Write-Host ("Message Analysis export retention: {0} of {1} eligible file(s) deleted from {2} (older than {3} days)." -f
    $deleted, $candidates.Count, $exportDir, $RetentionDays)

if ($failed -gt 0) {
    Write-Error "$failed export file(s) could not be deleted."
    exit 1
}

exit 0
