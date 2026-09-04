# One shared config database for dev and prod

Status: Implemented 2026-09-04 (S1 `be507f0`, S2 `5a9c832`, S3 `5776965`, S4 `8657b57`,
S5 = the commit that set this line; traceability in section 9). NOT DEPLOYED; cutover per
section 7/8 is owner-run, in this order: deploy to dev, promote to prod (the last
two-database promotion), `Move-ConfigDbToShared.ps1 -PlanOnly`, then `-Apply`. Codex
openreview over `e36e798..8bb46a4` returned `acceptable_with_changes` (scd-1..4, section
10), all folded in before implementation. The governing decision is RULED
(`.agents/decisions.md` 2026-09-04, "Dev and prod share ONE config database").
Owner: Michael
Last verified against code: `8657b57` / 2026-09-04
Versions: base app `2.18.0` -> `2.19.0` in S1 (shared infrastructure: startup path
resolution, migrator, cache readers). `docs/UsageTelemetry-Plan.md` also claims a base
bump; it now takes the next minor above `2.19.0`. No module bumps.
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`. On conflict the
higher source wins. This plan changes two Constitution lines and two repo-guidance
invariants (S5); until S5 lands, the decision entry is the authority for the change.

Owner rulings 2026-09-04: *"prod deployment cannot keep overwriting the prod db and all
settings with what's in dev"*, then *"can't prod and dev use the same DB with each
deployment making a backup first? I don't want to maintain two dbs and two sets of
settings, and not replacing the db on promotion means that we might not be getting new
database tables or settings. I need a good option, not more compromises."*

The Constitution requires a written plan: deployment scripts, persistence/storage, and
startup behaviour all change.

## 1. Owner decisions

**D1 (RULED): one database file, opened by both instances on this server; promotion
never copies it; each deploy backs it up first; dev's current database becomes the
shared one.** Canonical text in `.agents/decisions.md` 2026-09-04.

What the 2026-06-18 rejection of a shared DB rested on, and why each reason no longer
holds (recorded so the reversal is not re-litigated):

- "Dev config changes instantly live in prod" - the owner has now chosen exactly that
  over two settings sets. Dev is no longer a place to try a setting first.
- "Cannot hold per-environment values (security groups, connection targets,
  `PathBase`)" - `PathBase`, `PublicBaseUrl`, `Audit:LogRoot`, Delinea URL and the
  on-prem Exchange URI live in each instance's `appsettings.json`, not the DB; the
  DB-held values (section access, secret ids, module settings) have been byte-identical
  on both instances since promotion started copying them.
- "A network-shared single-file SQLite DB reintroduces file-locking corruption" - both
  instances run on ONE server and the file sits on local NTFS. SQLite's WAL mode with
  the store's `busy_timeout=5000` (`SqliteConnectionFactory.cs:45-58`) is built for
  several local processes on one file. A network path is refused (section 3, AC1).

Settled by the ruling or existing code, not open:

- **The per-instance databases stay per instance:** `exchangeadmin-jobs.db` (two job
  runners on one queue would each interrupt the other's live jobs at startup -
  `BulkJobRepository.InterruptAllNonTerminal`, `Program.cs:271-272` - and could run one
  job twice) and the planned `exchangeadmin-usage.db`. Only `exchangeadmin.db` is shared.
- **Additive-only migrations from now on** (decision corollary): a step may add tables,
  add nullable-or-defaulted columns, add indexes; it may never drop, rename, or
  repurpose. A tripwire test enforces it on the `Migrations` array text.
- **Cache freshness rides the existing change token.** `SqliteConfigStore.Write` already
  bumps `schema_meta.config_change_token` inside every write transaction
  (`SqliteConfigStore.cs:29-39`, `ConfigChangeToken.cs`), and nothing reads it
  (`IConfigStore.cs:15-18` says so). The four caching readers start reading it. No
  timer, no file watcher, no `data_version` (that pragma is per connection, and this
  store opens a fresh connection per operation).
- **Cutover order is fixed:** the tolerant build must be on BOTH instances before the
  file is shared, otherwise the first dev-only deploy that adds a table stops prod
  (today's migrator refuses a newer database, `ConfigStoreMigrator.cs:163-169`).
- **Promotion rollback no longer touches the database.** It restored the verified
  backup because promotion had replaced the file; with nothing replaced, restoring
  would roll back dev's live data too. The pre-promotion backup is still taken; a
  restore is a deliberate manual act.
- **Install-ExchangeAdminWeb.ps1 stays environment-neutral** (invariant 1): it gains an
  OPTIONAL `-ConfigStorePath`; without it, the single-instance default is unchanged.
  With it, the instance's own `config\` directory is STILL created and ACLed (the
  per-instance jobs database and the first-run seed files live there) and the shared
  directory is ACLed in addition (scd-4).
- **A configured path names a database that must already exist (scd-1).** When
  `ConfigStore:Path` is set, startup opens it read/write WITHOUT create and fails fast
  if the file is missing, and the deploy and promote backups THROW when the resolved
  shared file is absent. Otherwise a mistyped key, a moved file or a deleted file would
  start the app on a brand-new empty database - the startup seeding would then
  populate module rows at their defaults and the app would serve with no protected
  principals and no section access, looking healthy. Only the cutover script (S4) and
  the default no-key path (a fresh single-instance install) create a database.
- **Every cache records the change token as of its own load, not as of its first poll
  (scd-2).** A watcher that "seeds on first call" would miss a change made between a
  cache load and that first poll - for the log level, which never expires, until the
  next unrelated write. The token is read BEFORE the load so a write racing the load
  moves the token past the recorded value and is detected.
- **The tolerant migrator verifies the schema this build reads and writes - tables
  AND columns (scd-3)** - not table names alone: v6 is an `ALTER TABLE ... ADD COLUMN`
  (`section_access.group_display_name`) that `SectionAccessRepository` reads and
  writes, and a newer-labelled database lacking it would pass a table-only check and
  fail later inside a save.
- **The shared path on this server** is `D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db`
  - a deploy-host fact, the cutover script's default parameter, recorded in
  `.agents/machines.md`, never in code.

## 2. Non-goals

- Sharing the jobs or usage databases, or any file other than `exchangeadmin.db`.
- Per-environment overrides inside the shared database ("this setting differs on prod").
- A network share, a second server, or SQL Server.
- Changing what the audit/trace JSONL files do or where they live.
- Replacing the change-token mechanism with pub/sub or a file watcher.
- Changing the migration steps that exist (v1-v6).
- Automating the one-time cutover beyond the script in S4 (the owner runs it once,
  elevated, with `-PlanOnly` first).

## 3. Acceptance criteria

- AC1: `Program.cs` resolves the config DB path from `ConfigStore:Path` when set (an
  absolute file path), else today's `<ContentRoot>\config\exchangeadmin.db`. A UNC path
  (`\\`) or a relative path is refused at startup with a fatal log naming the key. When
  the key is set, the file MUST exist: the factory opens it with `Mode=ReadWrite` (no
  create) and a missing file is a fatal startup error naming the path and the key
  (scd-1). When the key is absent, today's `ReadWriteCreate` behaviour stands (a fresh
  single-instance install creates its own database). The jobs DB path derivation is
  untouched.
- AC2: `ConfigStoreMigrator.Migrate()` treats `user_version > TargetVersion` as
  acceptable: it logs a warning naming both versions, verifies the REQUIRED SCHEMA -
  every table this build's steps create AND every column they add (`PRAGMA
  table_info`), from a static `RequiredSchema` list pinned to the steps by a test - and
  returns the database's version without writing. A missing table or column still
  throws naming it (fail fast, scd-3). `user_version < TargetVersion` migrates as today.
- AC3: A tripwire test fails if any element of `Migrations` contains `DROP `, `RENAME`,
  or `ALTER TABLE ... DROP COLUMN` (case-insensitive), with a message pointing at the
  decision's additive-only rule.
- AC4: The four caching readers re-read when the change token moves: `ProtectedPrincipalService`
  (30 s TTL cache, `:110-123`), `ADAttributeEditorService` (allowlist and legend caches,
  `:59-63`), `PermissionValidator` (30-min cache, `:19-22`), `ExtendedLogService`
  (`_minimumLevel` loaded once, `:27,:52`). Each reader records the change token AS OF
  ITS LOAD: `var token = watcher.CurrentToken(); load...; _loadedToken = token;` (token
  read first, so a write racing the load is detected on the next check - scd-2). Each
  cache-hit path first asks `watcher.HasChangedSince(_loadedToken)`, which reads the
  stored token at most once per 2 seconds per watcher (throttle) and returns true when
  it differs. `CurrentToken()` is never throttled. A token read failure inside
  `HasChangedSince` returns false and logs once (stale-but-serving beats a hot loop of
  failures; the TTLs still bound staleness); a failure inside `CurrentToken()` at load
  time records `long.MinValue`, which every later successful read differs from, so the
  next check reloads.
- AC5: `tools/SqliteConfigBackup.psm1` gains `Resolve-ConfigDbPath -PublishPath` which
  reads `<PublishPath>\appsettings.json` `ConfigStore:Path` when present, else returns
  `<PublishPath>\config\exchangeadmin.db`; every existing function gains an optional
  `-DbPath` that overrides the `<ConfigDir>\exchangeadmin.db` derivation. Pure ASCII
  (invariant 6 - `deploy.ps1` imports this module under Windows PowerShell 5.1).
- AC6: `deploy.ps1` backs up and integrity-checks the resolved path (AC5), not the
  publish folder's `config\`. When appsettings names `ConfigStore:Path` and the file is
  absent, the backup step THROWS (`Write-Fail`) before anything is mirrored; the
  no-key case keeps today's "no DB yet, nothing to back up" warning (scd-1). The
  robocopy exclusions are unchanged. `deploy-pipeline.ps1` passes nothing new; its
  help text and final messages stop claiming that config was promoted (scd-4).
- AC7: `tools/promote-dev-to-prod.ps1`: the config-copy block (`:400-427`), the
  `-SkipConfigFragments` and `-Refresh` parameters and the `-Refresh` branch (`:274-324`)
  are removed; the pre-promotion verified backup is taken from the resolved path and
  THROWS when the key is set and the file is absent (scd-1); the rollback restores
  binaries only and prints where the DB backup is. `-PlanOnly` behaviour is preserved
  for every remaining step.
- AC8: `tools/Move-ConfigDbToShared.ps1` (new, `-PlanOnly` by default, `-Apply` to act):
  given `-DevPublishPath`, `-ProdPublishPath`, `-SharedDbPath` (default the section 1
  path) and the two app pool names, it (1) asserts both builds are at or above the
  tolerant version by reading each `ExchangeAdminWeb.dll` file version against a
  `-MinimumAppVersion` parameter, (2) stops both pools (`Assert-NoActiveBulkJobsBeforeRecycle`
  each), (3) takes verified backups of BOTH current databases into `-BackupRoot`,
  (4) `VACUUM INTO` dev's database to the shared path and integrity-checks it,
  (5) grants both app pool identities `(OI)(CI)M` on the shared directory, (6) writes
  `ConfigStore:Path` into both `appsettings.json` files atomically (the
  `Set-AppsettingsPathBase` pattern), (7) starts both pools, (8) verifies each instance
  logs "Config store schema ready" once. Every step honours `-PlanOnly`; any failure
  after step 3 prints the exact restore commands. Pure ASCII.
- AC9: `Install-ExchangeAdminWeb.ps1` accepts optional `-ConfigStorePath`; when given,
  it writes the key into the generated appsettings, still creates and ACLs the
  instance's `config\` (jobs DB, seed files - scd-4), ACLs the shared directory for the
  app pool identity IN ADDITION, and requires the shared file to exist already
  (refuses otherwise, pointing at the cutover script - the installer never creates the
  shared database, scd-1); when absent, behaviour is byte-identical to today.
- AC10: Pester: `DeployInvariants.Tests.ps1` rows that pinned the wholesale copy
  (`:349`), the DB rollback restore (`:372`) and `-Refresh` (`:391,:406,:412`) are
  replaced by rows asserting their absence; new describes cover `Resolve-ConfigDbPath`,
  the `-DbPath` overrides, and `Move-ConfigDbToShared.ps1` (plan mode emits every step,
  refuses a UNC `-SharedDbPath`, refuses when a pool's build is below the minimum).
- AC11: Docs: Constitution `:58` and `:64` name the shared file and drop "config
  promotion"; `:117` gains "promotion never copies the config database"; repo-guidance
  invariant 2 becomes "One shared config database; promotion never copies it
  (`.agents/decisions.md` 2026-09-04)" and invariant 3 names `ConfigStore:Path`; README
  operations section describes the shared file and the cutover; `.agents/machines.md`
  records the shared path.
- AC12: Base app version bumped in S1.

## 4. Failure behavior

| Step / dependency | If it fails | The operator sees | System state afterward |
|---|---|---|---|
| `ConfigStore:Path` is UNC or relative | Startup refuses (fatal log names the key) | App does not start | Unchanged |
| `ConfigStore:Path` set but the file is missing (mistyped key, moved or deleted file) | Startup refuses: the factory opens without create and the missing file is fatal, naming path and key (scd-1) | App does not start | Unchanged - NEVER a fresh empty database served as healthy |
| Shared file present but unreadable (ACL) | Open failure surfaces from `Migrate()` as today | App does not start; log names the path | Unchanged |
| DB newer than the build (dev migrated first) | Accepted with a warning; required-schema check passes | Nothing | Prod serves on the newer schema |
| DB newer AND a required table or column missing (a non-additive step slipped through) | `Migrate()` throws naming it (scd-3) | App does not start | The tripwire (AC3) exists to make this unreachable |
| Change committed on dev between prod's cache load and its first watcher poll | Detected: the token recorded at load predates the write (scd-2) | Prod reflects it within the throttle | - |
| Both instances start at once on an older DB | Each step runs in its own transaction; the second process's `PRAGMA user_version` read sees the first's commit or waits on busy_timeout; a step that already ran is skipped by the version loop | Nothing | One migration |
| Two processes write the same row | SQLite serialises; last commit wins; token bumps twice | Nothing | Last write wins - accepted (same as two operators on one instance today) |
| Token read fails inside a cache check | Returns "unchanged", logs once | Nothing | Cache serves until its TTL as today |
| Change made on dev while prod caches | Prod's next cache check (at most 2 s later) sees the token and reloads | Prod reflects it within seconds | - |
| Backup (deploy or promote) cannot find the DB at the resolved path, key SET | `Write-Fail` before any mirror (scd-1) | Deploy stops naming the path | Nothing changed |
| Backup cannot find the DB, key ABSENT (fresh single-instance install) | `Backup-SqliteConfigDb` returns null and the caller warns, as today | Warning | Deploy proceeds - unchanged posture |
| Cutover: a build below the minimum on either pool | Script refuses before stopping anything | Message naming the pool and version | Unchanged |
| Cutover: VACUUM INTO or integrity check fails | Script stops; pools stay stopped; prints restore commands | Message | Original files untouched (VACUUM INTO writes a new file) |
| Cutover: appsettings write fails on the second instance | Script stops; prints that instance 1 now points at the shared file and instance 2 at its own | Message | Split state - the printed commands revert instance 1's key from the backup |
| Promotion rollback | Binaries restored; DB not touched; backup path printed | Message | Shared DB as it was |

## 5. Rollback / blast radius

Code (S1, S2) reverts freely: without the appsettings key, S1 is byte-for-byte today's
path; S2 only adds token checks in front of existing cache hits. Scripts (S3, S4) revert
freely before the cutover runs.

After the cutover, "rollback" means returning to two databases: stop both pools, copy
the shared file to each instance's `config\` (verified `VACUUM INTO`), remove the key
from both appsettings, start. The cutover script prints these commands at the end of a
successful run and on any failure, and S4's Pester pins that.

Blast radius while shared: a bad setting on dev is live on prod within seconds. That is
the ruling, not a hazard the plan mitigates; the verified backup before every deploy
of either instance is the recovery path, and the retention of 3 (`-BackupRetention`)
is unchanged.

## 6. Design sketch

### Current code (read at `e36e798` via survey; spot-verify at implementation)

- Path: `Program.cs:49` (`Path.Combine(ContentRootPath, "config", "exchangeadmin.db")`),
  jobs `:68`; configurable-path precedent `AuditLogRoot.Require` (`Services/AuditLogRoot.cs:22-28`).
- Migrator: `ConfigStoreMigrator.cs:154-194`; refusal `:163-169`; test
  `ConfigStoreMigratorTests.Migrate_DatabaseNewerThanBuild_FailsFast` (`:96-111`).
- Token: `SqliteConfigStore.cs:16-20` (`GetChangeToken`), `:29-39` (bump in `Write`);
  `ConfigChangeToken.cs`; `IConfigStore.cs:15-18` (advisory note to update).
- Caches: `ProtectedPrincipalService.cs:110-123, :291-294, :317-319`;
  `ADAttributeEditorService.cs:59-63, :126-143, :269-285`; `PermissionValidator.cs:19-22, :34-41, :68-71`;
  `ExtendedLogService.cs:27, :52, :61-80`.
- Scripts: `deploy.ps1:139, :152, :436-451, :487-495, :607-616, :674-682`;
  `tools/promote-dev-to-prod.ps1:14-15, :274-324, :362-379, :400-427, :434-461`;
  `tools/SqliteConfigBackup.psm1:37-40, :54-92, :118-173, :179-196, :198`;
  `tools/Install-ExchangeAdminWeb.ps1:405-406, :483, :559, :598-616`;
  `tools/deploy-pipeline.ps1:34-51`. Pester: `tests/ps/DeployInvariants.Tests.ps1`,
  `tests/ps/SqliteConfigBackup.Tests.ps1`.

### S1 - path key and tolerant migrator

`Program.cs`:

```
var configuredDbPath = builder.Configuration["ConfigStore:Path"];
var configDbPath = ConfigStorePath.Resolve(configuredDbPath, builder.Environment.ContentRootPath);
```

New `Services/Storage/ConfigStorePath.cs`, pure: `Resolve(string? configured, string
contentRoot)` returns `(path, mustExist: false)` when `configured` is blank; throws
`InvalidOperationException` naming `ConfigStore:Path` when it is relative or starts
with `\\`; else returns `(configured, mustExist: true)`. Tested.
`SqliteConnectionFactory` gains a constructor flag `mustExist`: when true it does not
create the directory, uses `SqliteOpenMode.ReadWrite` instead of `ReadWriteCreate`, and
`Open()` on a missing file throws `FileNotFoundException` naming the path and the key
(scd-1). `Program.cs` passes the flag from `Resolve`; the jobs factory keeps today's
create mode.

`ConfigStoreMigrator.Migrate()`:

```
if (current > Migrations.Length)
{
    // Shared-DB rule (decisions 2026-09-04): the other instance is newer and steps are
    // additive-only, so serve on its schema. Verify this build's tables exist first.
    var missing = RequiredSchema.Missing(connection);   // "table" or "table.column" names
    if (missing.Count > 0)
        throw new InvalidOperationException($"Config database schema version {current} is newer than this build ({Migrations.Length}) and lacks schema this build requires: {string.Join(", ", missing)}.");
    _logger?.LogWarning("Config database schema version {Db} is newer than this build supports ({Build}); serving on the newer schema (additive-only rule)", current, Migrations.Length);
    return current;
}
```

`RequiredSchema` is a static list of `(table, columns[])` for every table v1-v6 create
and every column they add - including `section_access.group_display_name` from v6
(scd-3) - derived by reading the steps at implementation and pinned by a test that
regexes `CREATE TABLE` names and column lists plus `ALTER TABLE ... ADD COLUMN` out of
`Migrations` and compares. `Missing(connection)` checks `sqlite_master` for tables and
`PRAGMA table_info` for columns. The migrator gains an optional
`ILogger<ConfigStoreMigrator>?` constructor parameter (null in tests).

Tripwire `MigrationsAreAdditiveOnly` (AC3) reads `ConfigStoreMigrator.Migrations` via
an `internal static` accessor.

### S2 - `ConfigChangeWatcher`

```
public sealed class ConfigChangeWatcher(IConfigStore store, ILogger<ConfigChangeWatcher> log)
{
    public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(2);
    public const long Unknown = long.MinValue;
    // The stored token right now, unthrottled; Unknown when the read fails (logged).
    // Callers read it BEFORE loading the value they cache (scd-2).
    public long CurrentToken();
    // True when the stored token differs from loadedToken. Reads the DB at most once
    // per Throttle per watcher; a failed read returns false and logs once until the
    // next success. Unknown always compares as changed.
    public bool HasChangedSince(long loadedToken);
}
```

One instance per caching reader (constructed from the injected `IConfigStore`; the
readers already take the store or a repository - add the watcher where the store is
already in reach, or inject `ConfigChangeWatcherFactory`; decide at implementation,
record in the slice). Each reader's load becomes `var t = watcher.CurrentToken(); ...
load ...; _loadedToken = t;` and each cache-hit path becomes `if
(watcher.HasChangedSince(_loadedToken)) invalidate; then the existing TTL check`. A
reader's own save records the token the same way (its write bumped it).
`ExtendedLogService.MinimumLevel` getter checks the watcher and reloads
`_minimumLevel` when it moved (the 2 s throttle keeps the per-log-line cost to a field
compare); its constructor load records the token too. `IConfigStore.cs:15-18` and
`ConfigChangeToken.cs:13-15` comments updated: the token is now consulted.

### S3 - deploy scripts

`SqliteConfigBackup.psm1`: `Resolve-ConfigDbPath -PublishPath` (reads appsettings.json
with `ConvertFrom-Json`; PS 5.1-safe; returns the default when the key is absent or the
file is missing); `-DbPath` optional on `Test-IsSqliteConfigDbPresent`,
`Backup-SqliteConfigDb`, and a new `Copy-SqliteDbFile -SourceDbPath -DestDbPath`
(the `Copy-SqliteConfigDb` body generalised to file paths; the old function is deleted
with its only caller). `deploy.ps1:436-442, :607-616` use the resolved path.
`promote-dev-to-prod.ps1`: delete `:14-15` params, `:274-324`, `:400-427`; backup `:373-376`
from the resolved prod path, `Write-Fail` when the key is set and the file is absent;
rollback `:445-455` replaced by a `Write-Warn` naming the backup file. Every remaining
step keeps `Invoke-PlanOrAction` / `Write-Plan`. `deploy-pipeline.ps1`: help and final
messages no longer say config was promoted (scd-4). `deploy.ps1` backup step: same
`Write-Fail` rule when the key is set.

### S4 - `tools/Move-ConfigDbToShared.ps1`

Steps per AC8, each through `Invoke-PlanOrAction`; imports `SqliteConfigBackup.psm1`
and the pool helpers `deploy.ps1` uses (extract to a small module only if `deploy.ps1`
does not already export them - check first; do not duplicate). Windows PowerShell 5.1
(IIS provider), pure ASCII. `-MinimumAppVersion` defaults to the version S1 ships.

### S5 - docs

Constitution, repo-guidance, README, machines, plan status, state.

## 7. Task breakdown

**S1 - `ConfigStorePath`, tolerant migrator, required-table check, additive tripwire,
tests, base bump.** AC1, AC2, AC3, AC12. No behaviour change without the key.

**S2 - `ConfigChangeWatcher` in the four caching readers, tests.** AC4.

**S3 - backup module path resolution, `deploy.ps1`, `promote-dev-to-prod.ps1`,
Pester.** AC5, AC6, AC7, AC10 (part). PS lint + Pester.

**S4 - `Move-ConfigDbToShared.ps1` + Pester; `Install-ExchangeAdminWeb.ps1
-ConfigStorePath`.** AC8, AC9, AC10 (rest).

**S5 - docs and governance text.** AC11.

Then the owner-side cutover (section 8), in this order: deploy S1-S5 to dev; promote to
prod (the last promotion that runs with two databases - prod receives the tolerant
build); run `Move-ConfigDbToShared.ps1 -PlanOnly`, then `-Apply`.

## 8. Test plan

`ExchangeAdminWeb.Tests/ConfigStorePathTests.cs` (S1):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1 | `Resolve_Blank_UsesContentRootDefault` | null/""/" " -> `<root>\config\exchangeadmin.db` | Return configured; FAIL |
| AC1 | `Resolve_Absolute_ReturnsConfigured` | `D:\x\y.db` -> itself | n/a |
| AC1 | `Resolve_Unc_Throws_NamingTheKey` | `\\server\share\x.db` throws, message contains `ConfigStore:Path` | Drop the guard; FAIL |
| AC1 | `Resolve_Relative_Throws` | `config\x.db` throws | Drop the guard; FAIL |
| AC1 | `Resolve_Configured_MustExist_DefaultMustNot` | Configured -> mustExist true; blank -> false | Flip; FAIL |
| AC1 | `Factory_MustExist_MissingFile_Throws_AndCreatesNothing` (real temp path) | `Open()` throws `FileNotFoundException` naming the path; no file or directory created afterwards | Use ReadWriteCreate; FAIL |
| AC1 | `Factory_Default_CreatesTheFile` | Today's behaviour preserved | n/a |

`ConfigStoreMigratorTests.cs` (S1, real temp SQLite):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC2 | `Migrate_DatabaseNewerThanBuild_IsAcceptedWhenTablesExist` (replaces `_FailsFast`) | Fresh DB migrated to target, then `user_version = target + 5`: `Migrate()` returns target + 5, no throw, tables intact | Restore the throw; FAIL |
| AC2 | `Migrate_DatabaseNewerThanBuild_ThrowsWhenARequiredTableIsMissing` | Same, then `DROP TABLE app_setting`: throws naming `app_setting` | Drop the check; FAIL |
| AC2 | `Migrate_DatabaseNewerThanBuild_ThrowsWhenARequiredColumnIsMissing` (scd-3) | Same, then rebuild `section_access` without `group_display_name`: throws naming `section_access.group_display_name` | Check tables only; FAIL |
| AC2 | `RequiredSchema_MatchesTheMigrationStatements` | Regex over `Migrations` (CREATE TABLE columns + ADD COLUMN) equals `RequiredSchema` | Remove one column; FAIL |
| AC3 | `MigrationsAreAdditiveOnly` | No step contains DROP / RENAME / DROP COLUMN | Append a `DROP TABLE x;` step in a test copy; FAIL (the test takes the array as input) |

`ExchangeAdminWeb.Tests/ConfigChangeWatcherTests.cs` (S2, fake store):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC4 | `CurrentToken_ReturnsStoredValue_Unthrottled` | Two immediate calls both hit the store | Throttle it; FAIL |
| AC4 | `HasChangedSince_SameToken_False_MovedToken_True` | Loaded 5, stored 5 -> false; stored 6 -> true | Invert; FAIL |
| AC4 | `HasChangedSince_Throttles` | Two calls within 2 s hit the store once (fake counts) | Remove throttle; FAIL |
| AC4 | `HasChangedSince_StoreThrows_ReturnsFalseAndLogsOnce` | Throwing store -> false twice, one log | Rethrow; FAIL |
| AC4 | `HasChangedSince_UnknownLoadedToken_IsAlwaysChanged` | `Unknown` -> true | Compare numerically; FAIL |
| AC4 | per reader: `<Reader>_ReloadsWhenTokenMoves` (existing seamed harnesses) | Cached value replaced after a token bump within TTL | Skip the watcher; FAIL |
| AC4 | per reader: `<Reader>_DetectsAChangeMadeBeforeItsFirstPoll` (scd-2) | Load; bump the token and change the row; FIRST watcher check -> reloaded value | Seed on first poll; FAIL |
| AC4 | `ExtendedLogService_MinimumLevel_FollowsAnOutOfBandWrite` | Level written through a second repository instance on the same store is read back after the throttle | Load once; FAIL |

Pester (S3, S4) in `tests/ps/`:

| AC | Test | What it proves |
|---|---|---|
| AC5 | `Resolve-ConfigDbPath` returns the key's value, else the default; missing appsettings -> default | behavioural, temp dirs |
| AC5 | `Backup-SqliteConfigDb -DbPath` backs up a DB not named exchangeadmin.db | behavioural (skip without sqlite3) |
| AC6 | `deploy.ps1` calls `Resolve-ConfigDbPath` before `Backup-SqliteConfigDb` and for the post-deploy integrity check, and `Write-Fail`s when the key is set and the file is absent | source guard |
| AC6 | `deploy-pipeline.ps1` help/final text contains no "config" promotion claim | source guard (scd-4) |
| AC7 | `promote-dev-to-prod.ps1` contains no `Copy-SqliteConfigDb`, no `SkipConfigFragments`, no `Refresh`, and the rollback block has no `exchangeadmin.*.db` restore | source guard (replaces `:349,:372,:391,:406,:412`) |
| AC8 | `Move-ConfigDbToShared.ps1 -PlanOnly` prints all eight steps and changes nothing; refuses a UNC path; refuses when a DLL version is below `-MinimumAppVersion` | behavioural with fake publish dirs |
| AC9 | Installer with `-ConfigStorePath` writes the key, STILL ACLs `config\`, ACLs the shared directory too, and refuses when the shared file is absent; without it the generated appsettings has no `ConfigStore` node | source guard + generated-object check (scd-1, scd-4) |

Manual checks (owner-run, elevated), after S1-S5 are on BOTH instances:

1. `Move-ConfigDbToShared.ps1 -PlanOnly`: eight steps printed, nothing changed.
2. `-Apply`: both pools restart; both Serilog logs show "Config store schema ready at
   version 6" once each; both instances show the same Module Config values.
3. On dev, change a module setting; within 5 s prod's page shows it without restart.
   Add a protected principal on dev; on prod, a write against it is refused within 30 s
   (the TTL) and within 2 s of the next check.
4. Deploy a dev build (any): the backup lands from the shared path
   (`ConfigBackups\config.<ts>.bak\exchangeadmin.<ts>.db`), prod stays up and logs the
   "newer than this build" warning only if the build carried a migration.
5. Promote: no config copy line in the plan output; backup from the shared path.
6. Bulk jobs: submit a job on dev while prod is up; prod's startup on its next deploy
   does not interrupt it (per-instance jobs DB untouched).

Verification commands (from `.agents/repo-guidance.md`):

```
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
Invoke-ScriptAnalyzer -Path . -Recurse
Invoke-Pester tests/ps
git diff --check HEAD
```

Non-vacuity: revert the named target of each new test, confirm FAIL, restore, confirm
PASS.

## 9. Traceability check

Completed 2026-09-04. Every slice commit passed `dotnet build -c Release`, the FULL
`dotnet test ExchangeAdminWeb.slnx`, `dotnet format --verify-no-changes`, `git diff
--check` and the ASCII scan; S3 and S4 also passed `Invoke-ScriptAnalyzer -Path .
-Recurse` (0 errors; CI fails on errors only) and `Invoke-Pester tests/ps`.

**Final figures:** .NET suite 2320 passed / 0 failed / 3 skipped (2323 total) at S4
`8657b57` (S3 and S4 touched no C#; the S2 tree is byte-identical). Pester 125 passed / 0
failed. ScriptAnalyzer 0 errors. Mutation probes: S1 10, S2 12, S3 12, S4 11 = 45, each
failed its target test on the mutant and passed on restore.

| AC | Slice / commit | Tests (all mutation-probed) |
|---|---|---|
| AC1 path key, must-exist open | S1 `be507f0` | `ConfigStorePathTests`: `Resolve_Blank_UsesContentRootDefault` (x3 theory), `Resolve_Absolute_ReturnsConfigured`, `Resolve_Unc_Throws_NamingTheKey`, `Resolve_Relative_Throws` (x3), `Resolve_Configured_MustExist_DefaultMustNot`, `Factory_MustExist_MissingFile_Throws_AndCreatesNothing`, `Factory_MustExist_ExistingFile_Opens`, `Factory_Default_CreatesTheFile` |
| AC2 tolerant migrator, tables AND columns | S1 `be507f0` | `ConfigStoreMigratorTests`: `Migrate_DatabaseNewerThanBuild_IsAcceptedWhenTablesExist` (replaces `_FailsFast`), `_ThrowsWhenARequiredTableIsMissing`, `_ThrowsWhenARequiredColumnIsMissing`, `RequiredSchema_MatchesTheMigrationStatements` |
| AC3 additive-only tripwire | S1 `be507f0` | `MigrationsAreAdditiveOnly`, `MigrationsAreAdditiveOnly_Tripwire_CatchesADrop` (DROP, RENAME, DROP COLUMN on copies of the array) |
| AC12 base bump | S1 `be507f0` | csproj `2.18.0` -> `2.19.0` (VersionPrefix, AssemblyVersion, FileVersion) |
| AC4 change watcher in the four readers | S2 `5a9c832` | `ConfigChangeWatcherTests` (8: unthrottled CurrentToken, same/moved, throttles, store throws -> false + logs once, Unknown always changed, CurrentToken throws -> Unknown, CurrentToken refreshes the comparison, first check after a load reaches the store); per reader `ProtectedPrincipal_ReloadsWhenTokenMoves` / `_DetectsAChangeMadeBeforeItsFirstPoll`, `AttributeEditor_ReloadsWhenTokenMoves` / `_DetectsAChangeMadeBeforeItsFirstPoll` / `_LegendDetectsAChangeMadeBeforeItsFirstPoll`, `PermissionValidator_ReloadsWhenTokenMoves` / `_DetectsAChangeMadeBeforeItsFirstPoll`, `ExtendedLogService_MinimumLevel_FollowsAnOutOfBandWrite` / `_DetectsAChangeMadeBeforeItsFirstPoll` / `_OwnSetLevel_ThenForeignWrite_BothApply` |
| AC5 backup module path resolution | S3 `5776965` | `SqliteConfigBackup.Tests`: exports, pure ASCII, `Resolve-ConfigDbPath` context (set / absent / blank / no appsettings), `Test-IsSqliteConfigDbPresent -DbPath/-ConfigDir`, `Backup-SqliteConfigDb -DbPath` non-standard name, `Copy-SqliteDbFile` x4 (seed, replace + sidecars, absent source, -WhatIf) |
| AC6 deploy.ps1 + pipeline wording | S3 `5776965` | `DeployInvariants` deploy.ps1 row "backs up the config DB from the path appsettings resolves, and stops when a configured path is missing (scd-1)"; deploy-pipeline row "never claims that config was promoted (scd-4)" |
| AC7 promote loses the copy / -Refresh / DB rollback | S3 `5776965` | `DeployInvariants` promote rows: "never copies, replaces or merges the config database", "backs up the config DB prod opens (resolved via ConfigStore:Path) before the pool stops", "stops (Write-Fail) when ConfigStore:Path is set and the file is absent (scd-1)", "rollback restores binaries only and names the DB backup instead of restoring it", "keeps every remaining step behind the -Apply gate" |
| AC8 cutover script | S4 `8657b57` | `MoveConfigDbToShared.Tests` (9, behavioural in plan mode: all eight steps printed and nothing changed, plan by default, UNC, relative, below minimum, existing file, no dev DB, split state, PlanOnly+Apply); `DeployInvariants` static describe (10 rows) |
| AC9 installer -ConfigStorePath | S4 `8657b57` | `DeployInvariants` installer rows "with -ConfigStorePath: still ACLs config\, ACLs the shared directory too, and refuses a missing shared file (scd-1, scd-4)" and "generated appsettings carries ConfigStore:Path only when -ConfigStorePath is given (byte-identical otherwise)" (runs the real `New-AppSettingsObject`) |
| AC10 Pester replacements | S3 `5776965`, S4 `8657b57` | rows `:349,:361,:372,:382,:391,:406,:412` (pre-S3 numbering) replaced as above; the "grants the pool identity inheritable Modify on config/" row kept and re-asserted |
| AC11 docs | S5 (this commit) | Constitution Configuration bullets 1 and 7 and Deployment bullet 5; repo-guidance invariants 2 and 3; README "Shared config database" under Deploy to IIS, "Config Store" under Configuration Details, prerequisites bullet; `.agents/machines.md` shared path; this status; `.agents/state.md` |

**Deviations and implementation notes (nothing here changes an AC):**

1. A separate test-only commit `129d091` landed ahead of S1: `dotnet format
   --verify-no-changes` already failed on master (three xUnit2013 warnings in
   `GroupMemberNestingProtectionTests` from the 2026-09-03 GroupBulkActions slices). Same
   assertion, analyzer-clean form; nothing shipped changes.
2. `ConfigChangeWatcher` takes `ILogger?` (not `ILogger<ConfigChangeWatcher>`) and is created
   via `CreateChangeWatcher(logger)` on `ProtectedPrincipalRepository`,
   `AttributeEditorRepository`, `AppSettingRepository`, `ModuleConfigRepository` and
   `ModuleConfigService`, so no reader constructor changed (nine test files build them
   directly). An `internal Func<DateTime> UtcNow` is the throttle clock seam; each reader
   exposes `internal ChangeWatcher` for the tests.
3. `CurrentToken()` also refreshes the value `HasChangedSince` compares against (NOT the
   throttle timer). Without it a reader that reloaded after its own save would be told
   "changed" by an older throttled reading on every call for up to 2 s - for
   `PermissionValidator` that is an AD group expansion per call. Pinned by
   `CurrentToken_RefreshesWhatHasChangedSinceComparesAgainst`; the scd-2 property is
   pinned by `FirstCheckAfterALoad_ReachesTheStore`.
4. `ExtendedLogService.LoadLevel` no longer calls `SetLevel` (which persists): a write in
   the load path bumps the token and would make every reload trigger the next one. Own
   `SetLevel` re-reads after persisting so its own write is not counted as foreign.
5. Two existing `ADAttributeEditorServiceTests` (`InvalidateAllowlistCache_ForcesReload`,
   `IsAllowlistCorrupt_ValidThenCorruptedWithinTtl_...`) pinned "stale until the 30 s TTL";
   they now pin "stale inside the 2 s throttle, then reloaded / fail-closed (null)".
6. The promote rollback robocopy excludes `config\` as well as `logs` (binaries only, as
   AC7 says); the pre-promotion robocopy backup still includes `config\`.
7. `-DevAppPoolName` was removed from `promote-dev-to-prod.ps1` (only `-Refresh` used it).
8. `Move-ConfigDbToShared.ps1` defines its own Stop/Start pool helpers: `deploy.ps1` and
   `promote-dev-to-prod.ps1` each define theirs inline and neither exports, so there was
   nothing to reuse without a module refactor of two shipping scripts (out of slice scope).
9. The plan-by-default probe on the cutover script was run against the static guard only:
   the behavioural suite would have executed apply mode against the real IIS pools named by
   the defaults. All other cutover probes ran the behavioural suite.
10. FLAGGED, not changed: the decision entry (`.agents/decisions.md` 2026-09-04) says the
    other instance's change is "picked up on the next read (SQLite's `data_version`
    counter)". The implemented mechanism is the store's own change token, read at most once
    per 2 s per reader (section 1 of this plan explains why `data_version` does not fit a
    connection-per-operation store). Owner to amend the decision wording if wanted.
11. FLAGGED, not changed: the additive-only migration rule lives in the decision entry, the
    migrator comment, repo-guidance invariant 2 and the tripwire test - not in the
    Constitution, whose text this plan changed only where AC11 says.
12. Probe hygiene lesson recorded in the token log: restoring a probe backup with
    `Copy-Item` keeps the backup's timestamp, so the incremental build served the mutant
    once; the probe scripts now touch the restored file.

## 10. Review log

- 2026-09-04: openreview codex (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh,
  grade fallback; codex-cli 0.152.1, `codex exec -s read-only`) over
  `e36e798..8bb46a4`: verdict `acceptable_with_changes`, capability_ok true, both
  SHAs echoed. The reviewer's own approach was the plan's ("I would keep this
  approach, but harden the plan before implementation"; it would not replace it with
  a separate prod DB or merge-based promotion). Four material changes, four findings,
  all admitted and folded in:
  **scd-1 (HIGH)** - with the key set, a missing shared file would have opened a fresh
  empty database (`ReadWriteCreate`) that startup seeding then populates at defaults:
  the app serves with no protected principals and no section access, looking healthy,
  and the deploy backup only warns. Now: configured path => file must exist, opened
  without create, fatal otherwise; deploy and promote backups `Write-Fail` on an absent
  file when the key is set; only the cutover script and the no-key default create a DB.
  **scd-2 (HIGH)** - the watcher's "seed on first poll" would miss a change made
  between a cache load and that first poll, permanently for the log level. Now every
  reader records the token as of its own load (token read before the load), and a new
  per-reader test changes the DB before the first poll.
  **scd-3 (MEDIUM)** - the tolerant migrator checked table names only; v6 adds a
  column `SectionAccessRepository` reads and writes. Now `RequiredSchema` covers tables
  and columns via `PRAGMA table_info`, pinned to the steps by a test.
  **scd-4 (MEDIUM)** - the installer note read as replacing the local `config\` ACL,
  which would have left the per-instance jobs database unwritable on a fresh install;
  now local `config\` is always created and ACLed and the shared directory is ACLed in
  addition; `deploy-pipeline.ps1` messages stop claiming config was promoted.
  Records: `.agents/review/findings/scd-{1,2,3,4}.md`; envelope
  `.agents/review/scd.result.json` (gitignored scratch).
