# cdt-36: deploy.ps1 -Force re-points an existing application to whatever -PublishPath / -AppPoolName it is given

**Severity**: MEDIUM, design - `deploy.ps1` run with `-Force` over an existing deployment lands in the fresh-install branch and re-creates or re-points the IIS application to this run's `-PublishPath` and `-AppPoolName` with no cdt-15-style check that they still name the pool and folder serving the alias. A mistyped or defaulted value moves the live application (the cdt-15 / aud-a34 outage shape) while the run ends successfully.
**Status**: Merged to master `72430407` (fix `493c270d`); codex ops-batch-12 raised three candidates, admitted as cdt-44 (MEDIUM), cdt-45 and cdt-46 (LOW); closes when they land
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`, forked from `master` at `eb017102`)
**Commit**: the `fix(cdt-36)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (recorded by the supervisor at the 2026-10-23 hand-over, from the cdt-15 agent's hand-back)

## Evidence

At `4498da49`: `deploy.ps1` 33 (`-Force`), 202 (`$isUpgrade = (Test-Path $configPath) -and (-not $Force)`), 212-223 (the -Force consent and the appsettings copy), 815-882 (the -Force branch: backup, then `New-WebApplication` / re-point with `-PhysicalPath $PublishPath` and the pool), 618 and 932 (`Assert-AppPoolServesPath` runs on the upgrade path and after the mirror, not before the re-point). The cdt-15 fix in `tools/Install-ExchangeAdminWeb.ps1` (`480d290`) is the reference shape: read the existing application's physicalPath and applicationPool first and refuse a silent move. (Implementer's notes on this paragraph: the variable names in it were lost from the hand-over text, `$` and all, and are restored from the file; and 932 runs after the re-point but before the stop and the mirror, not after the mirror - the Approach quotes it.)

Trigger: `deploy.ps1 -Force` (or the pipeline passing it) with a pool name or publish path that differs from the serving one. Predicted failure: the alias now serves another pool or folder; the old one is left unserved; "complete".

## Approach

Written before the code. Line numbers are `deploy.ps1` at `eb017102`, unchanged since `4498da49`.

**Mechanism, read from the file: no error at all.** `-Force` makes the run a fresh install whatever
the folder holds, and is its own consent:

```
202: $isUpgrade = (Test-Path $configPath) -and (-not $Force)
...
223: if (-not $isUpgrade -and -not $Force -and -not $ConfirmFreshInstall) {
```

The fresh-install branch's "Configuring web application" step then sets an existing application's
folder and pool to this run's values without reading either:

```
872:         $existingApp = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction SilentlyContinue
873:         if ($existingApp) {
874:             Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name physicalPath    -Value $PublishPath
875:             Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name applicationPool -Value $AppPoolName
```

Nothing from 202 to 873 compares the application with `-PublishPath` or `-AppPoolName`. The run
configures the pool it names (343-353: `New-WebAppPool -Name $AppPoolName` when absent, then
`Set-ItemProperty "IIS:\AppPools\$AppPoolName" ...`), grants ACLs under `-PublishPath` (363-418),
and reads the application at 425 only to create it when absent (426-433). The checks before the
stop - `if ($boundPool -ne $AppPoolName)` (922) and `$null = Assert-AppPoolServesPath -Name
$AppPoolName -PhysicalPath $PublishPath -NestedNodes Refuse` (932) - ask IIS after 874-875 wrote
this run's values, so they pass on the moved application. The Evidence's "or the pipeline passing
it" does not arise: `tools/deploy-pipeline.ps1` 92-99 passes neither `-Force` nor
`-ConfirmFreshInstall`. `-ConfirmFreshInstall` reaches the same branch whenever `-PublishPath` holds
no `appsettings.json` (223), so a mistyped folder with that switch takes the same door; the check
below covers the branch, not one switch.

