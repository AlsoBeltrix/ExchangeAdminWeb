# aud-a38b: the promotion script still stops its pool the way aud-a35 stopped doing

**Severity**: MEDIUM. The same defect class as `aud-a35`, in the third script that finding
did not cover. (The 2026-10-20 codex review of `aud-a38` restated it as HIGH.)
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a4a7066146993eb49`
**Commit**: `25e7187` (merged)
**Reviewer**: implementer

## Evidence

`tools/promote-dev-to-prod.ps1`, `Stop-AppPoolChecked` (lines 232-233 at `0e18583`):

```powershell
Stop-WebAppPool -Name $Name -ErrorAction Stop
Start-Sleep -Seconds 3
```

Better than the sites `aud-a35` fixed - the exception is not swallowed, so a refused stop
does throw - but the three-second sleep is still a guess, and nothing confirms the worker
exited. `aud-a35` replaced exactly this pattern in `deploy.ps1` (two sites) and
`Install-ExchangeAdminWeb.ps1`, and `aud-a38` has just replaced the START half of this same
script with a verified call.

So the script now confirms its pool started and assumes its pool stopped. The call site
(line 339) is followed eight lines later by the mirror, `Invoke-RobocopyChecked` of `$dev`
into `$prod` with `/MIR` (line 347).

## Predicted observable failure

A promotion stops the prod pool, waits three seconds while a worker drains a long request,
and robocopies over a process that is still running. Some files are locked and skipped. The
promotion reports success and prod holds a mixture of two builds.

Narrower than `aud-a35` because the stop itself cannot fail silently here - only the wait
can be too short.

## Approach (proposed, not approved)

Replace the body with `Stop-AppPoolVerified -Name $Name` from `tools/AppPoolControl.psm1`,
which the script already imports as of `aud-a38`. Keep the `Assert-NoActiveBulkJobsBeforeRecycle`
warning and the `-Apply` / plan branch around it; only the stop-and-wait changes.

The existing `tests/ps/AppPoolControl.Tests.ps1` guard that asserts no bare
`try { Stop-WebAppPool ... } catch {}` survives in `deploy.ps1` and the installer should be
widened to this file, with the different shape accounted for - this one has no empty catch.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md` 2026-10-21, second entry). That approves
this follow-up, which was recorded "not approved". The plan is the proposed approach with the
supervisor's additions, and one correction to it:

1. `Stop-AppPoolChecked` keeps its shape: the `-Apply` gate, and the warn-not-block
   `Assert-NoActiveBulkJobsBeforeRecycle` before the stop. Only the stop-and-wait changes, to
   `Stop-AppPoolVerified -Name $Name` (imported relative to `$PSScriptRoot` since `aud-a38`).
2. **Nothing but `'Stopped'` gets past the stop.** This is the correction: the proposed
   approach, applied literally, regresses one case. The old `Stop-WebAppPool -ErrorAction Stop`
   refuses a pool that does not exist; `Stop-AppPoolVerified` returns `'Absent'` for it
   without throwing - right for an installer's first run, where nothing is serving. A plain
   replacement would have let a mistyped `-ProdAppPoolName` straight through to the mirror,
   with the real prod pool still running. An absent prod pool is now an error whose message
   says prod must exist to be promoted to; any other non-`Stopped` value is refused as well.
3. The dry-run plan line names the verified stop instead of `Stop-WebAppPool`, so the dry run
   describes what `-Apply` will do. No IIS call in a dry run.
4. The call site stays a direct statement of the script body, outside the promotion's `try`.
   A refusal therefore ends the script before the mirror. Inside the `try` it would be worse
   than ignored: the catch would answer it with a rollback - `robocopy /MIR` of the backup over
   the very application that would not stop.
5. Guards in `tests/ps/AppPoolControl.Tests.ps1`: the A35 "no bare stop" guard widened to this
   file's shape; AST guards for the import, the absence of any `Stop-WebAppPool` command, the
   single `Stop-AppPoolVerified` inside the stop step, and the stop's position (top level,
   before every robocopy whose destination is prod); behavioural tests that drive the real
   stop step through the module's injectable probes.
