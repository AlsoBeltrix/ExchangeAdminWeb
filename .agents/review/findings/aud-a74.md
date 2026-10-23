# aud-a74: Five test classes call SqliteConnection.ClearAllPools, which disposes every other test's pooled connection mid-use and makes the suite flaky

**Severity**: MEDIUM - the gate's trustworthiness. `SqliteConnection.ClearAllPools()` is process-global:
with xUnit running classes in parallel, a call from one class disposes a pooled connection another class is
using, and that class's `SqliteConnectionFactory.Open()` fails with `ObjectDisposedException` ("Cannot
access a disposed object. Object name: 'SQLitePCL.sqlite3'") in a PRAGMA. Observed once on the merged tree
at `579c0875` (`ModuleConfigServiceTests.ACommittedWrite_WithALoggerThatThrows_IsReportedSaved_AndRaisesConfigSaved`
failed; the class passes alone). Callers: `ConfigBooleanTests` 42, `ConfigChangeWatcherTests` 240,
`ConfigDbCliTests` 178 and 228, `ConfigValueTests` 34, `EventLogDiagnosticsTests` 57. The per-database
`ClearPool(connection)` the other classes use is safe.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); in progress
**Branch**: `worktree-agent-ad56ea6ad87844ffa` (from `944b8c72`)
**Commit**: -
**Reviewer**: supervisor (observed on the merged-tree gate, 2026-10-23)

## Evidence

At `e2a1574c`: `git grep -n "ClearAllPools" ExchangeAdminWeb.Tests/`; the suite log of the `579c0875` run
(one failure, the stack through `SqliteConnectionFactory.Open` line 99 and `ConfigStoreMigrator.Migrate`);
the same class green when run alone.

## Approach (to be written by the implementing agent before code)

- Every `ClearAllPools()` in the test project becomes `ClearPool(<a connection to that test's own database>)`
  for each database the test created (the `AttributeEditorHarness` shape), in the test's own disposal.
- A source tripwire in `SharedTempDatabaseTripwireTests` (aud-a26q) refuses `ClearAllPools` anywhere in the
  test project.
- Mutation at the call site: one `ClearAllPools` restored - the tripwire fails. The flake itself cannot be
  forced deterministically; the tripwire is the guard.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping
rule (MEDIUM). Branch `worktree-agent-ad56ea6ad87844ffa` from master `944b8c72`, where the finding was admitted. This commit is
the record alone; one fix commit `fix(tests): ... (aud-a74)` follows, carrying the tests and this record's Fix, Guard proof
and Gates. Test project only: no page, service, module or script changes, so no version bump is due.

Evidence re-read at `944b8c72` (its line numbers):
- `git grep -n ClearAllPools ExchangeAdminWeb.Tests/` finds the six calls the record names and no other. No production
  source calls `ClearAllPools` or `ClearPool`.
- xUnit v3 runs the test classes in parallel: there is no assembly-level collection behaviour and no `xunit.runner.json`;
  only `LiveDirectoryCollection` (23) turns parallelism off, for its own classes. So one class's `ClearAllPools` reaches the
  pooled connections of whichever classes run beside it.
- The pool is keyed by the connection string. `SqliteConnectionFactory`'s constructor (52-56) builds
  `Data Source=<path>;Mode=ReadWriteCreate` for every database a test makes (`mustExist` false); the busy wait is set on each
  connection, not in the string, so code clearing a file's pool can rebuild it (aud-a41, the `Open` remarks). A connection
  over that string, never opened, handed to `SqliteConnection.ClearPool`, clears that file's pool and no other's. That is the
  `AttributeEditorHarness.Dispose` shape (271-275), also in `PermissionValidatorTests.Dispose` (28),
  `ExoConnectionPoolConfigReadTests.Dispose` (68), `SettingsPageHarness.Dispose` (487) and, one file at a time in a loop,
  `Comms10kInterruptionTests.Dispose` (340-348).
  `SqliteBusyWaitTests.AFactorysPooledConnections_AreReleasedByClearingThePoolOfThePlainConnectionString` (75-92) pins that
  this clear releases the factory's pooled handle: the file is deleted after it.
