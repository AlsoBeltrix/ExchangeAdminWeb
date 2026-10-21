# cdt-21: a kept ConfigStore:Path naming a missing database is caught only inside the backup step

**Severity**: LOW. When the instance's existing `appsettings.json` names a `ConfigStore:Path` whose
file does not exist, the installer notices only inside the pre-upgrade backup's action: after it has
reconfigured the app pool, granted ACLs, rewritten `appsettings.json` under `-RegenerateAppSettings`,
seeded config fragments, reconfigured the IIS application and published. With `-SkipConfigBackup`
it does not notice at all: it mirrors the new build and reports "Install/update complete." over an
instance the app will refuse to start (it opens a configured path without create). A plan notices
in neither case: `-PlanOnly` completes and even plans a backup of the missing file. The instance was
already unable to start before the run, so nothing is lost - but the run changes the host and claims
success, which is the outcome the A36 review added the refusal to prevent. A supplied
`-ConfigStorePath` is checked early; only a kept one is not. Found while fixing cdt-11.
**Status**: Fixed on branch; awaiting merge
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `<pending>`
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `cbf52a8`. A supplied path is refused before anything:

```
699:     if (-not (Test-Path -LiteralPath $ConfigStorePath -PathType Leaf)) {
700:         Write-Fail "ConfigStorePath names $ConfigStorePath but no database exists there. ...
```

A kept one (`Resolve-ConfigStorePlan`, line 708, Source `Existing`) is checked only here, inside the
backup's `Invoke-PlanOrAction` action, which a plan never runs and `-SkipConfigBackup` skips:

```
971: if ($SkipConfigBackup) {
...
998:         if ($configuredDbPath) {
999:             Write-Fail "ConfigStore:Path names '$existingDb' but no database exists there. ...
```

Everything from the pool step (820) to the publish runs before it.

**Mechanism: no error at all** before the backup step - reproduced with two real `-PlanOnly` runs of
the installer under Windows PowerShell 5.1.20348 against a temporary instance whose
`appsettings.json` names a database that does not exist:

```
=== plan, existing key names a missing database  : exit=0
    Config DB   : <root>\shared\exchangeadmin.db (shared, ConfigStore:Path kept from the existing appsettings.json)
  PLAN Back up the config database before upgrading (via ... backup): <root>\shared\exchangeadmin.db
    !  Plan only. No changes were made.
=== plan, existing key names a missing database -SkipConfigBackup : exit=0
    Config DB   : <root>\shared\exchangeadmin.db (shared, ConfigStore:Path kept from the existing appsettings.json)
  PLAN SKIP the config database backup entirely (-SkipConfigBackup was passed). ...
    !  Plan only. No changes were made.
```

## Plan (approved by the owner's EVERYTHING go)

1. A function, `Assert-ConfigStorePlanDatabase -Plan -AppSettingsPath`: when the plan names a key
   (`Path` set) and that file does not exist, `Write-Fail` naming where the key came from (the kept
   `appsettings.json`, or `-ConfigStorePath`), the missing path, that the app never creates a
   configured database, and the remedies (fix the key, restore the file, the cutover script, or
   re-run with `-RegenerateAppSettings -ConfigStorePath` naming a database that exists). No key: no
   check - the per-instance default is created on first start.
2. Called once, at script level, directly after the decision (cdt-11) - before the banner and every
   change, in plan and apply mode alike, and whether or not `-SkipConfigBackup` is passed.
3. The backup step's own refusal stays, as defence against the file disappearing between the two.

Assumption, recorded: a plan refuses too, as the supplied-path check already does: a dry run that
completes over an instance that cannot start is the defect.

Constraints: as cdt-11. Files: `tools/Install-ExchangeAdminWeb.ps1`,
`tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

Guards: a behavioural test of the lifted function (missing file refused, naming it and the source;
existing file and no key pass); an AST test that the call is the statement right after the decision,
before the banner and the first change, and outside the `-SkipConfigBackup` branch; and a real
`-PlanOnly` run under Windows PowerShell 5.1 with `-SkipConfigBackup` against an instance whose key
names a missing file, which must now fail with the message and create nothing. Mutations: the call
removed; the check made to always pass; the call moved into the backup branch; the `cbf52a8` shape.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix: `Assert-ConfigStorePlanDatabase`
at lines 596-611 (comment, then the function), and its call at 725-726, the statement right after
the decision (724), before the banner. The backup step's refusal (now about line 1020) is unchanged.

The same two `-PlanOnly` runs as in Evidence now stop before the banner: "ConfigStore:Path from the
existing <root>\publish\appsettings.json names '<root>\shared\exchangeadmin.db' but no database
exists there. The app opens a configured database without creating it, so this instance cannot
start on it. ... Nothing was changed." - exit 1, with and without `-SkipConfigBackup`.

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-21: ...`) | Pins |
| --- | --- |
| `refuses a key naming a missing file, says where it came from, and passes an existing file or no key` | The lifted function throws for a missing file - "from the existing <appsettings>", "names '<path>' but no database exists there", "Nothing was changed" - and "from -ConfigStorePath" for a supplied one; an existing file and a plan with no key pass |
| `is checked right after the decision, before the banner and every change, whatever -SkipConfigBackup says (AST)` | The statement right after `$configStorePlan = Resolve-ConfigStorePlan ...` is exactly `Assert-ConfigStorePlanDatabase -Plan $configStorePlan -AppSettingsPath $configPath`, the only call; both before the banner and the first `Invoke-PlanOrAction`/`Set-DirectoryAcl` |
| `a -PlanOnly run with -SkipConfigBackup now refuses an instance whose key names a missing file, and creates nothing (Windows PowerShell 5.1)` | The real installer under `powershell.exe`, against such an instance: non-zero exit, the refusal in the output (compared with whitespace removed - 5.1 wraps errors at the console width), no "Plan only." line, the temporary tree unchanged (paths and SHA-256) |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `28C58FDD98182DD366B49CF41B153871`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed; the
restored file re-run green (137 passed). Post-comment md5 `8E5100993F84D16D2ECF4AE9A0E8136F` (137
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| K1 the call removed | `D46EA854C31F9ECE05D985603C09B414` | the AST test (the next statement is not the call); the `-PlanOnly` run ("Expected 0 to be different from the actual value, because a plan must not complete over an instance that cannot start") |
| K2 the check made to always pass (`if ($true) { return }`) | `4B2D703E0FE21BE0EE38B859967CE712` | the behavioural test ("Expected a value, because the app will not start on a configured database that is not there"); the `-PlanOnly` run |
| K3 the call moved into the backup branch (`else` of `if ($SkipConfigBackup)`) | `D68DAAFED54FD9A3CDA15A06FEC806BF` | the AST test; the `-PlanOnly` run (with `-SkipConfigBackup` it never ran) |
| K4 pre-fix shape (`cbf52a8`) | `2D88E1C3B2D35E6616BFB3EDB2234911` | all three (their `BeforeAll`: no `Assert-ConfigStorePlanDatabase`) |

A first K3 was rebuilt: its multi-line anchor did not survive the edit file's round trip and the
edit script refused it (matched 0 times) - the guard against a vacuous mutation doing its job.
