# Config Database — Operator Guide

Runtime configuration lives in a single SQLite database. This guide is the operator's
"open it and fix it" reference — the SQLite-world replacement for editing a JSON file in
Notepad.

> **Dependency:** these recipes need `sqlite3.exe` on PATH (`winget install SQLite.SQLite`),
> the same tool the deploy/promote scripts require.

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
   does not exist, the app refuses to start and `deploy.ps1` fails before mirroring anything
   (`deploy.ps1:447`). **Deleting the database is not a recovery step.** See *Recover from
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

sqlite3 $db "PRAGMA integrity_check;"   # should print "ok"
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

After any edit, re-run `PRAGMA integrity_check;` and start the pools.

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

## Recover from corruption

**Do not delete the database.** With `ConfigStore:Path` set, the app will not start against a
missing file and never creates one — deleting it turns a corrupt instance into a dead one.

1. **Restore the pre-deploy backup.** Every deploy and promote writes a *verified* online
   backup (`VACUUM INTO` + `integrity_check`, `tools/SqliteConfigBackup.psm1`) to
   `<LogRoot>\ExchangeAdminWeb\ConfigBackups\config.<timestamp>.bak\exchangeadmin.<timestamp>.db`
   — `LogRoot` defaults to `E:\WWWOutput`. With **both pools stopped**, copy it over the file
   named by `ConfigStore:Path`, delete any stale `-wal` / `-shm` beside it, then start the
   pools.
2. **Or salvage what is readable** with `.recover`:
   ```powershell
   sqlite3 $db ".recover" | sqlite3 "$db.recovered"
   sqlite3 "$db.recovered" "PRAGMA integrity_check;"
   # if ok, with both pools stopped: move the bad file aside, rename the recovered one into
   # its place - keeping the SAME path, because appsettings names it.
   ```
3. **If there is no usable backup**, create a fresh shared database with
   `tools\Move-ConfigDbToShared.ps1` rather than by hand, and expect to reconfigure from
   scratch. **Authorization is the first thing to restore and verify** — an empty
   `section_access` with its presence marker denies everyone.

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