- What each caller made - the databases it must clear:
  1. `ConfigBooleanTests.Dispose` (40-45, the call 42): `HomePageWith` (231-250) builds the module-config and enablement
     stores over `_dir/exchangeadmin.db` (237, 239, through `TestConfigStore.Create`) and the usage repository over
     `_dir/usage.db` (240; the repository opens it lazily, so its pool may be empty). The other cases make no database.
  2. `ConfigChangeWatcherTests.AReader_GivenALoggerThatThrows_AndAnUnreadableToken_StillAnswers`, its `finally` (238-243, the
     call 240): `dir/exchangeadmin.db` (221) and `dir/usage.db` (228).
  3. `ConfigDbCliTests.ActiveJobs_ReturnsRunningAndQueuedOnlyInSubmissionOrderAsFivePipeColumns` (177-178) and
     `ActiveJobs_WithNoActiveJobs_PrintsNothingAndExitsZero` (227-228): mid-test, not in a disposal. The call closes the pooled
     connection `JobStoreMigrator.Migrate` left on the temp folder's `exchangeadmin-jobs.db`, before the test writes the file
     unpooled (`ConfigDbTestFiles.Open`, `Pooling = false`) and the CLI reads it (`ConfigDbMaintenance`, unpooled). Every
     other connection in the class is unpooled, so after the call nothing of the test's is pooled, and `TempDir` deletes the
     folder.
  4. `ConfigValueTests.Dispose` (32-37, the call 34): one case makes a database,
     `TheExtendedLog_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults`: `_root/app/exchangeadmin.db` (220, 231).
  5. `EventLogDiagnosticsTests.Dispose` (53-60, the call 57): `_root/app/exchangeadmin.db` (39, 49), after the extended log
     that reads it is disposed (55).

1. **Only a test's own pools.** Each `ClearAllPools()` becomes `SqliteConnection.ClearPool` over a connection to each
   database that test made, in the `AttributeEditorHarness` shape (`DataSource` the file, `Mode =
   SqliteOpenMode.ReadWriteCreate`), where the call stands now: in the four disposals - one clear per file, in a loop, where
   the test made two (`ConfigBooleanTests`, `ConfigChangeWatcherTests`) - and in place in the two `ConfigDbCliTests` cases, on
   the jobs file. Moving those two into a disposal would leave the migration's connection open while the test and the CLI
   use the file, a change to what those tests run; in place they release exactly the file they needed released. Clearing a
   pool that holds nothing (a case that made no database, a usage file never opened) does nothing.
2. **Tripwire.** `SharedTempDatabaseTripwireTests` gains `NoTestSource_ClearsEveryConnectionPoolInTheProcess`: no test source,
   comments blanked, names `ClearAllPools` (`\bClearAllPools\b` - a call, a call through `using static`, a method group, a
   name handed to reflection). Every `.cs` in the test project is scanned, `TestConfigStore.cs` included; only the tripwire
   class, whose pattern names it, is exempt. The source walk takes its exempt files as an argument; the two temp-directory
   scans keep the two they have.

Tests: the tripwire case. The flake itself cannot be forced deterministically - it needs another class opening a connection
at the instant of the call - so the tripwire is the guard. Three consecutive full-suite runs green show that no test relied on
a clear reaching past its own files; they cannot show the flake gone, as it was seen once. A check, not a test: the five
classes run alone leave none of their folders behind (`eaw-a58-`, `eaw-a45-`, `eaw-a62-`, `eaw-a46-` created during the run
and still there after it), so each clear releases the files it is for. The code comments are written with the fix, before
the round, so the MD5s are the fix commit's bytes.

Mutations (MD5 of the mutated file and of the tripwire file before; the fix staged; one literal edit; a Release build with 0
errors; `SharedTempDatabaseTripwireTests` run; restored from the index, touched, rebuilt, MD5 equal; green):
- M1, at the call site: `ConfigBooleanTests.Dispose`'s clear back to `SqliteConnection.ClearAllPools();`.
- Extra E1: `ConfigValueTests.Dispose`'s clear as a method group, `Action clearAll = SqliteConnection.ClearAllPools;
  clearAll();` - the scan is not tied to the call's syntax.
- Extra E2: in `TestConfigStore.cs`, exempt from the temp-directory scans, an unused
  `internal static void ClearEveryPool() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();` - the new scan does not
  exempt it.

Files: `ExchangeAdminWeb.Tests/ConfigBooleanTests.cs`, `ConfigChangeWatcherTests.cs`, `ConfigDbCliTests.cs`,
`ConfigValueTests.cs`, `EventLogDiagnosticsTests.cs` and `SharedTempDatabaseTripwireTests.cs`; this record. No file another
agent owns.

## Guard proof

Pending.
