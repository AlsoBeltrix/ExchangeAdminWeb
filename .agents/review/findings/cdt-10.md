# cdt-10: every deploy copies the jobs and usage databases into a backup nothing prunes

**Severity**: MEDIUM. Every `deploy.ps1` upgrade - and every `-Force` run, before cdt-9 through
its raw recursive copy and after it through the same copy as the upgrade - copies the two
per-instance operational databases, `exchangeadmin-jobs.db` and `exchangeadmin-usage.db` with
their `-wal` and `-shm`, into `<LogRoot>\ExchangeAdminWeb\ConfigBackups\config.<timestamp>...`,
which nothing ever prunes. The jobs database holds each bulk job's authorization snapshot,
payload and per-row targets (a Comms-10k replacement is a whole member list); the usage
database holds the telemetry. Four pages tell users that data is kept 30 or 90 days. The deploy
keeps a copy of it forever, one per deploy, and the copies are raw copies of live WAL files, so
they are not even a usable backup. Architectural Invariant 3 and both plans say these databases
are deliberately not backed up. Nothing is lost and nothing breaks; a stated retention is
silently false.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a4611345a15a8c0bd`
**Commit**: `90eb74e` (merged)
**Reviewer**: implementer (found while fixing cdt-9; not yet confirmed by the supervisor)

## Evidence

`deploy.ps1` at `bb8b64e`. The upgrade path's copy of "the rest of config/" excludes only the
config database triplet:

```
499:            # Copy the rest of config/ (everything except a per-instance live DB triplet, which is
500:            # handled above when it is the configured database and is a stale leftover otherwise).
501:            Get-ChildItem -LiteralPath $runtimeConfigDir -File |
502:                Where-Object { $_.Name -notin @('exchangeadmin.db', 'exchangeadmin.db-wal', 'exchangeadmin.db-shm') } |
503:                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $configDirBackup $_.Name) -Force }
```

and the `-Force` copy (741-743, cdt-9) is the same statement. `Program.cs:88` and `:97` put the
jobs and usage databases in that same directory:

```
88:     var jobsDbPath = Path.Combine(builder.Environment.ContentRootPath, "config", "exchangeadmin-jobs.db");
97:     var usageDbPath = Path.Combine(builder.Environment.ContentRootPath, "config", "exchangeadmin-usage.db");
```

and `SqliteConnectionFactory.cs:76` opens every database `PRAGMA journal_mode=WAL`. Nothing
deletes anything under `$BackupDir` (`deploy.ps1:172`; no other script touches that path).

The filter was written on 2026-06-18 (`a6741ad`, SQLite Phase D1), before either database
existed (the jobs database arrived on 2026-07-02, `971555f`; the usage database on 2026-09-04),
and deploy.ps1 has never named either. The plans that added them say the opposite of what the
copy does: `docs/BulkJobRunner-Plan.md` "It is NOT part of the config backup/promote path";
`docs/UsageTelemetry-Plan.md` "Telemetry is disposable, so no backup is added"; Invariant 3
"deliberately neither backed up nor promoted".

The retention the copies outlive, as users are told it: `Home.razor:45` (usage data "is kept for
@UsageTelemetryService.RetentionDays days", 90), `AdminEventLog.razor:117-118` ("rows are kept for
@UsageTelemetryService.RetentionDays days"), `Comms10k.razor:55` ("Finished jobs are retained for @BulkJobs.RetentionDays days") and
`AdminBulkJobs.razor:35` ("Finished jobs are kept for @RetentionSummary", `BulkJobs:RetentionDays`,
default 30). The app enforces both at startup (`Program.cs:421-428`, `BulkJobService.cs:85`); the
backups are outside its reach.

**Mechanism: no error at all.** The copy is a successful `Copy-Item` per file, and the files
are open live WAL databases, which `Copy-Item` reads without complaint (reproduced for cdt-9
under Windows PowerShell 5.1.20348). The deploy then reports "Runtime config directory backed
up".

## Plan (approved by the owner's EVERYTHING go)

1. One list, defined once before the mode branch next to `$runtimeConfigDir`, so the two paths
   cannot disagree: `$configCopyExclusions` = the three SQLite databases the app keeps in
   `config\` - `exchangeadmin.db` (backed up separately, by the verified online backup),
   `exchangeadmin-jobs.db` and `exchangeadmin-usage.db` (deliberately not backed up) - each with
   its `-wal` and `-shm`.
2. Both rest-of-config copies filter with `Where-Object { $_.Name -notin $configCopyExclusions }`,
   and the upgrade copy's comment says why the operational databases are left out.
3. Not changed: the verified backup of the config database; what is backed up otherwise (the
   seed and archive JSON files, `extended-log-level.txt`); the upgrade's drift inventory
   (`$driftCheckExclusions`), which is a separate question and is reported, not changed; existing
   backup directories on hosts (deleting old copies is an operator decision, not a deploy's).

Constraints: as cdt-8 and cdt-9 (5.1, ASCII, LF, no plan mode, no version bump).

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1` (the new test, and cdt-9's test, which
pinned the inline triplet text and now pins the shared list), this record.

