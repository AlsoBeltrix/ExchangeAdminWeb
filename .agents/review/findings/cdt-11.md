# cdt-11: the installer's -RegenerateAppSettings drops ConfigStore:Path, and its backup then snapshots the wrong database

**Severity**: HIGH. `tools/Install-ExchangeAdminWeb.ps1 -RegenerateAppSettings` without
`-ConfigStorePath` regenerates `appsettings.json` without the existing `ConfigStore:Path`. On a
shared-database instance (Architectural Invariant 2: instances on one server open one
`exchangeadmin.db` named by that key) the app then opens its own `config\exchangeadmin.db`
instead: the stale pre-cutover copy the cutover leaves in place, or, with none, a new database
seeded at defaults. That is the instance's authorization and settings store - section access,
protected principals, module enablement, module admins - silently replaced while the other
instance keeps the shared file. The pre-upgrade backup (A36) re-reads `appsettings.json` AFTER
that write, so it snapshots the stale per-instance file (or reports "nothing to back up") and
the shared database the operator believes is protected is not backed up at all. The same class
as cdt-8 (deploy.ps1 -Force), in the installer.
**Status**: Merged to master; codex ops-batch-3 extends it as cdt-29 (-RegenerateAppSettings -ConfigStorePath B still repoints an existing shared-DB instance) - fix in progress
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `5784583` (merged)
**Reviewer**: implementer (surfaced by the cdt-8/9/10 agent)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `27f8167`. The writer preserves an existing file unless
told to regenerate, and on regenerate writes exactly the object it is given:

```
283:     if ((Test-Path -LiteralPath $Path -PathType Leaf) -and -not $RegenerateAppSettings) {
284:         Write-Ok "appsettings.json exists - preserving"
...
295:     Invoke-PlanOrAction "Write appsettings.json at $Path" {
...
300:         $Settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
```

The object carries the key only when the PARAMETER was given:

