# cdt-31: a manifest write that fails after a failed pool start hides that the application is down

**Severity**: MEDIUM. cdt-16 (`deploy.ps1`) and cdt-18 (the installer) made a failed pool restart
after a complete mirror its own failure: the start's error is recorded, the deployment manifest is
still written (the files are deployed), and a check after that throws "... The application is DOWN
until the pool is started by hand." The manifest write itself is unguarded. When it throws too - the
publish folder not writable, the disk full - its exception is the one the caller and
`tools/deploy-pipeline.ps1` receive, the closing check is never reached, and the only trace that the
application is down is a red line printed earlier. The operator is pointed at a manifest file while
the real state is a stopped application. In `deploy.ps1` the write sits in the `finally` that
restarts the pool, so its throw also skips that `finally`'s own staging removal (the outer one still
removes it, cdt-6). From the codex review of the cdt-11..cdt-27 merge (ops-batch-3, its finding 4).
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a80bd818fc511a34f` (worktree `agent-a80bd818fc511a34f`)
**Commit**: `<pending>`
**Reviewer**: codex / gpt-5.5-dzs / xhigh, ops-batch-3, 2026-10-21

## Evidence

`deploy.ps1` at `fce5c5e` (cdt-28 added fourteen lines above; at `2478ede` the same lines are
1006-1030 for `-Force`), the upgrade branch (the fresh-install branch, which every `-Force` run
takes, has the same lines at 1020-1045):

```
699:         } finally {
700:             if ($mirrorSucceeded) {
...
703:                 try {
704:                     Start-AppPoolWithRetry -Name $AppPoolName
...
706:                 } catch {
707:                     $poolStartError = $_.Exception.Message
...
710:                 if ($deploymentReadyForManifest) {
711:                     Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $ProjectRoot
712:                 }
...
721:         if ($poolStartError) {
722:             throw "The files and configuration are deployed, but app pool $AppPoolName did not start ($poolStartError). The application is DOWN until the pool is started by hand."
```

`tools/Install-ExchangeAdminWeb.ps1` at `fce5c5e` (1101-1106 at `2478ede`):

```
1107:                 $poolStartError = $_.Exception.Message
...
1115:     Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $repoRoot
...
1118:     if ($poolStartError) {
1119:         Write-Fail "The files are deployed, but app pool $AppPoolName did not start ($poolStartError). The application is DOWN until the pool is started by hand."
```

Both writers end in `$manifest | ConvertTo-Json | Set-Content -LiteralPath ...` (`deploy.ps1:157`;
the installer's inside `Invoke-PlanOrAction`), which throws under the scripts' `Stop` when the file
cannot be written. In `deploy.ps1` nothing is propagating when the write runs (`$deploymentReadyForManifest`
is set by the last statement of the `try` body), so its exception becomes the `finally`'s, and the
script's; in the installer it is a statement before the check.

**Mechanism: a terminating error that replaces the one that matters**, reproduced under Windows
PowerShell 5.1.20348. Scratch harness: each real region - in `deploy.ps1` the flag statements, the
mirror `try`/`finally` and the checks after it, per branch; in the installer the flags, the mirror
`try`/`catch`/`finally`, the manifest call and the check - lifted from the `fce5c5e` files by the 5.1
parser and run under `Stop` with the scripts' own helpers, only the mirror, the pool start and the
manifest writer stubbed (the stub writer throws "Access to the path '<publish>\deployment-manifest.json'
is denied."):

| Region | Pool start | Manifest write | Calls | The caller receives |
| --- | --- | --- | --- | --- |
| upgrade, `-Force`, installer | fails | fails | `robocopy,start,manifest` | "Access to the path '<root>\publish\deployment-manifest.json' is denied." - nothing says the application is down |
| the same | fails | succeeds | `robocopy,start,manifest` | "... did not start (...). The application is DOWN until the pool is started by hand." |
| the same | succeeds | fails | `robocopy,start,manifest` | "Access to the path ... is denied." |

## Plan (approved by the owner's EVERYTHING go)

The second fix the brief allows: the manifest write is wrapped so that its failure is reported
alongside and the pool-start failure stays the terminating error - the promotion script's "both
facts, not whichever threw last" (`tools/promote-dev-to-prod.ps1`, A38) - rather than throwing the
pool-start failure before the manifest is written. That keeps cdt-16's and cdt-18's decision that a
deployed build still gets its manifest when only the restart failed (their tests pin it: "the
deploy is complete, so the manifest is still written").

`deploy.ps1`, in each branch, the same change:

1. `$manifestError = $null` with the other flags before the mirror `try`.
2. In the `finally`, the manifest write gets its own `try`/`catch`, like the restart's: the catch
   records `$manifestError = $_.Exception.Message` and prints it in red; it never throws, so the
   `finally` now never throws from either and always reaches its staging removal.
3. After the `finally` (reached only when the `try` body succeeded): `if ($poolStartError) { throw
   ... }` first - its message now ends with the manifest failure when there was one - then `if
   ($manifestError) { throw ... }`, so a manifest failure with the pool running still fails the run,
   as it did, with a message saying the files are deployed and the pool runs.

The installer, the same shape: `$manifestError = $null` and the manifest call in a `try` whose
`catch` records and reports it, after the mirror's `try` and before `if ($poolStartError) {
Write-Fail ... }`, whose message gains the manifest failure; then `if ($manifestError) { Write-Fail
... }`. Both checks stay inside the `try` whose `finally` removes staging (cdt-23).

Assumptions, recorded:

- The pool-start failure is the terminating error when both happen: a stopped application is the
  state the operator must act on first, and the message carries the manifest failure too, so
  neither fact is lost.
- A manifest failure with the pool running stays a failure (it was the terminating error before;
  the promotion's provenance gate reads the manifest: "Dev has no deployment manifest. Run -Dev
  first with the current source."); only its message changes, from the raw exception to one that
  says what is deployed and running and quotes the exception.
- In `-PlanOnly` the installer's manifest write is a plan line, so its `catch` is never reached;
  nothing new acts in a plan (Invariant 4).

Constraints: `deploy.ps1` has no plan mode; the installer stays standalone (Invariant 1, no
`deploy.ps1` string); pure ASCII, no BOM, LF in the index, no ternary, no `??`, no PS7-only
parameters; parsed under Windows PowerShell 5.1. No version bump: ops-script-only.

Files: `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/DeployInvariants.Tests.ps1`
(two new tests; the cdt-16 behavioural test's flag pattern gains `manifestError`),
`tests/ps/InstallerConfigBackup.Tests.ps1` (two new tests), this record.

Guards: an AST ordering test per script and branch - `$manifestError = $null` before the mirror;
the manifest call inside a `try` whose single catch sets `$manifestError` and neither throws nor
calls `Write-Fail`; that `try` after the restart's (deploy) / after the mirror's (installer); then
`if ($poolStartError)` opening with the throw whose text carries `$manifestError`, then
`if ($manifestError)` opening with a throw. And a behavioural test per region, lifted and run with
the mirror, the start and the writer stubbed: both failing gives the DOWN failure naming the
manifest error; only the start failing gives the DOWN failure as before; only the manifest failing
gives the manifest failure; neither gives no error. Mutations: the order of the two checks swapped
(per script); the manifest write unwrapped (the pre-fix shape, per script); the catch rethrowing.

## Approach

As planned. In `deploy.ps1` after the fix, the upgrade branch:

- Line 638: `$manifestError = $null`, between `$poolStartError = $null` and
  `$deploymentReadyForManifest = $false`.
- Lines 711-721: in the `finally`, under `if ($deploymentReadyForManifest)`, a comment and `try {
  Write-DeploymentManifest ... } catch { $manifestError = $_.Exception.Message; Write-Host "  X  The
  deployment manifest was not written: $manifestError" -ForegroundColor Red }`.
- Lines 730-736: `if ($poolStartError) { throw "... The application is DOWN until the pool is started
  by hand.$(if ($manifestError) { " The deployment manifest was not written either ($manifestError)." })" }`
  (still the first statement after the `try`, still opening with the `throw` cdt-16 pins), then a
  comment and `if ($manifestError) { throw "The files and configuration are deployed and app pool
  $AppPoolName is running, but the deployment manifest was not written to $PublishPath
  ($manifestError), so nothing records which build it holds. Fix the cause and re-run this deploy." }`.
- Fresh install / `-Force`: the same at 951, 1045-1055 and 1064-1070.

In `tools/Install-ExchangeAdminWeb.ps1`: lines 1115-1124, a comment, `$manifestError = $null` and the
manifest call in its own `try`/`catch`; 1127-1132, the DOWN check (its message gains the manifest
failure) and then `if ($manifestError) { Write-Fail "The files are deployed and app pool $AppPoolName
is running, but the deployment manifest was not written to $PublishPath ($manifestError), so nothing
records which build it holds. Fix the cause and re-run the installer." }` - both inside the `try`
whose `finally` removes staging (cdt-23).

The same 5.1 harness as in Evidence, against the fixed files:

| Region | Pool start | Manifest write | Calls | The caller receives |
| --- | --- | --- | --- | --- |
| upgrade, `-Force` | fails | fails | `robocopy,start,manifest` | "The files and configuration are deployed, but app pool ExamplePool did not start (the pool would not start). The application is DOWN until the pool is started by hand. The deployment manifest was not written either (Access to the path '<root>\publish\deployment-manifest.json' is denied.)." |
| installer | fails | fails | `robocopy,start,manifest` | "The files are deployed, but app pool ExamplePool did not start (App pool 'ExamplePool' is 'Stopped' after 60 seconds, ...). The application is DOWN until the pool is started by hand. The deployment manifest was not written either (Access to the path ... is denied.)." |
| all three | fails | succeeds | `robocopy,start,manifest` | the DOWN failure, as before |
| all three | succeeds | fails | `robocopy,start,manifest` | "... app pool ExamplePool is running, but the deployment manifest was not written to <root>\publish (Access to the path ... is denied.), so nothing records which build it holds. Fix the cause and re-run ..." |
| all three | succeeds | succeeds | `robocopy,start,manifest` | no error |

## Guard proof

| Test | Pins |
| --- | --- |
| `records a failed manifest write like a failed restart, and a failed restart stays the error the caller gets (cdt-31, AST)` (`tests/ps/DeployInvariants.Tests.ps1`) | For the upgrade and the fresh-install branch alike: `$manifestError = $null` among the statements before the restart `try`; one `Write-DeploymentManifest` in the `finally`, inside its own `try` (not the restart `try` itself) whose single catch sets `$manifestError = $_.Exception.Message` and holds no `throw` or `Write-Fail`, and which starts after `Start-AppPoolWithRetry`; the statement right after the restart `try` is `if ($poolStartError)` opening with a `throw` whose text carries `$manifestError`; the next is `if ($manifestError)` opening with a `throw` |
| `runs each branch's real region: a failed manifest write never hides a failed restart (cdt-31)` (`DeployInvariants.Tests.ps1`) | Lifts each branch's region (the flags, the `try`/`finally`, the checks after it, whatever their order) by AST and runs it under `Stop` with robocopy, `Start-AppPoolWithRetry` and `Write-DeploymentManifest` stubbed: both failing throws the DOWN message with "The deployment manifest was not written either (Access to the path '...deployment-manifest.json' is denied.)" and leaves no staging; the restart alone throws the DOWN message ending as before and still writes the manifest; the manifest alone throws "... is running, but the deployment manifest was not written to ..."; neither throws nothing |
| `writes the manifest in its own try after the mirror, and fails on the restart before the manifest (AST)` (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-31: ...`) | The one script-level `Write-DeploymentManifest` is the only statement of a `try` beside and after the mirror's, preceded by `$manifestError = $null`, with one catch that sets `$manifestError = $_.Exception.Message` and holds no `throw` or `Write-Fail`; right after it `if ($poolStartError)` opening with `Write-Fail "The files are deployed, but app pool...` carrying `$manifestError`; then `if ($manifestError)` opening with `Write-Fail` |
| `runs the real region: both failing gives the DOWN failure naming the manifest, and each alone its own (behavioural)` (same Describe) | Lifts the region (the flags, the mirror `try`, the manifest `try`, the checks) and runs it with the installer's own helpers, only the mirror runner, the pool start and the manifest writer stubbed: the same four cases and messages as for `deploy.ps1`, with the installer's wording |
| cdt-16's behavioural test (one line changed) | Its walk back over the flags before the restart `try` now includes `manifestError`, so the lifted region still starts at `$mirrorSucceeded = $false` |
| cdt-16's and cdt-18's tests (otherwise unchanged) | A failed restart alone still writes the manifest and throws the DOWN failure first; a failed mirror still never restarts or writes |

Mutations (pre-comment fixed md5s: `deploy.ps1` `351A177C874AACA89143EBCB4F0EDDB1`, installer
`2A6E93D4DEFBCADF659004E9A6EB4AB4`; md5s of the content as committed, LF), each applied at the call
site by a script that fails unless every edit matches exactly once (one branch targeted by its
unique `Write-Step` line), run against `DeployInvariants.Tests.ps1` and
`InstallerConfigBackup.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed, and
both rerun green (156 passed) each time. Post-comment md5s: `deploy.ps1`
`9E7EA7C756DB626079312E482A3C59A4`, installer `29B09E3A1034D75A1DEED68A20093B79` (156 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| D1 `deploy.ps1` upgrade: the two checks after the `finally` swapped | `9257E017F04F18B7E03AA95188CF87DC` | 4: the cdt-31 behavioural test ("Expected like wildcard '*did not start (the pool would not start). The application is DOWN until the pool is started by hand.*' to match 'The files and configuration are deployed and app pool ExamplePool is running, but the deployment manifest was not written ...'" - the DOWN failure hidden again), the cdt-31 AST test ("Expected: '$poolStartError' But was: '$manifestError'"), and both cdt-16 tests (the restart check is no longer right after the `try`; the lifted cdt-16 region then ends before it) |
| D2 `deploy.ps1` `-Force`: the two checks swapped | `B5BD4936D02273F2D096BBA821191EEC` | the same four, for `-Force` |
| D3 `deploy.ps1` upgrade: the manifest write unwrapped | `25A7AEAFDA4BED10146C547196FDF2E3` | 2: the cdt-31 AST test ("Expected $false, because the upgrade manifest write must have its own try inside the finally, but got $true") and the behavioural test (the caller got "Access to the path '...deployment-manifest.json' is denied.") |
| D4 `deploy.ps1` `-Force`: the manifest catch rethrows | `DEDF2996EFF8FC35FB8C3B6DE00BC230` | 2: the AST test ("Expected 0, because a throw from the finally would replace the error the caller must get, but got 1") and the behavioural test (the caller got "Access to the path ... is denied.") |
| D5 `deploy.ps1` pre-fix shape (`fce5c5e`, byte-identical: the same md5 as that commit's file and cdt-28's post-comment md5) | `96CE0EF9E725956C25D082F664C6F0AE` | 2: the AST test ("Expected 1, because the upgrade branch starts with no manifest failure recorded, but got 0") and the behavioural test (as D3) |
| I1 installer: the two checks swapped | `2A9668F0B4CEF7FBF4843D9A8F632540` | 2: the installer AST test ("Expected: '$poolStartError' But was: '$manifestError'", "because the restart failure is checked first") and the behavioural test (both failing gave the manifest failure) |
| I2 installer: the manifest write unwrapped | `2BD45BE8E408DED3288697667BE7211A` | 2: the AST test ("Expected the actual value to be greater than 6, because the manifest write has its own try, beside the mirror's and after it, but got -1") and the behavioural test (the caller got "Access to the path ... is denied.") |
| I3 installer: the manifest catch rethrows | `D8D47177B25A23FCC2B9216CC90B46A8` | 2: the AST test ("Expected 0, ... but got 1") and the behavioural test (as I2) |
| I4 installer pre-fix shape (`fce5c5e`, byte-identical: the same md5 as that commit's file and cdt-29's post-comment md5) | `53099B9DD6A47A947596B6141E36B28C` | 2: as I2 |

Not run: a real deploy or install whose manifest write fails (needs elevation, IIS and an unwritable
publish folder); the stub writer stands in for `Set-Content` failing.
