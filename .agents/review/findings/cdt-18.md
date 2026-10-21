# cdt-18: the installer restarts the pool over a failed mirror, unverified, in a finally that can hide the failure

**Severity**: HIGH. `tools/Install-ExchangeAdminWeb.ps1` mirrors the new build over the publish
folder inside a `try` whose `finally` calls `Start-WebAppPool` unconditionally. A mirror that fails
part-way is followed by a restart, so IIS serves whatever mix of old and new binaries the folder
holds; a `Start-WebAppPool` that throws there replaces the mirror failure (Known Failure Class 1);
and a start that IIS accepts but that never reaches Started is not noticed at all - the run ends
"Install/update complete." with the application down. The same pair of defects cdt-16 fixed in
the ADI deploy script, plus the unverified start aud-a38 fixed in the promotion script. Found by
the promote agent in this batch's files; admitted by the supervisor.
**Status**: Merged to master; codex group review pending
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `4fcd9f1` (merged)
**Reviewer**: implementer (found by the promote agent, admitted by the supervisor)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `dc1ba6e`:

```
933: Invoke-PlanOrAction "Stop app pool $AppPoolName and confirm it stopped" {
...
936:     $poolState = Stop-AppPoolVerified -Name $AppPoolName
...
942: try {
943:     Invoke-RobocopyChecked -Description "Deploy application files" -Arguments @(
...
949:     )
950: } finally {
951:     Invoke-PlanOrAction "Start app pool $AppPoolName" {
952:         Start-WebAppPool -Name $AppPoolName
953:     }
954: }
955:
956: if (-not $PlanOnly) {
957:     Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
958: }
959:
960: Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $repoRoot
```

`Invoke-RobocopyChecked` (line 310) turns robocopy's exit code of 8 or more into `Write-Fail`
(a throw) after robocopy has already copied and deleted what it could. `Start-WebAppPool` returns
when IIS accepts the request; nothing checks the pool reached Started. The installer already
imports `tools/AppPoolControl.psm1` (A34/A35), which has `Start-AppPoolVerified` (A38) for
exactly that.

**Mechanism, reproduced** under Windows PowerShell 5.1.20348 (scratch harness: the real
mirror-and-restart region lifted from the `dc1ba6e` file by the 5.1 parser and run under
`$ErrorActionPreference = "Stop"` with the script's own `Invoke-PlanOrAction` and output helpers,
only `Invoke-RobocopyChecked`, the IIS pool calls and the manifest writer stubbed):

| Case | Calls | What the caller saw |
| --- | --- | --- |
| the mirror fails | `robocopy,Start-WebAppPool` | "robocopy failed with exit code 8 during: Deploy application files" - after the pool was started over the partial publish |
| the mirror fails and the start throws | `robocopy,Start-WebAppPool` | only "the pool would not start" - the mirror failure is gone |
| the mirror succeeds; IIS accepts the start but the pool stays stopped | `robocopy,Start-WebAppPool` | nothing: the region completes, and the run would end "Install/update complete." |

## Plan (approved by the owner's EVERYTHING go)

The cdt-16 shape, in the installer, with its own helpers (no coupling to the ADI deploy script):

1. `$mirrorSucceeded = $false` and `$poolStartError = $null` before the mirror's `try`;
   `$mirrorSucceeded = $true` right after `Invoke-RobocopyChecked` returns (it throws on failure,
   so returning is the mirror being complete).
2. A `catch` on that `try` reports a failed mirror with what it left and what to do, and rethrows
   it with the original message first (`Write-Fail`): the pool was deliberately left stopped, the
   publish folder may hold a mix of builds, the mirror does not touch the config database (the
   pre-upgrade backup, when one was taken, is under `<LogRoot>\ConfigBackups`), and the published
   build is left in the staging folder - so fix the cause and re-run the installer, which
   publishes and mirrors afresh.
3. In the `finally`, `if ($mirrorSucceeded)` gates the restart; otherwise one warning says the pool
   is not being restarted. The restart becomes `Start-AppPoolVerified` (confirmed running, A38)
   inside `Invoke-PlanOrAction` - so `-PlanOnly` still only prints it - and that call sits in its
   own `try`/`catch` that records `$poolStartError` and reports it, so the `finally` never throws.
