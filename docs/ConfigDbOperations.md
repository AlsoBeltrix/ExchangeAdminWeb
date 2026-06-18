# Config Database — Operator Guide

Runtime configuration lives in a single SQLite database, `config/exchangeadmin.db`, in the
app's publish folder (one per environment — dev and prod each have their own). This replaced
the previous per-file JSON fragments under `config/` (SqliteConfigStore migration). This guide
is the operator's "open it and fix it" reference — the SQLite-world replacement for editing a
JSON file in Notepad.

> **Dependency:** these recipes need `sqlite3.exe` on PATH (`winget install SQLite.SQLite`),
> the same tool the deploy/promote scripts require.

> **Safety first:** the app keeps the DB open (WAL mode). For anything more than a read, **stop
> the app pool first** so you are the only writer, then start it again afterward:
> `Stop-WebAppPool -Name ExchangeAdminWeb` … `Start-WebAppPool -Name ExchangeAdminWeb`
> (use `ExchangeAdminWebDev` for dev). Reads are safe while running.

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

```sh
# Integrity check — should print "ok"
sqlite3 config\exchangeadmin.db "PRAGMA integrity_check;"

# Schema version
sqlite3 config\exchangeadmin.db "PRAGMA user_version;"

# List tables
sqlite3 config\exchangeadmin.db ".tables"

# Dump a store
sqlite3 -header -column config\exchangeadmin.db "SELECT * FROM module_enablement;"
sqlite3 -header -column config\exchangeadmin.db "SELECT policy_alias, group_value FROM section_access ORDER BY 1;"
```

## Repair / edit (STOP THE POOL FIRST)

```sh
# Example: re-enable a module that was switched off
sqlite3 config\exchangeadmin.db "UPDATE module_enablement SET enabled=1 WHERE module_id='MailboxPermissions';"

# Example: grant a group access to a section (authorization — double-check the alias)
sqlite3 config\exchangeadmin.db "INSERT OR IGNORE INTO section_access(policy_alias, group_value) VALUES ('MailboxPermissions','DOMAIN\\Exchange-Admins');"

# Example: set a scalar app setting
sqlite3 config\exchangeadmin.db "INSERT INTO app_setting(key,value) VALUES('extended_log_level','Information') ON CONFLICT(key) DO UPDATE SET value=excluded.value;"
```

After any edit, re-run `PRAGMA integrity_check;` and start the pool.

## Recover from corruption

1. **Restore the pre-deploy backup.** Every deploy/promote writes a verified online backup to
   the backup root (default `D:\backups\ExchangeAdminWeb\…`) as `exchangeadmin.<timestamp>.db`.
   With the pool stopped, copy it over `config\exchangeadmin.db` and delete any stale
   `config\exchangeadmin.db-wal` / `-shm`, then start the pool.
2. **Or salvage what's readable** with `.recover`:
   ```sh
   sqlite3 config\exchangeadmin.db ".recover" | sqlite3 config\exchangeadmin.recovered.db
   sqlite3 config\exchangeadmin.recovered.db "PRAGMA integrity_check;"
   # if ok, with the pool stopped, swap it in:
   #   move config\exchangeadmin.db aside; rename recovered -> exchangeadmin.db
   ```
3. **Last resort — rebuild from JSON.** The original migration archived the old JSON files as
   `config\*.imported-<timestamp>`. With the pool stopped, delete `exchangeadmin.db*`, rename
   the relevant `*.imported-*` files back to their original names, and restart — the app
   re-imports them on next start. (Authorization note: `sectionaccess.json` is the one that
   gates access; verify its groups before relying on the rebuild.)

## Fail-closed behavior to expect

If a config store cannot be read (a corrupt/locked DB, or an unparseable legacy file still on
disk from a half-finished migration), the security-sensitive modules **fail closed**:
section access denies, protected-principals checks block, the AD attribute allowlist reads as
"nothing allowed". Admin config pages show a "corrupt" banner and refuse to save over the bad
state. Fix the DB (above) rather than trying to force a save.