```
513:     if (-not [string]::IsNullOrWhiteSpace($ConfigStorePath)) {
514:         $settings.ConfigStore = [ordered]@{
515:             Path = $ConfigStorePath
516:         }
517:     }
...
736: Write-AppSettings -Path $configPath -Settings (New-AppSettingsObject `
...
742:     -ConfigStorePath $ConfigStorePath)
```

Nothing reads the file being replaced. The backup, eighty lines later and after the publish,
resolves the database from the file as it is NOW - the one line 736 just wrote:

```
831: Invoke-PlanOrAction "Back up the config database before upgrading (via $($configDbToolPlan.Summary) backup)" {
832:     $existingDb = Resolve-ConfigDbPath -PublishPath $PublishPath
833:     $configuredDbPath = Get-ConfigStorePathSetting -PublishPath $PublishPath
...
859:     $backupFile = Backup-SqliteConfigDb -DbPath $existingDb -DestDir $backupDir -Timestamp $backupStamp -AppPath $stagingPath
```

The banner reports the parameter, not the instance: line 583 prints `config\exchangeadmin.db (this
instance only)` whenever `-ConfigStorePath` is omitted, so a plan shows nothing wrong either.

The app side (`Services/Storage/ConfigStorePath.cs`): a blank or absent key resolves to
`<ContentRoot>\config\exchangeadmin.db` with `MustExist: false`, "created on first start if
missing" - the supported per-instance default, not a fault. `tools/Move-ConfigDbToShared.ps1`
leaves each instance's own database in place (its rollback is "remove the key"; cdt-8 Evidence),
so after a cutover there is a stale per-instance copy for the backup to find.

**Mechanism: no error at all**, reproduced rather than reasoned. Scratch probe under Windows
PowerShell 5.1.20348 (the installer's runtime): the installer's own `Write-AppSettings`,
`New-AppSettingsObject`, `Invoke-PlanOrAction` and output helpers, lifted from the `27f8167`
file by the 5.1 parser (0 parse errors), run with `$RegenerateAppSettings = $true` and no
`-ConfigStorePath` against a temporary publish folder whose `appsettings.json` names a shared
database, beside a stale `config\exchangeadmin.db`:

```
before the write : ConfigStore:Path = [...\shared\exchangeadmin.db]
after the write  : ConfigStore:Path = []
backup resolver, re-reading the file after the write: [...\publish\config\exchangeadmin.db]
top-level keys now: Application,OperationTrace,Security,Delinea,Email,ServiceNow,Audit,AllowedHosts,Serilog,ExchangeOnline,OnPremExchange
appsettings backups: 1
```

`Set-Content` succeeded, nothing was written to the error stream, and the `.bak` copy is the
only trace of the key. The backup then verifies and reports the stale per-instance file
("Config database backed up (verified)"), or with none, "No existing config database ... -
nothing to back up", and the run ends on "Install/update complete."

## Plan (approved by the owner's EVERYTHING go)

**Decisions, recorded as assumptions** (taken under the owner's go; the owner did not rule on
these cases):

- Precedence, as briefed: a supplied `-ConfigStorePath` wins; when it is omitted the existing
  key is preserved exactly as `Get-ConfigStorePathSetting` reads it (trimmed, as the app trims
  it); with neither, no key. No new parameter.
- The backup targets the database the instance will open after this run, decided ONCE before
  anything is written, from the pre-write `appsettings.json` and the parameters - never by
  re-reading the file this run may have just rewritten. With `-ConfigStorePath` omitted (this
  finding's case) that is exactly the pre-write file's database. With `-ConfigStorePath` supplied
  and written, it is the supplied database, as at `27f8167`: that is the file the new binaries
  open and may migrate (a fresh install of a second instance against an existing shared database
  is the documented use), so backing up the pre-write file's database instead would leave the
  one at risk unprotected. This is narrower than the brief's literal "resolve from the pre-write
  appsettings.json" in that one case, deliberately.
- When `appsettings.json` exists and `-RegenerateAppSettings` is not passed, the file is kept, so
  its key stands and the plan reports it even if `-ConfigStorePath` was given. That a supplied
  path is then ignored is a separate defect, recorded and fixed separately in this batch; this
  finding only stops the backup and the banner from contradicting the file that stays.

Steps:

1. Move `Import-Module ... SqliteConfigBackup.psm1` up, above the shared-database block (it is
   side-effect free; its reader is needed before the banner). Unchanged otherwise.
2. A function, `Resolve-ConfigStorePlan -PublishPath -SuppliedPath -RegenerateAppSettings`,
   defined beside `New-AppSettingsObject`: reads the existing key once with
   `Get-ConfigStorePathSetting`; `WritesAppSettings` is Write-AppSettings' own rule (no file, or
   `-RegenerateAppSettings`); `Path` is the supplied path when this run writes the file and one
   was given, else the existing key, else `$null`; `Source` says which (`Supplied`, `Existing`,
   `None`); `DbPath` is `Path` when set, else `Resolve-ConfigDbPath` (the per-instance default);
   `Summary` is the banner text.
3. Call it once, at script level, right after `-ConfigStorePath` is validated and made absolute
   and before the banner and every state change: `$configStorePlan = Resolve-ConfigStorePlan
   -PublishPath $PublishPath -SuppliedPath $ConfigStorePath -RegenerateAppSettings
   $RegenerateAppSettings.IsPresent`. A file that does not parse stops the run there (a
   terminating error from `ConvertFrom-Json`, fatal under the script's `Stop`), before anything
   changes; it used to be overwritten blind under `-RegenerateAppSettings`, which is exactly the
   risk here.
4. The banner prints `$configStorePlan.Summary`, so a plan shows a kept key and where it came
   from. `Write-AppSettings` names a carried `ConfigStore:Path` in its plan and action line.
5. The write passes `-ConfigStorePath $configStorePlan.Path`.
6. The backup reads `$configStorePlan.DbPath` and `$configStorePlan.Path` instead of re-reading
   the file, and its plan line names the database it will snapshot. Order unchanged: backup
   after the publish (the tool is in staging), before the pool stops and the mirror.
7. The help text's shared-database paragraph says what an omitted `-ConfigStorePath` now means.

Constraints: Invariant 1 (no coupling to or mention of the ADI deploy script; the new code names
no host or environment); Invariant 4 (every new output is a plan line or a read; nothing new acts
in `-PlanOnly`); Invariant 6 (pure ASCII, no BOM, no ternary, no `??`, no PS7-only parameters;
parsed under Windows PowerShell 5.1). No version bump: ops-script-only (precedent `aud-a34`,
`aud-a36`, `cdt-5`..`cdt-10`).

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`,
`tests/ps/DeployInvariants.Tests.ps1` (one pinned call-site regex), this record.

Guards: a behavioural test that lifts `Resolve-ConfigStorePlan`, `New-AppSettingsObject` and
`Write-AppSettings` and runs them as the script does against a temporary publish folder - key
preserved, key supplied, key absent, fresh install, file kept; an AST test pinning the single
top-level resolve (after the import and the `-ConfigStorePath` validation, before the first
state change and the write), the write's argument, the backup's use of the plan, and that
nothing else reads `ConfigStore:Path` from disk; and a `-PlanOnly` run of the real installer
under Windows PowerShell 5.1 against temporary paths that asserts the plan prints the kept key
and creates nothing. Mutations: drop the carry-over; the old parameter at the call site; the
resolve moved after the write; the backup re-reading the file; the writes rule broken; the
`27f8167` shape.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix:

- Lines 43-45: the help text says an omitted `-ConfigStorePath` keeps an existing instance's key.
- Lines 297-300: `Write-AppSettings` appends `with ConfigStore:Path = <value>` to its plan and
  action line when the settings carry the key.
- Lines 527-579: `Resolve-ConfigStorePlan` (comment, then the function).
- Line 621: the `SqliteConfigBackup.psm1` import, moved up from below the elevation check.
- Line 641: `$configStorePlan = Resolve-ConfigStorePlan -PublishPath $PublishPath -SuppliedPath
  $ConfigStorePath -RegenerateAppSettings $RegenerateAppSettings.IsPresent`, right after the
  `-ConfigStorePath` validation block, before the banner.
- Line 651: the banner prints `$configStorePlan.Summary`.
- Line 808: the write passes `-ConfigStorePath $configStorePlan.Path`.
- Lines 897-901: the backup's plan line ends with the database it will snapshot; the action takes
  `$existingDb` and `$configuredDbPath` from the plan instead of re-reading the file.

Order in the installer after the fix: parameters and paths; the module import (621); the
`-ConfigStorePath` validation (628-636); the decision (641); the banner (651); the elevation and
`dotnet` checks; IIS reads and the identity decision; the first state change, the app pool
(734); ACLs; the certificate key; the `appsettings.json` write (802-808); seeds; the IIS web
application; the publish (853); the payload check; the backup (897); the pool stop (933); the
mirror (943); the pool start; staging removal; the manifest. Backup before mirror is unchanged.

Shape probe under Windows PowerShell 5.1.20348 after the fix, the same scratch probe as in
Evidence: `plan: Source=Existing Path=[...\shared\exchangeadmin.db]
DbPath=[...\shared\exchangeadmin.db]`, `after the write : ConfigStore:Path =
[...\shared\exchangeadmin.db]`, the top-level keys now include `ConfigStore`, one `.bak`. A
malformed `appsettings.json` under the same runtime, in the script's own shape (script-scope
`Stop`, the lifted function called at top level inside a `try`/`finally`): `ConvertFrom-Json`
raised `ArgumentException`, the `finally` ran, the next statement did not, exit 1. The message
quotes the bad JSON but not the file name; recorded rather than wrapped, as in cdt-8.