Predicted outcomes, from the code: a different folder - the alias serves it, and its
`appsettings.json` is generated from parameters with no `ConfigStore:Path`, because 820 reads the
key from the new folder's file, which does not exist, so the app opens that folder's own new config
database (the cdt-8 outcome: no section access, no protected principals); a pool name that exists
(another instance's) - this instance moves into that pool, and the stop at 938 stops it for the
mirror, taking the other instance down with it; a new pool name with `-ServiceAccount` - a second
pool. Each time the old pool and folder are left as they were, and the run ends "Deploy complete
(INSTALL)".

**Change.**

1. `deploy.ps1` param block: `[switch]$AllowRepoint` after `-ConfirmFreshInstall`, with a comment:
   the explicit consent to move an existing application; read only on the fresh-install path, since
   an upgrade never re-points.
2. `deploy.ps1` helpers, beside `Write-AppSettingsFile`: `Assert-ApplicationTargetUnchanged
   -Location -Exists -ExistingPool -ExistingPhysicalPath -AppPoolName -PublishPath -AllowRepoint`,
   the cdt-15 function plus the switch. No application: prints that the run creates it, passes. The
   same pool (case-insensitive, as IIS compares names) and folder (both through
   `ConvertTo-ComparablePhysicalPath`, compared by equality, never by prefix; an empty folder counts
   as different): prints the comparison, passes. A difference: `Write-Fail` naming what IIS reports,
   what the run names, the values to re-run with and `-AllowRepoint`, ending "Nothing was deployed."
   A difference with `-AllowRepoint`: `Write-Warn` naming the move, passes.
3. `deploy.ps1` at script level, right after the banner (227-232) and before "Collect & validate
   config" (234-236): one statement, `if (-not $isUpgrade) { Write-Step ...; try {
   $existingApplication = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop }
   catch { Write-Fail ... }; Assert-ApplicationTargetUnchanged ... }`. After the consent check (223),
   so an unconsented fresh install is refused as before; before the prompts, the pool step, the ACLs,
   the application read at 425, the publish and the branch, so it refuses before IIS, an ACL or the
   publish folder is touched. Nothing catches it (Invariant 5). A lookup that throws refuses. Only the
   two backup writes at 205 (the backup folder) and 212-216 (the pre-force copy of
   `appsettings.json`) come before it; they stay, and the refusals say "Nothing was deployed", as
   deploy.ps1's other refusals do.
4. `tools/AppPoolControl.psm1`: export `ConvertTo-ComparablePhysicalPath` (its "Not exported" comment
   updated), so this check and `Assert-AppPoolServesPath`, which the same run calls on the same folder
   before the stop, agree on what one folder is - no third copy of the normaliser. The installer
   keeps its own copy and is not touched (Invariant 1).
5. `README.md`, deploy section: one paragraph - `-Force` and `-ConfirmFreshInstall` point an existing
   application at the run's folder and pool, a differing run is refused, `-AllowRepoint` moves it.

**Not changed, deliberately.**

- **No `-PlanOnly`.** The outline this record carried, and the brief, ask that `-PlanOnly` report the
  comparison, but `deploy.ps1` has no plan mode: `tools/deploy-pipeline.ps1` 85 prints "deploy.ps1
  has no native plan mode yet; -PlanOnly skips the dev deploy entirely.", and cdt-8, cdt-9, cdt-12,
  cdt-19 and cdt-28 each record the same constraint. A `-PlanOnly` that planned this one step would
  present a partial plan as a plan; a plan mode for the script is a work stream of its own. The new
  step is read-only and prints its comparison on every run before anything is touched, so a future
  plan mode runs it unchanged. Owner call, in Known gaps.
- `tools/deploy-pipeline.ps1`: nothing to plumb (above).
- The upgrade path: it never re-points (426-433 create the application only when absent), and it
  refuses a pool or a folder that does not serve the application before its stop (its own
  `$boundPool` check, and 618).
- 873-875: after the check they rewrite the values IIS already has, or carry out the move
  `-AllowRepoint` asked for.
- `tools/Install-ExchangeAdminWeb.ps1` and its normaliser (cdt-15).

Constraints: Invariant 6 (pure ASCII, no BOM; no ternary, no `??`, no PS7-only parameters; parsed
under Windows PowerShell 5.1); Invariant 5 (`Write-Fail` throws; nothing catches the refusal);
Invariant 7 (no environment names; fixtures use neutral placeholders). No version bump: ops scripts.

Guards, in `tests/ps/DeployInvariants.Tests.ps1`:

- Behavioural, the function lifted by AST with deploy.ps1's own `Write-*` helpers and the real
  module: no application passes; the same pool and folder pass however IIS spells the folder (case,
  trailing separator, forward slashes, dot segments, an environment variable) and with the pool in
  another case; a different pool, a different folder, both, a folder that only starts with the
  run's, and an empty folder each refuse, naming what IIS reports, the run's values, the values to
  re-run with, `-AllowRepoint` and "Nothing was deployed"; each difference with `-AllowRepoint`
  passes, warning of the move.
- Behavioural, the real script-level statement lifted by AST and run with `Get-WebApplication`
  stubbed and every cmdlet that changes IIS, an ACL or a file stubbed to record a call: the same
  values proceed and print the comparison; a different pool refuses; a different folder refuses;
  `-AllowRepoint` proceeds with the warning; an absent application proceeds; a lookup that throws
  refuses; a `-ConfirmFreshInstall` run is checked as a `-Force` run is; an upgrade reads nothing.
  In every case the one IIS call is the read - the step prints and changes nothing.
- AST: exactly one such statement, at script level, conditioned exactly on `-not $isUpgrade`, after
  the consent check and the banner and before the config collection, `$poolExists`, the first
  top-level statement that changes IIS, an ACL or the publish folder, and the `try` that holds the
  mode branch; no `try` with a `catch` around the assertion; the read's `catch` refuses; the
  assertion is given the read's pool and folder and the run's `-AppPoolName`, `-PublishPath` and
  `-AllowRepoint`; `AllowRepoint` is a switch.

Mutations: the whole fix reverted; the call removed; the pool comparison dropped; the folder
comparison dropped; the folder compared by prefix; `-AllowRepoint` ignored; the gate narrowed to
`$Force`; the statement moved after the pool step; the read's `catch` only warning.

**After the fix**, as built: `deploy.ps1` 35-39 (the switch and its comment), 191-238 (the comment
and `Assert-ApplicationTargetUnchanged`), 288-305 (the script-level statement, between the banner and
"Collect & validate config" at 307-309); the re-point is now 946-948. `tools/AppPoolControl.psm1`
259-277 (the comment and the export). `README.md` 682-686. Two additions to the plan above, both in
the guards: an M9 (the module export removed) and an M10 (the statement moved into the fresh-install
branch, right before the re-point - the other reading of "before the -Force branch changes IIS").

## Files changed

Declared before the repair (snapshot at `eb017102`: blob, and md5 of the committed LF content), and
the md5 after it:

- `deploy.ps1` - `9f345207d2d0`, `BC0E7485E1C2AD5DDD159CCE32A43542`; after `9CF132CC687BD96780E9EDF347C310DD`
- `tools/AppPoolControl.psm1` - `e04ff5f4fdba`, `1A5E78C1B9091C2FFDB29E912F40C0A4`; after `5FA32F59DE86C57282B1EE5A65C18253`
- `tests/ps/DeployInvariants.Tests.ps1` - `995d70cf328c`, `0D01313E8836100A0530DD83443E0FCC`; after `46B17658FA5013208DF10013A5A57123`
- `README.md` - `808a9bde6c56`, `694148346D09AE1DB978AD6241C8515C`; after `7AAEE18A2673763253B70873FAC443F0`
- this record - `2b6696fe25fa`

No version bump (ops scripts and docs only). Not touched: `tools/Install-ExchangeAdminWeb.ps1`,
`tools/deploy-pipeline.ps1`, `.agents/review/index.md`, `.agents/state.md`, `.agents/token-log.md`.

## Guard proof

Tests, `tests/ps/DeployInvariants.Tests.ps1`, Describe `deploy.ps1`, 736-944:

| Test | Pins |
| --- | --- |
| `refuses a fresh-install or -Force run that would move an existing application, unless -AllowRepoint names the move (cdt-36)` | The real `Assert-ApplicationTargetUnchanged`, lifted by AST with deploy.ps1's `Write-Success`, `Write-Warn` and `Write-Fail` and the real module, its output captured. No application passes, printing that the run creates it. The same folder spelled `D:\example\publish`, `d:\EXAMPLE\Publish`, with a trailing separator, with forward slashes, through `x\..`, and as `%CDT36_ROOT%\publish` passes, with the pool in another case, printing the comparison. A different pool, a different folder, both, a folder that only starts with the run's, and an empty folder each throw, naming what IIS reports and what the run names, and ending "Re-run with -AppPoolName '<IIS>' -PublishPath '<IIS>' to deploy it in place, or add -AllowRepoint if the move is intended. Nothing was deployed."; each of the five with `-AllowRepoint` passes, warning "-AllowRepoint: this run MOVES it to app pool 'ExamplePool' with its files at 'D:\example\publish'" |
| `runs the real check before a fresh-install or -Force run configures anything: one IIS read, the comparison printed, nothing changed (cdt-36)` | The real script-level statement, lifted by AST and run under `Stop`, for a `-Force` run and for a `-ConfirmFreshInstall` run, with `Get-WebApplication` stubbed and seventeen commands that change IIS, an ACL or a file stubbed to record a call (`New-WebAppPool`, `Set-ItemProperty`, `New-WebApplication`, `Set-WebConfigurationProperty`, `Remove-WebConfigurationProperty`, `Stop-WebAppPool`, `Start-WebAppPool`, `Stop-AppPoolVerified`, `Start-AppPoolVerified`, `Set-AclChecked`, `icacls`, `robocopy`, `dotnet`, `New-Item`, `Copy-Item`, `Remove-Item`, `Set-Content`): the same pool and folder proceed and print the comparison; a different pool and a different folder refuse; both, with `-AllowRepoint`, proceed with the warning; no application proceeds; a lookup that throws refuses, `-AllowRepoint` or not, naming the error. In every case the one call is `lookup(Example Site/ExampleApp|Stop)` - the step prints and changes nothing. An upgrade makes no call at all |
| `checks the application at script level, after the consent check and before anything is configured, and nothing catches its refusal (cdt-36, AST)` | One script-level `if (-not $isUpgrade)` holding the check, with no else; one `try` in it whose body reads `$existingApplication = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop` and whose one `catch` statement is `Write-Fail "Cannot read IIS application $ParentSite/$AppAlias ...`; one `Assert-ApplicationTargetUnchanged` call in the script, a statement of that block, after the read, given `"$ParentSite/$AppAlias"`, `($null -ne $existingApplication)`, `"$($existingApplication.applicationPool)"`, `"$($existingApplication.PhysicalPath)"`, `$AppPoolName`, `$PublishPath` and `$AllowRepoint`, with no `try` around it that has a `catch`. The statement comes after the consent check and the banner, and before the config collection, `$poolExists`, the first top-level statement that runs `New-WebAppPool`, `Set-ItemProperty`, `Set-AclChecked`, `New-WebApplication`, `Set-WebConfigurationProperty`, `Remove-WebConfigurationProperty`, `robocopy`, `dotnet` or `Stop-AppPoolVerified`, and the `try` that holds the mode branch. `AllowRepoint` is a switch parameter |