Guards: an AST test - `$configCopyExclusions` assigned once, unconditionally, before the mode
branch, to exactly the nine names; every `Get-ChildItem ... $runtimeConfigDir` pipeline that feeds
`Copy-Item` (two) filters on it; and a tripwire that reads `Program.cs` and the config store's
path code for every `*.db` the app creates under `config\` and requires each one, with its
`-wal` and `-shm`, in the list - so a fourth database cannot repeat this. Mutations: drop the
jobs or usage names; put the inline triplet back on either copy; a fourth database added to
`Program.cs` without the list; the pre-fix shape.

## Approach

As planned. In `deploy.ps1` after the fix:

- Lines 456-465, right after `$runtimeConfigDir` (454) and before the mode branch, inside the
  outer `try`: a comment and `$configCopyExclusions`, the nine names.
- Line 514 (upgrade) and line 754 (`-Force`): `Where-Object { $_.Name -notin
  $configCopyExclusions }`, replacing the inline triplet.
- Lines 510-512: the upgrade copy's comment, which described the filter as "a per-instance live DB
  triplet" only; and line 735, the same correction in cdt-9's comment.

cdt-9's test now pins the shared filter on the `-Force` copy (its N3 still bites: dropping the
filter fails both tests); the list's contents are pinned here.

Behaviour, reproduced under Windows PowerShell 5.1.20348: the two rest-of-config copies were
lifted from `deploy.ps1` by AST and run against a `config\` in which a second process held all
three databases open (WAL, `-wal` and `-shm` present), beside `sectionaccess.json` and
`extended-log-level.txt`. At `bb8b64e` each copy wrote `exchangeadmin-jobs.db`, `-shm`, `-wal`,
`exchangeadmin-usage.db`, `-shm`, `-wal` and `sectionaccess.json`, and reported "Runtime config
directory backed up". After the fix each wrote `extended-log-level.txt` and `sectionaccess.json`
only.

Not changed, and reported rather than fixed: the upgrade's post-deploy drift inventory
(`$driftCheckExclusions`, line 531) also predates the two databases and inventories them, although
the reason its comment (line 527) gives for excluding the config database - it "changes
byte-for-byte across a pool restart (WAL checkpoints, startup seeding) by design" - applies to
them too. Whether that produces false drift warnings depends on whether the app's pooled
connections close cleanly when IIS stops the pool, which cannot be observed off a host. Existing copies in
`ConfigBackups` on the hosts are left alone.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `leaves every SQLite database the app keeps in config\ out of the config backup copies (cdt-10)` | Parses the AST: exactly one assignment to `$configCopyExclusions`, under no conditional construct, ending before the mode branch; its string elements are exactly the databases the app's own source creates under `config\` (every `Path.Combine(..., "config", "<name>.db")` in the tracked non-test `.cs` files, which must include `exchangeadmin.db`, `exchangeadmin-jobs.db` and `exchangeadmin-usage.db`), each with `-wal` and `-shm`, and nothing else; exactly two `Get-ChildItem ... $runtimeConfigDir` pipelines that feed `Copy-Item`, each filtering `Where-Object { $_.Name -notin $configCopyExclusions }` after the list is set |
| `takes the verified online backup on -Force ... (cdt-9)` (changed) | The `-Force` copy filters on `$configCopyExclusions` (was: the inline triplet text) |

Mutations (fixed `deploy.ps1` md5 `18DDD49C31B9F5138A13EC942A6D9C6E`, pre-comment; fixed
`Program.cs` md5 `35E425D520A1C96BB946FE4CCA234405`; md5s of the content as committed, LF), each run
against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5
re-confirmed; both restored files re-run green (93 passed). Post-comment `deploy.ps1` md5
`F8F2A93158CB6F0BA19FC2A3447A79BD`.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| P1 the jobs database dropped from the list | `D39346751DB16F66CAECACF95998B3E9` | the cdt-10 test (the list against the app's databases, "Expected strings to be the same, because exactly the databases the app keeps in config\ ...") |
| P2 the usage database dropped from the list | `368C11A44112BB662E98B02214AEEF2D` | the cdt-10 test (same assertion) |
| P3 the upgrade copy back to the inline triplet | `FEBD2CEEC7FEF85F6710688D26164A6B` | the cdt-10 test ("... because the copy at line 505 must filter on the list, but it did not match") |
| P4 the `-Force` copy back to the inline triplet | `BF689071B33893DA219465DE9E7D447B` | the cdt-10 test ("the copy at line 745 must filter on the list") and the cdt-9 test (its filter assertion) |
| P5 the list made conditional, `if (-not $Force) { ... }` | `0BEAF398BA4E7ED65331B720CEB98904` | the cdt-10 test ("Expected $null or empty, because both paths read it, but got if (-not $Force) ...") |
| P6 a non-database file, `sectionaccess.json`, added to the list | `5CEE2FE1BCFBD570FDECF8ED4CCBA70C` | the cdt-10 test ("... and nothing else") |
| P7 pre-fix shape (`bb8b64e`, after cdt-9) | `7BF624216769246A16DA261503F3F0B2` | the cdt-10 test ("Expected 1, because one list ... but got 0") and the cdt-9 test (its filter assertion) |
| P8 `Program.cs` gains `Path.Combine(..., "config", "exchangeadmin-reports.db")`, list unchanged | `5F7FBB0C2EC5C047AFD493034D456029` (`Program.cs`) | the cdt-10 test (the tripwire, "... each with its WAL sidecars, and nothing else") |
