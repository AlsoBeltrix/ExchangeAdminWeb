# cdt-13: the upgrade's drift check still inventories the jobs and usage databases

**Severity**: LOW. The upgrade path's post-deploy drift check compares a size/mtime inventory of
`config\` taken before the pool stops with one taken after it restarts, and warns on every
difference. It leaves out the config database triplet because a live WAL database changes across
a pool restart by design - but not the jobs and usage databases (`exchangeadmin-jobs.db`,
`exchangeadmin-usage.db` and their `-wal`/`-shm`), which live in the same folder and behave the
same way. A clean close of either at the pool stop is up to three false "POST-DEPLOY CHECK"
warnings per database, ending in "runtime config drifted during this deploy. Compare against
<the config backup> ..." - a backup that, since cdt-10, deliberately does not contain those files.
A false operational alarm pointing at a comparison the operator cannot make; nothing is lost.
Found by cdt-10 (reported, not changed) and by the codex review of the cdt-8..10 batch (its
finding 3).
**Status**: Fixed on branch; awaiting merge
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `<pending>`
**Reviewer**: implementer (surfaced by the cdt-8/9/10 agent)

## Evidence

`deploy.ps1` at `4c0364e`. One list, set before the mode branch, already names every database
the app keeps in `config\` (cdt-10; its tripwire derives the names from the app's source):

```
462:     $configCopyExclusions = @(
463:         'exchangeadmin.db', 'exchangeadmin.db-wal', 'exchangeadmin.db-shm',
464:         'exchangeadmin-jobs.db', 'exchangeadmin-jobs.db-wal', 'exchangeadmin-jobs.db-shm',
465:         'exchangeadmin-usage.db', 'exchangeadmin-usage.db-wal', 'exchangeadmin-usage.db-shm')
```

The drift inventory, in the upgrade branch, has its own literal, written before the jobs and
usage databases existed (`a6741ad`, 2026-06-18):

```
527:         # The SQLite config DB triplet (.db/-wal/-shm) changes byte-for-byte across a pool restart
528:         # (WAL checkpoints, startup seeding) by design, so a size/mtime drift comparison is
529:         # meaningless for it - it is verified by PRAGMA integrity_check after deploy instead (see the
530:         # post-deploy DB health check). Exclude it from the file-drift inventory.
531:         $driftCheckExclusions = @('extended-log-level.txt', 'exchangeadmin.db', 'exchangeadmin.db-wal', 'exchangeadmin.db-shm')
...
534:             Get-ChildItem $runtimeConfigDir -File |
535:                 Where-Object { $_.Name -notin $driftCheckExclusions } |
...
648:             Get-ChildItem $runtimeConfigDir -File |
649:                 Where-Object { $_.Name -notin $driftCheckExclusions } |
```

and warns on every difference, MISSING, MODIFIED or APPEARED (654-668), then:

```
679:             Write-Warn "POST-DEPLOY CHECK: runtime config drifted during this deploy. Compare against ${backupHint}$BackupDir\$backupName before trusting the app, and investigate what wrote it."
```

`Program.cs:88` and `:97` put the jobs and usage databases in `config\`, and
`SqliteConnectionFactory.cs:76` opens every database `PRAGMA journal_mode=WAL` (cdt-10 Evidence).

**Mechanism, reproduced with the app's own engine** (Microsoft.Data.Sqlite from the build, scratch
probe under PowerShell 7.6.6): a WAL database written through one connection and inventoried the
way lines 534-537 do, before and after that connection closes cleanly:

```
while open : exchangeadmin-jobs.db=4096|...171  exchangeadmin-jobs.db-shm=32768|...171  exchangeadmin-jobs.db-wal=12392|...171
after close: exchangeadmin-jobs.db=8192|...355
  drift check would say: config\exchangeadmin-jobs.db was MODIFIED during the deploy
  drift check would say: config\exchangeadmin-jobs.db-wal existed before the deploy but is MISSING now
  drift check would say: config\exchangeadmin-jobs.db-shm existed before the deploy but is MISSING now
