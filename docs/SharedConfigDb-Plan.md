# One shared config database for dev and prod

Status: Draft 2026-09-04, awaiting codex openreview, then an owner go per slice. The
governing decision is RULED (`.agents/decisions.md` 2026-09-04, "Dev and prod share ONE
config database"); no owner decision is outstanding.
Owner: Michael
Last verified against code: `e36e798` / 2026-09-04
Versions: base app bump in S1 (shared infrastructure: startup path resolution,
migrator, cache readers). The bump is "next minor above whatever `<VersionPrefix>`
reads when S1 lands" - `docs/UsageTelemetry-Plan.md` also claims a base bump and the
two plans are independent, so whichever lands first takes `2.19.0`. No module bumps.
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
  (`\\`) or a relative path is refused at startup with a fatal log naming the key. The
  jobs DB path derivation is untouched.
- AC2: `ConfigStoreMigrator.Migrate()` treats `user_version > TargetVersion` as
  acceptable: it logs a warning naming both versions, verifies every table this build's
  steps create exists (`sqlite_master` check against a static list derived from the
  steps), and returns the database's version without writing. A missing required table
  still throws (fail fast). `user_version < TargetVersion` migrates as today.
- AC3: A tripwire test fails if any element of `Migrations` contains `DROP `, `RENAME`,
  or `ALTER TABLE ... DROP COLUMN` (case-insensitive), with a message pointing at the
  decision's additive-only rule.
- AC4: The four caching readers re-read when the change token moves: `ProtectedPrincipalService`
  (30 s TTL cache, `:110-123`), `ADAttributeEditorService` (allowlist and legend caches,
  `:59-63`), `PermissionValidator` (30-min cache, `:19-22`), `ExtendedLogService`
  (`_minimumLevel` loaded once, `:27,:52`). Each keeps its cache hit path but first asks
  a shared `ConfigChangeWatcher.HasChangedSince(ref lastSeenToken)`, which reads the
  token at most once per 2 seconds per watcher (throttle) and returns true when the
  stored token differs from the last seen. A token read failure returns false and logs
  once (stale-but-serving beats a hot loop of failures; the TTLs still bound staleness).
- AC5: `tools/SqliteConfigBackup.psm1` gains `Resolve-ConfigDbPath -PublishPath` which
  reads `<PublishPath>\appsettings.json` `ConfigStore:Path` when present, else returns
  `<PublishPath>\config\exchangeadmin.db`; every existing function gains an optional
  `-DbPath` that overrides the `<ConfigDir>\exchangeadmin.db` derivation. Pure ASCII
  (invariant 6 - `deploy.ps1` imports this module under Windows PowerShell 5.1).
- AC6: `deploy.ps1` backs up and integrity-checks the resolved path (AC5), not the
  publish folder's `config\`. The robocopy exclusions are unchanged. `deploy-pipeline.ps1`
  is unchanged except passing nothing new.
- AC7: `tools/promote-dev-to-prod.ps1`: the config-copy block (`:400-427`), the
  `-SkipConfigFragments` and `-Refresh` parameters and the `-Refresh` branch (`:274-324`)
  are removed; the pre-promotion verified backup is taken from the resolved path; the
  rollback restores binaries only and prints where the DB backup is. `-PlanOnly`
  behaviour is preserved for every remaining step.
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
  it writes the key into the generated appsettings and ACLs that directory for the app
  pool identity instead of (in addition to) `config\`; when absent, behaviour is
  byte-identical to today.
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
| Shared file unreachable at startup (ACL, missing dir) | `SqliteConnectionFactory` creates the directory; an open failure surfaces from `Migrate()` as today | App does not start; log names the path | Unchanged |
| DB newer than the build (dev migrated first) | Accepted with a warning; required-table check passes | Nothing | Prod serves on the newer schema |
| DB newer AND a required table missing (a non-additive step slipped through) | `Migrate()` throws | App does not start | The tripwire (AC3) exists to make this unreachable |
| Both instances start at once on an older DB | Each step runs in its own transaction; the second process's `PRAGMA user_version` read sees the first's commit or waits on busy_timeout; a step that already ran is skipped by the version loop | Nothing | One migration |
| Two processes write the same row | SQLite serialises; last commit wins; token bumps twice | Nothing | Last write wins - accepted (same as two operators on one instance today) |
| Token read fails inside a cache check | Returns "unchanged", logs once | Nothing | Cache serves until its TTL as today |
| Change made on dev while prod caches | Prod's next cache check (at most 2 s later) sees the token and reloads | Prod reflects it within seconds | - |
| Backup (deploy or promote) cannot find the DB at the resolved path | `Backup-SqliteConfigDb` returns null and the caller warns, as today for a first install | Warning | Deploy proceeds (no DB to protect) - unchanged posture |
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
contentRoot)` returns the default when `configured` is blank; throws
`InvalidOperationException` naming `ConfigStore:Path` when it is relative or starts
with `\\`; else returns it. Tested.

`ConfigStoreMigrator.Migrate()`:

```
if (current > Migrations.Length)
{
    // Shared-DB rule (decisions 2026-09-04): the other instance is newer and steps are
    // additive-only, so serve on its schema. Verify this build's tables exist first.
    var missing = RequiredTables.Where(t => !TableExists(connection, t)).ToList();
    if (missing.Count > 0)
        throw new InvalidOperationException($"Config database schema version {current} is newer than this build ({Migrations.Length}) and lacks tables this build requires: {string.Join(", ", missing)}.");
    _logger?.LogWarning("Config database schema version {Db} is newer than this build supports ({Build}); serving on the newer schema (additive-only rule)", current, Migrations.Length);
    return current;
}
```

`RequiredTables` is a static `string[]` of the table names v1-v6 create (derived by
reading the steps at implementation and pinned by a test that regexes `CREATE TABLE`
names out of `Migrations` and compares). The migrator gains an optional
`ILogger<ConfigStoreMigrator>?` constructor parameter (null in tests).

Tripwire `MigrationsAreAdditiveOnly` (AC3) reads `ConfigStoreMigrator.Migrations` via
an `internal static` accessor.

### S2 - `ConfigChangeWatcher`

```
public sealed class ConfigChangeWatcher(IConfigStore store, ILogger<ConfigChangeWatcher> log)
{
    public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(2);
    // Returns true when the stored token differs from lastSeen (and updates it).
    // Reads the DB at most once per Throttle per instance; a failed read returns false
    // and logs once until the next success.
    public bool HasChangedSince(ref long lastSeen);
}
```

One instance per caching reader (constructed from the injected `IConfigStore`; the
readers already take the store or a repository - add the watcher where the store is
already in reach, or inject `ConfigChangeWatcherFactory`; decide at implementation,
record in the slice). Each cache-hit path becomes: `if (watcher.HasChangedSince(ref
_token)) invalidate; then the existing TTL check`. `ExtendedLogService.MinimumLevel`
getter checks the watcher and reloads `_minimumLevel` when it moved (the 2 s throttle
keeps the per-log-line cost to a field compare). `IConfigStore.cs:15-18` and
`ConfigChangeToken.cs:13-15` comments updated: the token is now consulted.

### S3 - deploy scripts

`SqliteConfigBackup.psm1`: `Resolve-ConfigDbPath -PublishPath` (reads appsettings.json
with `ConvertFrom-Json`; PS 5.1-safe; returns the default when the key is absent or the
file is missing); `-DbPath` optional on `Test-IsSqliteConfigDbPresent`,
`Backup-SqliteConfigDb`, and a new `Copy-SqliteDbFile -SourceDbPath -DestDbPath`
(the `Copy-SqliteConfigDb` body generalised to file paths; the old function is deleted
with its only caller). `deploy.ps1:436-442, :607-616` use the resolved path.
`promote-dev-to-prod.ps1`: delete `:14-15` params, `:274-324`, `:400-427`; backup `:373-376`
from the resolved prod path; rollback `:445-455` replaced by a `Write-Warn` naming the
backup file. Every remaining step keeps `Invoke-PlanOrAction` / `Write-Plan`.

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

`ConfigStoreMigratorTests.cs` (S1, real temp SQLite):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC2 | `Migrate_DatabaseNewerThanBuild_IsAcceptedWhenTablesExist` (replaces `_FailsFast`) | Fresh DB migrated to target, then `user_version = target + 5`: `Migrate()` returns target + 5, no throw, tables intact | Restore the throw; FAIL |
| AC2 | `Migrate_DatabaseNewerThanBuild_ThrowsWhenARequiredTableIsMissing` | Same, then `DROP TABLE app_setting`: throws naming `app_setting` | Drop the check; FAIL |
| AC2 | `RequiredTables_MatchTheCreateTableStatements` | Regex over `Migrations` equals `RequiredTables` | Remove one; FAIL |
| AC3 | `MigrationsAreAdditiveOnly` | No step contains DROP / RENAME / DROP COLUMN | Append a `DROP TABLE x;` step in a test copy; FAIL (the test takes the array as input) |

`ExchangeAdminWeb.Tests/ConfigChangeWatcherTests.cs` (S2, fake store):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC4 | `HasChangedSince_FirstCall_SeedsAndReturnsFalse` | First call records the token, returns false | Return true; FAIL |
| AC4 | `HasChangedSince_TokenMoved_ReturnsTrueOnce` | Bump -> true, then false | Never true; FAIL |
| AC4 | `HasChangedSince_Throttles` | Two calls within 2 s hit the store once (fake counts) | Remove throttle; FAIL |
| AC4 | `HasChangedSince_StoreThrows_ReturnsFalseAndLogsOnce` | Throwing store -> false twice, one log | Rethrow; FAIL |
| AC4 | per reader: `<Reader>_ReloadsWhenTokenMoves` (existing seamed harnesses) | Cached value replaced after a token bump within TTL | Skip the watcher; FAIL |

Pester (S3, S4) in `tests/ps/`:

| AC | Test | What it proves |
|---|---|---|
| AC5 | `Resolve-ConfigDbPath` returns the key's value, else the default; missing appsettings -> default | behavioural, temp dirs |
| AC5 | `Backup-SqliteConfigDb -DbPath` backs up a DB not named exchangeadmin.db | behavioural (skip without sqlite3) |
| AC6 | `deploy.ps1` calls `Resolve-ConfigDbPath` before `Backup-SqliteConfigDb` and for the post-deploy integrity check | source guard |
| AC7 | `promote-dev-to-prod.ps1` contains no `Copy-SqliteConfigDb`, no `SkipConfigFragments`, no `Refresh`, and the rollback block has no `exchangeadmin.*.db` restore | source guard (replaces `:349,:372,:391,:406,:412`) |
| AC8 | `Move-ConfigDbToShared.ps1 -PlanOnly` prints all eight steps and changes nothing; refuses a UNC path; refuses when a DLL version is below `-MinimumAppVersion` | behavioural with fake publish dirs |
| AC9 | Installer with `-ConfigStorePath` writes the key and ACLs the directory; without it the generated appsettings has no `ConfigStore` node | source guard + generated-object check |

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

Completed in S5.

## 10. Review log

(Filled by the openreview dispatch.)
