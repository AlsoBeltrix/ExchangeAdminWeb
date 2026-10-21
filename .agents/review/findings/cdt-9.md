# cdt-9: a -Force deploy takes no verified backup of the config database

**Severity**: HIGH. Architectural Invariant 3 broken: the config database "is backed up via
verified online backup from that path before each deploy, not by raw file copy". Every
`deploy.ps1 -Force` over an existing deployment instead takes its only snapshot as a raw
recursive `Copy-Item` of the live `config\` directory while the app is running. Nothing verifies
it, the main file it copies can hold none of the data (it lives in the `-wal` copied separately),
and when `ConfigStore:Path` names a shared database, that file is outside `config\` and is not
backed up at all. The new build then starts against the authorization store, and runs its schema
migrations on it, with no verifiable rollback snapshot. With cdt-8 fixed a `-Force` run keeps
the shared database, so it is exactly the unprotected one.
**Status**: Closed - codex group review (ops-batch-1, 2026-10-21) raised no defect in this change; its three candidates are pre-existing and tracked as cdt-13, cdt-16, cdt-17
**Branch**: `worktree-agent-a4611345a15a8c0bd`
**Commit**: `bb8b64e` (merged)
**Reviewer**: implementer (surfaced by the cdt-5/6 agent, confirmed by the supervisor)

## Evidence

`deploy.ps1` at `0e18583`. The only `-Force` backup runs before the publish, with the pool still
running (it is stopped at line 741):

```
156: if ($Force -and (Test-Path $configPath)) {
157:     $backupName = "appsettings.${timestamp}.pre-force.bak"
158:     Copy-Item $configPath (Join-Path $BackupDir $backupName)
159:     Write-Warn "Existing config backed up to $BackupDir\$backupName"
160:     $forceConfigDir = Join-Path $PublishPath "config"
161:     if (Test-Path $forceConfigDir) {
162:         $forceConfigBackup = Join-Path $BackupDir "config.${timestamp}.pre-force.bak"
163:         Copy-Item $forceConfigDir $forceConfigBackup -Recurse
164:         Write-Warn "Existing runtime config directory backed up to $forceConfigBackup"
165:     }
166: }
```

The fresh-install branch that `-Force` takes (lines 678-835) contains no `Backup-SqliteConfigDb`,
no `Resolve-ConfigDbPath` and no `Get-ConfigStorePathSetting`. The upgrade branch has all three:

```
463:         $configStorePathSetting = Get-ConfigStorePathSetting -PublishPath $PublishPath
464:         $configDbPath = Resolve-ConfigDbPath -PublishPath $PublishPath
465:         if ($configStorePathSetting -and -not (Test-IsSqliteConfigDbPresent -DbPath $configDbPath)) {
466:             Write-Fail "ConfigStore:Path in $configPath names '$configDbPath' but no database exists there. ...
...
473:         $dbBackup = Backup-SqliteConfigDb -DbPath $configDbPath -DestDir $configDirBackup -Timestamp $timestamp -AppPath $StagingPath
...
483:             Get-ChildItem -LiteralPath $runtimeConfigDir -File |
484:                 Where-Object { $_.Name -notin @('exchangeadmin.db', 'exchangeadmin.db-wal', 'exchangeadmin.db-shm') } |
485:                 ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $configDirBackup $_.Name) -Force }
```

**Mechanism: no error at all**, reproduced rather than reasoned. Scratch run under Windows
PowerShell 5.1.20348 (the deploy's runtime) and again under PowerShell 7.6.6, using line 163's
exact statement under `$ErrorActionPreference = "Stop"`, while a second process held a WAL-mode
database open the way the running app does (connection open, 2000 rows written, automatic
checkpoint off): `Copy-Item` succeeded with no error and no warning, copying
`exchangeadmin.db` (4096 bytes), `exchangeadmin.db-shm` (32768) and `exchangeadmin.db-wal`
(9167032). Opened on its own, the copied main file held no table at all ("SQLite Error 1: 'no
such table: t'"); every row was in the copied `-wal`. The copy is a sequence of independent
file reads with no snapshot across them, nothing checks it, and line 164 then reports "backed
up". A checkpoint or a write between those reads leaves a database and `-wal` that disagree,
the torn copy SQLite's own backup guidance warns against; that interleaving itself was not
reproduced. With `ConfigStore:Path` set, none of this touches the database the app opens.

## Plan (approved by the owner's EVERYTHING go)

1. At the top of the fresh-install branch, right after cdt-8's read and before the branch
   reconfigures IIS, runs the job check, stops the pool or mirrors: the upgrade path's resolve
   and refusal, verbatim - `$configDbPath = Resolve-ConfigDbPath -PublishPath $PublishPath`, and
   `Write-Fail` when `$configStorePathSetting` is set and `Test-IsSqliteConfigDbPresent` says the
   file is not there. A fresh install has no `appsettings.json`, so the setting is `$null`, the
   refusal cannot fire, and the path is the per-instance default. cdt-8's "is kept" line moves
   below the refusal, so a run never prints "kept" for a file it then refuses.
2. Then, when `-Force` runs over an existing deployment (`if ($Force -and (Test-Path
   $configPath))`, the condition the old block used), the upgrade path's backup with the
   upgrade's layout: one timestamped directory under `$BackupDir`, keeping the old name
   `config.${timestamp}.pre-force.bak` so it still pairs with `appsettings.${timestamp}.pre-force.bak`;
   inside it `Backup-SqliteConfigDb -DbPath $configDbPath -DestDir $configDirBackup -Timestamp
   $timestamp -AppPath $StagingPath` (verified online backup; throws and aborts on failure;
   `$null` when there is no database yet, reported with the upgrade path's warning), then the
   rest of `config\` copied exactly as the upgrade path copies it: top-level files, the live
   triplet `exchangeadmin.db`, `-wal`, `-shm` excluded.
3. Remove the raw recursive copy from the early block (lines 160-165). The `appsettings.json`
   copy (157-159) stays where it is: a plain file, not a live database, and the earliest snapshot.
4. Ordering: the backup runs the db tool from staging, so it sits after the publish, inside the
   outer `try` (cdt-6 removes staging on any failure, a backup refusal included). The invariant
   is backup before stop and mirror, and it holds: refusal and backup precede the IIS
   reconfiguration, the job check, `Stop-AppPoolVerified`, robocopy and the regeneration.

Assumptions, recorded:

- Gate `$Force -and (Test-Path $configPath)`: an existing deployment by the script's own
  definition (line 174: "No existing deployment at $PublishPath (no appsettings.json)"). A real
  fresh install takes no backup, as before.
- Contents: `config\` subdirectories (the app writes `config\MigrationReports`) are no longer in
  the `-Force` snapshot, matching the upgrade snapshot. No deploy step writes `config\` (robocopy
  `/XD config`), so they are not at risk from the run.
- The jobs and usage databases in `config\` are still raw-copied as "the rest of config\", as
  the upgrade path already does; Invariant 3 deliberately gives them no verified backup. Not
  changed here.

Constraints: no plan mode exists in `deploy.ps1` (`tools/deploy-pipeline.ps1 -PlanOnly` skips it),
so Invariant 4 adds no plan line; nothing prompts. Same 5.1/ASCII/LF rules as cdt-8. No version
bump: ops-script-only.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

Guards: an AST test on the fresh-install branch - exactly one `Backup-SqliteConfigDb` with the
four arguments above, ending before the first `Stop-AppPoolVerified`/`robocopy`, whose only
conditional ancestor in the branch is the `$Force -and (Test-Path $configPath)` gate; the resolve
unconditional and before it; the refusal before the stop and mirror; the destination directory;
the rest-of-config copy filtered on the triplet and before the stop and mirror; and no
`Copy-Item -Recurse` anywhere in the script. Mutations: remove the call; move the backup after
the stop; drop the triplet filter; restore the raw recursive copy; drop the refusal; a gate that
is never true for `-Force`; the pre-fix shape.

## Approach

As planned. In `deploy.ps1` after the fix:

- Lines 175-184, the early `-Force` block: a comment, then only the `appsettings.json` copy. The
  raw recursive copy of `config\` (old lines 160-165) is gone.
- Lines 705-718, top of the fresh-install branch: cdt-8's read (705), then the resolve (712) and
  the refusal (713-715), verbatim from the upgrade path, then cdt-8's "is kept" line (716-718),
  moved below the refusal.
- Lines 720-748: the backup, gated `if ($Force -and (Test-Path $configPath))`:
  `$configDirBackup = Join-Path $BackupDir "config.${timestamp}.pre-force.bak"` (730),
  `Backup-SqliteConfigDb -DbPath $configDbPath -DestDir $configDirBackup -Timestamp $timestamp
  -AppPath $StagingPath` (733), and the rest of `config\` with the triplet excluded (740-747).

Order in the `-Force` path after the fix: the `appsettings.json` copy (182), publish to staging and
the db tool resolved from it, then in the fresh-install branch the read (705), the resolve and the
refusal (712-715), the verified backup and the rest-of-config copy (728-748), the IIS web
application (750 on), the job check (793), the pool binding check, `Stop-AppPoolVerified` (809),
robocopy (827), and the regenerated `appsettings.json` (890). All of it inside the outer `try`
whose `finally` removes staging (cdt-6).

Also in this commit, a test-helper fix: `Get-ConditionalAncestor` (added by cdt-8) returned its
list as `, $found`, so `@(...)` at the call site wrapped it in a one-element array. cdt-8's use was
unaffected - an empty inner list still reads as empty and a non-empty one still fails, which its
M5 proved - but this test indexes the result, so the helper now emits the list itself. cdt-8's M5
was re-run against the corrected helper (last row below).

Shape probe under Windows PowerShell 5.1.20348, the deploy's runtime: the fresh-install-branch
statements from the read through the backup gate were lifted from `deploy.ps1` by the 5.1 parser
(the branch's first five statements, lines 700-729 of the pre-comment file) and run with `$Force` set, the script's helpers and error model,
and the built app (2.34.0) as `$StagingPath`, while a second process held each live WAL database
open. No key: a verified backup of `config\exchangeadmin.db` (466944 bytes, integrity passed) plus
`sectionaccess.json`, and no file of the live triplet copied. Key set to a live shared database
outside `config\`, with a stale `config\exchangeadmin.db` beside it: "is kept" printed, the shared
file backed up (integrity passed), the stale file not copied. Opened on its own, each backup held
all 2000 rows, with no sidecar files. Key naming a missing file: refused with the `Write-Fail`
message and no backup directory created.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `takes the verified online backup on -Force, before the pool stops or anything is mirrored (cdt-9)` | Parses the AST of the fresh-install branch: exactly one `Backup-SqliteConfigDb`, with `-DbPath $configDbPath -DestDir $configDirBackup -Timestamp $timestamp -AppPath $StagingPath`, ending before the first `Stop-AppPoolVerified`/`robocopy`, whose only conditional ancestor in the branch is `if ($Force -and (Test-Path $configPath))`; exactly one `$configDbPath = Resolve-ConfigDbPath -PublishPath $PublishPath`, unconditional, before the backup; exactly one `if ($configStorePathSetting -and -not (Test-IsSqliteConfigDbPresent -DbPath $configDbPath))` whose body starts with `Write-Fail "ConfigStore:Path`, unconditional, before the stop and mirror; the `config.${timestamp}.pre-force.bak` directory; exactly one rest-of-config copy pipeline from `Get-ChildItem ... $runtimeConfigDir`, with the triplet filter, before the stop and mirror; and no `Copy-Item` with `-Recurse` anywhere in `deploy.ps1` |
| `backs up the runtime config directory before the upgrade mirror (incident fix #4)`, `backs up the config DB from the path appsettings resolves ... (scd-1)` (existing) | Still green: the upgrade path is untouched |

