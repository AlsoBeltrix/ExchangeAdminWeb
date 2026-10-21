# cdt-15: an installer update with a default -AppPoolName or -PublishPath silently moves the existing application

**Severity**: HIGH. `tools/Install-ExchangeAdminWeb.ps1` "Create or update IIS web application"
sets an EXISTING application's `physicalPath` and `applicationPool` to this run's `-PublishPath`
and `-AppPoolName`, and both parameters default to fixed values (`<SystemDrive>\inetpub\<alias>`
and `ExchangeAdminWeb`) when omitted. An update of an instance installed with either value
customised, re-run without repeating it, therefore moves the live application: to a new pool,
which the installer creates with ApplicationPoolIdentity because no pool of that name exists (the
aud-a34 outage - the app loses the service account its credential store, certificate key,
directory access and shared database hang off), and/or to a new folder, where there is no
`appsettings.json`, so the run treats it as a fresh install and the URL now serves a new instance
with generated defaults and an empty per-instance config database (the cdt-8 outcome - no
protected principals, no section access). The old pool and folder are left as they were, unserved.
Nothing errors; the run ends "Install/update complete." Found while fixing cdt-11 and cdt-18.
**Status**: Closed - codex group review (ops-batch-3, 2026-10-21) raised no defect in this change
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `480d290` (merged)
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `80bdd65`. The defaults:

```
61:     [string]$AppAlias = "ExchangeAdminWeb",
62:     [string]$AppPoolName = "ExchangeAdminWeb",
63:     [string]$PublishPath,
...
581: $defaultPublishPath = Join-Path $env:SystemDrive "inetpub\$AppAlias"
...
584: $PublishPath = Read-Value "PublishPath" "Publish path" $PublishPath $defaultPublishPath
```

(`Read-Value` returns the default without asking under `-NonInteractive`.) The step that moves the
application:

```
832: Invoke-PlanOrAction "Create or update IIS web application $ParentSite/$AppAlias" {
...
837:     $existingApp = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction SilentlyContinue
838:     if ($existingApp) {
839:         Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name physicalPath -Value $PublishPath
840:         Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name applicationPool -Value $AppPoolName
```

Nothing before it compares the existing application with the run's values. The pool step runs
first and keys everything on the name it was given: `$poolExists` (line 690) is false for a new
name, so the identity decision (`Resolve-AppPoolIdentityPlan`, aud-a34) is `SetApplicationPoolIdentity`
and every folder ACL goes to `IIS AppPool\<new name>`. aud-a34's "an omitted parameter is not an
instruction to change anything" protects the identity of the pool the run names, not the pool the
application actually runs under. Every later step keys on `-PublishPath`: `$configPath` is
`<new folder>\appsettings.json`, absent, so `Resolve-ConfigStorePlan` (cdt-11) finds no key and
the file is generated from parameters.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348 (scratch probe: the
installer's own step lifted by the 5.1 parser and run with only the IIS cmdlets stubbed; IIS reports
the application served from `D:\apps\Portal` by `PortalPool`; the run's `-AppAlias`, `-AppPoolName`
and `-ParentSite` are the script's own defaults and `-PublishPath` a stand-in for the default
folder):

```
existing application 'Default Web Site/ExchangeAdminWeb': pool PortalPool, files D:\apps\Portal
this run (defaults): -AppPoolName ExchangeAdminWeb, -PublishPath <default>
  Set-ItemProperty physicalPath = <tmp>\...\inetpub-default
  Set-ItemProperty applicationPool = ExchangeAdminWeb
```

A plan shows nothing either: `-PlanOnly` prints "PLAN Create or update IIS web application ..." and
never reads the application.

## Plan (approved by the owner's EVERYTHING go)

1. Read the application once, at script level, after WebAdministration is imported and the site
   checked and before the pool step and every other change: `Get-WebApplication -Name $AppAlias
   -Site $ParentSite`. A lookup that throws refuses the run in apply mode (the target cannot be
   verified); in plan mode, where WebAdministration may be absent, it warns that the plan cannot
   check and that a real run refuses a differing application.
2. A function, `Assert-ApplicationTargetUnchanged -Location -ExistingPool -ExistingPhysicalPath
   -AppPoolName -PublishPath`: no existing application passes (a fresh install); otherwise the
   existing pool must equal `-AppPoolName` (case-insensitive, as IIS compares names) and the
   existing folder must equal `-PublishPath` after both are normalised (environment variables
   expanded - IIS often stores `%SystemDrive%\...` - made absolute, trailing separators dropped,
   compared case-insensitively by equality, never by prefix). A difference refuses with the
   installer's `Write-Fail`, naming what IIS reports, what the run would set, and the values to
   re-run with; moving an application deliberately is an IIS change made first, not a side effect
   of an update. Nothing has been changed at that point.
3. The help text's Update section says an update must name the same application, pool and folder
   the instance was installed with, and that a differing run is refused.

Assumptions, recorded:

- No new switch to allow the move: it is rare, destructive, and expressible in IIS directly; an
  opt-in can be added if the owner wants one. Refusing is the fail-closed answer.
- A missing or empty `physicalPath` on an existing application counts as different (refused):
  the run cannot show it is updating the folder IIS serves.
- `-AppAlias` is not compared: it is the key the application is looked up by, so a different alias
  is a different application (a second instance), which this check does not prevent.
- The normalisation is the same idea as master's `ConvertTo-ComparablePhysicalPath` (aud-a38c, in
  `tools/AppPoolControl.psm1`, which this branch predates and does not edit); a local copy, so the
  installer does not depend on a module change it cannot see.

