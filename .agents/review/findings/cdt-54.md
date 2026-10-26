# cdt-54: other applications sharing the -AppPoolName pool are stopped with it and their jobs are not read

**Severity**: LOW - pre-existing. `deploy.ps1` stops the named pool; any other IIS application served by the same pool stops too, and the active-jobs warning (cdt-49) reads only this instance's jobs database.
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-23 evening from the cdt-36 agent's third hand-back; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-53 commit `4b94d08f`
**Commit**: the `fix(cdt-54)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `fb2b288f`. Trigger: a shared pool. Predicted failure: another app's in-flight work is interrupted with no warning.

Confirmed at `4b94d08f`, where `deploy.ps1` and `tools/` are as they were at `fb2b288f`. Both of deploy.ps1's
stops are preceded by checks that look only at this instance.

The upgrade branch:

```
735:         Write-Step "Stopping app pool"
736:         # Warn (do not block) if a durable bulk job is active - recycling interrupts it.
737:         Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $runtimeConfigDir -AppPath $StagingPath | Out-Null
...
762:         $null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath -NestedNodes Refuse
...
768:         $poolState = Stop-AppPoolVerified -Name $AppPoolName
```

The fresh-install branch:

```
1069:         Write-Step "Stopping app pool for file deployment"
...
1074:         $stoppedConfigDir = Join-Path $PublishPath "config"
1075:         if (-not $movesApplication -or -not (Test-SamePhysicalFolder -Path $stoppedConfigDir -Other $runtimeConfigDir)) {
1076:             Write-Host "       Active bulk jobs of app pool '$AppPoolName', with its files at '$PublishPath', which is stopped next:" -ForegroundColor DarkGray
1077:             Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $stoppedConfigDir -AppPath $StagingPath | Out-Null
1078:         }
...
1102:         $null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath -NestedNodes Refuse
...
1108:         $poolState = Stop-AppPoolVerified -Name $AppPoolName
```

The jobs check reads one folder's `config\`. `Assert-AppPoolServesPath` lists every IIS node, but it
then keeps only those at or inside the folder (`tools/AppPoolControl.psm1` 508-509: `$inside = @($applications | Where-Object { & $isInFolder $_ })`,
`$serving = @($inside | Where-Object { & $isAtFolder $_ })`). So an application elsewhere that runs
under the same pool is never looked at. The stop takes it down with this instance, unannounced, and
its bulk jobs, if it is another instance of this app, are never read.

## Approach

Written before the code. Line numbers are at `4b94d08f`.

**Warn, not refuse - the choice the coordinator left open.** Three reasons:

1. **It is the event the owner has already ruled on.** `tools/JobStateWarning.psm1`'s header (12)
   reads: "Policy (owner-requested): WARN, do not hard-block". The reason it gives is that a stuck
   job "must never be able to prevent the very recycle that clears it". Another application in the
   pool loses the same thing to the same stop: in-flight work, until the pool runs again. A refusal
   would be a new hard gate on that event, and the owner has not asked for one.
2. **What a refusal would protect against is already refused.** Nothing of the other application's
   is replaced or deleted. Any node inside `-PublishPath` is refused before the stop whatever its
   pool (aud-a38i), and the folder's own nodes must run under the pool (cdt-28). What is left is
   availability, which this run already takes from its own instance and only warns about.
3. **A switch would add no safety.** An installation that shares a pool on purpose would need it on
   every run, so it would be a permanent extra step, not a check.

**Change.**

1. `tools/AppPoolControl.psm1` gets a new exported function, `Get-AppPoolOtherApplication -Name
   -PhysicalPath [-ApplicationsProbe]`.
   - It lists every site root and application IIS runs under the pool whose files are neither at
     the folder nor inside it. The folder's own nodes are this instance, and a node inside the
     folder is refused before the stop anyway.
   - Folders compare as in `Assert-AppPoolServesPath`: lexically at a directory boundary
     (`Test-PhysicalPathInside`), or by final path when both resolve (cdt-46). Pool names compare
     case-insensitively.
   - The default probe reads `Get-Website` and `Get-WebApplication -Site`, the same reads as the
     first half of `Assert-AppPoolServesPath`'s probe. It reads no virtual directories: each one
     belongs to an application, and that application is listed if it is another one. Counting
     them would report this instance's own virtual directories as other applications.
   - It errs toward listing. A node is left out only when its lexical or final path puts it at or
     inside the folder. An application with no folder, or one whose folder cannot be resolved, is
     listed.
   - An unreadable listing throws, naming the pool and the folder.
2. `deploy.ps1` gets a new function, `Write-AppPoolSharingReport -AppPoolName -PublishPath
   -StagingPath`. deploy.ps1 has no plan mode, so, like the cdt-49 report, it prints what it found
   either way and changes nothing:
   - **No other application:** one DarkGray line saying that the pool serves no IIS application
     other than this instance at the folder.
   - **Other applications:** a `Write-Warn` that names each one (its location and its files) and
     says that it stops with the pool. Then, for each one with a folder, its active bulk jobs: a
     DarkGray line naming it, and `Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $otherConfigDir
     -AppPath $StagingPath`, where `$otherConfigDir` is its folder's `config\`, with environment
     variables expanded. This uses the staged build's db tool, as cdt-49 does. A folder with no
     jobs database is not an instance of this app, and the existing check reports nothing for it.
   - **The listing cannot be read:** a `Write-Warn` saying that the stop may take down other
     applications whose jobs are not read. The run is not refused here. It is still refused before
     the stop: `Assert-AppPoolServesPath`, which reads the same IIS listing, refuses an unreadable
     one.
3. Both branches call it once, unconditionally, at the start of their stop step. The call goes
   straight after `Write-Step "Stopping app pool"` (735) and after `Write-Step "Stopping app pool
   for file deployment"` (1069), so it comes before this instance's own jobs report, the binding
   checks and the stop. At the start of the step it does not disturb any statement-order pin
   between the binding read and the stop (cdt-19, cdt-28). A comment names cdt-54.

