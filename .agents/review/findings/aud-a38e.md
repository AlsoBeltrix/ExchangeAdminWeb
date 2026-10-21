# aud-a38e: the cutover snapshots dev's database from a pool it assumed had stopped

**Severity**: LOW-MEDIUM. The defect class of `aud-a35` and `aud-a38b`, in the one ops script
that stops pools and was covered by neither: the shared-config cutover. Narrower in effect -
the cutover runs once, and what is lost is a write committed while a worker drains, not a
mixed build - but the loss is silent and lands in a file the run itself declares abandoned.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a6bb7956d82362f14`
**Commit**: `<pending>`
**Reviewer**: implementer (surfaced by the `aud-a38b` agent)

## Evidence

`tools/Move-ConfigDbToShared.ps1` at `ca6575a`, `Stop-AppPoolChecked` (lines 204-217):

```powershell
    Invoke-PlanOrAction "Stop app pool $Name (warns about active bulk jobs first)" {
        Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir -AppPath $AppPath | Out-Null
        $state = (Get-ItemProperty "IIS:\AppPools\$Name").State
        if ($state -ne 'Stopped') {
            Stop-WebAppPool -Name $Name -ErrorAction Stop
            Start-Sleep -Seconds 3
        }
        Write-Ok "App pool $Name stopped"
    }
