# Config Database — Operator Guide

Runtime configuration lives in a single SQLite database. This guide is the operator's
"open it and fix it" reference — the SQLite-world replacement for editing a JSON file in
Notepad.

> **Tools, two kinds.** The backup, integrity-check and "create an empty database" recipes use
> the app's own `db` command-line mode, run through the .NET host in Program Files - the same
> thing the deploy/promote/installer scripts use (`docs/ConfigDbToolInApp-Plan.md`). Nothing
> extra to install:
>
> ```powershell
> $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
> $app    = "<PublishPath>\ExchangeAdminWeb.dll"   # either instance's publish folder, app 2.34.0+
> & $dotnet $app db                                # prints the verbs; exit 0 ok, 1 usage, 2 failure
> ```
>
> The ad-hoc SQL recipes and `.recover` need the real `sqlite3` shell; the app deliberately has
> no arbitrary-SQL verb. If you need it: install it machine-wide under Program Files as an
> administrator (never per-user, never through a per-user package manager - `winget` on a
> server installs into your own profile, where any process running as you can replace the
> binary), and never run it from an elevated script. `.recover` has no in-app equivalent.

## Where the database is — read this before anything else

**The database is SHARED between dev and prod on this server** (decision 2026-09-04,
`docs/SharedConfigDb-Plan.md`). Each instance's `appsettings.json` names it:

| `ConfigStore:Path` in that instance's `appsettings.json` | Database used |
|---|---|
| Set (the normal arrangement) | That path — **one file, both instances** |
| Absent | That instance's own `<PublishPath>\config\exchangeadmin.db` |

Find it before you touch anything:

```powershell
# What this instance is actually configured to open
(Get-Content D:\inetpub\ExchangeAdminWeb\appsettings.json | ConvertFrom-Json).ConfigStore.Path
```

Three consequences, all of which have bitten:

1. **A setting saved on dev is live on prod immediately.** There is no promotion step for
   configuration and promotion never copies the database.
2. **The file lives outside both publish folders**, so a deploy never touches it — `config\`
   is excluded from robocopy mirroring.
3. **The app never creates a configured database.** If `ConfigStore:Path` names a file that
   does not exist, the app refuses to start and `deploy.ps1` fails before mirroring anything,
   with "ConfigStore:Path in ... names '...' but no database exists there" (the upgrade and
   the `-Force` path alike). **Deleting the database is not a recovery step.** See *Recover from
   corruption*.

### Databases that are NOT this one

`config\exchangeadmin-jobs.db` (bulk jobs) and `config\exchangeadmin-usage.db` (anonymous
usage telemetry) are **per-instance runtime files**. They are deliberately neither backed up
nor promoted. Their absence from a backup is correct — do not "fix" it.

> **Safety first:** the app keeps the DB open (WAL mode). For anything more than a read,
> **stop every pool that opens this file** so you are the only writer — with a shared
> database that means **both** instances, not just the one you are fixing:
> ```powershell
> Stop-WebAppPool -Name ExchangeAdminWeb; Stop-WebAppPool -Name ExchangeAdminWebDev
> # ... edit ...
> Start-WebAppPool -Name ExchangeAdminWeb; Start-WebAppPool -Name ExchangeAdminWebDev
> ```
> Stopping only one pool leaves the other writing underneath you. Reads are safe while
> running.

## Where things are

| Store | Table(s) |
|---|---|
| Module enablement | `module_enablement` |
| Per-module config | `module_config` (+ `module_config_present`) |
| Section access (authorization) | `section_access` (+ `section_access_present`) |
| Module admins | `module_admins` |
| Protected principals | `protected_principal` (+ `protected_principal_present`) |
| AD attribute allowlist / legend | `editable_attribute`, `attribute_legend` (+ presence markers) |
| Scalar settings (e.g. extended log level) | `app_setting` |
| Schema version / change token | `schema_meta` (`PRAGMA user_version` = schema version) |

The `*_present` marker tables record that a store was configured even when it is empty (so an
intentionally-empty config is distinct from "never configured"). Leave them alone unless you
are deliberately resetting a store.

## Inspect (safe while running)

Set `$db` to the path you found above and reuse it.

```powershell
$db = "<the ConfigStore:Path value>"

& $dotnet $app db check $db             # app tool: prints "ok", exit 0; exit 2 on any failure

# A verified online backup (VACUUM INTO + integrity_check, safe while the app runs).
# Refuses an existing destination.
& $dotnet $app db backup --source $db --dest "D:\backups\exchangeadmin.$(Get-Date -Format yyyyMMdd-HHmmss).db"

