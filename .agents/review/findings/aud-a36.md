# aud-a36: the installer upgraded an instance with no way back

**Severity**: HIGH. Not an outage on its own - it is the removal of the recovery option,
discovered at the moment it is needed. Schema migration steps are additive-only by invariant,
which lowers the odds without making a snapshot optional.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 36 of the 2026-10-16 audit, verified against the code.

`tools/Install-ExchangeAdminWeb.ps1` documents an **Update** path: build, stop the pool,
`robocopy /MIR` new binaries over the instance, start the pool. Those binaries run
`ConfigStoreMigrator` against the existing config database the moment the pool starts.

Nowhere in the script was that database copied, exported or snapshotted. `grep` for
`sqlite` across the file returned nothing but a comment saying it deliberately does not
speak SQLite:

```powershell
# This script stays environment-neutral and standalone; it does not speak SQLite or couple to
# any environment-specific deploy tooling.
```

That is the design position the gap came from, and it was defensible when the config store
was JSON fragments. It stopped being defensible when the store became a database an upgrade
migrates.

**`deploy.ps1` already does this correctly** - a verified online backup via
`tools/SqliteConfigBackup.psm1`, aborting the deploy if it fails. But the ADI deploy script
is not the supported way to upgrade a generic environment; this installer is, and this
installer had nothing.

## Predicted observable failure

An operator upgrades a generic installation. The new build's migrator runs a step that fails
partway, or succeeds and proves wrong. There is no pre-upgrade copy of
`exchangeadmin.db` anywhere - not in the publish folder, which is excluded from mirroring but
never copied either, and not in any backup root, because nothing wrote one. Module
enablement, section-access authorization, protected principals and the attribute allowlist
are all in that one file. Recovery means reconstructing authorization by hand.

## Approach

A `Back up the config database before upgrading` step, before the pool stop and well before
the mirror, using the same `SqliteConfigBackup` module `deploy.ps1` uses - `VACUUM INTO`
followed by `PRAGMA integrity_check`, so a backup that cannot be verified is not reported as
one.

Four decisions worth stating:

- **It resolves the database the way the app does**, through `Resolve-ConfigDbPath`, which
  honours `ConfigStore:Path`. A shared database lives outside the publish folder; backing up
  `<PublishPath>\config\exchangeadmin.db` unconditionally would snapshot the wrong file - or
  nothing - and report success either way.
- **A missing `sqlite3.exe` is fatal**, via `Assert-Sqlite3Available`. Warning and
  continuing would reproduce the finding with extra output. `-SkipConfigBackup` is the
  explicit opt-out for an operator who accepts having no way back.
- **A fresh install is checked first.** `Test-IsSqliteConfigDbPresent` runs before
  `Assert-Sqlite3Available`, so an environment with no database yet does not acquire a new
  hard dependency on sqlite3 - which matters for a script whose selling point is being
  installable anywhere.
- **It runs before the pool stops**, not after. The backup is an online one so it does not
  need a stopped pool, and going first means a failure aborts before anything has been
  touched.

The stale comment was narrowed rather than deleted: the environment-neutral and standalone
claim still holds - `SqliteConfigBackup.psm1` is a sibling that names no host or environment
- but the sentence saying the script does not speak SQLite is now false and is gone.

## Files changed

- `tools/Install-ExchangeAdminWeb.ps1` - the switch, the module import, the backup step, the
  corrected comment, the help text
- `tests/ps/InstallerConfigBackup.Tests.ps1`

No version bump; ops-script-only, same precedent as `aud-a34` and `aud-a35`.

## Guard proof

Source guards, and the test file says why in its header rather than implying more: the
backup is inline in a 790-line script that needs IIS, a build and a real database to run.
Extracting it into a function only so a test could call it would move the **ordering**
question - which is the actual finding - out of the place the ordering has to hold. The
backup mechanism itself is already covered behaviourally against a real sqlite3 and a real
database by `SqliteConfigBackup.Tests.ps1`, and is not re-tested here.

| Test | What it pins |
| --- | --- |
| `backs up before the new binaries are mirrored` | **the finding** - by source index, not by appearance |
| `backs up before the app pool is stopped, so a failure aborts before anything is touched` | the ordering choice |
| `treats a missing sqlite3 as fatal, not as a warning` | no silent downgrade to no-snapshot |
| `checks for an existing database before demanding sqlite3` | a fresh install gains no new dependency |
| `resolves the database the way the app does, honouring ConfigStore:Path` | the shared-database case |
| `makes skipping the snapshot an explicit switch` | the opt-out is opt-in |
| `supports -PlanOnly like every other step` | Invariant 4 |
| `has dropped the stale "does not speak SQLite" claim` | drift |
| `still states the standalone and environment-neutral position` | the narrowing did not delete the invariant |

| Mutation | What was changed | Result |
| --- | --- | --- |
| `no-backup` | the step's description renamed so nothing matches it | FAIL - 1 |
| `warn-not-fatal` | `Assert-Sqlite3Available` replaced with a warn-and-return | FAIL - 2 |
| `wrong-db-path` | `Resolve-ConfigDbPath` replaced with the hardcoded publish path | FAIL - 1 |

Each confirmed applied before its run. Source restored byte-identical (md5 verified).

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 188 passed / 0 failed; PSScriptAnalyzer 0
errors; the installer parses clean; `git diff --check` clean. No .NET run - no C# or Razor
changed.

## Known gaps

- **No upgrade was performed**, against a real instance or a real database. Every assertion
  here reads source; the behaviour is inferred from the module's own tested behaviour plus
  the ordering proved above.
- **The restore side is not automated.** The backup lands in `<LogRoot>\ConfigBackups` and a
  human has to find it and copy it back - the procedure is in `docs/ConfigDbOperations.md`,
  rewritten under `aud-a37`, but nothing tests that the two agree.
- **`<LogRoot>\ConfigBackups` is a new path this script invents.** `deploy.ps1` uses
  `<LogRoot>\ExchangeAdminWeb\ConfigBackups`. They are deliberately different because the
  generic installer does not assume an app-named subfolder, but an operator working on a
  host that has seen both tools will find backups in two places.
- **Retention is nobody's job.** Each upgrade writes another file and nothing prunes them.
- **The `-SkipConfigBackup` path is warned about but not audited** - there is no durable
  record that an upgrade ran without a snapshot.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`). The two backup roots named under
*Known gaps* are the thing most likely to be judged wrong by a second reader.