Mutations on the fixed files (md5s above), each applied by a script that fails unless every edit
matches exactly once, run against `DeployInvariants.Tests.ps1` (with `AppPoolControl.Tests.ps1` for
R0 and M9); then the original bytes restored, the mtime refreshed, the md5 re-confirmed, and the same
suites re-run green each time (118 passed; 205 for R0 and M9).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R0 the whole fix reverted: `deploy.ps1` and the module as at `eb017102` (the snapshot md5s) | `BC0E7485E1C2AD5DDD159CCE32A43542`; module `1A5E78C1B9091C2FFDB29E912F40C0A4` | the three cdt-36 tests (the function test: "deploy.ps1 defines Assert-ApplicationTargetUnchanged, but got $null"; the other two: "Expected 1 ... but got 0") and nothing else - 202 passed |
| M1 the call commented out | `509364A675241BF69A5155D3BF7F5659` | the statement test (nothing compared or printed after the step line) and the AST test ("one call, in this statement, but got 0") |
| M2 the pool comparison dropped | `7C07BAEE5387A0BD130BA3A61C167E5A` | the function test ("a different pool": no refusal) and the statement test (a `-Force` run moved into another pool) |
| M3 the folder comparison dropped | `BCA70284AE4AB9DAF668787AA8669CA0` | the function test ("a different folder") and the statement test (moved to another folder) |
| M4 the folder compared by prefix (`-not $existingFolder.StartsWith($runFolder, ...)`) | `A5E8FB404327307D26C52DC9F8A655D3` | the function test ("a folder that only starts with the run's") |
| M5 `-AllowRepoint` ignored (`if ($false)`) | `299AF14BDFD3834C38EBA0E9D3974C0A` | the function test and the statement test (the consented move refused) |
| M6 the gate narrowed to `-Force` (`if ($Force)`) | `FAC894FED52A8CC1C279B81C7AF49AA1` | the statement test (the `-ConfirmFreshInstall` run read nothing) and the AST test (the condition) |
| M7 the statement moved after the pool step (`Write-Success "App pool configured"`) | `DEF8CB79844271256D8BF8FC33CACE9F` | the AST test ("Expected the actual value to be less than 35, because a run that would move the application is refused before anything is prompted for, but got 50") - the behavioural tests run the same statement wherever it sits, so only the order pin can see this, which is what it is for |
| M8 the read's `catch` only warns (`Write-Warn`) | `CCD877DE17B0FAF4535D44B50709C0EB` | the statement test (an unreadable application passed) and the AST test (the `catch` statement) |
| M9 the module export removed | module `AF8FC6D9F03E7CFD44CA39525FAFF9CB` | both behavioural tests ("The term 'ConvertTo-ComparablePhysicalPath' is not recognized") - the export is load-bearing; 203 passed |
| M10 the statement moved into the fresh-install branch, right before the re-point (three blocks deep) | `32555AB0AE10937C96EC960967D0D983` | the statement test and the AST test (no script-level statement) |

