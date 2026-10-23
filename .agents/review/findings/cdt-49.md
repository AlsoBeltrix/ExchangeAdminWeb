# cdt-49: in a pool move the active-jobs warning reads the instance running today while the stop hits the new pool

**Severity**: LOW - a warning only. `deploy.ps1`: with `-AllowRepoint` to a new pool, the active-jobs check (`tools/JobStateWarning.psm1`) reads the current instance's jobs database while the stop/start acts on the new pool, so the warning can describe the wrong instance.
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-23 evening from the cdt-36 agent's second hand-back; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-48 commit `cc5f8c82`
**Commit**: the `fix(cdt-49)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5`. Trigger: a -AllowRepoint pool move with jobs running on the old instance. Predicted failure: the warning is about the wrong pool, or absent.

## Approach

Written before the code. Line numbers are `deploy.ps1` at `cc5f8c82`.

**Mechanism.** In the fresh-install branch the order is:

```
1005:         # Web application
...
1014:             Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name physicalPath    -Value $PublishPath
1015:             Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name applicationPool -Value $AppPoolName
...
1046:         Write-Step "Stopping app pool for file deployment"
1047:         # Warn (do not block) if a durable bulk job is active - recycling interrupts it.
1048:         Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $runtimeConfigDir -AppPath $StagingPath | Out-Null
...
             $poolState = Stop-AppPoolVerified -Name $AppPoolName
```

and `$runtimeConfigDir` is the serving folder's `config\` since cdt-44. Two instances are involved in a
`-AllowRepoint` move, at two moments:

- The instance IIS runs today, under the old pool and from the serving folder. It is taken down when
  1014-1015 re-point the application: IIS unloads an application whose pool or folder changes. That
  happens before 1048, so a warning about its jobs comes after the fact.
- The pool the stop acts on, `-AppPoolName`, which after the re-point serves `-PublishPath`. Its
  instance's jobs are in `<PublishPath>\config`. 1048 reads the serving folder instead, which in a
  folder move is the old one.

The one warning names neither. Its words, "Recycling the app pool will INTERRUPT them", describe the
stop.

**Change** (the fresh-install branch only; the upgrade never moves):

1. Right before `# Web application` (1005): when the run moves an existing application
   (`-AllowRepoint`, and its folder or pool differs from what IIS reports), report the active jobs of
   the instance IIS runs today. A line names it: "Active bulk jobs of the instance IIS runs today,
   under app pool '<old>' with its files at '<serving folder>': re-pointing the application
   interrupts them." Then `Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $runtimeConfigDir`.