The other stops of the same kind - the promotion, the installer and the cutover - are outside this
record's scope, which names `deploy.ps1`. They are noted as candidates.

Constraints:

- Invariant 6: both files stay pure ASCII.
- Invariant 7: no pool, site or host is named; the list is whatever IIS reports.
- No new mention of the stop function in deploy.ps1. `AppPoolControl.Tests` counts its occurrences.
- No version bump.

Guards:

- `tests/ps/AppPoolControl.Tests.ps1`, new Describe `cdt-54: ...`, through `-ApplicationsProbe`:
  - another application under the pool is listed, and so is one with no folder;
  - the folder's own node is not listed, under any spelling (case, a trailing separator,
    `%VAR%`, a junction to it);
  - a node inside the folder is not listed;
  - other pools' nodes are not listed;
  - pool names match case-insensitively;
  - an unreadable listing throws, naming the pool;
  - the default probe, with in-module stand-ins for `Get-Website` and `Get-WebApplication`, lists
    the site root and the applications, and never calls `Get-WebConfiguration`.
- `tests/ps/DeployInvariants.Tests.ps1`, new tests:
  - The report, lifted from deploy.ps1 and run with the real module, a probe injected through
    `$PSDefaultParameterValues`, and `Write-Host` and the jobs check stubbed:
    - no other application: the DarkGray line and no jobs read;
    - two others: a warning naming both, and jobs read for the one with a folder (its
      `%VAR%` folder expanded) but not for the one without;
    - an unreadable listing: a warning, and no throw.
  - AST: each branch calls the report exactly once, at its top level, with `$AppPoolName`,
    `$PublishPath` and `$StagingPath`, right after its stop step's `Write-Step` and before the
    binding read and the stop.
- Existing tests that change with the code:
  - The cdt-5 job-check inventory now has 4 calls, not 3, and the new one reads `$otherConfigDir`.
  - The cdt-49 region test, whose lifted region now contains the report call, stubs the report.

