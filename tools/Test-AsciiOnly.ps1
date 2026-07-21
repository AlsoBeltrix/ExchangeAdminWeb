#Requires -Version 5.1
<#
.SYNOPSIS
    Fails if any in-scope tracked source file contains a non-ASCII byte.

.DESCRIPTION
    Enforces the pure-ASCII rule for code and logging (.agents/decisions.md 2026-07-21,
    scoped by owner to code/logging only). Scans tracked .cs, .ps1, and .psm1 files for any
    byte > 0x7F and lists every offending file:line so the hit is fixable at a glance.

    Out of scope (NOT scanned): docs (.md), UI-rendered content (.razor markup), and
    Services/EmailService.cs (whose only non-ASCII is deliberate emoji inside HTML email
    bodies). Toolkit-owned files (AGENTS.md, .agents/playbooks/*) are not .cs/.ps1 and so
    are excluded implicitly.

    Exit 0 = clean; exit 1 = at least one non-ASCII byte found (also throws for CI).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = (git rev-parse --show-toplevel).Trim()
Push-Location $repoRoot
try {
    $tracked = git ls-files -- "*.cs" "*.ps1" "*.psm1"
    $excluded = @("Services/EmailService.cs")

    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $tracked) {
        if ($excluded -contains $rel) { continue }
        $full = Join-Path $repoRoot $rel
        if (-not (Test-Path $full)) { continue }

        $lineNo = 0
        foreach ($line in [System.IO.File]::ReadAllLines($full)) {
            $lineNo++
            foreach ($ch in $line.ToCharArray()) {
                if ([int]$ch -gt 127) {
                    $hits.Add(("{0}:{1}: {2}" -f $rel, $lineNo, $line.Trim()))
                    break
                }
            }
        }
    }

    if ($hits.Count -gt 0) {
        Write-Information "Non-ASCII characters found in code/logging source (pure-ASCII rule):" -InformationAction Continue
        $hits | ForEach-Object { Write-Information "  $_" -InformationAction Continue }
        Write-Information ("{0} offending line(s). Replace with ASCII equivalents (-- for dashes, -> for arrows, straight quotes, ...)." -f $hits.Count) -InformationAction Continue
        throw "ASCII lint failed: $($hits.Count) non-ASCII line(s) in tracked code/logging files."
    }

    Write-Information "ASCII lint passed: no non-ASCII in tracked .cs/.ps1/.psm1 (excluding UI files)." -InformationAction Continue
}
finally {
    Pop-Location
}