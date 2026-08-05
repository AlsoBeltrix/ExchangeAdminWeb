#Requires -Version 7.0
<#
    Tests for the coverage gate itself.

    Review finding tsr-1: the gate shipped with its floor set 0.7 points BELOW the measured
    baseline, so scoped coverage could fall and CI would still pass. A gate nobody tests is a
    claim, not a control - the same lesson the whole test-remediation effort is about, applied to
    the thing enforcing it.

    These run the real script against synthetic Cobertura reports, so they exercise the actual
    comparison rather than a restatement of it.
#>

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' '..' 'tools' 'Test-CoverageFloor.ps1' |
        Resolve-Path | Select-Object -ExpandProperty Path

    # Builds a Cobertura report whose scoped coverage is a known ratio: one in-scope file with
    # $covered hit lines out of $total.
    function New-CoverageReport {
        param(
            [int] $Covered,
            [int] $Total,
            [string] $FileName = 'Authorization\GroupMembershipChecker.cs'
        )

        $lines = 1..$Total | ForEach-Object {
            $hits = if ($_ -le $Covered) { 1 } else { 0 }
            "          <line number=`"$_`" hits=`"$hits`" />"
        }

        $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0" branch-rate="0">
  <packages>
    <package name="ExchangeAdminWeb">
      <classes>
        <class name="X" filename="$FileName">
          <lines>
$($lines -join "`n")
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@

        $path = Join-Path $TestDrive ("cov-{0}.xml" -f [guid]::NewGuid().ToString('N'))
        Set-Content -LiteralPath $path -Value $xml -Encoding utf8
        return $path
    }

    function New-FloorFile {
        param([string] $Content)
        $path = Join-Path $TestDrive ("floor-{0}.txt" -f [guid]::NewGuid().ToString('N'))
        Set-Content -LiteralPath $path -Value $Content -Encoding utf8
        return $path
    }

    function Invoke-Gate {
        param([string] $Report, [string] $FloorFile)
        $null = pwsh -NoProfile -File $script:ScriptPath -CoverageFile $Report -FloorFile $FloorFile 2>&1
        return $LASTEXITCODE
    }

    # Runs the gate with NO -CoverageFile, from inside a sandbox directory, so the auto-discovery
    # path (and therefore the staleness guard) is what gets exercised. Defined here rather than
    # inside its Describe because Pester 5 does not resolve functions declared in a Describe body.
    function Invoke-AutoDiscoverGate {
        param([string] $Sandbox, [string] $FloorFile)
        Push-Location $Sandbox
        try {
            $null = pwsh -NoProfile -File $script:ScriptPath -FloorFile $FloorFile 2>&1
            return $LASTEXITCODE
        }
        finally { Pop-Location }
    }
}

Describe 'Coverage floor gate' {

    It 'fails when coverage is below the floor' {
        # THE finding. 649/1000 = 64.9%, floor 65.1. Before the fix the shipped default of 64.0
        # let this pass.
        $report = New-CoverageReport -Covered 649 -Total 1000
        $floor = New-FloorFile '65.1'

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 1
    }

    It 'passes at exactly the floor' {
        # A ratchet must not fail the state it was set from, or the first green build is red.
        $report = New-CoverageReport -Covered 651 -Total 1000
        $floor = New-FloorFile '65.1'

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 0
    }

    It 'passes when coverage is above the floor' {
        $report = New-CoverageReport -Covered 800 -Total 1000
        $floor = New-FloorFile '65.1'

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 0
    }

    It 'fails on a drop too small to show after rounding' {
        # 65.06% displays as "65.1%" but is below a 65.1 floor. Comparing the ROUNDED value would
        # pass this - a smaller instance of the same slack tsr-1 found.
        $report = New-CoverageReport -Covered 6506 -Total 10000
        $floor = New-FloorFile '65.1'

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 1
    }

    It 'fails when the floor file is missing' {
        # A gate that supplies its own threshold when the committed one vanishes is not a gate.
        $report = New-CoverageReport -Covered 800 -Total 1000

        Invoke-Gate -Report $report -FloorFile (Join-Path $TestDrive 'no-such-floor.txt') | Should -Be 1
    }

    It 'fails when the floor file is not a number' {
        $report = New-CoverageReport -Covered 800 -Total 1000
        $floor = New-FloorFile 'quite high'

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 1
    }

    It 'ignores comment lines in the floor file' {
        $report = New-CoverageReport -Covered 800 -Total 1000
        $floor = New-FloorFile "# a comment`n# another`n65.1"

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 0
    }

    It 'fails when no file matches the scope' {
        # An empty scope means the path patterns went stale. Passing then would report proof it
        # does not have - worse than having no gate at all.
        $report = New-CoverageReport -Covered 800 -Total 1000 -FileName 'Components\Pages\Index.razor'
        $floor = New-FloorFile '65.1'

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 1
    }

    It 'counts only in-scope files' {
        # Out-of-scope code must not dilute or inflate the number - the whole point of scoping is
        # that easy-to-test code cannot satisfy the gate.
        $report = New-CoverageReport -Covered 649 -Total 1000
        $floor = New-FloorFile '65.1'

        # Same in-scope shortfall as the first test; an unrelated fully-covered file is appended.
        $xml = Get-Content -LiteralPath $report -Raw
        $extra = @'
        <class name="Y" filename="Components\Pages\Index.razor">
          <lines><line number="1" hits="1" /></lines>
        </class>
'@
        $xml = $xml -replace '</classes>', "$extra`n      </classes>"
        Set-Content -LiteralPath $report -Value $xml -Encoding utf8

        Invoke-Gate -Report $report -FloorFile $floor | Should -Be 1
    }
}

