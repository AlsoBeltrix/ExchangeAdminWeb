#Requires -Version 7.0
<#
    Tests for the Message Analysis export retention sweep.

    This script deletes files inside the AUDIT LOG ROOT. The risk is not that it fails to delete an
    expired export - it is that it deletes something it should not. Most of what follows asserts
    what SURVIVES, not what goes.

    They run the real script against a temp tree, so they exercise the actual pattern and cutoff
    rather than a restatement of them.
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' '..' 'tools' 'Remove-MessageTraceExports.ps1' |
        Resolve-Path | Select-Object -ExpandProperty Path

    # Builds a log root with an export directory, mirroring the real layout
    # (<LogRoot>\ExchangeAdminWeb\MessageTraceExports).
    function New-LogRoot {
        $root = Join-Path $TestDrive ("root-{0}" -f [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $root 'ExchangeAdminWeb' 'MessageTraceExports') -Force | Out-Null
        return $root
    }

    function Get-ExportDir {
        param([string] $Root)
        return Join-Path $Root 'ExchangeAdminWeb' 'MessageTraceExports'
    }

    # A file with a controlled age. AgeDays is how long ago it was last written.
    function New-AgedFile {
        param(
            [string] $Directory,
            [string] $Name,
            [int] $AgeDays
        )
        $path = Join-Path $Directory $Name
        Set-Content -LiteralPath $path -Value 'x' -Encoding utf8
        (Get-Item -LiteralPath $path).LastWriteTime = (Get-Date).AddDays(-$AgeDays)
        return $path
    }

    # A validly-shaped export name: MessageTraceDetail_<32 hex>_<yyyyMMdd-HHmmss>.csv
    function New-ExportName {
        param([string] $Stamp = '20260101-120000')
        return "MessageTraceDetail_{0}_{1}.csv" -f [guid]::NewGuid().ToString('N'), $Stamp
    }
}

Describe 'Remove-MessageTraceExports' {

    Context 'Deleting expired exports' {

        It 'deletes an export older than the retention window' {
            $root = New-LogRoot
            $dir = Get-ExportDir $root
            $old = New-AgedFile -Directory $dir -Name (New-ExportName) -AgeDays 45

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 | Out-Null

            Test-Path -LiteralPath $old | Should -BeFalse
        }

        It 'keeps an export inside the retention window' {
            $root = New-LogRoot
            $dir = Get-ExportDir $root
            $recent = New-AgedFile -Directory $dir -Name (New-ExportName) -AgeDays 6

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 | Out-Null

            # The two real files on the ADI host are 6 days old. They must survive.
            Test-Path -LiteralPath $recent | Should -BeTrue
        }

        It 'honours a custom retention window' {
            $root = New-LogRoot
            $dir = Get-ExportDir $root
            $f = New-AgedFile -Directory $dir -Name (New-ExportName) -AgeDays 10

            & $script:ScriptPath -LogRoot $root -RetentionDays 7 | Out-Null

            Test-Path -LiteralPath $f | Should -BeFalse
        }
    }

    Context 'What it must never touch' {

        It 'leaves a non-export file in the export directory alone even when ancient' {
            $root = New-LogRoot
            $dir = Get-ExportDir $root
            $stray = New-AgedFile -Directory $dir -Name 'notes.csv' -AgeDays 900

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 | Out-Null

            Test-Path -LiteralPath $stray | Should -BeTrue
        }

        It 'leaves an audit log in the parent directory alone' {
            # The export directory sits INSIDE the audit log root. This is the deletion this script
            # must never make, and the reason it matches an anchored pattern rather than *.csv.
            $root = New-LogRoot
            $audit = New-AgedFile -Directory (Join-Path $root 'ExchangeAdminWeb') -Name 'exchangeadmin_20240101.jsonl' -AgeDays 900

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 | Out-Null

            Test-Path -LiteralPath $audit | Should -BeTrue
        }

        It 'leaves a near-miss filename alone' {
            $root = New-LogRoot
            $dir = Get-ExportDir $root
            # Right prefix, wrong id length - not something this app wrote.
            $nearMiss = New-AgedFile -Directory $dir -Name 'MessageTraceDetail_abc_20260101-120000.csv' -AgeDays 900

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 | Out-Null

            Test-Path -LiteralPath $nearMiss | Should -BeTrue
        }

        It 'leaves a subdirectory alone' {
            $root = New-LogRoot
            $dir = Get-ExportDir $root
            $sub = Join-Path $dir 'archive'
            New-Item -ItemType Directory -Path $sub -Force | Out-Null

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 | Out-Null

            Test-Path -LiteralPath $sub | Should -BeTrue
        }
    }

    Context 'WhatIf' {

        It 'deletes nothing with -WhatIf' {
            $root = New-LogRoot
            $dir = Get-ExportDir $root
            $old = New-AgedFile -Directory $dir -Name (New-ExportName) -AgeDays 900

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 -WhatIf | Out-Null

            Test-Path -LiteralPath $old | Should -BeTrue
        }
    }

    Context 'Missing paths' {

        It 'succeeds quietly when the export directory does not exist yet' {
            # The ordinary state before the first export is written.
            $root = Join-Path $TestDrive ("bare-{0}" -f [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $root -Force | Out-Null

            & $script:ScriptPath -LogRoot $root -RetentionDays 30 | Out-Null

            $LASTEXITCODE | Should -Be 0
        }

        It 'fails loudly when the log root does not exist' {
            # Pointed somewhere wrong. Silently doing nothing is how a retention task looks healthy
            # for months while deleting nothing - which is the defect this whole script repairs.
            #
            # The script sets $ErrorActionPreference = 'Stop' (repo PowerShell error model), so
            # Write-Error throws rather than falling through to the exit. Asserting the throw is
            # asserting the real behaviour; asserting an exit code here would be asserting a path
            # that never executes.
            $missing = Join-Path $TestDrive 'no-such-root'

            { & $script:ScriptPath -LogRoot $missing -RetentionDays 30 } |
                Should -Throw -ExpectedMessage '*LogRoot not found*'
        }
    }
}