# Everything below needs the sqlite3 shell (see the note at the top).
sqlite3 $db "PRAGMA user_version;"      # schema version
sqlite3 $db ".tables"

sqlite3 -header -column $db "SELECT * FROM module_enablement;"
sqlite3 -header -column $db "SELECT policy_alias, group_display_name, group_value FROM section_access ORDER BY 1;"
```

## Repair / edit (STOP BOTH POOLS FIRST)

```powershell
# Example: re-enable a module that was switched off
sqlite3 $db "UPDATE module_enablement SET enabled=1 WHERE module_id='MailboxPermissions';"

# Example: set a scalar app setting
sqlite3 $db "INSERT INTO app_setting(key,value) VALUES('extended_log_level','Information') ON CONFLICT(key) DO UPDATE SET value=excluded.value;"
```

After any edit, re-run `& $dotnet $app db check $db` and start the pools.

### Authorization rows are SIDs, not names

`section_access` has **three** columns — `policy_alias`, `group_value`, `group_display_name`
— and since the 2026-08-03 SID migration (`docs/SectionAccessSidStorage-Plan.md`)
`group_value` holds a **SID**. `group_display_name` is only what the admin page shows.

A row inserted with a `DOMAIN\Group` string in `group_value` is the retired form. Prefer the
admin page, which resolves the SID for you. If you must do it by hand, resolve the SID first
and write both columns:

```powershell
$sid = (New-Object System.Security.Principal.NTAccount("DOMAIN","Exchange-Admins")).
        Translate([System.Security.Principal.SecurityIdentifier]).Value

sqlite3 $db @"
INSERT OR IGNORE INTO section_access(policy_alias, group_value, group_display_name)
  VALUES ('MailboxPermissions','$sid','DOMAIN\Exchange-Admins');
INSERT OR IGNORE INTO section_access_present(marker) VALUES (1);
"@
```

**The presence marker matters.** Section access is only read from the DB when the marker
exists. Without it, inserted rows are ignored and the app falls back / fails closed — so set
it too (harmless if already present).

> This is the authorization store. A mistake here locks people out of every module, or lets
> the wrong people in. Double-check the alias and the resolved SID, and prefer the admin page.

## Converting protected principals and module-admin grants to identifiers

Once, by hand, before deploying the first build with `docs/ProtectedPrincipalIdentifiers-Plan.md` section K
(decision 2026-11-03). That build never resolves or matches a stored name: a protected user, group or OU stored by
name only refuses every protection check that finds no other match, naming it ("not converted - run db
convert-identifiers"), and a module-admin grant stored by name grants nothing. The deploy pipeline does not run,
check or know about this step.

- `db convert-identifiers <db> [--plan]` looks up every protected user, group and OU row that has no identifier
  companion and every module-admin grant that is not a SID, once each, **as you** - no credential, no Delinea - in the
  forest this host belongs to (the ActiveDirectory PowerShell module must be installed, as the app already needs). It
  adds a `user_sid` / `group_sid` / `ou_guid` row beside each protected-principal row (the name row stays, for the older
  build sharing the database) and replaces each name grant with its group's SID. **All or nothing:** if any entry
  matches no object or several, or a lookup fails, it writes nothing, exits 2, and names every such entry with its
  reason. `--plan` prints what a real run would write and never opens the database for writing. Exit 0 prints each
  conversion (`kind: legacy value -> identifier`). A second run finds nothing to do and says so.
- `db identifier-status <db>` is read-only: one line per protected-principal row and module-admin grant -
  `converted`, `identifier` (an identifier row with no name row), `legacy` (a name row with no identifier),
  `grant-sid`, `grant-name` - and exit 0 only when nothing is `legacy` or `grant-name`.

On exit 0 the report is on stdout; on exit 2 it is on stderr, ending in an `error:` line.

Run from an **elevated** shell on the deploy host, with `$app` the `ExchangeAdminWeb.dll` of the build about to be
deployed (its publish output, not the running instance's folder), `$dotnet` as at the top, and `$db` the
`ConfigStore:Path` value above. The live database is touched only after a copy converts cleanly:

```powershell
$copy = "D:\backups\exchangeadmin.identifiers-test.$(Get-Date -Format yyyyMMdd-HHmmss).db"

# 1. Test against a copy.
& $dotnet $app db backup --source $db --dest $copy
& $dotnet $app db convert-identifiers $copy --plan
& $dotnet $app db convert-identifiers $copy
& $dotnet $app db identifier-status $copy        # must exit 0