6. No version bump: ops-script-only, the `aud-a34`/`aud-a35`/`aud-a36`/`aud-a38` precedent.

## Files changed

- `tools/promote-dev-to-prod.ps1` - `Stop-AppPoolChecked`: the verified stop, the
  `'Stopped'`-only gate with the absent-prod message, the plan line; comments at the import,
  in the stop step and at the call site.
- `tests/ps/AppPoolControl.Tests.ps1` - the widened A35 guard and the
  `aud-a38b: the promotion confirms its pool stopped before mirroring over it` block.

## Guard proof

The stop step is behaviourally tested. The script runs its promotion at the top level, so it
cannot be dot-sourced; the tests lift `Stop-AppPoolChecked` and the `Write-*` helpers it calls
out of the parsed file, unchanged, and drive them with `-Apply` set. The real
`Stop-AppPoolVerified` runs underneath, with its probes injected through
`$PSDefaultParameterValues` from the test's own scope. `Write-Plan`, the jobs check and
`Stop-WebAppPool` are recording stand-ins - the last so that no run of the suite, a mutation
pasting the bare call back included, can reach the real cmdlet on a host that has IIS.

| Test (`tests/ps/AppPoolControl.Tests.ps1`) | Pins |
| --- | --- |
| `A35 call sites` / `leaves no bare Stop-WebAppPool before a mirroring deploy` (widened) | no line of promote starts with a `Stop-WebAppPool` call - this file's shape, which the empty-catch pattern could never match |
| `stops the pool it was given, through the verified stop, when IIS confirms Stopped` | the stop goes through `Stop-AppPoolVerified`; the bare cmdlet is not called |
| `refuses an absent prod pool, because prod must exist to be promoted to` | **the correction**: `'Absent'` is an error on a promotion; asserts its own probe was consulted |
| `lets a pool that will not stop end the promotion before the mirror` | **the finding**: the module's refusal propagates out of the stop step |
| `warns about active bulk jobs before the stop, and does not let them block it` | the jobs check runs first, and an active job does not stop the stop |
| `in a dry run prints the plan and touches no pool` | no probe and no stop; one plan line naming the verified stop; the jobs check in `-PlanOnly` |
| `imports AppPoolControl from beside the script` | the `$PSScriptRoot`-relative import |
| `calls Stop-WebAppPool nowhere in the promotion` | AST: zero `Stop-WebAppPool` commands anywhere in the file |
| `stops the pool through Stop-AppPoolVerified, once, inside the stop step` | AST: exactly one call, `-Name $Name`, inside `Stop-AppPoolChecked` |
| `stops the prod pool at the top level, before any robocopy writes into prod` | AST: one `Stop-AppPoolChecked -Name $ProdAppPoolName` call; it is a direct statement of the script body (no `try` around it); it ends before both robocopies whose destination is `$prod` (the mirror and the rollback), and there must be exactly two. The backup copy reads from prod and is meant to run while prod is serving |

Mutations on `tools/promote-dev-to-prod.ps1`, fixed md5 `170ECF477BAE6089192D772A39394389`
(pre-comment). Each was applied by exact-text replacement asserted to match once, confirmed
applied by md5, run against `AppPoolControl.Tests.ps1` and `DeployInvariants.Tests.ps1`, then
restored byte-identical, touched, md5 re-confirmed and the two suites re-run green (131 passed,
0 failed). The md5s are of the working-tree bytes, which are CRLF (`.gitattributes`:
`*.ps1 text eol=crlf`); the index holds LF.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the original `Stop-WebAppPool -Name $Name -ErrorAction Stop` and `Start-Sleep -Seconds 3` restored at the call site, the verified stop and its gate removed | `9EEBC97C87CEDB7B20E34D5617A9F03D` | 7: the widened A35 guard; the four apply-path behavioural tests; `calls Stop-WebAppPool nowhere`; `stops the pool through Stop-AppPoolVerified, once` |
| M2, present but dead: `deploy.ps1`'s "Absent is fine" shape pasted in - the verified stop still called, an absent pool warns and the promotion carries on | `48184B1F3CBA50041E7F6233FF9FA289` | 1: `refuses an absent prod pool` ("no exception was thrown") |
| M3: the dry-run branch's `return` removed, so a dry run falls into the apply path | `643FEB7A486B39FDEC475FD910D4182A` | 1: `in a dry run prints the plan and touches no pool` |
| M4: the call site wrapped in `try { ... } catch { Write-Warn ... }`, so a refusal falls through to the mirror | `B90FFE4C1167BEEF2BD9929C447CDDA1` | 1: `stops the prod pool at the top level, before any robocopy writes into prod` |