Describe 'Stale report guard' {
    # dotnet test writes a new GUID-named directory per run and never cleans up, so the
    # newest-wins auto-discovery is only correct if nothing interrupted the sequence. Scoring a
    # stale report is worse than not running the gate, because the result reads as proof.
    # Observed 2026-08-04: a floor check silently scored an earlier run's report.

    BeforeEach {
        $script:Sandbox = Join-Path $TestDrive ("stale-{0}" -f [guid]::NewGuid().ToString('N'))
        $script:Results = Join-Path $script:Sandbox 'TestResults'
        New-Item -ItemType Directory -Path (Join-Path $script:Results 'run-1') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:Sandbox 'bin') -Force | Out-Null
    }

    It 'fails when the auto-discovered report predates the test assembly' {
        $report = Join-Path $script:Results 'run-1' 'coverage.cobertura.xml'
        Copy-Item (New-CoverageReport -Covered 800 -Total 1000) $report
        (Get-Item $report).LastWriteTime = (Get-Date).AddHours(-2)

        # A build newer than the report: the report cannot describe this assembly.
        $asm = Join-Path $script:Sandbox 'bin' 'ExchangeAdminWeb.Tests.dll'
        Set-Content -LiteralPath $asm -Value 'x'
        (Get-Item $asm).LastWriteTime = Get-Date

        # Coverage is 80% against a 65.1 floor, so WITHOUT the guard this passes. The failure can
        # only come from the staleness check.
        Invoke-AutoDiscoverGate -Sandbox $script:Sandbox -FloorFile (New-FloorFile '65.1') | Should -Be 1
    }

    It 'passes when the auto-discovered report is newer than the test assembly' {
        $asm = Join-Path $script:Sandbox 'bin' 'ExchangeAdminWeb.Tests.dll'
        Set-Content -LiteralPath $asm -Value 'x'
        (Get-Item $asm).LastWriteTime = (Get-Date).AddHours(-2)

        $report = Join-Path $script:Results 'run-1' 'coverage.cobertura.xml'
        Copy-Item (New-CoverageReport -Covered 800 -Total 1000) $report
        (Get-Item $report).LastWriteTime = Get-Date

        Invoke-AutoDiscoverGate -Sandbox $script:Sandbox -FloorFile (New-FloorFile '65.1') | Should -Be 0
    }

    It 'does not apply the guard to an explicitly passed report' {
        # An explicit -CoverageFile is a deliberate choice - including by this test file, which
        # would otherwise have to fake an assembly for every single case above.
        $report = New-CoverageReport -Covered 800 -Total 1000
        (Get-Item $report).LastWriteTime = (Get-Date).AddYears(-1)

        Invoke-Gate -Report $report -FloorFile (New-FloorFile '65.1') | Should -Be 0
    }
}

Describe 'Committed coverage floor' {

    It 'is a parseable number' {
        $path = Join-Path $PSScriptRoot '..' '..' '.agents' 'review' 'coverage-floor.txt'
        Test-Path -LiteralPath $path | Should -BeTrue

        $value = (Get-Content -LiteralPath $path |
            Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') } |
            Select-Object -First 1)

        $parsed = 0.0
        [double]::TryParse($value, [ref] $parsed) | Should -BeTrue
        $parsed | Should -BeGreaterThan 0
    }
}