# Any entry named in step 1: fix it in the OLD build's Admin Settings or Module Configuration (remove it, or pick it
# again from the search), then repeat step 1 with a new copy until it converts cleanly.

# 2. The same three against the live database (safe while the app runs; the write is one transaction under the lock).
& $dotnet $app db convert-identifiers $db --plan
& $dotnet $app db convert-identifiers $db
& $dotnet $app db identifier-status $db          # must exit 0

# 3. Then the dev deploy (tools/deploy-pipeline.ps1 -Dev).
```

**The one constraint:** between the live conversion and the prod deploy, do **not** save Admin Settings' Protected
principals pane on prod's older build. Its save rewrites the whole table and drops every identifier row, which makes
every protection check on the new build refuse until the conversion is run again. If it happens, run step 2 again;
`db identifier-status` shows it (`legacy` lines).

## Recover from corruption

**Do not delete the database.** With `ConfigStore:Path` set, the app will not start against a
missing file and never creates one — deleting it turns a corrupt instance into a dead one.

1. **Restore the most recent verified backup.** Both routines write a *verified* online
   backup (`VACUUM INTO` + `integrity_check`, `tools/SqliteConfigBackup.psm1`), but **to two
   different places** — check both and take the newer:

   | Written by | Location |
   |---|---|
   | `deploy.ps1` (a `-Dev` or `-Prod` deploy) | `<LogRoot>\ExchangeAdminWeb\ConfigBackups\config.<timestamp>.bak\exchangeadmin.<timestamp>.db`, `LogRoot` defaulting to `E:\WWWOutput` |
   | `promote-dev-to-prod.ps1` (a promotion) | `<BackupRoot>\cutover.<timestamp>\…`, `BackupRoot` defaulting to `D:\backups\ExchangeAdminWeb` |
   | `Install-ExchangeAdminWeb.ps1` (a generic upgrade) | `<LogRoot>\ConfigBackups\exchangeadmin.<timestamp>.db` |

   With **both pools stopped**, copy the chosen file over the path named by
   `ConfigStore:Path`, delete any stale `-wal` / `-shm` beside it, then start the pools.

2. **Or salvage what is readable** with `.recover`:
   ```powershell
   sqlite3 $db ".recover" | sqlite3 "$db.recovered"   # needs the sqlite3 shell
   & $dotnet $app db check "$db.recovered"
   ```
   If that prints `ok`, then with **both pools stopped**:
   ```powershell
   Move-Item  $db "$db.corrupt"
   # The sidecars belong to the CORRUPT database. Leaving them beside the recovered file
   # lets SQLite replay a WAL that does not match it.
   Remove-Item "$db-wal", "$db-shm" -ErrorAction SilentlyContinue
   Move-Item "$db.recovered" $db
   ```
   Keep the SAME path - `appsettings.json` names it.

3. **If there is no usable backup**, create an EMPTY database at the configured path and let
   the app build the schema on next start:
   ```powershell
   & $dotnet $app db init $db   # creates an empty, integrity-checked database; refuses an existing file
   ```
   The app refuses to start against a *missing* configured database but migrates an empty
   one (`ConfigStoreMigrator` issues `CREATE TABLE IF NOT EXISTS`), so an empty file is a
   valid starting point and a missing one is not.

   > **Do NOT use `tools\Move-ConfigDbToShared.ps1` for this.** That script is a one-time
   > cutover from two per-instance databases to one shared file. On a server that has
   > already been cut over it refuses by design - its preconditions reject a `SharedDbPath`
   > that already exists and expect per-instance databases to copy from. A previous version
   > of this guide recommended it here; the instruction could not succeed.

   **Authorization is the first thing to restore and verify** — an empty `section_access`
   with its presence marker denies everyone.

> The pre-2026 "rebuild from the archived JSON" route is **retired**. The legacy
> `config\*.imported-<timestamp>` archives may still exist on disk, but the app no longer
> re-imports the section-access store from them, and the group values they contain are
> pre-SID names. Treat them as historical evidence of what the config used to say, not as a
> restore path.

## Fail-closed behavior to expect

If a config store cannot be read (a corrupt/locked DB, or an unparseable legacy file still on
disk from a half-finished migration), the security-sensitive modules **fail closed**:
section access denies, protected-principals checks block, the AD attribute allowlist reads as
"nothing allowed". Admin config pages show a "corrupt" banner and refuse to save over the bad
state. Fix the DB (above) rather than trying to force a save.
