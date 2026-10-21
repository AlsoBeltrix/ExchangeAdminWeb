<#
Behavioral tests for tools/JobStateWarning.psm1 (BulkJobRunner-Plan; ConfigDbToolInApp-Plan S2).

These exercise the real module against the real built app (its `db active-jobs` verb) and a real
temp jobs DB:
  - active (Running/Queued) jobs are detected and returned;
  - terminal jobs (Completed/Cancelled/Interrupted) are NOT reported as active;
  - a missing jobs DB returns nothing (no warning);
  - the warning never blocks (returns a count, does not throw);
  - a missing tool or an unreadable jobs DB is a soft warning, never a failure;
  - PlanOnly reports without acting.

Nothing here skips: the app is built when no build output exists (Get-BuiltApp.ps1).
#>

Set-StrictMode -Version Latest

BeforeAll {
    . (Join-Path $PSScriptRoot 'Get-BuiltApp.ps1')
    $script:AppPath = Get-BuiltApp

    $modulePath = Join-Path $PSScriptRoot '..\..\tools\JobStateWarning.psm1'
    Import-Module $modulePath -Force

    function New-TempConfigDir {
        $d = Join-Path ([System.IO.Path]::GetTempPath()) ("jsw_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        return $d
    }

    # Creates a jobs DB with the minimal bulk_job schema the query reads, then inserts one row
    # per (status) requested, in submission order.
    function New-JobsDb {
        param(
            [string]$ConfigDir,
            [string[]]$Statuses
        )
        $db = Join-Path $ConfigDir 'exchangeadmin-jobs.db'
        $sql = @'
CREATE TABLE bulk_job (
  id TEXT PRIMARY KEY, module_id TEXT, job_type TEXT, status TEXT,
  submitted_by TEXT, submitted_ip TEXT, ticket TEXT, payload_json TEXT,
  submitted_at TEXT, total_rows INTEGER DEFAULT 0, processed_rows INTEGER DEFAULT 0);
'@
        $i = 0
        foreach ($s in $Statuses) {
            $i++
            $sql += "INSERT INTO bulk_job (id, module_id, job_type, status, submitted_by, submitted_ip, ticket, payload_json, submitted_at, total_rows, processed_rows) VALUES ('job$i','ConferenceRooms','SetMetadata_Bulk','$s','user$i','ip','INC$i','[]','2026-01-0${i}T00:00:00Z', 10, 3);"
        }
        New-TestDb -AppPath $script:AppPath -Path $db -Sql $sql | Out-Null
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
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $dir -AppPath $script:AppPath)
            $jobs.Count | Should -Be 0
            # And the warning helper reports zero without throwing.
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir -AppPath $script:AppPath 3>$null
            $count | Should -Be 0
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'detects Running and Queued jobs but not terminal ones, with every column parsed' {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running', 'Queued', 'Completed', 'Cancelled', 'Interrupted') | Out-Null
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $dir -AppPath $script:AppPath)
            $jobs.Count | Should -Be 2
            $jobs[0].Status | Should -Be 'Running'
            $jobs[1].Status | Should -Be 'Queued'
            $jobs[1].Kind | Should -Be 'SetMetadata_Bulk'
            $jobs[1].SubmittedBy | Should -Be 'user2'
            $jobs[1].Ticket | Should -Be 'INC2'
            $jobs[1].Progress | Should -Be '3/10'
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'returns 0 (no active) when only terminal jobs exist' {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Completed', 'Interrupted') | Out-Null
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir -AppPath $script:AppPath 3>$null
            $count | Should -Be 0
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'warns (does not throw) and returns the active count when jobs are active' {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running', 'Queued') | Out-Null
            # 3>$null suppresses the warning stream so the test output stays clean; the call must
            # still return the count and never throw (warn, not block).
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir -AppPath $script:AppPath 3>$null
            $count | Should -Be 2
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'PlanOnly reports without throwing and returns the count' {
        $dir = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running') | Out-Null
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir -AppPath $script:AppPath -PlanOnly 3>$null 6>$null
            $count | Should -Be 1
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'warns and returns nothing, never throws, when the app binary is missing' {
        $dir = New-TempConfigDir
        $noApp = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running') | Out-Null
            $warnings = @()
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $dir -AppPath $noApp -WarningVariable warnings 3>$null)
            $jobs.Count | Should -Be 0
            @($warnings).Count | Should -BeGreaterThan 0
            "$warnings" | Should -BeLike "*$noApp*"
        } finally { Remove-Item $dir, $noApp -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'warns and returns nothing, without running it, when the app binary predates the db mode' {
        # An app older than the db mode would not exit with a usage error; it would try to
        # start. So an old binary is never executed - the check degrades to the soft note.
        $old = Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll' -File |
            Where-Object {
                $v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName)
                $v.FileVersion -and ($v.FileMajorPart -lt 2 -or ($v.FileMajorPart -eq 2 -and $v.FileMinorPart -lt 34))
            } | Select-Object -First 1
        $old | Should -Not -BeNullOrEmpty -Because 'the test needs some DLL with a file version below 2.34'
        $dir = New-TempConfigDir
        $oldApp = New-TempConfigDir
        try {
            New-JobsDb -ConfigDir $dir -Statuses @('Running') | Out-Null
            Copy-Item -LiteralPath $old.FullName -Destination (Join-Path $oldApp 'ExchangeAdminWeb.dll')
            $warnings = @()
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $dir -AppPath $oldApp -WarningVariable warnings 3>$null)
            $jobs.Count | Should -Be 0
            "$warnings" | Should -BeLike '*version 2.34.0 or later*'
        } finally { Remove-Item $dir, $oldApp -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'warns and returns nothing, never throws, when the jobs DB is unreadable' {
        $dir = New-TempConfigDir
        try {
            [System.IO.File]::WriteAllText((Join-Path $dir 'exchangeadmin-jobs.db'), 'not a database')
            $warnings = @()
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $dir -AppPath $script:AppPath -WarningVariable warnings 3>$null)
            $jobs.Count | Should -Be 0
            "$warnings" | Should -BeLike '*Could not read the bulk jobs database*'
            $count = Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $dir -AppPath $script:AppPath 3>$null
            $count | Should -Be 0
        } finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