A first M7 was discarded, recorded so it is not re-learned. It was built as `@("old", "new" +
($block ...))`, which PowerShell reads as `("old", "new") + $block` - the comma binds tighter than
`+` - so the edit replaced the statement with nothing, and the mutant
(`B4AA3B1FB3B62F0F3F28F594E2471C2E`) was "the statement removed". It failed the statement and AST
tests, as a removal would; its label did not match it. The builder was parenthesised, a check that
each moved mutant still holds exactly one call was added, the runner now logs where the call sits in
each mutant, and M7 and M10 were re-run (M10 rebuilt byte-identical to its first run).

Windows PowerShell 5.1.20348.5756: `deploy.ps1` and the module parse with no errors, and a scratch
harness that lifts the function and the statement with the 5.1 parser, IIS stubbed, gave the Pester
results for nine cases (the same; the same in another spelling and case; a different pool; a
different folder; a prefix folder; both, with `-AllowRepoint`; absent; unreadable, with
`-AllowRepoint`; an upgrade).

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 481 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse` (1.25.0) 0 errors, 317 warnings - one new, a
`PSAvoidUsingWriteHost` on the function's no-application line, the script's own `Write-Host` style;
`git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed; `dotnet build ExchangeAdminWeb.slnx
-c Release` 0 errors (no `error CS`).

