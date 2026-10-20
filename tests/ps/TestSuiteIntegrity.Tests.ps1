<#
Every Pester file in tests/ps must parse.

This exists because of an incident in this session rather than a theory. A stray edit left
an unterminated string in DeployInvariants.Tests.ps1. Pester could not parse the file, so it
ran NONE of its roughly eighty tests - and reported:

    P=170 F=0

Green. A third of the PowerShell suite had silently stopped running, and the only signal was
a passed-count that nobody compares against the previous run. It was noticed by accident,
while reading an unrelated mutation result.

That is the "passes for the wrong reason" trap one level up: not a test that asserts the
wrong thing, but a test file that is not there at all. A guard inside the broken file cannot
fire, so the check has to live somewhere else and parse its siblings from the outside.
#>

Set-StrictMode -Version Latest

Describe 'tests/ps integrity' {

    BeforeAll {
        $script:SuiteDir = $PSScriptRoot
        $script:Files = Get-ChildItem -Path $script:SuiteDir -Filter '*.Tests.ps1' -File
    }

    It 'finds the suite' {
        # If this directory is ever empty or moved, everything below would pass vacuously.
        $script:Files.Count | Should -BeGreaterThan 5
    }

    It 'parses every test file, so none is silently skipped' {
        $broken = @()

        foreach ($file in $script:Files) {
            $errors = $null
            $null = [System.Management.Automation.Language.Parser]::ParseFile(
                $file.FullName, [ref]$null, [ref]$errors)

            if ($errors) {
                $first = $errors | Select-Object -First 1
                $broken += "$($file.Name):$($first.Extent.StartLineNumber) $($first.Message)"
            }
        }

        $broken.Count | Should -Be 0 -Because (
            "a test file that does not parse contributes zero tests and the run still " +
            "reports success: " + ($broken -join '; '))
    }

    It 'has a Describe block in every test file' {
        # A file that parses but declares nothing also contributes zero tests, with the
        # same green result.
        $empty = $script:Files |
            Where-Object { (Get-Content $_.FullName -Raw) -notmatch '(?m)^\s*Describe\s' } |
            ForEach-Object { $_.Name }

        $empty.Count | Should -Be 0 -Because (
            "these files declare no Describe and so run nothing: " + ($empty -join ', '))
    }
}
