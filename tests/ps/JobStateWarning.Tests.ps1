<#
Behavioral tests for tools/JobStateWarning.psm1 (BulkJobRunner-Plan).

These exercise the real module against a real sqlite3.exe and a real temp jobs DB:
  - active (Running/Queued) jobs are detected and returned;
  - terminal jobs (Completed/Cancelled/Interrupted) are NOT reported as active;
  - a missing jobs DB returns nothing (no warning);
  - the warning never blocks (returns a count, does not throw);
  - PlanOnly reports without acting.

If sqlite3.exe is not on PATH the winget install location is prepended (mirrors the
SqliteConfigBackup suite). If it still cannot be found, the sqlite3-dependent tests skip.
#>

Set-StrictMode -Version Latest

if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\SQLite.SQLite_Microsoft.Winget.Source_8wekyb3d8bbwe'
    if (Test-Path -LiteralPath (Join-Path $candidate 'sqlite3.exe')) {
        $env:PATH = "$candidate;$env:PATH"
    }
}
$HasSqlite = [bool](Get-Command sqlite3 -ErrorAction SilentlyContinue)

BeforeAll {
    $modulePath = Join-Path $PSScriptRoot '..\..\tools\JobStateWarning.psm1'
    Import-Module $modulePath -Force

    function New-TempConfigDir {
        $d = Join-Path ([System.IO.Path]::GetTempPath()) ("jsw_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        return $d
    }

    # Creates a jobs DB with the minimal bulk_job schema the module queries, then inserts one row
    # per (status) requested.
    function New-JobsDb {
        param(
            [string]$ConfigDir,
            [string[]]$Statuses
        )
        $db = Join-Path $ConfigDir 'exchangeadmin-jobs.db'
        & sqlite3 $db @'
CREATE TABLE bulk_job (
  id TEXT PRIMARY KEY, module_id TEXT, job_type TEXT, status TEXT,
  submitted_by TEXT, submitted_ip TEXT, ticket TEXT, payload_json TEXT,
  submitted_at TEXT, total_rows INTEGER DEFAULT 0, processed_rows INTEGER DEFAULT 0);
'@
        $i = 0
        foreach ($s in $Statuses) {
            $i++
            & sqlite3 $db "INSERT INTO bulk_job (id, module_id, job_type, status, submitted_by, submitted_ip, ticket, payload_json, submitted_at, total_rows, processed_rows) VALUES ('job$i','ConferenceRooms','SetMetadata_Bulk','$s','user$i','ip','INC$i','[]','2026-01-0${i}T00:00:00Z', 10, 3);"
        }
        return $db
    }
}

Describe 'JobStateWarning module' {

    It 'parses and exports the expected functions' {
        $m = Get-Module JobStateWarning
        $m | Should -Not -BeNullOrEmpty
        $m.ExportedFunctions.Keys | Should -Contain 'Get-ActiveBulkJobs'
        $m.ExportedFunctions.Keys | Should -Contain 'Assert-NoActiveBulkJobsBeforeRecycle'
    }

    It 'returns nothing when there is no jobs DB' {
        $dir = New-TempConfigDir
        try {
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $dir)
            $jobs.Count | Should -Be 0
            # And the warning helper reports zero without throwing.
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir 3>$null
            $count | Should -Be 0
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'detects Running and Queued jobs but not terminal ones' -Skip:(-not $HasSqlite) {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running', 'Queued', 'Completed', 'Cancelled', 'Interrupted') | Out-Null
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $dir)
            $jobs.Count | Should -Be 2
            ($jobs.Status | Sort-Object) | Should -Be @('Queued', 'Running')
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'returns 0 (no active) when only terminal jobs exist' -Skip:(-not $HasSqlite) {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Completed', 'Interrupted') | Out-Null
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir 3>$null
            $count | Should -Be 0
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'warns (does not throw) and returns the active count when jobs are active' -Skip:(-not $HasSqlite) {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running', 'Queued') | Out-Null
            # 3>$null suppresses the warning stream so the test output stays clean; the call must
            # still return the count and never throw (warn, not block).
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir 3>$null
            $count | Should -Be 2
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'PlanOnly reports without throwing and returns the count' -Skip:(-not $HasSqlite) {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running') | Out-Null
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir -PlanOnly 3>$null 6>$null
            $count | Should -Be 1
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
