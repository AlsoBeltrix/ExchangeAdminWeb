# cdt-14: the deploy manifest writer calls Substring on a commit that can be null

**Severity**: LOW, latent. `deploy.ps1`'s `Write-DeploymentManifest` prints the commit it recorded
with `$gitCommit.Substring(...)`. When `git` fails, Windows PowerShell 5.1 turns its stderr into a
terminating error that the surrounding `catch` maps to `"unknown"`, so the call works; PowerShell
7 does not, `$gitCommit` is `$null`, and the call throws. The writer runs inside the `finally`
that restarts the pool, so that throw would replace the deploy's own outcome after the files were
deployed (Known Failure Class 1). `deploy.ps1` refuses to run under PowerShell 7 today (the IIS:
drive check), so this cannot fire yet; the same pattern in `tools/deploy-pipeline.ps1` was
already fixed with a length guard (`5e31477`).
**Status**: Fixed on branch; awaiting merge
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `<pending>`
**Reviewer**: implementer (surfaced by the cdt-8/9/10 agent)

## Evidence

`deploy.ps1` at `61b5916`:

```
117:     $gitCommit = try { (git -C $RepoRoot rev-parse HEAD 2>$null) } catch { "unknown" }
...
131:     $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
132:     Write-Success "Deployment manifest written: v$($manifest.version) @ $($gitCommit.Substring(0, [Math]::Min(8, $gitCommit.Length)))"
```

called from both inner `finally` blocks, after the pool restart (lines 637 and 921).

`git` is a native command: it signals failure by exit code and stderr, never by throwing, so the
`catch` only catches what the shell itself raises. **Mechanism, reproduced** (scratch probe, line
117's exact statement under `$ErrorActionPreference = "Stop"`, `-C` pointed at a directory that is
not a repository):

```
PS 5.1.20348.5756 gitCommit=[unknown] isNull=False
substring=[unknown]
PS 7.6.6 gitCommit=[] isNull=True exit=128
substring THREW: RuntimeException: You cannot call a method on a null-valued expression.
```

Windows PowerShell 5.1 wraps redirected stderr in an error record that `Stop` makes terminating,
so the `catch` runs; PowerShell 7 (7.6.6 here) does not apply `$ErrorActionPreference` to
redirected native stderr, so nothing is caught and the empty output is `$null`. Line 132 then
throws from inside the `finally` - after `Set-Content` has written the manifest, and in place of
whatever the deploy's own result was. The pipeline's own copy of this expression had exactly this
crash and was fixed with `if ($manifest.gitCommit.Length -ge 8) { ...Substring(0, 8) } else {
$manifest.gitCommit }` (`tools/deploy-pipeline.ps1:180`).

## Plan (approved by the owner's EVERYTHING go)

1. Shorten only what is long enough: `$shortCommit = $gitCommit`, then `if ($gitCommit -and
   $gitCommit.Length -ge 8) { $shortCommit = $gitCommit.Substring(0, 8) }`, and print
   `$shortCommit`. The pipeline's guard, plus a null short-circuit so it does not depend on
   `$null.Length` being 0 (true without strict mode, which `deploy.ps1` does not set; a property
   read on `$null` throws under `Set-StrictMode -Version Latest`).
2. Nothing else changes: the probe, the manifest's contents (`gitCommit` is still `$null` under 7
   and `"unknown"` under 5.1, both of which the pipeline treats as no usable commit), the callers.

Assumption, recorded: a printed `@ ` with nothing after it when no commit is known is acceptable;
it is the same value the manifest records, and the pipeline prints it the same way.

Constraints: as cdt-8 (5.1, ASCII, LF, no plan mode, no version bump).

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

Guards: a behavioural test that lifts `Write-DeploymentManifest` (and `Write-Success`) from the
script and runs it under PowerShell 7 - Pester's runtime, where `git` failing yields `$null` -
against a temporary publish folder, once with a repository root that does not exist (must not
throw; the manifest is written) and once with this repository (the line names the first eight
characters of HEAD); and a structural test that the only `Substring` call in the function sits in
the then-branch of `if ($gitCommit -and $gitCommit.Length -ge 8)`. Mutations: the guard removed
(the `61b5916` line); a guard that guards nothing (`-ge 0`).

## Approach

As planned. In `deploy.ps1` after the fix, lines 132-138: a comment, `$shortCommit = $gitCommit`,
`if ($gitCommit -and $gitCommit.Length -ge 8) { $shortCommit = $gitCommit.Substring(0, 8) }`, and
the success line printing `$shortCommit`. Probed under both shells before the change: with
`$gitCommit` `$null`, `"unknown"` and a 16-character string the guard yields empty, `unknown` and
the first eight characters, with no error, under Windows PowerShell 5.1.20348 and PowerShell 7.6.6.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `writes and reports the manifest even when git cannot answer (cdt-14)` | Lifts `Write-DeploymentManifest` and `Write-Success` from the script and runs them under PowerShell 7 (Pester's runtime) with deploy.ps1's own `$ErrorActionPreference = 'Stop'`, against a temporary publish folder: with a repository root that does not exist (git exits 128, `$gitCommit` is `$null`) the call does not throw, prints "Deployment manifest written: " and writes a manifest whose `gitCommit` is null, empty or `unknown`; with this repository it prints `@ ` and the first eight characters of HEAD |
| `shortens the manifest commit only when there is one long enough to shorten (cdt-14)` | The function's only `Substring` call sits in the then-branch of an `if` whose condition is exactly `$gitCommit -and $gitCommit.Length -ge 8` |

The `Stop` in the first test matters: under the default `Continue`, `$null.Substring(...)` is a
statement-terminating error that is written and skipped, so the old line would not escape the
function and the test would pass on the bug.

Mutations on `deploy.ps1` (fixed md5 `287006C213E1ACE0C1B95BA69B5FE5EA`, pre-comment; md5s of the
content as committed, LF), each run against the whole `DeployInvariants.Tests.ps1`, then the
original bytes restored, touched, md5 re-confirmed; the restored file re-run green (97 passed).
Post-comment md5 `4EC160E0796780E162B8AD4CD5677C89` (97 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| S1 the guard removed (pre-fix shape, `61b5916`) | `8BFDD14A07CA2CFF7E1ABC4CAACDC71A` | the behavioural test ("You cannot call a method on a null-valued expression." - the crash itself); the structural test ("Expected a value, because Substring on a null or short commit throws, but got $null or empty") |
| S2 a guard that guards nothing, `if ($gitCommit.Length -ge 0)` | `A3FC056A2ECB00A41FA65E5601974CE5` | the behavioural test (same crash); the structural test (the condition text) |
| S3 the pipeline's exact guard, without the null short-circuit, `if ($gitCommit.Length -ge 8)` | `6FFF52DBD95DB9524E2E2E8913EB6D14` | the structural test only (the condition text) - expected: without strict mode `$null.Length` is 0, so behaviour is the same; the test pins the form that also survives `Set-StrictMode` |
