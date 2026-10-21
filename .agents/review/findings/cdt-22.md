# cdt-22: the installer grants the shared database's folder only when -ConfigStorePath is re-stated

**Severity**: MEDIUM. The installer grants the app pool identity Modify on the shared config
database's directory (SQLite writes `-wal`/`-shm` beside the file) only when `-ConfigStorePath` is
passed on this run. An update of a shared-database instance usually does not pass it - since cdt-11
the key is kept from `appsettings.json` - so when that update also changes the pool identity
(`-ServiceAccount` naming a new account, or `-ResetAppPoolIdentity`), the new identity is granted
the publish folder, `logs\` and `config\` but not the directory of the database the app opens. The
app then cannot open or write its configured database, and the run has ended "Install/update
complete.". Found while fixing cdt-11.
**Status**: Fixed on branch; awaiting merge
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `<pending>`
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `cd5ad65`. The directory is taken from the parameter only:

```
711: $sharedConfigDir = $null
712: if (-not [string]::IsNullOrWhiteSpace($ConfigStorePath)) {
...
718:     $sharedConfigDir = Split-Path -Parent $ConfigStorePath
719: }
...
870: if ($sharedConfigDir) {
...
874:     Set-DirectoryAcl -Path $sharedConfigDir -Identity $appPoolIdentity -Rights "(OI)(CI)M"
```

while the database the instance opens is `$configStorePlan.Path` - the supplied path when written,
otherwise the kept key (cdt-11). `tools/AppPoolControl.psm1:177`, the `-ResetAppPoolIdentity`
warning, already lists "shared config database" among the access that "will stop working until it
is re-granted"; the installer knows the path and re-grants the other folders, but not this one.

**Mechanism: no error at all**, reproduced with two real `-PlanOnly` runs of the installer under
Windows PowerShell 5.1.20348 against a temporary instance whose `appsettings.json` names an
existing shared database, with `-ServiceAccount CONTOSO\svc-new`:

```
=== kept shared key, new -ServiceAccount, without -ConfigStorePath : exit=0
    Config DB   : <root>\shared\exchangeadmin.db (shared, ConfigStore:Path kept from the existing appsettings.json)
  PLAN Grant CONTOSO\svc-new (OI)(CI)M on <root>\publish
  PLAN Grant CONTOSO\svc-new (OI)(CI)M on <root>\logs
  PLAN Grant CONTOSO\svc-new (OI)(CI)M on <root>\publish\config
  PLAN Grant CONTOSO\svc-new (OI)(CI)M on <root>\publish\logs
=== kept shared key, new -ServiceAccount, with -ConfigStorePath re-stated : exit=0
  ... the same four, and
  PLAN Grant CONTOSO\svc-new (OI)(CI)M on <root>\shared
```

The same database, the same new identity; whether it can open its database depends on an
optional parameter being repeated.

## Plan (approved by the owner's EVERYTHING go)

1. `$sharedConfigDir` is derived from the decision, not the parameter: after the decision and the
   database check (cdt-21), `Split-Path -Parent $configStorePlan.Path` when the plan names a key.
   The supplied-path block keeps its validation and no longer sets it.
2. Nothing else changes: the grant (`if ($sharedConfigDir) { Set-DirectoryAcl ... }`), its position
   after the instance's own `config\` grant, and its `(OI)(CI)M` rights.

The directory exists whenever the plan names a key: a supplied path is refused if missing, a kept
one since cdt-21, and a supplied path a kept file would not carry since cdt-20 - so the grant
never creates a directory for a database that is not there.

Assumption, recorded: re-granting an identity that already has the right is harmless (`icacls
/grant` is idempotent), so the grant runs on every run that has a shared database, not only when
the identity changes.

Constraints: as cdt-11. Files: `tools/Install-ExchangeAdminWeb.ps1`,
`tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

Guards: a real `-PlanOnly` run under Windows PowerShell 5.1 against an instance with a kept shared
key, without `-ConfigStorePath`, that must plan the grant on the shared directory; and an AST test
that `$sharedConfigDir` is assigned only from `$configStorePlan.Path`, after the decision and the
database check, and that the supplied-path block no longer assigns it. Mutations: the
parameter-derived assignment back; the plan-derived one made to apply only to a supplied path; the
`cd5ad65` shape.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix: the supplied-path block
(711-717) validates and no longer sets `$sharedConfigDir`; lines 725-731, right after the database
check (724): a comment, `$sharedConfigDir = $null`, and `if ($configStorePlan.Path) {
$sharedConfigDir = Split-Path -Parent $configStorePlan.Path }`. The grant (875-879) is unchanged.

The same plan run as in Evidence, without `-ConfigStorePath`, now plans "PLAN Grant
CONTOSO\svc-new (OI)(CI)M on <root>\shared" after the four other grants.

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-22: ...`) | Pins |
| --- | --- |
| `takes the folder from the decision, after it, and nowhere else (AST)` | The two statements after `Assert-ConfigStorePlanDatabase ...` are `$sharedConfigDir = $null` and `if ($configStorePlan.Path) { $sharedConfigDir = Split-Path -Parent $configStorePlan.Path }`; those are the only two assignments to `$sharedConfigDir` in the script |
| `a -PlanOnly run that keeps a shared key and changes the identity grants the new identity the shared folder (Windows PowerShell 5.1)` | The real installer under `powershell.exe`, an instance whose `appsettings.json` names an existing shared database, `-ServiceAccount CONTOSO\svc-new`, no `-ConfigStorePath`: exit 0 and "PLAN Grant CONTOSO\svc-new (OI)(CI)M on <shared folder>" in the output |
| `with -ConfigStorePath: still ACLs config\, ACLs the shared directory too, ...` (`DeployInvariants.Tests.ps1`, unchanged) | The grant itself and its order after the instance's own `config\` grant |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `1FCBA149C5155208A8323E519F4835F5`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed; the
restored file re-run green (139 passed). Post-comment md5 `624AA01641C4FC783DC9652CF904FA6F` (139
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| A1 derived from the parameter again (`if ($ConfigStorePath) { ... Split-Path -Parent $ConfigStorePath }`) | `8C22E2363474817EC01D0A756D4CAB4F` | both cdt-22 tests (AST: "Expected: '$configStorePlan.Path' But was: '$ConfigStorePath'"; the plan run: no grant on the shared folder) |
| A2 the plan-derived assignment only for a supplied path (`if ($configStorePlan.Source -eq 'Supplied')`) | `049E43DD6C1A8EF1EBF95BF74F4A7CC5` | both (AST: the condition text; the plan run: no grant) |
| A3 pre-fix shape (`cd5ad65`) | `8E5100993F84D16D2ECF4AE9A0E8136F` | both (AST: the statement after the check is the banner; the plan run: no grant) |