A `-PlanOnly` run of the fixed installer under 5.1 against temporary paths (existing key,
`-RegenerateAppSettings`, no `-ConfigStorePath`) printed `Config DB : <shared> (shared,
ConfigStore:Path kept from the existing appsettings.json)`, `PLAN Write appsettings.json at
<publish>\appsettings.json with ConfigStore:Path = <shared>` and `PLAN Back up the config database
before upgrading (via ... backup): <shared>`, and left the temporary tree as it found it.

## Guard proof

| Test | Pins |
| --- | --- |
| `keeps the existing key when -RegenerateAppSettings is passed without -ConfigStorePath` (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-11: ...`) | Lifts `Resolve-ConfigStorePlan`, `New-AppSettingsObject`, `Write-AppSettings`, `Invoke-PlanOrAction` and the output helpers from the installer by AST and runs them as the script does against a temporary publish folder whose `appsettings.json` names a key: plan `Existing`, `Path` and `DbPath` the existing key, `WritesAppSettings` true, the banner text; after the write `Get-ConfigStorePathSetting` reads the same key back, every other value regenerated, one `.bak` |
| `lets a supplied -ConfigStorePath win when the file is written` | Plan `Supplied`, `DbPath` the supplied database; the written file names it |
| `writes no key, and backs up the per-instance database, when there is none to keep and none supplied` | Plan `None`, `DbPath` `<publish>\config\exchangeadmin.db`, "this instance only"; no `ConfigStore` written |
| `on a fresh install writes the supplied key, or none` | No file: `WritesAppSettings` true; a supplied key is written |
| `leaves a kept file untouched, and its own key is the database the plan reports` | No `-RegenerateAppSettings`: plan `Existing`, `WritesAppSettings` false, the file byte-identical after `Write-AppSettings`; a supplied path that is not written does not move `DbPath` |
| `stops on an appsettings.json it cannot parse, before writing anything` | The resolver throws on malformed JSON; the file is unchanged |
| `decides it once, at script level, before anything changes, and nothing re-reads the key from disk (AST)` | Exactly one `$configStorePlan = Resolve-ConfigStorePlan -PublishPath $PublishPath -SuppliedPath $ConfigStorePath -RegenerateAppSettings $RegenerateAppSettings.IsPresent`, a top-level statement; after the module import and the script-level `-ConfigStorePath` validation; before every script-level state change (`Invoke-PlanOrAction`, `Set-DirectoryAcl`, `Write-AppSettings`, `Write-JsonFileIfMissing`, `Invoke-RobocopyChecked`, `Write-DeploymentManifest`, `Remove-Item`, `New-Item`, `Set-Content`, `Copy-Item`) and before the banner, whose line is pinned; one script-level `Write-AppSettings`, whose `New-AppSettingsObject` gets `-ConfigStorePath $configStorePlan.Path`; every `Get-ConfigStorePathSetting`/`Resolve-ConfigDbPath` call inside `Resolve-ConfigStorePlan`; the backup step sets `$existingDb = $configStorePlan.DbPath` and `$configuredDbPath = $configStorePlan.Path` (each assigned once in the script) and its `Backup-SqliteConfigDb` gets `-DbPath $existingDb` |
| `a -PlanOnly run prints the kept key and creates nothing (Windows PowerShell 5.1, the installer runtime)` | Runs the real installer under `powershell.exe` with `-PlanOnly -NonInteractive -RegenerateAppSettings` against temporary paths and an unused pool/application name: exit 0; the banner, write and backup plan lines name the kept key; "Plan only. No changes were made."; the temporary tree is unchanged (paths, sizes, timestamps) |
| `refuses when ConfigStore:Path names a database that is not there` (A36, one line changed) | `$configuredDbPath = $configStorePlan.Path` (was the re-read `Get-ConfigStorePathSetting`) |
| `with -ConfigStorePath: still ACLs config\, ...` (`DeployInvariants.Tests.ps1`, one line changed) | `-ConfigStorePath $configStorePlan.Path)` (was `$ConfigStorePath`) |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `E2D32F6436103C030147715A85C719C8`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed; the
restored file re-run green (114 passed). Post-comment md5 `DAD27ACBC5C01A60DA610FA1624692CC`
(114 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 carry-over dropped (the `elseif ($existingPath)` branch removed from the resolver) | `DBA0E0EC73E1A0B4A0EE2198D9412750` | `keeps the existing key ...` and `leaves a kept file untouched ...` ("Expected: 'Existing' But was: 'None'"); the `-PlanOnly` run (the banner line did not match) |
| M2 the call site passes `-ConfigStorePath $ConfigStorePath` again | `31853E5CF8190C95B0F9FC307ACFBDC9` | the AST test ("Expected: '$configStorePlan.Path' But was: '$ConfigStorePath'"); the `-PlanOnly` run (the write line did not match); `with -ConfigStorePath: ...` |
| M3 the decision moved after the `appsettings.json` write | `67AF9F952BAC3B61DF3DC1E51D4EAFE2` | the AST test ("Expected the actual value to be less than 29134, because it is decided before anything is changed or written, but got 33537"); the `-PlanOnly` run (the banner line did not match) |
| M4 the backup re-reads the file (`Resolve-ConfigDbPath` / `Get-ConfigStorePathSetting` restored in the action) | `D7C2B64AF26FD77C97B8588E9248C448` | the AST test ("Expected exactly 'Resolve-ConfigStorePlan', because line 876 would re-read ConfigStore:Path from a file this run may have rewritten, but got $null"); `refuses when ConfigStore:Path names a database that is not there` |
| M5 the writes rule broken, `$writesAppSettings = $true` | `6F837ADD11900870A8335CA0380181BF` | `leaves a kept file untouched ...` ("Expected $false, but got $true") |
| M6 pre-fix shape (`27f8167`) | `1C14FA9CF5FF27BCB402BC81B88C1D94` | all eight cdt-11 tests (their `BeforeAll`: "the installer defines no function Resolve-ConfigStorePlan"); `refuses when ConfigStore:Path names a database that is not there`; `with -ConfigStorePath: ...` |

Not covered by a test: the banner and plan lines in apply mode (the same strings; an apply run
needs elevation and IIS), and the wording of the help text.

## Correction: the `-PlanOnly` test was flaky (found while proving cdt-15; fixed in its own commit)

The `-PlanOnly` test's "creates nothing" assertion compared a snapshot of the temporary tree that
included every DIRECTORY's `LastWriteTimeUtc`. Run alone it failed 2 of 10 and 1 of 12 times: the
`shared` directory's write time read back one clock tick (15.6 ms) later after the plan than
before it, although the plan touched nothing there - the directory's time, as enumeration reports
it, lagged the file the test had just created in it. The table above, "the temporary tree is
unchanged (paths, sizes, timestamps)", described that flaky check.

The snapshot now records every path, and for files their length and SHA-256, sorted - no timestamps.
That still fails on anything a plan could do to the tree (create, delete or rewrite), and no longer
on the file system's own bookkeeping. Proof, in `tests/ps/InstallerConfigBackup.Tests.ps1`: the
test alone passed 25 of 25 runs after the change, and 15 of 15 again with the hash switched from
MD5 to SHA-256 (the analyzer flags MD5); and with the installer's `appsettings.json`
backup copy made to run in `-PlanOnly` (its PLAN line kept, so every other assertion of the test
still passes - installer md5 `C0CB19CC5BB12694D342607C5FFDD4D9`, restored to
`314823863614C2CCA08D7B173E2F2D21`), the test failed on the snapshot itself ("Expected strings to
be the same, because a plan creates and changes nothing ... \publish\appsettings.json.bak..."). Test
file md5 after the change `4233A067305DB19F507242B12385681D` (LF).