Post-comment md5 `8B947F84A42B255AB77DC5D9ABE4F9F7`.

**The first draft of the behavioural tests passed for the wrong reason, twice.** It handed
the probes to `$PSDefaultParameterValues` bare. A script block value there is EVALUATED to
produce the default, so the binding failed with only a warning, the module's real
`Test-Path IIS:\...` probe ran instead - and, with no `IIS:` drive in the test session,
returned False. The "stops through the verified stop" test failed, which is how it was
caught; but the absent-pool and dry-run tests passed regardless. The probes are now wrapped
in blocks that return them, and each test asserts that its own probe was the one consulted.

## Gates

One pwsh 7.6.6 session, Pester 6.2.0 imported before the analyzer, on the final code:

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors. 298 warnings against a baseline of
  287; 9 of the 11 are from the SDK's own PowerShell modules in `bin\` (untracked; the build
  that the db-mode suites trigger had not run when the baseline was taken), and 2 are
  `PSUseShouldProcessForStateChangingFunctions` on the test stand-ins `Stop-WebAppPool` and
  `New-PoolProbe`.
- `Invoke-Pester tests/ps`: 282 passed, 0 failed, 0 skipped (baseline 273; 9 new tests).
- Windows PowerShell 5.1.20348 `Parser::ParseFile`: `tools/promote-dev-to-prod.ps1`,
  `tools/AppPoolControl.psm1`, `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1` - 0 errors.
- `tools/Test-AsciiOnly.ps1` after `git add`: passed. `git diff --check HEAD`: clean.
- No .NET build or test run for its own sake - no C# or Razor changed. (The Pester db-mode
  suites built the app once, in Release, through `tests/ps/Get-BuiltApp.ps1`.)

## Known gaps

- **No promotion was run and no IIS was touched.** The stop step was driven through injected
  probes; the call-site position is a source guard. That `Stop-WebAppPool` throws for a pool
  that does not exist - the old shape's accidental refusal of a missing pool, which point 2 of
  the plan preserves - is the cmdlet's expected behaviour, not observed here: IIS is not
  readable without elevation on this host.
- **A stop that times out ends the promotion and restarts nothing.** If the pool is still
  `Stopping` after 60 seconds it may go on to stop, leaving prod down with its old binaries.
  The module's message tells the operator to stop it by hand and retry, and a retry ends with
  the verified start. Same behaviour as `deploy.ps1`'s stop sites; not changed.
- **A correctly spelled but WRONG pool is still stopped, confirmed, and mirrored past.**
  `deploy.ps1` checks which pool IIS binds the application to before stopping (review of
  `aud-a35`); this script has no such check. Out of this finding's scope; recorded and fixed
  as `aud-a38c`.
- **Under PowerShell 7 every pool reads as absent.** WebAdministration is Desktop-only on this
  host, so PS7 loads it through the compatibility session without the `IIS:` drive, and the
  module's `Test-Path IIS:\...` probe returns False. After this fix that refuses with "does not
  exist" before anything changes - safe, but misleading. Recorded and fixed as `aud-a38d`.
- The proposed approach expected `Stop-AppPoolVerified` to be a drop-in replacement. It is
  not, for the reason in point 2. Worth a reviewer's look at any other caller that treats the
  module's `'Absent'` as success: `deploy.ps1` does, and its record says that is safe "only
  once the binding has been checked" (`aud-a35`, independent review section).
