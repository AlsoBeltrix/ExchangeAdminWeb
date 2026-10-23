# cdt-42: deploy.ps1's upgrade path reconfigures and grants folder ACLs to the pool named by -AppPoolName before the binding check can refuse

**Severity**: MEDIUM - an upgrade run whose `-AppPoolName` names another existing pool (one that runs as a user) reconfigures that pool (managedRuntimeVersion "", AlwaysRunning, loadUserProfile) and grants its account Modify on this instance's folders, then refuses with "runs under app pool X" - leaving the other pool changed and its account holding ACLs on this instance. The refusal arrives after the side effects (Known Failure Class 1: side-effect ordering).
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-23 evening from the cdt-36 agent's hand-back; inside the released scope. Admitted first as `cdt-38` in `f8d9e6fd`, an ID a completed finding already held; re-filed as cdt-42 by the coordinator (2026-10-23, `4755a8b3`), who restored the original `cdt-38` record on master
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), continuing from `493c270d`
**Commit**: the `fix(cdt-42)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; admitted by the supervisor)

## Evidence

At `72430407` (`deploy.ps1` there is the file at `493c270d`): `deploy.ps1` 416-426 (the upgrade path's pool step), 474-490 (the ACL grants), 673-681 (the upgrade's own binding check, which runs after both). Trigger: `deploy.ps1` upgrade with `-AppPoolName` naming a different existing pool. Predicted failure: the other pool is reconfigured and its identity gets Modify on this instance's folders; the run then refuses; nothing undoes the changes.

## Approach

Written before the code. Line numbers are `deploy.ps1` at `493c270d`.

**Mechanism, read from the file.** The pool the run names is read, configured and granted ACLs at
script level, on every path; the upgrade only asks which pool serves the application inside its
branch, after all of it:

```
371: $poolExists = Test-Path "IIS:\AppPools\$AppPoolName"
...
386:         $ServiceAccount = $existingUser
...
416: Write-Step "Configuring app pool"
...
422: Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion        -Value ""
423: Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.loadUserProfile -Value $true
424: Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode                    -Value "AlwaysRunning"
425: Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType    -Value 3
426: Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName        -Value $ServiceAccount
...
474: Set-AclChecked $auditLogFolder $ServiceAccount "(OI)(CI)M"
...
490: Set-AclChecked $configFolder $ServiceAccount "(OI)(CI)M"
...
673:         $boundPool = try { (Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop).applicationPool } catch { $null }
...
680:         if ($boundPool -ne $AppPoolName) {
```

With `-ServiceAccount` omitted, an existing pool that runs as a user lends its user to the run (386),
so no password is asked for. 416-426 then set that pool's runtime, profile loading and start mode
(identity type and user are rewritten with the values it already had); 440-491 grant its account
Read on the certificate key (when the certificate is found) and Modify on `<LogRoot>\ExchangeAdminWeb`,
`<PublishPath>\logs` and `<PublishPath>\config`, creating the folders that are missing; 495-526 set
authentication on the application; the build is published; and only then does 680 refuse. The folder
has the same ordering: an upgrade whose `-PublishPath` holds an `appsettings.json` but is not the
folder the application serves gets its grants inside that folder, and is refused only by
`Assert-AppPoolServesPath` at 691. The cdt-36 check that reads the application before all of this
(288-305) runs only `if (-not $isUpgrade)`.

**Change.**

1. `deploy.ps1` 288-305: the cdt-36 statement loses its `if (-not $isUpgrade)` wrapper. Its
   `Write-Step`, the read and the assertion stay at script level, where they are - after the consent
   check and the banner, before the config collection and everything that configures - so every run,
   upgrade or not, reads the application before any change. The comment above them is rewritten for
   both paths.
2. `Assert-ApplicationTargetUnchanged` gains a `-Upgrade` switch, passed `-Upgrade:$isUpgrade`. On an
   upgrade, a difference always refuses: an upgrade never re-points the application, so
   `-AllowRepoint` cannot make the run right. The refusal names what IIS reports and what the run
   names, says that an upgrade configures the pool and folders it names before it deploys, and says
   to re-run with the values IIS reports or move the application in IIS first; when `-AllowRepoint`
   was given, it adds that the switch applies only to a `-Force` or `-ConfirmFreshInstall` run. No
   application, or the same pool and folder: as before - an upgrade whose application is missing
   still creates it at 497-503.
3. The upgrade branch's own checks (673-691) stay, unchanged: they also catch what the early check
   cannot - another site or application serving the folder, a node rooted inside it, and IIS changing
   between the two reads. The cdt-19 comment there ("both paths configure the application and the
   pool above") stays true.
4. `README.md`, the cdt-36 paragraph: one more sentence - an upgrade is checked the same way and is
   refused, `-AllowRepoint` or not.
5. The cdt-36 tests in `tests/ps/DeployInvariants.Tests.ps1`: the statement test and the AST test
   find the check as top-level statements rather than an `if`, and the statement test's "an upgrade
   makes no call" pin is reversed (deliberately: that is this finding). Their names stay true and are
   kept, so the cdt-36 record's guard proof still names real tests; the cdt-36 record gets a note.

Afterwards, the only thing an upgrade does before the check is create the backup folder (205), when
it is missing.

**Not changed:** the fresh-install semantics and `-AllowRepoint` on `-Force` / `-ConfirmFreshInstall`;
the pool step itself (an application that does not exist yet is created under `-AppPoolName`, which
is then the pool that serves it).

Constraints: Invariant 6 (pure ASCII, no BOM; no ternary, no `??`, no PS7-only parameters; parsed
under Windows PowerShell 5.1); Invariant 5 (`Write-Fail` throws; nothing catches it); Invariant 7
(neutral placeholders in the tests); Known Failure Class 1 (the point of the fix). No version bump:
ops scripts.

Guards, in `tests/ps/DeployInvariants.Tests.ps1`:

- Behavioural, stubbed-step: the real top-level statements from the one after the banner through
  `Write-Success "Config folder ACL set: $configFolder"` - the check, the config collection, the
  identity resolution, the pool step and the ACL grants - lifted by AST and run under `Stop` for an
  upgrade, with IIS, the pool provider, the certificate store, the folders and the ACL helper stubbed
  to record each call. The same pool and folder reach the pool step and the grants; a different
  existing pool that runs as a user, a different folder, and a different pool with `-AllowRepoint`
  each refuse with the read as the only call - no `Set-ItemProperty`, no `New-WebAppPool`, no
  `New-Item`, no grant; an unreadable application refuses the same way; a missing application
  proceeds, as today.
- Behavioural, the function: with `-Upgrade`, each difference refuses with and without
  `-AllowRepoint`, in the upgrade's words; the same pool and folder, and no application, pass.
- AST (the cdt-36 test, updated): the read and the assertion are top-level statements with no
  conditional ancestor, the assertion is given `-Upgrade:$isUpgrade`, and the order pins hold.

Mutations: the whole fix reverted (the `493c270d` file); the check moved back (after the ACL grants);
`-Upgrade` not passed; `-AllowRepoint` honoured on an upgrade.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, `README.md`, this record (new), and a
note in `.agents/review/findings/cdt-36.md`.

**After the fix**, as built: `deploy.ps1` 38-39 (the `-AllowRepoint` comment), 203-206 (the cdt-42
paragraph above the function), 216 (`[switch]$Upgrade`), 239-243 (the upgrade refusal), 299-317
(the comment and the four script-level statements, the assertion at 317 given `-Upgrade:$isUpgrade`).
`README.md` 686-688.

## Files changed

Before the repair at `493c270d` (blob; md5 of the committed LF content), and after:

- `deploy.ps1` - `618c874f8bc8`, `9CF132CC687BD96780E9EDF347C310DD`; after `B1F4B8BE24E2E86AB9965912D904D158`
- `tests/ps/DeployInvariants.Tests.ps1` - `efe1d077d19a`, `46B17658FA5013208DF10013A5A57123`; after `CB3C760D11B06C0B39D31565314FF6EB`
- `README.md` - `ab025fa5f147`, `7AAEE18A2673763253B70873FAC443F0`; after `ACE78A17B0FB267B260D46904A41EC84`
- `.agents/review/findings/cdt-36.md` (the note) - `9f2508215bb9`, `1E9AD0F21E76E8928656221E58A88F1B`; after `3C8DCF9F8AE179BF2C96056E7EDB185F`
- this record - new

No version bump. Not touched: the old `cdt-38.md` and `cdt-39.md` on this branch (the coordinator
restored them on master), `.agents/review/index.md`, `.agents/state.md`, `.agents/token-log.md`.

## Guard proof

Tests, `tests/ps/DeployInvariants.Tests.ps1`, Describe `deploy.ps1`:

| Test | Pins |
| --- | --- |
| `refuses an upgrade whose pool or folder does not serve the application, -AllowRepoint or not (cdt-42)` (957) | The real `Assert-ApplicationTargetUnchanged`, lifted by AST with deploy.ps1's helpers and the real module, called with `-Upgrade`: a missing application passes; the same pool and folder in another case and with a trailing separator pass; another pool, another folder, a folder that only starts with the run's, and an empty folder each refuse - with and without `-AllowRepoint` - with "already exists, with <what IIS reports> ... An upgrade configures the app pool and the folders it names before it deploys, and never moves the application ... Re-run with -AppPoolName '<IIS>' -PublishPath '<IIS>', or move the application in IIS first. ... Nothing was deployed."; with `-AllowRepoint` the refusal adds "-AllowRepoint does not apply: only a -Force or -ConfirmFreshInstall run re-points an application.", without it it does not; nothing ever prints "MOVES it" |
| `runs an upgrade's real steps from the check through the ACL grants: a pool or folder that does not serve the application is refused before the pool is configured or any folder is granted (cdt-42)` (1007) | The real script-level statements from the one after the banner through `Write-Success "Config folder ACL set: $configFolder"` - the check, the config collection, the identity resolution, the pool step and the grants - lifted by AST and run under `Stop` as an upgrade, with `Get-WebApplication`, `Test-Path`, `Get-ItemProperty` (a pool that runs as `EXAMPLE\svc-other`), `New-WebAppPool`, `Set-ItemProperty`, `Get-ChildItem` (the certificate store), `New-Item` and `Set-AclChecked` stubbed to record each call. The same pool and folder: the read is the first call, and `set(IIS:\AppPools\ExamplePool\|managedRuntimeVersion)` and `grant(D:\example\publish\config\|EXAMPLE\svc-other\|(OI)(CI)M)` follow. Another existing pool, another folder, another pool with `-AllowRepoint`, and an application IIS cannot read: each refuses, and the read is the only call. A missing application proceeds to the pool step, as before |
| `runs the real check before a fresh-install or -Force run configures anything ... (cdt-36)` (800, updated) | Lifts the check as the script-level statements from its step line through the assertion; its last case now pins that an upgrade with the same values passes with the read as the one call (it used to pin that an upgrade read nothing) |
| `checks the application at script level, after the consent check and before anything is configured, and nothing catches its refusal (cdt-36, AST)` (891, updated) | The step line, `$existingApplication = $null`, the read's `try` and the assertion are four consecutive statements of the script itself (the assertion's parent is the script's end block: no condition, no catch); the assertion is given `-Upgrade` `$isUpgrade` beside the cdt-36 arguments; the assertion comes before the config collection, `$poolExists`, the first statement that configures anything, and the mode branch |

Mutations on the fixed `deploy.ps1` (md5 `B1F4B8BE24E2E86AB9965912D904D158`), each applied by a script
that fails unless every edit matches exactly once and the mutant passes a sanity check of its own
shape (the runner also logs where the assertion sits), run against `DeployInvariants.Tests.ps1`;
then the original bytes restored, the mtime refreshed, the md5 re-confirmed, and the file re-run
green each time (120 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R0 the whole fix reverted: `deploy.ps1` as at `493c270d` (byte-identical: its md5 is that commit's) | `9CF132CC687BD96780E9EDF347C310DD` | 4: both cdt-42 tests (the function: an upgrade got the fresh-install refusal; the steps: the first call was not the read) and the two updated cdt-36 tests (no script-level check) |
| K1 the check moved back, after the ACL grants | `2453F898D0C77A7C2B9BC2629E554F3B` | 2: the AST test ("Expected the actual value to be less than 35 ... but got 68") and the steps test (the first call was not the read) |
| K2 `-Upgrade` not passed at the call | `97B6A9ED1FFCF322A90964F792B6692A` | 2: the AST test ("Expected exactly '$isUpgrade' ... but got $null") and the steps test (another pool got the fresh-install refusal; with `-AllowRepoint` it would have passed) |
| K3 `-AllowRepoint` honoured on an upgrade (`if ($Upgrade -and -not $AllowRepoint)`) | `A209E2FF03D28278CC7AD640F43CDD70` | 2: the function test and the steps test (the upgrade with `-AllowRepoint` was not refused) |
| K4 the check wrapped in `if (-not $isUpgrade)` again, `-Upgrade` kept | `210941BCC130F2675ED71A33A1BDDCF6` | 3: the steps test, and the two updated cdt-36 tests (no script-level check) |

Windows PowerShell 5.1.20348.5756: `deploy.ps1` parses with no errors, and a scratch harness lifting
the function and the four statements with the 5.1 parser, IIS stubbed, gave the Pester results for
eight cases (an upgrade: the same values in another spelling; another pool; another folder; another
pool with `-AllowRepoint`; absent; unreadable. A fresh install: another pool and folder, with and
without `-AllowRepoint`).

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 483 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 317
warnings, none new; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed. `dotnet build`
not re-run for this commit: no C# changed (the cdt-36 build was 0 errors).

Not run: a real upgrade (it needs elevation and IIS, and would change this host).

## Coder dispute

None.

## Known gaps

- An application that does not exist yet is created under `-AppPoolName` (497-503 at `493c270d`),
  and the pool step configures that pool first. If the name is another instance's pool, this
  instance joins it. Before cdt-42 this was the same; there is no application to compare with, so
  the check cannot see it.
- The upgrade's own checks (cdt-19's binding check, cdt-28's `Assert-AppPoolServesPath`) still run
  before the stop. They now repeat this check's pool comparison, and they still alone catch another
  node serving the folder, a nested node, and IIS changing between the two reads.
- The backup folder (205) is still created before the check on every run; cdt-45 moves it.

## Reviewer comments

None yet.

## Closeout

Pending: codex review, merge into `master`, branch deletion.
