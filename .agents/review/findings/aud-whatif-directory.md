# aud-whatif-directory: the audit was wrong, and the ordering is fixed anyway

**Severity**: NONE as reported. The predicted behaviour does not occur. What landed is
hardening plus the test coverage that proved the finding false.
**Status**: FALSE POSITIVE - disproved, independently verified, ordering hardened
**Branch**: -- (direct-to-main)
**Commit**: `dd2f10d`

## The claim

From the lower-priority Wave 4 list of the 2026-10-16 audit:
*"`tools/SqliteConfigBackup.psm1:202` - `-WhatIf` creates a directory before
`ShouldProcess`."*

The code did read that way:

```powershell
$destDir = Split-Path -Parent $DestDbPath
if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null   # <- above the gate
}

if (-not $PSCmdlet.ShouldProcess($DestDbPath, "Replace with consistent copy of $SourceDbPath")) {
    return $DestDbPath
}
```

## Why it is wrong

`New-Item` declares `SupportsShouldProcess` itself. Called from inside an advanced function
that was invoked with `-WhatIf`, it inherits `$WhatIfPreference` and reports instead of
acting. A `-WhatIf` run printed

```
What if: Performing the operation "Create Directory" on target "Destination: ...\not-created-yet".
```

and created nothing.

**Demonstrated, not argued.** Two tests were written that fail if the directory appears, and
then run against the **unmodified** module - the real one, restored from HEAD, with the
`New-Item` still above the gate:

```
Passed: 18  Failed: 0
```

The existing `-WhatIf` test in the suite could never have caught this either way: it passes
a destination inside a directory that already exists, so `New-Item` was a no-op.

## What landed

**The ordering change is kept, as hardening rather than a fix.** Creating a directory is an
action and belongs below the gate. After the move the guarantee holds because of where the
call sits, not because of what `New-Item` happens to support - and someone swapping it for
`[System.IO.Directory]::CreateDirectory`, which honours nothing, would have made the audit
right.

**The comment in the code says all of this**, including that the audit was wrong. An
unexplained reordering invites someone to undo it.

**Two tests** that pin the behaviour from both sides:

| Test | What it pins |
| --- | --- |
| `Copy-SqliteDbFile -WhatIf does not create the destination directory` | the guarantee, with a destination whose parent does **not** exist |
| `Copy-SqliteDbFile still creates the destination directory when it acts` | that the fix did not simply stop creating it |

The second matters: a "fix" that deleted the `New-Item` would satisfy the first test and
break every real caller.

| Mutation | Result |
| --- | --- |
| directory creation moved back above the gate | **PASS - and that is the finding.** Restoring the audited code does not reproduce the audited behaviour |
| directory creation removed entirely | FAIL - 2 tests |

## Files changed

- `tools/SqliteConfigBackup.psm1` - the reorder and the explanation
- `tests/ps/SqliteConfigBackup.Tests.ps1` - the two tests

Also: this file came back from a `git stash` round-trip converted to CRLF, and was
normalised to LF before committing. The repo is LF.

No version bump; ops-script-only.

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 228 passed / 0 failed; PSScriptAnalyzer 0
errors. No .NET run - no C# or Razor changed.

## Why this is written up at all

A finding investigated and disproved is worth the same record as one fixed. Without it the
next sweep reads the plan, sees an unticked lower-priority item, and spends the same hour.
The plan's entry should be read as "checked, does not reproduce, ordering hardened".

It is also the second audit claim in this wave that did not survive contact with the code -
`aud-a35` and `aud-a38` found **more** than the audit listed, and this one found less. The
audit is a good map and not a specification.

## Known gaps

- **Verified on this machine's PowerShell only.** `New-Item`'s `-WhatIf` inheritance is
  long-standing documented behaviour, but it was confirmed on one host, one version. The
  hardening makes the question moot, which is most of the argument for keeping it.
- **Only `Copy-SqliteDbFile` was examined.** `Backup-SqliteConfigDb` in the same module does
  not declare `SupportsShouldProcess` at all - it always acts, and its callers gate it
  through `Invoke-PlanOrAction` instead. That is a different shape, not obviously wrong, and
  not audited here.
- No other `ShouldProcess` function in the repo was swept for the same ordering.

## Closeout

Landed on `master`. Independent review pending - and currently impossible: both review
harnesses are returning 401 (`.agents/state.md`). A reviewer should check the disproof
before the change: if the finding is real on some PowerShell version, the record above is
the thing that is wrong.

## Independent review - `gemini-4-argon-high`, 2026-10-20

Reviewed as batch 3 of the backlog. Prompt kept at `.agents/review/prompt-backlog-3.md`.

**Any earlier claim in this record that review was impossible was wrong** - the probes had
been invoked incorrectly. `.agents/state.md` carries that correction.

**The disproof was independently verified.** A second reader confirmed that `New-Item` is a
compiled cmdlet inheriting `$WhatIfPreference` from `Copy-SqliteDbFile`'s scope on both
Windows PowerShell 5.1 and PowerShell 7, so `-WhatIf` never created the directory and the
audit's claim was wrong. The ordering change stands as hardening.

Remediation landed in the follow-up commit, every fix mutation-proven.