2. Before the stop (1048): the instance the stop acts on, named on a line of its own ("... of app pool
   '<-AppPoolName>', with its files at '<-PublishPath>', which is stopped next"), read from
   `Join-Path $PublishPath "config"`. In a move whose folder does not change, that is the folder just
   reported, so it is not read twice. Outside a move this is the folder 1048 reads today, so a plain
   `-Force` or fresh install behaves as before, with the line added.
3. The pool-move test - whether the old pool differs - uses the pool IIS reported to the cdt-36
   check (`$existingApplication.applicationPool`), case-insensitively. The folder-move test is cdt-44's
   `$movesFolder`.

Constraints: Invariants 5 and 6; the warning stays a warning (warn, not block). No version bump.

Guards, in `tests/ps/DeployInvariants.Tests.ps1`:

- Behavioural, stubbed-step: the fresh-install branch's real statements from the move report
  through the check before the stop. They are lifted by AST and run with the IIS cmdlets and
  `Assert-NoActiveBulkJobsBeforeRecycle` stubbed to record each call and its `-ConfigDir`. Cases: a
  pool and folder move (the old instance's jobs read before the re-point, the new folder's before
  the stop, and both named); a pool-only move (read once, before the re-point); a run that moves
  nothing, and a fresh install (only the check before the stop, of `<PublishPath>\config`).
- The cdt-5 AST test's job-check pins follow: three calls. The upgrade's call and the move report read
  `$runtimeConfigDir`; the check before the stop reads `$stoppedConfigDir`.

Mutations: the old-instance read dropped (the move report removed); the check before the stop back
to `$runtimeConfigDir`.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

**After the fix**, as built: `deploy.ps1` 1005-1015 (the comment, `$movedOffPool`, `$movesApplication`,
and the move report at 1012-1015, before `# Web application`), 1056-1067 (the check before the stop:
`$stoppedConfigDir` at 1063, the named check at 1064-1067).

## Files changed

Before the repair at `cc5f8c82` (blob; md5 of the committed LF content), and after:

- `deploy.ps1` - `9c2556a44e8b`, `B0306FB2BE46EBF0715ACC7098323B81`; after `8ED4217FA9641D2A5E54518C666DF53B`
- `tests/ps/DeployInvariants.Tests.ps1` - `3af1437a0f88`, `F2F7ECAC228B1849A11B678E059B7184`; after `1E00F2238817CA5408D8A7D863791F8E`
- this record - copied from master's (`8a984e94`) and filled in

No version bump.

## Guard proof

Tests, `tests/ps/DeployInvariants.Tests.ps1`, Describe `deploy.ps1`:

| Test | Pins |
| --- | --- |
| `reports the active jobs of the instance a -AllowRepoint move takes down before the re-point, and checks the stopped pool's own (cdt-49)` (1402) | The fresh-install branch's real statements from `$movedOffPool = ...` through the statement right before `$boundPool = ...`, lifted by AST and run under `Stop`. `Get-Website`, `Get-WebApplication`, `Set-ItemProperty`, `Test-Path`, `New-Item`, `New-WebApplication`, the authentication cmdlets and `Assert-NoActiveBulkJobsBeforeRecycle` are stubbed, the last recording its `-ConfigDir`; the real module is imported. A pool and folder move: exactly `jobs(D:\example\old\config)`, `set(physicalPath)`, `set(applicationPool)`, `jobs(D:\example\publish\config)`, with both lines naming their instance ("... under app pool 'OldPool' with its files at 'D:\example\old': re-pointing the application interrupts them." / "... of app pool 'NewPool', with its files at 'D:\example\publish', which is stopped next:"). A pool-only move: the jobs read once, before the re-point. A run that moves nothing: the re-point's sets, then one read of `<PublishPath>\config`. A fresh install: `New-WebApplication`, then that one read |
| `sets $runtimeConfigDir unconditionally before any use ... (cdt-5)` (updated pins) | Three `Assert-NoActiveBulkJobsBeforeRecycle` calls whose `-ConfigDir` values are exactly `$runtimeConfigDir` (the upgrade), `$runtimeConfigDir` (the move report) and `$stoppedConfigDir` (the check before the stop) |

Mutations on the fixed `deploy.ps1` (md5 `8ED4217FA9641D2A5E54518C666DF53B`), each applied by a script
that fails unless every edit matches exactly once and the mutant passes a sanity check of its own
shape, run against `DeployInvariants.Tests.ps1`; then the original bytes restored, the mtime refreshed,
the md5 re-confirmed and the file re-run green each time (125 passed):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R0 the whole fix reverted (`deploy.ps1` as at `cc5f8c82`; byte-identical) | `B0306FB2BE46EBF0715ACC7098323B81` | 2: the cdt-49 test (no move report to lift) and the cdt-5 pins ("Expected 3 ... but got 2") |
| J1 the old-instance read dropped (the move report disabled) - the record's mutation | `5C5DD30172F38834A07B08775D959C00` | 1: the cdt-49 test (the pool and folder move's calls lacked the read before the re-point) |
| J2 the check before the stop back to `$runtimeConfigDir` | `49734FC4FDE4E92F353E50FD5F2CCCAE` | 2: the cdt-49 test (the stopped pool's check read the old folder) and the cdt-5 pins |

A first run of the three was kept as evidence of a weaker anchor, not as the proof. The test found
its region's end by the text `-ConfigDir $stoppedConfigDir`, so J2 broke the lift rather than
failing on behaviour. The end is now the statement right before `$boundPool = ...`, whatever it
reads. The three were re-run against that test file (`1E00F223...`) - the table above - and J2 now
fails on the call sequence.

Windows PowerShell 5.1.20348.5756: `deploy.ps1` parses with no errors (the new condition continues a
line after `-and`, which both shells accept).

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 493 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 319
warnings. The two new ones are `PSAvoidUsingWriteHost` on the two detail lines (1013, 1065), the
script's own style for such lines - it has 34 others. `git diff --check HEAD` clean;
`tools/Test-AsciiOnly.ps1` passed; both files stay CRLF.

Not run: a real move with active jobs (it needs IIS and a running instance).

## Coder dispute

None.

## Known gaps

- "IIS unloads an application whose pool or folder changes" is how IIS treats a change to an
  application's configuration; this was not observed on a host here. If an old worker kept running
  jobs past the re-point, the report still comes first, so the warning is early rather than late.
- Other applications that share `-AppPoolName` are stopped with it, and their jobs are not read: the
  check reads this application's folder, as it always has.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