```

Step 2 calls it for both pools (lines 708-709), and the statements after it are the snapshot:
step 3 backs up dev's database (`Backup-SqliteConfigDb -DbPath $devDb`, 712-718) and prod's
(719-727); step 4 copies dev's into the shared file (`Copy-SqliteDbFile -SourceDbPath $devDb
-DestDbPath $SharedDbPath`, the VACUUM INTO, 730-734). Step 6 then points both instances'
`ConfigStore:Path` at the shared file (752-755).

**Mechanism: no error at all.** `Stop-WebAppPool` returns once IIS has accepted the request;
the worker process then drains its in-flight requests before it exits, for up to the pool's
shutdown time limit (90 seconds by default - the documented IIS default, an assumption here, not
read from this host's configuration). The script waits a fixed three seconds and prints "App
pool X stopped" without asking IIS anything. A worker still running at that point raises
nothing, writes nothing to the error stream, and the snapshot proceeds. (A REFUSED stop does
throw - `-ErrorAction Stop` - into the catch that restarts both pools; that half was right
already, as it was in the promotion before `aud-a38b`.)

## Predicted observable failure

An operator runs the cutover with `-Apply` while a colleague saves a setting on dev. The dev
pool's worker is still draining that request three seconds later. "App pool
ExchangeAdminWebDev stopped" is printed, and the backup and the VACUUM INTO snapshot dev's
database while the worker can still commit. The save lands in dev's per-instance file after
the snapshot. Step 6 points dev at the shared file, which does not have it; the file that does
is the one the closing message says "is no longer opened by the app". The run reports success.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). The `aud-a38b` agent surfaced this site while surveying
which other scripts stop a pool before acting on the files behind it (`aud-a38b`'s own known
gap); the supervisor filed it under the same go.

1. **Import `tools/AppPoolControl.psm1`** relative to `$PSScriptRoot`, beside the script's two
   existing imports - the way `promote-dev-to-prod.ps1` imports it.
2. **`Stop-AppPoolChecked` keeps its shape**: the plan-or-act wrapper, and the warn-not-block
   bulk-job check first. The state read, the bare stop and the sleep become
   `$poolState = Stop-AppPoolVerified -Name $Name`, which issues the stop and polls IIS until
   the pool reports Stopped, throwing if it never does (60 s) or its state cannot be read.
3. **Only `'Stopped'` proceeds.** `'Absent'` - which `Stop-AppPoolVerified` returns without
   throwing, right for an installer's first run - is an error here: it means a wrong pool name,
   with the instance's real pool still up and writing. It gets its own message; any other value
   is refused as well. Both through `Write-Fail` (a throw). One function serves both pools, so
   this holds for prod too, which is equally required: step 1b already resolves both pools'
   identities before anything is stopped.
4. **The call sites do not move**: inside the cutover's top-level `try`, before step 3. A
   refusal throws into the existing catch, which - nothing repointed yet - restarts both pools
   and reports both instances unchanged. That is the right answer here, unlike the promotion,
   whose catch answers by mirroring a backup over the pool that would not stop (`aud-a38b`
   plan point 4).
5. **Plan mode** prints a plan line naming the confirmation; the action block, and with it
   every IIS call, does not run; the `-PlanOnly` bulk-job check still prints.
6. **Guards** in `tests/ps/MoveConfigDbToShared.Tests.ps1`: AST - the import at the top level
   before the first stop; no `Stop-WebAppPool` command anywhere; exactly one
   `Stop-AppPoolVerified -Name $Name`, inside the action block `Invoke-PlanOrAction` runs only
   outside plan mode; both `Stop-AppPoolChecked` calls direct statements of the top-level `try`,
   ending before all three snapshot commands. Behavioural - the lifted stop step driven through
   the module's injectable probes, as `aud-a38b` drives the promotion's.
7. **No version bump**: ops-script-only (`aud-a34`, `aud-a35`, `aud-a38`, `aud-a38b`). Pure
   ASCII; CRLF in the working tree (`.gitattributes`: `*.ps1 text eol=crlf`), LF in the index;
   no PS7-only syntax; parsed under Windows PowerShell 5.1.

Mutations planned, each at the call site: the pre-fix shape; `'Absent'` let through; the
result ignored; the dev stop moved below the snapshot; the import removed; the verified stop
moved out of the plan-mode wrapper.

## Approach

As planned. `tools/Move-ConfigDbToShared.ps1` after the fix:

- Lines 86-87: the import, with a one-line comment.
- Lines 207-233: `Stop-AppPoolChecked` - the plan line ("Stop app pool X and confirm IIS
  reports it Stopped before any database is copied (warns about active bulk jobs first)"), the
  jobs check, the comment, `Stop-AppPoolVerified`, the `'Stopped'`-only gate with the absent
  message, and "App pool X confirmed stopped".
- `.DESCRIPTION`, step 2: says the stop is confirmed and nothing is copied before.

The call sites are unchanged (724-725, before the first `Backup-SqliteConfigDb` at 730). The
existing plan-mode test's `PLAN Stop app pool ...` expectations still hold against the new
plan line, unedited.

## Files changed

- `tools/Move-ConfigDbToShared.ps1` - the import, `Stop-AppPoolChecked`, the step-2 help line.
- `tests/ps/MoveConfigDbToShared.Tests.ps1` - the `aud-a38e` block.
- This record.

## Guard proof

The stop step is behaviourally tested. The cutover runs at the top level, so it cannot be
dot-sourced; the tests lift `Stop-AppPoolChecked`, `Invoke-PlanOrAction` and the `Write-*`
helpers out of the parsed file, unchanged, and run them with the real `Stop-AppPoolVerified`
underneath, its probes injected through `$PSDefaultParameterValues`, each wrapped in a block
that returns it (the `aud-a38b` lesson) and each test asserting its own probe was asked.
`Write-Plan`, the bulk-job check and `Stop-WebAppPool` are recording stand-ins, and
`Get-ItemProperty` is mocked to answer `'Started'`, so no run - the pre-fix shape pasted back
included - reaches IIS.

| Test (`tests/ps/MoveConfigDbToShared.Tests.ps1`, `aud-a38e: ...`) | Pins |
| --- | --- |
| `stops the pool through the verified stop and goes on only when IIS confirms Stopped` | the stop goes through `Stop-AppPoolVerified` (its exists probe and stop action were used); the bare cmdlet is not called |
| `lets a pool that will not stop end the cutover before any database is copied` | **the finding**: the module's refusal ("is still 'Started'") propagates out of the stop step |
| `refuses an absent pool, because a cutover stops two instances that must exist` | `'Absent'` is an error here, naming the pool and "No database was copied"; asserts its own probe was consulted |
| `refuses any answer from the verified stop other than Stopped` | the gate itself: `Stop-AppPoolVerified` mocked to answer `'Stopping'` is refused, the reported state named |
| `warns about active bulk jobs before the stop, and does not let them block it` | order (jobs check, then the stop) and an active job does not stop the stop |
| `in plan mode prints one plan line and touches no pool` | no probe, no stop, no IIS read; one plan line naming the confirmation; the jobs check in `-PlanOnly` |
| `imports AppPoolControl from beside the script, at the top level, before any pool is stopped` | AST: exactly one such `Import-Module`, a direct statement of the script body, ending before the first `Stop-AppPoolChecked` call |
| `calls Stop-WebAppPool nowhere in the cutover` | AST: zero `Stop-WebAppPool` commands (the restore instructions print it as text, which is a string) |
| `stops through Stop-AppPoolVerified once, inside the action plan mode never runs` | AST: exactly one call, `Stop-AppPoolVerified -Name $Name`, inside the script block handed to the one `Invoke-PlanOrAction` in the stop step |
| `stops both pools before any database is backed up or copied` | AST: two `Stop-AppPoolChecked` calls, dev then prod, both direct statements of the top-level `try`; the snapshot commands (two `Backup-SqliteConfigDb`, one `Copy-SqliteDbFile` - there must be exactly three) all start after the prod stop ends |

Mutations on `tools/Move-ConfigDbToShared.ps1`, fixed md5 `D1A65CEEA8D4DB0EB103A320AD6FC299`
(pre-comment). The md5s are of the working-tree bytes, which are CRLF; the index holds LF.
Each was applied by exact-text replacement asserted to match once (M4 and M5 as whole files
built the same way), confirmed by md5, run against `MoveConfigDbToShared.Tests.ps1` and
`DeployInvariants.Tests.ps1` (the other suite that reads this script; 154 tests), then the
fixed bytes restored, touched and md5-confirmed. The final, commented file ran green in the
full suite (Gates).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the pre-fix shape restored at the call site - the `Get-ItemProperty` state read, bare `Stop-WebAppPool`, `Start-Sleep -Seconds 3`, "stopped", the old plan line | `3756A39752612F02DE122E37CB606107` | 8: all six behavioural tests; `calls Stop-WebAppPool nowhere`; `stops through Stop-AppPoolVerified once` |
| M2, present but let through: `deploy.ps1`'s shape - the gate admits `'Absent'` with a warning | `73DD2350B9C9749B757BAABD6E4D2427` | 1: `refuses an absent pool` |
| M3: the verified stop called and its answer ignored (`$null = Stop-AppPoolVerified -Name $Name`, no gate) | `31A0188D8E36AE39A08576A36C099CCE` | 2: `refuses an absent pool`; `refuses any answer ... other than Stopped` |
| M4: the dev stop moved below the snapshot, just before step 5 | `FD156F7CBD5FA23742CEB2632DDD6FFF` | 1: `stops both pools before any database is backed up or copied` |
| M5: the `AppPoolControl.psm1` import removed | `7BEDA2943519E52A3E0278549366A5AA` | 1: `imports AppPoolControl from beside the script ...` |
| M6: the verified stop moved out of the plan-mode wrapper, above `Invoke-PlanOrAction` | `3D835A41774C23B18A41742D638C39E0` | 3: `warns about active bulk jobs before the stop ...`; `in plan mode prints one plan line and touches no pool`; `stops through Stop-AppPoolVerified once, inside the action ...` |

Post-comment md5 `A5F9840EE38609E3E67DB09E385ACEBC`.

## Gates

One pwsh 7.6.6 session, Pester 6.2.0 imported before the analyzer, on the final code (on top
of `cdr-followup-1`, `f2cfc6c`):

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors. 325 warnings, 36 of them under the
  untracked `bin\` build output; the two this change adds are
  `PSUseShouldProcessForStateChangingFunctions` on the test stand-ins `Stop-WebAppPool` and
  `New-PoolProbe` (the `aud-a38b` precedent). No new finding in the script itself.
- `Invoke-Pester tests/ps`: 287 passed, 0 failed, 0 skipped, 0 not run (10 of them new). The
  db-mode suites used the Release build already in `bin\Release` (`tests/ps/Get-BuiltApp.ps1`).
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile`: `tools/Move-ConfigDbToShared.ps1`,
  `tools/AppPoolControl.psm1`, `tests/ps/MoveConfigDbToShared.Tests.ps1` - 0 errors each.