Mutations:

- E1: both calls removed - the record's "enumeration removed".
- E2: the module function returns before it enumerates.
- E3: the report names the others but reads none of their jobs.
- E4: the module's pool filter removed.
- E5: the module's folder exclusion removed, so the instance lists itself.

Files: `tools/AppPoolControl.psm1`, `deploy.ps1`, `tests/ps/AppPoolControl.Tests.ps1`,
`tests/ps/DeployInvariants.Tests.ps1`, this record.

**After the fix**, as built:

- `deploy.ps1`:
  - 260-268: the comment.
  - 269-295: `Write-AppPoolSharingReport`.
  - 773-775 and 1110-1112: each branch's comment and call, straight after its stop step's
    `Write-Step`.
- `tools/AppPoolControl.psm1` 570-638: `Get-AppPoolOtherApplication` and its export.

The test that receives the probes was first written with the probe captured in a
`GetNewClosure()` block. PSScriptAnalyzer cannot see a parameter used inside such a block, and
reported it unused. A suppression attribute on the script block's `param` did not silence it, so the
test now takes a block that returns its probe (`{ { ... } }`). This is the form the module's own
tests use for `$PSDefaultParameterValues`. The proof below was run on that final form.

## Files changed

Each file before the repair, at `4b94d08f` (its blob, then the md5 of its committed LF content), and the
md5 after:

- `deploy.ps1`: blob `563cd2f1ac77`, md5 `FB108AD1005FA20B94F84A70E4EA4DF7`; after `BF9EE7C3F3D9A9560E4F22DDB3C769C3`
- `tools/AppPoolControl.psm1`: blob `6249fc0222e1`, md5 `C77C62268160179B6A60851BDA055140`; after `F7B3F292CE970A2E2876137296F05C62`
- `tests/ps/AppPoolControl.Tests.ps1`: blob `de145f25f0b0`, md5 `E8ABA839F8AA58DCE588750AF1DB3619`; after `A9A2F9E446868BDC4AE8E9E0D314F91C`
- `tests/ps/DeployInvariants.Tests.ps1`: blob `ace5d76686e0`, md5 `20B7D27FA87DC11B518CA4DF4D1A73CE`; after `D694B0CFB2B40223CB642D7A4CB12A12`
- this record: copied from master's (`e9db736f`) and filled in

No version bump.

## Guard proof

Tests:

| Test | Pins |
| --- | --- |
| `tests/ps/AppPoolControl.Tests.ps1`, Describe `cdt-54: Get-AppPoolOtherApplication lists what else a stop of the pool takes down` (1324), five tests | Through `-ApplicationsProbe`. Listed: the pool's applications elsewhere, in IIS order, including one under a differently cased pool name, one with no folder, and one whose folder only starts with the instance's. Not listed: the instance under any spelling (case, a trailing separator, `%CDT54_ROOT%`, `/`), a node inside it, or any other pool's node. With real files: IIS at a junction to the folder is the instance, and a sibling is listed. An unreadable listing throws "Cannot read the IIS sites and applications, so the applications app pool 'ExamplePool' serves besides the one at 'D:\sites\App' cannot be listed: Access is denied". The default probe, with in-module stand-ins, lists the site root and the applications, and never calls `Get-WebConfiguration` (its stand-in throws) |
| `tests/ps/DeployInvariants.Tests.ps1`, `names every other application the stopped pool serves and reads their bulk jobs, as a warning and never a refusal (cdt-54)` (1483) | deploy.ps1's real report, lifted, with the real module; each probe records that it was consulted. A pool with only this instance: exactly the DarkGray line, and no jobs read. Two others: the warning names both, in order; jobs are read for the one with a folder, from `D:\example\other\config` with `%CDT54_ROOT%` expanded and with the staging tool, and not for the one without a folder. An unreadable listing: the warning, no throw, and no jobs read |
| same file, `reports the pool's other applications once in each branch, first in its stop step, before the binding read and the stop (cdt-54, AST)` (1552) | Each branch has the exact call at its top level, once, straight after `Write-Step "Stopping app pool"` or `Write-Step "Stopping app pool for file deployment"`, and before `$boundPool = ...` and the stop. There are two calls in the script, and no others |
| the cdt-5 inventory and the cdt-49 region test (updated) | 4 job checks, the new one reading `$otherConfigDir`. The cdt-49 region now contains the report, which that test stubs; its expectations are unchanged |

