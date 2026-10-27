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

    It 'has no test that can skip itself, because CI fails the job on any skip (cdt-50)' {
        # .github/workflows/ci.yml fails the powershell job when SkippedCount is above zero, but only on
        # a runner where the skip actually happens. A skip path that never fires here - an 8.3 fixture,
        # a missing tool - turns CI red somewhere else, later. So none may exist: no Set-ItResult that
        # skips, pends or gives up, and no It, Context or Describe -Skip. Found by AST, so comments and
        # strings do not count.
        $paths = @(foreach ($file in $script:Files) {
                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
                $commands = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
                foreach ($command in $commands) {
                    $name = $command.GetCommandName()
                    $parameters = @($command.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] } | ForEach-Object { $_.ParameterName })
                    $skips = ($name -eq 'Set-ItResult' -and @($parameters | Where-Object { $_ -in 'Skipped', 'Pending', 'Inconclusive' }).Count -gt 0) -or
                        ($name -in 'It', 'Context', 'Describe' -and $parameters -contains 'Skip')
                    if ($skips) { "$($file.Name):$($command.Extent.StartLineNumber): $($command.Extent.Text.Split([char]10)[0].Trim())" }
                }
            })
        $paths.Count | Should -Be 0 -Because (
            "a test that can skip itself fails CI wherever the skip fires: " + ($paths -join '; '))
    }
}
