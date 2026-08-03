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
    Minimum acceptable line-coverage percentage for the scoped paths.
#>
[CmdletBinding()]
param(
    [string] $CoverageFile,
    # Measured 2026-08-03 at 64.7%. Set at the measured value, not at an aspiration: a floor above
    # reality is a red build that teaches people to ignore the gate.
    [double] $Floor = 64.0
)

$ErrorActionPreference = 'Stop'

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

$percent = [math]::Round(100 * $covered / $total, 1)

Write-Host "Security-critical line coverage: $percent% ($covered / $total)"
Write-Host ''
foreach ($entry in $perFile.GetEnumerator() | Sort-Object { $_.Value.Cov / [math]::Max($_.Value.Tot, 1) }) {
    $filePct = [math]::Round(100 * $entry.Value.Cov / [math]::Max($entry.Value.Tot, 1))
    $short = ($entry.Key -replace '.*ExchangeAdminWeb\\', '')
    Write-Host ("  {0,3}%  {1}" -f $filePct, $short)
}
Write-Host ''

if ($percent -lt $Floor) {
    Write-Error "Coverage $percent% is below the floor of $Floor%. Add tests for the change, or raise the floor deliberately in a commit that explains why."
    exit 1
}

Write-Host "Coverage floor of $Floor% satisfied."