- `tools/Test-AsciiOnly.ps1` after `git add`: passed. `git diff --check HEAD`: clean.
- No .NET build or test for its own sake - no C# or Razor changed in this commit.

## Known gaps

- **No cutover was run and no IIS was touched.** The stop step was driven through injected
  probes; the call-site order is a source guard. Plan mode was exercised by the existing suite.
- **A correctly spelled but WRONG pool is still stopped, confirmed and copied past.** The
  cutover never asks IIS which pool serves `-DevPublishPath`, the `aud-a38c` class. The fix is
  that finding's `Assert-AppPoolServesPath`, which is on `master` but not in this branch's base,
  so it is recorded as `aud-a38g` rather than fixed here.
- **The start side was still a request, not a confirmation.** `Start-AppPoolChecked` reported
  "started" without asking IIS, including on the failure path that restarts both pools. Fixed
  separately as `aud-a38f`.
- **Under PowerShell 7 nothing is stopped at all**: WebAdministration loads there without its
  `IIS:` drive (`aud-a38d`), and the step 1b identity read refuses on the missing drive before
  step 2. The verified stop is never reached on that path; the refusal message is the raw
  provider error rather than the promotion's explanation.
- **A stop that times out** ends the cutover, and the catch then tries to restart both pools; a
  pool still Stopping may refuse, in which case the catch warns to start it by hand. Unchanged
  behaviour of the catch.