```

The last close checkpoints the WAL into the main file and deletes both sidecars. Whether IIS's
pool stop closes the app's pooled connections that cleanly cannot be observed off a host (as
cdt-10 noted), and a request arriving between the restart and the second inventory would also
open the databases (the app interrupts and prunes jobs and prunes telemetry at startup); either
way the comparison is meaningless for these files for the same reason line 527 gives for the
config database. Each warning is a `Write-Warn`; nothing throws.

## Plan (approved by the owner's EVERYTHING go)

1. `$driftCheckExclusions = @('extended-log-level.txt') + $configCopyExclusions`: the drift
   inventory leaves out every database the backup copies leave out, from the one list cdt-10
   introduced, so one list owns "the databases the app keeps in config\" and the cdt-10 tripwire
   covers both uses. `extended-log-level.txt` stays the one extra.
2. The comment above it says why the jobs and usage databases are left out too, and that the
   config database is still verified by the post-deploy integrity check.
3. Not changed: what else the inventory covers (every other top-level file in `config\`), the
   `appsettings.json` key check, the messages.

Assumption, recorded: the jobs and usage databases are operational state, not runtime config
(Invariant 3), so leaving them out of a runtime-config drift check loses nothing the check is
for; nothing in a deploy writes `config\` (robocopy `/XD config`).

Constraints as cdt-10 (5.1, ASCII, LF, no plan mode, no version bump).

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1` (the new test, and one line of the
existing drift test, which pinned the old literal), this record.

Guard: a sibling of the cdt-10 test - exactly one assignment to `$driftCheckExclusions`, in the
upgrade branch, after `$configCopyExclusions`, whose right side references `$configCopyExclusions`;
the two real statements evaluated together yield exactly `extended-log-level.txt` plus every name
in `$configCopyExclusions`; and both inventory pipelines filter on `$driftCheckExclusions`.
Mutations: the old literal; one file dropped from the derived list; a complete literal that does
not derive from the shared list; the post-deploy inventory filtering on the wrong list.

## Approach

As planned. In `deploy.ps1` after the fix:

- Line 536: `$driftCheckExclusions = @('extended-log-level.txt') + $configCopyExclusions`; lines
  528-535 above it say why the jobs and usage databases are left out with the config database.
- Line 462: one sentence on cdt-10's list, that the drift inventory now reads it too.
- Lines 540 and 654, the two inventory pipelines, unchanged: they already filter on
  `$driftCheckExclusions`.

The drift list now holds ten names: `extended-log-level.txt` and the nine of the shared list. The
inventory still covers every other top-level file in `config\` (the seed and archive JSON files).

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `leaves the same databases out of the drift inventory as out of the backup copies (cdt-13)` | Parses the AST: exactly one assignment to `$configCopyExclusions` and one to `$driftCheckExclusions`, the second after the first, its right side referencing `$configCopyExclusions`; the two real statements, evaluated together, yield exactly `extended-log-level.txt` plus every name the shared list holds (at least nine; the cdt-10 test pins which); exactly two `Get-ChildItem ... $runtimeConfigDir` pipelines that do not copy, each filtering `Where-Object { $_.Name -notin $driftCheckExclusions }` after the list is set |
| `runs a post-deploy live DB integrity check (DB is excluded from file drift by design)` (existing, one line changed) | The drift list is `@('extended-log-level.txt') + $configCopyExclusions` (was: a literal starting `'extended-log-level.txt', 'exchangeadmin.db'`) |
| `leaves every SQLite database the app keeps in config\ out of the config backup copies (cdt-10)` (existing) | Unchanged: the shared list is exactly the app's databases with their sidecars, from the app's source |

Mutations on `deploy.ps1` (fixed md5 `0F87241EAEE8FDBB338D663834C88DAE`, pre-comment; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once, run against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed; the restored file re-run green (95 passed). Post-comment md5
`8BFDD14A07CA2CFF7E1ABC4CAACDC71A` (95 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| D1 pre-fix shape (`4c0364e`, the four-name literal) | `4CDA558BBAD34827D70C4A030A9AEDE2` | the cdt-13 test ("Expected 1, because the drift list derives from the shared list, so one list owns the databases, but got 0"); the existing drift test (its pinned form did not match) |
| D2 one file dropped, `@('extended-log-level.txt') + @($configCopyExclusions \| Where-Object { $_ -ne 'exchangeadmin-usage.db-shm' })` | `7FFDD872243623125BAE5EFC1AB41EC4` | the cdt-13 test (the evaluated list: "Expected length: 232 Actual length: 205"); the existing drift test |
| D3 a complete ten-name literal that does not derive from the shared list | `F421FDDE5376B1A4E2D3D899EC67205E` | the cdt-13 test ("... derives from the shared list ... but got 0"); the existing drift test |
| D4 the post-deploy inventory filtering on `$configCopyExclusions` | `70FBAC76E375B8A40330166EB8569B94` | the cdt-13 test ("... because the inventory at line 648 must filter on the drift list") |

D3 is deliberate: it lists exactly the right names today, and is wrong anyway, because a fourth
database added to the app would reach the shared list through the cdt-10 tripwire and not reach
this one.