Mutations on `deploy.ps1` (fixed md5 `AEAEC0380CCFDFF4CDB924A0A1E2F410`, pre-comment; md5s of the
content as committed, LF), each run against the whole `DeployInvariants.Tests.ps1`, then the
original bytes restored, touched, md5 re-confirmed; the restored file re-run green (92 passed).
Post-comment md5 `7BF624216769246A16DA261503F3F0B2`.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| N1 the backup call removed (`$dbBackup = $null`) | `60C272522E3B2369219662EC90C2BB95` | the cdt-9 test ("Expected 1, because the -Force path must take the verified online backup, but got 0") |
| N2 the whole backup block moved after `Stop-AppPoolVerified` | `B9B1CBFDC3DA012F0FCA1D1D05E9C8CB` | the cdt-9 test ("Expected the actual value to be less than 43204, because the snapshot must exist before the pool stops or any file changes, but got 43902") |
| N3 the triplet filter dropped from the rest-of-config copy | `102D9CE1C53DE9F87EEFFFEEF030D8AF` | the cdt-9 test (the `Where-Object { $_.Name -notin @('exchangeadmin.db', ...) }` regex "did not match") |
| N4 the raw recursive copy restored in the early block | `B1EA164D90493C610E505FD82ED1F3BA` | the cdt-9 test ("... but got 'line 182: Copy-Item $forceConfigDir $forceConfigBackup -Recurse'") |
| N5 the configured-but-missing refusal dropped | `191E89AE52A1CC767CC249FA69EB78FA` | the cdt-9 test ("Expected 1, because a configured path naming a missing file must stop the run, not back up nothing, but got 0") |
| N6 a gate never true over an existing deployment, `if ($Force -and -not (Test-Path $configPath))` | `498C51C9EDF780EDF6466EE4C2C62E18` | the cdt-9 test (the gate text, "Expected strings to be the same, but they were different") |
| N7 the per-instance path, `-DbPath (Join-Path $runtimeConfigDir 'exchangeadmin.db')` | `4FFDF18DFA15DFD6F5A6FE6029FD1CC1` | the cdt-9 test ("Expected strings to be the same, because the backup must target the database the instance opens") |
| N8 pre-fix shape (`6297f1b`, after cdt-8) | `A822D66C1E9234382D36685CECA1095F` | the cdt-9 test ("Expected 1 ... but got 0") |
| cdt-8 M5 re-run under the corrected helper, `if (-not $Force) { $configStorePathSetting = ... }` | `983D620C141D65617734AE941B374209` | the cdt-8 AST test ("Expected $null or empty, because every run down this branch must read it, but got if (-not $Force) ...") |
