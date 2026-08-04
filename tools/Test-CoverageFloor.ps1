#Requires -Version 7.0
<#
.SYNOPSIS
    Fails when line coverage of the security-critical code drops below a committed floor.

.DESCRIPTION
    A ratchet, not a target. The floor is whatever these paths measured when it was last raised
    deliberately; the gate only stops it going DOWN.

    Scoped deliberately to authorization and permission code rather than the whole solution. A
    global percentage is satisfied by testing whatever is easiest, which is exactly how this repo
    reached 1145 tests at 27.8% line coverage with its two mailbox-permission services at 0%
    (measured 2026-08-03). Gating the code where a defect is an outage or a security exposure
    cannot be satisfied that way.

    Raising the floor is a deliberate commit that says so. Never lower it to make a build pass -
    that converts the ratchet into decoration.

.PARAMETER CoverageFile
    Path to a Cobertura XML report. Defaults to the newest under TestResults.

.PARAMETER Floor
    Minimum acceptable line-coverage percentage. Defaults to the committed value in
    .agents/review/coverage-floor.txt; pass explicitly only to test the gate itself.
#>
[CmdletBinding()]
param(
    [string] $CoverageFile,
    [double] $Floor = -1,
    [string] $FloorFile
)

$ErrorActionPreference = 'Stop'

# The floor lives in a committed file rather than a parameter default, so raising it is a visible
# one-line diff instead of an edit buried in a signature - and so the script and the docs cannot
# quietly disagree about it, which is what review finding tsr-1 caught (the default was 0.7 points
# below the measured value, leaving exactly enough slack to delete tests unnoticed).
if (-not $FloorFile) {
    $FloorFile = Join-Path $PSScriptRoot '..' '.agents' 'review' 'coverage-floor.txt'
}

if ($Floor -lt 0) {
    if (-not (Test-Path -LiteralPath $FloorFile)) {
        # Fail rather than fall back to a built-in default: a gate that silently supplies its own
        # threshold is the same failure class as one that silently matches no files.
        Write-Error "Coverage floor file not found: $FloorFile"
        exit 1
    }

    $floorText = (Get-Content -LiteralPath $FloorFile |
        Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') } |
        Select-Object -First 1)

    $parsed = 0.0
    if (-not [double]::TryParse($floorText, [ref] $parsed)) {
        Write-Error "Coverage floor file does not contain a number: '$floorText'"
        exit 1
    }

    $Floor = $parsed
}

# Scope: the code whose failure is an outage or a security exposure. Extend deliberately.
# Paths in the Cobertura report are repo-relative with no leading separator, and the separator
# differs by platform (Windows CI vs a Linux runner), so patterns are written unanchored and
# accept either. An over-anchored pattern silently matches nothing, which the empty-scope check
# below turns into a hard failure rather than a passing gate.
$scopePatterns = @(
    '^Authorization[\\/]',
    '^Services[\\/]ProtectedPrincipal',
    '^Services[\\/]PermissionValidator\.cs',
    '^Services[\\/]MailboxPermissionOutcome\.cs',
    '^Services[\\/]CalendarFolderIdentity\.cs',
    '^Services[\\/]BulkCsvRowLimit\.cs',
    '^Services[\\/]SectionAccess'
)

if (-not $CoverageFile) {
    $found = Get-ChildItem -Path 'TestResults' -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $found) {
        Write-Error "No coverage.cobertura.xml found. Run: dotnet test --collect:'XPlat Code Coverage'"
        exit 1
    }
    $CoverageFile = $found.FullName
}

[xml]$report = Get-Content -LiteralPath $CoverageFile

$covered = 0
$total = 0
$perFile = @{}

foreach ($class in $report.coverage.packages.package.classes.class) {
    $name = [string]$class.filename
    if (-not ($scopePatterns | Where-Object { $name -match $_ })) { continue }

    if (-not $perFile.ContainsKey($name)) { $perFile[$name] = @{ Cov = 0; Tot = 0 } }

    foreach ($line in $class.lines.line) {
        $total++
        $perFile[$name].Tot++
        if ([int]$line.hits -gt 0) {
            $covered++
            $perFile[$name].Cov++
        }
    }
}

if ($total -eq 0) {
    # An empty scope means the patterns stopped matching - a silently passing gate is worse than
    # no gate, because it reads as proof.
    Write-Error "Coverage scope matched no files. The patterns in this script are stale."
    exit 1
}

# Compare the UNROUNDED value; round only for display. Comparing a rounded percentage would
# reintroduce sub-0.05 slack - a smaller version of the defect tsr-1 recorded.
$exact = 100 * $covered / $total
$percent = [math]::Round($exact, 1)

Write-Host "Security-critical line coverage: $percent% ($covered / $total)"
Write-Host ''
foreach ($entry in $perFile.GetEnumerator() | Sort-Object { $_.Value.Cov / [math]::Max($_.Value.Tot, 1) }) {
    $filePct = [math]::Round(100 * $entry.Value.Cov / [math]::Max($entry.Value.Tot, 1))
    $short = ($entry.Key -replace '.*ExchangeAdminWeb\\', '')
    Write-Host ("  {0,3}%  {1}" -f $filePct, $short)
}
Write-Host ''

if ($exact -lt $Floor) {
    Write-Error "Coverage $percent% is below the floor of $Floor%. Add tests for the change, or raise the floor deliberately in a commit that explains why."
    exit 1
}

Write-Host "Coverage floor of $Floor% satisfied."