Constraints: Invariant 1 (standalone; no mention of the ADI deploy script); Invariant 4 (the read
is side-effect free and runs in both modes; the refusal is a refusal in both); Invariant 6 (pure
ASCII, no BOM, no ternary, no `??`, no PS7-only parameters; parsed under 5.1). No version bump.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

Guards: a behavioural test driving `Assert-ApplicationTargetUnchanged` (lifted by AST, with the
installer's `Write-Fail`): no application passes; the same pool and folder pass, including a
different case, a trailing separator and an environment-variable spelling; a different pool, a
different folder, both, and an empty folder each refuse, naming both values and the values to
re-run with. An AST test: the read and the assertion at script level, once each, after the
WebAdministration import block and before the first state change and the identity decision; the
assertion is given the read's pool and folder and the run's `-AppPoolName` and `-PublishPath`.
Mutations: the call removed; the pool comparison dropped; the folder comparison dropped; the
comparison by prefix; the read moved after the pool step; the `80bdd65` shape.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix:

- Lines 33-35: the Update help says the application is updated in place and a moving run is refused.
- Lines 583-597: `ConvertTo-InstallerComparablePath` (comment, then the function).
- Lines 600-633: `Assert-ApplicationTargetUnchanged` (comment, then the function).
- Lines 742-759: at script level, right after the WebAdministration import block and before the
  `$poolExists` read and the identity decision (777): a comment, the read in a `try`, the
  lookup-failure branch (plan: `Write-Warn`; apply: `Write-Fail`), and the assertion.

A plan on a machine without IIS (this one, and the CI runner) now prints "This plan cannot read IIS
application ... a real run refuses if IIS serves it from a different folder or pool." beside the
existing WebAdministration warning.

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-15: ...`) | Pins |
| --- | --- |
| `passes a fresh install and an in-place update, however IIS spells the folder` | No application passes; the same folder spelled `d:\APPS\app`, `D:\apps\App\`, `D:/apps/App`, `D:\apps\x\..\App` and `%CDT15_ROOT%\App` passes, with the pool in another case |
| `refuses <Case>, naming what IIS reports and what to re-run with` (5 cases: a different pool, a different folder, both, a folder that only starts with the run's, an existing application with no folder) | Each throws, naming what IIS reports, "Re-run with -AppPoolName '<existing>' -PublishPath '<existing>'" and "Nothing was changed" |
| `reads the application and asserts once, at script level, before anything changes (AST)` | One script-level `try` that reads `$existingApplication = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop`; one `if ($applicationLookupFailed)` that warns in a plan and `Write-Fail`s otherwise; one `Assert-ApplicationTargetUnchanged` given `"$ParentSite/$AppAlias"`, `($null -ne $existingApplication)`, the read's `applicationPool` and `PhysicalPath`, `$AppPoolName` and `$PublishPath`; in that order after the WebAdministration import block, and before the identity decision and the first state change |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `2F9D96C01DA73B3331CCB4AACB793607`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed; the
restored file re-run green (131 passed). Post-comment md5 `314823863614C2CCA08D7B173E2F2D21` (131
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| Z1 the call removed | `ED770B9BF206B84EE5F42CC3F0E2BB40` | the AST test ("Expected 1, but got 0") - the behavioural tests drive the function, so only the call-site pin can see this, which is what it is for |
| Z2 the pool comparison dropped | `E45934E46431F54D2AEFE7C64F507E74` | `refuses a different pool` ("Expected a value ... but got $null or empty"); `refuses both` (no "app pool 'PortalPool'" in the message) |
| Z3 the folder comparison dropped | `5AADC7A8B257246E6ED86033BA5BB60D` | `refuses a different folder`, `refuses both`, `refuses a folder that only starts with the run's`, `refuses an existing application with no folder` |
| Z4 the folder compared by prefix (`-not $existingFolder.StartsWith($runFolder, ...)`) | `9CDAADF4875B7A55BAA4243F1FB5E1D0` | `refuses a folder that only starts with the run's` ("Expected a value ... but got $null or empty") |
| Z5 the read and the assertion moved after the pool step, just before the web application step | `3C2403DB46FB5BEEAD6664C04834CD51` | the AST test ("Expected the actual value to be less than 80, because nothing about the pool is decided before the target is known to be the existing one, but got 108") |
| Z6 pre-fix shape (`80bdd65`) | `A6CDBAD0FC9AA70907B01F47D6B7E672` | all seven cdt-15 tests (their `BeforeAll`: no `ConvertTo-InstallerComparablePath`) |

Two things this round exposed, recorded so neither is re-learned:

- A first Z1 was discarded. It was built as `($lines | Where-Object { ... })[0]`, which on a single
  match indexes the STRING, so the edit removed only the call's leading "A" and the installer then
  died on `ssert-ApplicationTargetUnchanged` - a different mutation, which also failed the cdt-11
  `-PlanOnly` test for that reason. The exactly-once check passed because "CRLF + A" happened to
  occur once. Rebuilt as `@(...)[0]`.
- In the same round the cdt-11 `-PlanOnly` test failed once under Z2, a mutation that cannot affect
  it. It is flaky: run alone it failed 2 of 10 and 1 of 12 times, each time on a directory's
  timestamp moving one tick between the "before" and "after" snapshots. Fixed separately, in the
  next commit, with a correction to the cdt-11 record.
