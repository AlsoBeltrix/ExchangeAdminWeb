#Requires -Version 7.0
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers the daily scheduled task that enforces Message Analysis export retention.

.DESCRIPTION
    The app never deletes export files; a host scheduled task does. That task was documented in
    code, README and an owner ruling, but was never actually created on any host - measured on the
    ADI server 2026-08-04, where schtasks listed 266 tasks and none belonged to this application.
    This script creates it.

    Environment-neutral and standalone, like tools/Install-ExchangeAdminWeb.ps1: every
    site-specific value is a parameter. Deliberately NOT called from deploy.ps1 - a deploy must not
    perform privileged host registration as a side effect, and the task is per-host, not
    per-deploy. Run it once per server.

    Idempotent: re-running replaces the existing task definition rather than failing or duplicating.

.PARAMETER LogRoot
    The Audit:LogRoot configured for the app on this host (e.g. E:\WWWOutput). Passed through to
    the retention script; there is no default, deliberately.

.PARAMETER RetentionDays
    Retention window in days. Default 30, matching MessageTraceExportStore.RetentionDays. The app
    computes its "Available Until" column from that constant, so changing one without the other
    makes the app lie about when a file disappears.

.PARAMETER TaskName
    Scheduled task name. Defaults to ExchangeAdminWeb-MessageTraceExportRetention.

.PARAMETER At
    Time of day to run. Defaults to 03:20 - off the hour and off the half hour, so it does not
    contend with every other daily task on the box.

.PARAMETER PlanOnly
    Print what would be registered and exit without touching the task store.

.EXAMPLE
    ./Install-MessageTraceExportRetention.ps1 -LogRoot 'E:\WWWOutput' -PlanOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $LogRoot,

    [ValidateRange(1, 3650)]
    [int] $RetentionDays = 30,

    [string] $TaskName = 'ExchangeAdminWeb-MessageTraceExportRetention',

    [string] $At = '03:20',

    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'

$removerPath = Join-Path $PSScriptRoot 'Remove-MessageTraceExports.ps1'
if (-not (Test-Path -LiteralPath $removerPath)) {
    Write-Error "Retention script not found next to this installer: $removerPath"
    exit 1
}
$removerPath = (Resolve-Path -LiteralPath $removerPath).Path

# Validate the root here rather than leaving it to the first 3am run. A task registered against a
# wrong path looks healthy indefinitely while deleting nothing - the exact failure this repairs.
if (-not (Test-Path -LiteralPath $LogRoot)) {
    Write-Error "LogRoot not found: $LogRoot"
    exit 1
}

$pwshPath = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source
if (-not $pwshPath) {
    Write-Error "pwsh (PowerShell 7) not found on PATH. The retention script requires it."
    exit 1
}

$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -LogRoot "{1}" -RetentionDays {2}' -f
    $removerPath, $LogRoot, $RetentionDays

if ($PlanOnly) {
    Write-Host "PLAN  Would register scheduled task '$TaskName'"
    Write-Host "PLAN    Run daily at : $At as SYSTEM"
    Write-Host "PLAN    Program      : $pwshPath"
    Write-Host "PLAN    Arguments    : $arguments"
    exit 0
}

$action = New-ScheduledTaskAction -Execute $pwshPath -Argument $arguments
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd `
    -ExecutionTimeLimit (New-TimeSpan -Hours 1)

# -Force replaces an existing registration, making a re-run a definition update rather than an
# error or a duplicate task.
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Registered scheduled task '$TaskName' (daily at $At, SYSTEM)."
Write-Host "  Retention: $RetentionDays days under $LogRoot"
Write-Host "  Verify with: schtasks /query /tn `"$TaskName`" /v /fo list"
Write-Host "  Dry run   : pwsh -File `"$removerPath`" -LogRoot `"$LogRoot`" -WhatIf"

exit 0
