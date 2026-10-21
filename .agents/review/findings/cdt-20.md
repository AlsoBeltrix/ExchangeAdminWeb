# cdt-20: the installer silently ignores -ConfigStorePath on an instance whose appsettings.json it keeps

**Severity**: MEDIUM. `tools/Install-ExchangeAdminWeb.ps1` writes `-ConfigStorePath` into
`appsettings.json` only when it generates that file (a fresh install, or `-RegenerateAppSettings`).
Run against an existing instance without `-RegenerateAppSettings`, the file is kept as it is, so a
supplied `-ConfigStorePath` never takes effect - yet the run validates it, grants the pool Modify on
its directory and completes. An operator who passed the shared database's path to move an
instance onto it is told "Install/update complete." while the instance keeps its own database: the
two instances do not share settings or authorization, and a change saved on one (a section-access
grant removed, a protected principal added) silently does not apply to the other. Before cdt-11
the banner even claimed the shared database; since cdt-11 it states the truth, but nothing stops
or warns. Found while fixing cdt-11 (recorded there as a separate defect).
**Status**: Fixed on branch; awaiting merge
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `<pending>`
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `6251571`. The writer keeps an existing file unless told
otherwise:

```
288:     if ((Test-Path -LiteralPath $Path -PathType Leaf) -and -not $RegenerateAppSettings) {
289:         Write-Ok "appsettings.json exists - preserving"
290:         return
```

and the decision made before it (cdt-11) uses a supplied path only when the file is written:

```
551:     $writesAppSettings = (-not $appSettingsExists) -or $RegenerateAppSettings
...
555:     if ($writesAppSettings -and -not [string]::IsNullOrWhiteSpace($SuppliedPath)) {
```

The supplied value is still validated and its directory still taken for the shared-directory ACL
(the script-level `-ConfigStorePath` block, `$sharedConfigDir = Split-Path -Parent $ConfigStorePath`,
and `if ($sharedConfigDir) { Set-DirectoryAcl -Path $sharedConfigDir ... }`). Nothing compares the
supplied value with what the kept file names, or says it was not applied.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348 (scratch probe: the
installer's script-level `-ConfigStorePath` block, `Resolve-ConfigStorePlan`, `New-AppSettingsObject`
and `Write-AppSettings`, lifted by the 5.1 parser and run as the script runs them, against an
instance whose `appsettings.json` has no `ConfigStore` key, with `-ConfigStorePath` naming an
existing database and no `-RegenerateAppSettings`):

```
banner: Config DB   : <root>\publish\config\exchangeadmin.db (this instance only)
shared-directory ACL would be granted on: <root>\shared
the file after the run: ConfigStore:Path = []  -> the app keeps its own config\exchangeadmin.db
```

The same holds when the kept file names a DIFFERENT shared database: the kept key stands and the
supplied one is dropped.

## Plan (approved by the owner's EVERYTHING go)

1. In `Resolve-ConfigStorePlan`: when the file is kept (it exists and `-RegenerateAppSettings` was not
   passed) and a supplied path is not the key the file already names, refuse with `Write-Fail`,
   before the banner and before anything changes: the supplied path would not take effect; the
   message names it and what the kept file names (or that it names none), and says to re-run with
   `-RegenerateAppSettings` to write it (which regenerates the other values too), or without
   `-ConfigStorePath` to keep the instance as it is, and that moving an existing instance onto a
   shared database is `tools\Move-ConfigDbToShared.ps1`'s job.
2. A supplied path that IS what the kept file names is accepted: re-stating the key on an update is
   harmless and is what an operator following the help does. Compared after normalising both
   (absolute, case-insensitive, by equality) - the supplied value is already absolute, and the kept
   one is validated as `X:\...` by the shared reader (cdt-17).
3. Nothing else changes: a written file still takes the supplied path (cdt-11).

Assumption, recorded: refusing rather than warning-and-continuing, because the parameter is an
explicit instruction the run would not carry out, and the consequence (two instances that are
believed to share a configuration and do not) is invisible afterwards.

Constraints: as cdt-11 (standalone, plan mode, 5.1, ASCII, no version bump).

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1` (the new
cases, and the second half of a cdt-11 test that pinned the old "kept key wins silently" result),
this record.

Guards: behavioural cases on the lifted resolver - a kept file with no key and a supplied path
refuses; a kept file naming a different key refuses, naming both; a kept file naming the same key,
spelled with a different case, is accepted; a written file still takes the supplied path.
Mutations: the refusal removed; the comparison made case-sensitive; the refusal applied to a
written file too; the `6251571` shape.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix, inside `Resolve-ConfigStorePlan`:
lines 553-565, a comment and the refusal, between the writes rule and the choice of path; the
function's header comment (536-538) names it. The refusal runs where the decision already runs -
script level, before the banner and every change - so `-PlanOnly` refuses the same way.

The same 5.1 probe as in Evidence now stops at the decision: "-ConfigStorePath <root>\shared\
exchangeadmin.db was given, but this run keeps the existing appsettings.json in <root>\publish,
which names no ConfigStore:Path, so the supplied path would not take effect. Re-run with
-RegenerateAppSettings to write it ... Nothing was changed."

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-11: ...`) | Pins |
| --- | --- |
| Context `cdt-20: ...`, `refuses it when the kept file names no key` | The lifted resolver throws for a kept key-less file and a supplied path, naming the path, "names no ConfigStore:Path", "would not take effect", `-RegenerateAppSettings`, `Move-ConfigDbToShared.ps1` and "Nothing was changed" |
| `refuses it when the kept file names a different key, naming both` | ... naming the supplied path and "names ConfigStore:Path = <kept key>" |
| `accepts the key the kept file already names, however it is cased` | The kept key, supplied upper-cased, gives plan `Existing` with the kept key |
| `leaves a kept file untouched, and its own key is the database the plan reports` (cdt-11, second half changed) | A supplied path a kept file would not carry now throws "would not take effect" (was: the kept key silently won) |
| `lets a supplied -ConfigStorePath win when the file is written` and `on a fresh install writes the supplied key, or none` (cdt-11, unchanged) | A written file still takes the supplied path |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `FEA4A1EA1A719823A81EA1DC762385C8`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed; the
restored file re-run green (134 passed). Post-comment md5 `2D88E1C3B2D35E6616BFB3EDB2234911` (134
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| C1 the refusal removed | `3C3E870B92FEA765EF15310EFD4C8D2B` | both refusal cases ("Expected a value, because an instruction the run would not carry out must not pass silently"); the cdt-11 kept-file test ("Expected an exception with message like '*would not take effect*'") |
| C2 the comparison made case-sensitive (`Ordinal`) | `B5AA32E90D17601BE890C49EC5ED7AE4` | `accepts the key the kept file already names, however it is cased` (refused "-ConfigStorePath D:\SHARED\CONFIG\EXCHANGEADMIN.DB was given ...") |
| C3 the refusal applied to a written file too (`-not $writesAppSettings -and` dropped) | `8E244812DD40297B0211824A99C56DDF` | the cdt-11 `lets a supplied -ConfigStorePath win when the file is written` and `on a fresh install writes the supplied key, or none` (both refused) |
| C4 pre-fix shape (`6251571`) | `314823863614C2CCA08D7B173E2F2D21` | as C1 |
