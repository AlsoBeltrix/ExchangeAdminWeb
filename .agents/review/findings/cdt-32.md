# cdt-32: the installer stops its pool and mirrors without asking what else serves the folder

**Severity**: HIGH. cdt-28's defect, in `tools/Install-ExchangeAdminWeb.ps1`. Before replacing the
publish folder with `robocopy /MIR`, the installer stops `-AppPoolName` and confirms it stopped, but
nothing asks IIS whether any other site or application has its files in `$PublishPath` under a
different pool. One that does keeps serving files the mirror replaces. cdt-15's check
(`Assert-ApplicationTargetUnchanged`) compares only the application `$ParentSite/$AppAlias` with
the run's pool and folder; `Assert-AppPoolServesPath` (aud-a38c), which asks about every node, is
exported by `tools/AppPoolControl.psm1`, which the installer already imports, and is not called.
Found while fixing cdt-28 (the same check in `deploy.ps1`); not in the codex verdict.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a80bd818fc511a34f` (worktree `agent-a80bd818fc511a34f`)
**Commit**: `94eb43a` (merged)
**Reviewer**: implementer (found while fixing cdt-28 from codex / gpt-5.5-dzs / xhigh, ops-batch-3, 2026-10-21)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `d32a848`:

```
780: Import-Module (Join-Path $PSScriptRoot 'AppPoolControl.psm1') -Force
...
818: Assert-ApplicationTargetUnchanged -Location "$ParentSite/$AppAlias" -Exists ($null -ne $existingApplication) -ExistingPool "$($existingApplication.applicationPool)" -ExistingPhysicalPath "$($existingApplication.PhysicalPath)" -AppPoolName $AppPoolName -PublishPath $PublishPath
...
964: Invoke-PlanOrAction "Create or update IIS web application $ParentSite/$AppAlias" {
...
1070:     Invoke-PlanOrAction "Stop app pool $AppPoolName and confirm it stopped" {
...
1073:         $poolState = Stop-AppPoolVerified -Name $AppPoolName
...
1088:         Invoke-RobocopyChecked -Description "Deploy application files" -Arguments @(
```

The only IIS reads about who serves what are the one application's (818). By the stop (1070) the
run has created or updated that application at `$PublishPath` under `$AppPoolName` (964), so the
module's check has a node to find on a fresh install and an update alike.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348. Scratch harness: the
installer's real statements from the first after the backup decision through the mirror's `try`,
lifted from the `d32a848` file by the 5.1 parser and run under `Stop` with the installer's own
`Invoke-PlanOrAction` and helpers; IIS stubbed as one picture - `Example Site/ExampleApp` under
`ExamplePool` with its files at the publish folder, and `Other Site/Copy` with its files at the same
folder under `OtherPool` - and every IIS read logged:

| IIS picture | Result | Calls |
| --- | --- | --- |
| the folder is also served by `Other Site/Copy` under `OtherPool` | mirrored | `stop(ExamplePool)`, `mirror`, `start(ExamplePool)` - no IIS read |
| only the application serves the folder | mirrored | the same |

## Plan (approved by the owner's EVERYTHING go)

1. Immediately before the stop step, a step of its own: `Invoke-PlanOrAction "Confirm that IIS
   serves $PublishPath from app pool $AppPoolName and no other" { $null = Assert-AppPoolServesPath
   -Name $AppPoolName -PhysicalPath $PublishPath }` - the promotion script's call. Nothing catches
   it; it throws inside the `try` whose `finally` removes staging (cdt-23), before anything is
   stopped or mirrored.
2. A step, not a line inside the stop step, so `-PlanOnly` prints it (Invariant 4), as the
   promotion script prints its check, and runs nothing: on a fresh install the application does
   not exist until the apply run creates it.
3. `tools/AppPoolControl.psm1` is not edited (another stream is changing it for aud-a38f).

Assumptions, recorded:

- As in cdt-28: the folder is compared as the module compares it; an IIS configuration that spells
  it another way is refused, naming what IIS reported. No host's IIS configuration was read.
- The refusal comes after the IIS, ACL, `appsettings.json` and seed changes made earlier in the run,
  which stay, as for every refusal after them; the publish folder itself is unchanged.
- The installer stays standalone (Invariant 1): the module is the sibling it already imports, and the
  check names no environment.

Constraints: Invariants 1, 4 and 6 (pure ASCII, no BOM, no ternary, no `??`, no PS7-only parameters;
parsed under 5.1). The text `Stop-AppPoolVerified` occurs once in the installer
(`tests/ps/AppPoolControl.Tests.ps1` counts it); the new step does not add one. No version bump:
ops-script-only.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1` (new tests;
the cdt-23 behavioural test, which runs the whole region with the IIS calls stubbed, gets a
pass-through stub for the new call), this record.

Guards: an AST test - the statement right before the stop step is the new step, at the same level,
whose only statement is `$null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath
$PublishPath`; one call in the script; no `try` with a `catch` around it. And a behavioural test
running the real region with the IIS calls stubbed: a folder another pool also serves is refused
before any stop or mirror and leaves no staging; a folder served by the named pool alone reaches
the stop and the mirror; `-PlanOnly` prints the check and calls nothing. Mutations: the step
removed; the step moved after the stop; the call swallowed by a `try`/`catch`.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix: a comment at 1070-1076 and, at
1077-1079, `Invoke-PlanOrAction "Confirm that IIS serves $PublishPath from app pool $AppPoolName and
no other" { $null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath }`,
immediately before the stop step (1081), inside the `try` whose `finally` removes staging.

The same 5.1 harness as in Evidence, against the fixed file, with the module's real
`Assert-AppPoolServesPath` (run inside the module, its IIS probe fed the same stubbed picture):

| IIS picture | Result | Calls |
| --- | --- | --- |
| the folder is also served by `Other Site/Copy` under `OtherPool` | refused: "The files at '<root>\publish' are served by an app pool other than 'ExamplePool': Other Site/Copy runs under 'OtherPool'. Stopping 'ExamplePool' would leave that application running while its files are replaced. Name the pool that serves them, or fix the binding." | the check and its IIS reads; no stop, no mirror |
| only the application serves the folder | mirrored | the check and its IIS reads, `stop(ExamplePool)`, `mirror`, `start(ExamplePool)` |

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-32: ...`) | Pins |
| --- | --- |
| `checks the folder in a step of its own right before the stop, and nothing catches its refusal (AST)` | The statement right before `Invoke-PlanOrAction "Stop app pool $AppPoolName and confirm it stopped"`, in the same block, is `Invoke-PlanOrAction "Confirm that IIS serves $PublishPath from app pool $AppPoolName and no other"` whose scriptblock's only statement is exactly `$null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath`; the script calls it once; no `try` around it has a `catch` |
| `runs the real region: a folder another pool also serves is refused before anything is stopped or mirrored (behavioural)` | Lifts the `try` that removes staging (the publish through the checks after the restart) by AST and runs it under `Stop` with the installer's own helpers, the external calls stubbed: a folder another pool also serves calls only the check, throws the module's "served by an app pool other than 'ExamplePool'" refusal and leaves no staging; a folder served by the named pool alone gives `serves,stop,mirror,start,manifest`; `-PlanOnly` makes no check call and prints "PLAN Confirm that IIS serves ..." |
| cdt-23's behavioural test (one stub added) | Runs the same region; a pass-through `Assert-AppPoolServesPath` stub keeps its cases as they were |
| `tests/ps/AppPoolControl.Tests.ps1` `calls the verified stop in both deploy paths and in the installer` (unchanged) | Still one `Stop-AppPoolVerified` in the installer |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `B6DF3A97FD4CFDEC172171E94DFEF6A1`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1`,
`DeployInvariants.Tests.ps1` and `AppPoolControl.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed, and the three rerun green (218 passed) each time. Post-comment md5
`DAEB5336FF44EFC80098EAD7C7C7AEBE` (218 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| S1 the step removed (byte-identical to `d32a848`: the same md5 as that commit's file and cdt-31's post-comment md5) | `29B09E3A1034D75A1DEED68A20093B79` | both cdt-32 tests (AST: "Expected like wildcard 'Invoke-PlanOrAction "Confirm that IIS serves *' to match 'if ($SkipConfigBackup) { ...'"; behavioural: "Expected: 'serves' But was: 'stop,mirror,start,manifest'" - the folder served elsewhere was stopped and mirrored over) |
| S2 the step moved after the stop step | `6E0A63CF5741DFF80625E770DA8985A3` | both (AST as S1; behavioural: "But was: 'stop,serves'" - the pool was stopped before the check) |
| S3 the call wrapped in a `try` whose `catch` only warns | `46EFAE4A8A62BD12D27F4A984750B1F0` | both (AST: "Expected: '$null = Assert-AppPoolServesPath ...' But was: 'try { $null = Assert-AppPoolServesPath ... } catch { Write...'"; behavioural: "But was: 'serves,stop,mirror,start,manifest'" - the refusal was swallowed) |

Not run: a real install (needs elevation and IIS); the module's own decision is covered by its tests
(`tests/ps/AppPoolControl.Tests.ps1`, Describe `Assert-AppPoolServesPath`).