Not run: a real deploy (it needs elevation and IIS, and would change this host); `Get-WebApplication`
against IIS (no live probe) - see Known gaps.

## Coder dispute

None. Two departures from the brief, both explained in Approach: no `-PlanOnly`, because
`deploy.ps1` has no plan mode; and the check covers `-ConfirmFreshInstall` as well as `-Force`,
because both reach the branch that re-points.

## Known gaps

- **Owner call - a plan mode for `deploy.ps1`.** The brief asked that `-PlanOnly` report the
  comparison; the script has no plan mode (Approach). Proposed: the owner decides whether
  `deploy.ps1` gets a native plan mode as its own work item; the cdt-36 statement would run in it
  unchanged.
- **Assumed, not verified:** `Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop`
  returns nothing, without an error, when no such application exists - the assumption the installer's
  cdt-15 read makes. Not checked against IIS (no live probe). If it is wrong, a genuine fresh install
  through `deploy.ps1` is refused with "Cannot read IIS application ... (<the error>)": fail-closed,
  and the message names the error.
- An application whose pool IIS reports as empty (an inherited pool, if the provider reports one
  that way - not verified) is now refused as `app pool ''` unless `-AllowRepoint`; before the fix a
  fresh-install or `-Force` run re-pointed it to `-AppPoolName` and went on, because the re-point
  writes the pool before the cdt-19 binding check reads it. Fail-closed: the run cannot show which
  pool serves it. Applications this script creates always name their pool (`New-WebApplication
  -ApplicationPool $AppPoolName`).
- `-AppAlias` and `-ParentSite` are the lookup key, not compared: a different alias or site is a
  different application (as in cdt-15).
- `-AllowRepoint` moves the application as before (946-948): the new folder gets this run's generated
  `appsettings.json`, with a `ConfigStore:Path` only if that folder's own file had one. The warning
  names the move; it does not carry the old folder's key (cdt-8 territory, not in scope).
- The backup folder (205 at `eb017102`) and the pre-force copy of `appsettings.json` (212-216) are
  still written before a refusal; both are backups.
- The check reads IIS once, before the prompts and the publish; a change another hand makes to the
  application between that read and the re-point is not seen.

## Reviewer comments

None yet.

## Closeout

Pending: codex review, merge into `master`, branch deletion (local and remote).

## Reviewer comments (supervisor, ops-batch-12)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 493c270d (merged 72430407); 2026-10-23T19:06-19:13Z; capability proof: AGENTS.md read and git show --stat 493c270d both succeeded. Verdict: three candidate findings, all admitted - cdt-44 (-AllowRepoint backs up from the new path),
cdt-45 (backup writes precede the check), cdt-46 (8.3 / junction normalisation). Codex confirmed: the tests stub the real
`Get-WebApplication` name; the position test is structural; the module export list is intact; both scripts ASCII-clean.
Codex could not live-probe `WebAdministration` missing-app behaviour (module absent, no IIS access); the code treats no
output as fresh and a thrown read as fail-closed, the same assumption the cdt-15 installer check makes. Result and stream
in `.agents/review/ops-batch-12.result.txt` and `.stream.log`; prompt `prompt-ops-batch-12.md` (it names the two
follow-ons by their first, mistaken IDs cdt-38/39; they are cdt-42/43).