4. After staging removal and the manifest - which still happen, the deploy being complete - `if
   ($poolStartError) { Write-Fail ... }`: the files are deployed and the application is down,
   which is now its own failure instead of "Install/update complete.".

Assumptions, recorded:

- The staging folder is left behind after a failed mirror, as it is today for every failure after
  the publish; the message says where. Removing it on every failure is a separate defect,
  recorded and fixed separately in this batch (the message changes with it).
- No binary rollback: the installer keeps no copy of the previous build; re-running it is the
  recovery, as with the ADI deploy script.
- The restart failure is reported with `Write-Host ... -ForegroundColor Red` in the `catch`,
  because the installer's `Write-Fail` throws, which is what the `finally` must not do.

Constraints: Invariant 1 (the installer stays standalone; the string naming the ADI deploy script
does not appear); Invariant 4 (the restart is still an `Invoke-PlanOrAction` step); Invariant 6
(pure ASCII, no BOM, no ternary, no `??`, no PS7-only parameters; parsed under 5.1). Does not edit
`tools/AppPoolControl.psm1` (supervisor: master has since changed it); uses only
`Start-AppPoolVerified`, which this branch's copy already exports. No version bump: ops-only.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

Guards: an AST test - the flags before the `try`, `$mirrorSucceeded = $true` directly after the
mirror call, the `catch` that rethrows with `LEFT STOPPED` and the staging path, the restart as
`Start-AppPoolVerified` in the then-branch of `if ($mirrorSucceeded)`, inside an
`Invoke-PlanOrAction` inside its own `try` whose catch records `$poolStartError` and does not
throw, no `Start-WebAppPool` left in the script, and `if ($poolStartError) { Write-Fail }` after the
`try`; and a behavioural test running the real region with only the robocopy runner, the pool start
and the manifest stubbed: a failed mirror never starts the pool and says so (also when the start
would throw); a pool that does not reach Started fails the run after the manifest is written;
success starts once and completes; `-PlanOnly` starts nothing. Mutations: the restart unconditional;
the inner catch dropped; the unverified `Start-WebAppPool` back; the flag set before the mirror; the
closing check removed; the `dc1ba6e` shape.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix:

- Lines 942-949: a comment, `$mirrorSucceeded = $false`, `$poolStartError = $null`.
- Lines 950-958: the mirror `try`, ending `$mirrorSucceeded = $true` after `Invoke-RobocopyChecked`.
- Lines 959-960: its `catch`, `Write-Fail "$($_.Exception.Message). App pool ... was deliberately LEFT
  STOPPED: ..."`.
- Lines 961-976: the `finally`: `if ($mirrorSucceeded)` -> its own `try` around `Invoke-PlanOrAction
  "Start app pool ... and confirm it started" { $null = Start-AppPoolVerified ...; Write-Ok ... }`,
  whose `catch` records `$poolStartError` and prints it in red; `else` a warning that the pool is
  not being restarted.
- Lines 978-982 unchanged (staging removal, manifest); 984-987: `if ($poolStartError) { Write-Fail
  "The files are deployed, but app pool ... did not start (...). The application is DOWN ..." }`,
  before the HTTPS check and the success banner.

The same 5.1 harness as in Evidence, against the fixed file:

| Case | Calls | What the caller saw |
| --- | --- | --- |
| the mirror fails (the start stub set to throw or not - same result) | `robocopy` | "App pool ExamplePool is NOT being restarted ...", then "robocopy failed with exit code 8 during: Deploy application files. App pool ExamplePool was deliberately LEFT STOPPED: ... The published build is left in <staging>; fix the cause and re-run the installer, which publishes and mirrors afresh." |
| the mirror succeeds; the pool never reaches Started | `robocopy,Start-AppPoolVerified,manifest` | "X App pool ExamplePool did not start: App pool 'ExamplePool' is 'Stopped' after 60 seconds ...", then "The files are deployed, but app pool ExamplePool did not start (...). The application is DOWN until the pool is started by hand." |
| everything succeeds | `robocopy,Start-AppPoolVerified,manifest` | "OK App pool ExamplePool confirmed running"; completes |
| `-PlanOnly` | `robocopy,manifest` (both stubs; the real ones plan) | "PLAN Start app pool ExamplePool and confirm it started"; no start |

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-18: ...`) | Pins |
| --- | --- |
| `gates the confirmed restart on a complete mirror and keeps every failure its own (AST)` | The two statements before the mirror `try` are `$mirrorSucceeded = $false` and `$poolStartError = $null`; the `try` body is exactly the mirror call then `$mirrorSucceeded = $true` (the only such assignment); one catch whose first statement is `Write-Fail "$($_.Exception.Message)...` naming `LEFT STOPPED`, `$PublishPath`, `$stagingPath` and `ConfigBackups`; exactly one `Start-AppPoolVerified`, in the `finally`, whose ancestors up to the mirror `try` are one `if ($mirrorSucceeded)` (it is in the then-branch), one `Invoke-PlanOrAction` and one `try` with a single catch that sets `$poolStartError = $_.Exception.Message` and contains no `throw` or `Write-Fail`; no `Start-WebAppPool` anywhere; one later `if ($poolStartError)` opening with `Write-Fail "The files are deployed, but app pool...`, before the "Install/update complete." banner |
| `runs the real region: a failed mirror never starts the pool, and an unconfirmed start fails the run (behavioural)` | Lifts the region (the flags through the `if ($poolStartError)` check) by AST and runs it under `Stop` with the script's own `Invoke-PlanOrAction` and helpers, only the robocopy runner, the pool start (and any `Start-WebAppPool`) and the manifest stubbed: a failed mirror calls nothing else and throws the robocopy message followed by `LEFT STOPPED`, whether or not the start would throw; a pool that does not reach Started gives `robocopy,start,manifest` and the "did not start ... DOWN" failure; success gives `robocopy,start,manifest` and no error; `-PlanOnly` calls no start |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `CDE38AC02630A4D9A9959475EDF191A7`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed; the
restored file re-run green (122 passed). Post-comment md5 `A6CDBAD0FC9AA70907B01F47D6B7E672` (122
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| W1 the restart unconditional (`if ($true)`) | `C16E359267D60CCDD63A1F877044AD2E` | both cdt-18 tests (AST: "Expected: '$mirrorSucceeded' But was: '$true'"; behavioural: "Expected: 'robocopy' But was: 'robocopy,start'") |
| W2 the restart's own try/catch dropped | `E21DF97955A2966140EAC87B3F86BF52` | both (AST: "Expected 1, because the restart has its own try inside the finally, but got 0"; behavioural: the failing start escaped the `finally`, no manifest - "But was: 'robocopy,start'") |
| W3 the unverified `Start-WebAppPool` back | `259E9D0B40FA46429EEC22B803CE9C85` | both (AST: "Expected 1, because the restart is confirmed, not assumed (A38), but got 0"; behavioural: a pool that never started completed the run - "But was: 'robocopy,unverified-start,manifest'") |
| W4 `$mirrorSucceeded = $true` moved before the mirror | `3DB93924D61169508E28F39824FBDAFA` | both (AST: the first body statement is the flag; behavioural: "But was: 'robocopy,start'") |
| W5 the closing `if ($poolStartError)` removed | `9DADB053240E410E54B6CB4459F4083B` | both (AST: "Expected 1, because a failed restart is its own failure once the files are deployed, but got 0"; behavioural: with no check to end on, the lifted range stops at the mirror `try`, so neither the manifest nor a failure follows - "But was: 'robocopy,start'") |
| W6 pre-fix shape (`dc1ba6e`) | `DAD27ACBC5C01A60DA610FA1624692CC` | both (AST: the statement before the mirror `try` is not the flag; behavioural: "But was: 'robocopy,unverified-start'") |

W5's behavioural failure is honest but indirect: without the check the lifted range has nothing
after the `try` to include, so the manifest call falls outside it. The AST test is the direct pin.