Each mutation was applied to the fixed file it names by a script that fails unless every edit matches
exactly once and the mutant passes a sanity check of its own shape. Each was run against the four
suites that load the module or deploy.ps1: `DeployInvariants`, `AppPoolControl`,
`InstallerConfigBackup` and `MoveConfigDbToShared`. After each one the original bytes were restored,
the mtime refreshed and the md5 re-confirmed, and all four suites were re-run green (391 passed, 0
failed, 0 skipped). The fixed files' md5s are those above. The set was run twice, and the second run,
on the final test file, gave the same results:

| Mutation | File, md5 after mutation | Failed |
| --- | --- | --- |
| E1: both calls removed, so the enumeration never runs before either stop (the record's mutation) | deploy.ps1, `200105D8D86C2BDD40CF8CA7838AF4FA` | 1: the AST test |
| E2: the module function lists nothing (its IIS read replaced by an empty list) | module, `181042CDAD770F6EC27F8D8E4DFEBB70` | 5: the report test (its probe was never consulted), and four of the module tests |
| E3: the report names the others but reads none of their jobs | deploy.ps1, `C156DDC549B3CE484E9665DB82054185` | 2: the report test (no jobs read), and the cdt-5 inventory (3 checks) |
| E4: the pool filter removed | module, `4615FAE97A784E242A8E216813D5C22C` | 3: the report test (the other pool's site named), the listing test, and the default-probe test |
| E5: the lexical folder exclusion removed | module, `226D3626077C0FAF7C0B10BF7AA2B984` | 4: the report test, the listing test, the instance-spellings test (4 listed), and the default-probe test |
| E6: the final-path exclusion removed | module, `7C1F3CACE1CD49BAC5872B9FF5CCFF6E` | 1: the junction test |

Windows PowerShell 5.1.20348.5756:

- deploy.ps1 and the module parse with no errors.
- A scratch harness ran the real module and the lifted report:
  - the module listed `Other Site/` and `Example Site/Static`, and not the instance at
    `D:\example\publish\` nor the other pool's site;
  - the report warned about both;
  - it read jobs from `D:\example\other\config`, with the staging tool, for the one with a folder;
  - with an unreadable listing, it warned and did not throw.

Gates on the final tree:

- `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7): 504 passed, 0 failed, 0 skipped.
- `Invoke-ScriptAnalyzer -Path . -Recurse` (1.25.0): 0 errors and 321 warnings. Two are new, both
  `PSAvoidUsingWriteHost`, on the report's two DarkGray lines (deploy.ps1 283 and 292). That is this
  script's convention for read-only report lines, as with cdt-49's; nothing else is new.
- `git diff --check`: clean.
- `tools/Test-AsciiOnly.ps1`: passed.
- All four files stay CRLF.

Not run: a real deploy, against IIS with a shared pool (the brief forbids it).

## Coder dispute

None.

## Known gaps

These are candidates, found and not fixed. They are outside this record's scope, which names
`deploy.ps1`.

- The promotion (`tools/promote-dev-to-prod.ps1`), the installer (`tools/Install-ExchangeAdminWeb.ps1`)
  and the cutover (`tools/Move-ConfigDbToShared.ps1`) stop a pool after the same kind of
  `Assert-AppPoolServesPath` check. None of them names, or reads the jobs of, the other applications
  that pool serves. `Get-AppPoolOtherApplication` is exported for them, and none calls it yet.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
