# aud-a74: Five test classes call SqliteConnection.ClearAllPools, which disposes every other test's pooled connection mid-use and makes the suite flaky

**Severity**: MEDIUM - the gate's trustworthiness. `SqliteConnection.ClearAllPools()` is process-global:
with xUnit running classes in parallel, a call from one class disposes a pooled connection another class is
using, and that class's `SqliteConnectionFactory.Open()` fails with `ObjectDisposedException` ("Cannot
access a disposed object. Object name: 'SQLitePCL.sqlite3'") in a PRAGMA. Observed once on the merged tree
at `579c0875` (`ModuleConfigServiceTests.ACommittedWrite_WithALoggerThatThrows_IsReportedSaved_AndRaisesConfigSaved`
failed; the class passes alone). Callers: `ConfigBooleanTests` 42, `ConfigChangeWatcherTests` 240,
`ConfigDbCliTests` 178 and 228, `ConfigValueTests` 34, `EventLogDiagnosticsTests` 57. The per-database
`ClearPool(connection)` the other classes use is safe.
**Status**: Complete; merged `fc14800f`; codex test-batch-1 clean
**Branch**: `worktree-agent-ad56ea6ad87844ffa` (from `944b8c72`)
**Commit**: `c414a8c2` (fix, tests, record); plan `7e4c042e`; merged `fc14800f`
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

## Fix

- `ExchangeAdminWeb.Tests/ConfigBooleanTests.cs` `Dispose`: `SqliteConnection.ClearPool` over a connection to each of the
  test's two files, `_dir/exchangeadmin.db` and `_dir/usage.db`, in a loop (46), before the folder is deleted.
- `ExchangeAdminWeb.Tests/ConfigChangeWatcherTests.cs`, the `finally` of
  `AReader_GivenALoggerThatThrows_AndAnUnreadableToken_StillAnswers`: the same for `dir/exchangeadmin.db` and `dir/usage.db`
  (244).
- `ExchangeAdminWeb.Tests/ConfigDbCliTests.cs`, the two `ActiveJobs_` cases: in place, after the migration, the clear of the
  jobs file alone (181, 237).
- `ExchangeAdminWeb.Tests/ConfigValueTests.cs` `Dispose`: the clear of `_root/app/exchangeadmin.db` (36).
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs` `Dispose`: the clear of `_root/app/exchangeadmin.db` (60), after the
  extended log and the progress service are disposed, as before.
- `ExchangeAdminWeb.Tests/SharedTempDatabaseTripwireTests.cs`: new `NoTestSource_ClearsEveryConnectionPoolInTheProcess` (75),
  `\bClearAllPools\b` over every test source with comments blanked, the class itself the one file skipped; each hit is named
  "`<file>:<line>` clears every connection pool in the process". `ScannedSources` takes the files it skips as `params`; the
  two temp-directory scans pass `NameTheTempDirectory` (24), the two files they skipped before. The class summary and remarks
  name the new rule.

Each clear is the `AttributeEditorHarness` shape: a connection, never opened, over `Data Source=<file>;Mode=ReadWriteCreate`
- the string `SqliteConnectionFactory` builds - handed to `SqliteConnection.ClearPool`. Each carries a comment naming aud-a74
and why only the test's own pools. No departure from the plan. No caller-visible change: no app code changed; each test still
releases the files it made, and no longer anything another class holds.

## Guard proof

Driver: session scratchpad `a74-mutate.ps1`.
- For each mutant: the file must equal its staged version (`git diff --quiet`), its MD5 and the tripwire file's are taken,
  and one literal replacement must match exactly once.
- A Release build of the solution with 0 errors is required. The filter then runs with a TRX logger, and the failing tests
  are read from the TRX. The filtered runs get a temp directory of their own (TEMP and TMP), so the folders E3 counts are the
  driver's and no other session's.
- In a `finally`, the file is restored from the index (`git checkout --`), its mtime advanced so MSBuild rebuilds, and its
  MD5 re-checked. Then the restored tree is rebuilt (0 errors) and the filter must be green.

Filter: `FullyQualifiedName~SharedTempDatabaseTripwireTests` (4 tests); E3 `FullyQualifiedName~ConfigBooleanTests` (20). The
tripwire file, `SharedTempDatabaseTripwireTests.cs`, was `85EA939EC9BD13646B6C8F23DB8C7825` before and after every row.

| # | Mutation | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|---|
| M1 | at the call site: `ConfigBooleanTests.Dispose`'s clear back to `SqliteConnection.ClearAllPools();` | `ConfigBooleanTests.cs`, `9F81EED0F2F4916C30C70C5CFD4D06C4` | `4B9A5FB30CA9E584D43CF412BF6401C2` | 1 of 4: `NoTestSource_ClearsEveryConnectionPoolInTheProcess` - "ConfigBooleanTests.cs:44 clears every connection pool in the process" |
| E1 | extra: `ConfigValueTests.Dispose`'s clear as a method group, `Action clearAll = SqliteConnection.ClearAllPools; clearAll();` | `ConfigValueTests.cs`, `1AD3382F310A8692AED3D58EE7F4B1B9` | `C6241F9C138361775FD1FA0FA33FB0D9` | 1 of 4: the same case - "ConfigValueTests.cs:36 clears every connection pool in the process" |
| E2 | extra: in `TestConfigStore.cs`, which the temp-directory scans skip, an unused `internal static void ClearEveryPool() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();` | `TestConfigStore.cs`, `2865DC20F3BBFA838F0253CB1655528C` | `5D94F70B27DF286341F861306D15C274` | 1 of 4: the same case - "TestConfigStore.cs:34 clears every connection pool in the process" |
| E3 | a check, not a test: `ConfigBooleanTests.Dispose`'s clear without `Mode` in its string, a key that misses the factory's pool | `ConfigBooleanTests.cs`, `9F81EED0F2F4916C30C70C5CFD4D06C4` | `2845BC647A2E0CA6E6C299FAC0407049` | none, as expected (20/20; no test watches the folder). The one case that builds the Home page, `TheHomePage_GivenANotificationSwitchThatIsNotABoolean_Renders_WithoutClaimingNotifications`, left its `eaw-a58-` folder behind: 1 folder. The restored tree's run left 0. |

Every mutant built with 0 errors and each restore matched its original MD5. After each row the restored tree rebuilt with 0
errors and its filter was green: 4/4 (E3: 20/20, no folder left). E3 shows the folder count sees a clear that misses its
file; on the fixed bytes, the five classes and the tripwire run alone (Release, the shared temp directory) passed 123/123,
and no `eaw-a58-`, `eaw-a45-`, `eaw-a62-` or `eaw-a46-` folder, and no `ExchangeAdminWebTests` folder, created during that
run was left after it.

The flake is not reproduced: it needs another class opening a connection at the instant of a call, and was seen once. The
tripwire is the guard; the three full-suite runs below are consistent with the fix, not a proof that the flake is gone.

## Known gaps

- The scan sees the name `ClearAllPools` in the test project's code - a call, a call through `using static`, a method group, a
  string handed to reflection. A name built from pieces at run time is not seen. The tripwire class itself, whose pattern
  names it, is not scanned.
- A per-file clear is safe because no other test uses that file: each of these tests makes its databases under a folder of its
  own (a GUID name, the aud-a26q rule). Nothing checks that a cleared path is the clearing test's own.
- `Comms10kOwnedJobTests`'s fixture `Dispose` (260) clears `Data Source=<jobs.db>` with no `Mode`, a key that misses its
  factory's pool (the E3 shape). Run alone in a temp directory of its own, the class passed 23/23 and left 23 folders, one per
  case, each holding its `jobs.db`. Per-file, so it reaches no other class: not this finding; listed in the hand-back as a
  candidate (LOW).

## Gates - 2026-10-23 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors, 19 warnings.
  - The baseline: a Release build at `944b8c72` before any change, 19 warnings and 0 errors.
  - CS8604, CS8602, CS0649 and CS0414 in `AccountLockoutRemediationService.cs`, `GroupManagement.razor`,
    `SelfServiceGroups.razor`, `MigrationReportExportProcessor.cs`, `HeaderAnalysisService.cs`, `RiskyUsers.razor`,
    `MessageTrace.razor` and `Comms10kApprovedSetTests.cs`; none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug), three times in a row on the staged fix:
  - run 1: exit 0, 6,647 passed / 0 skipped / 0 failed, 6,647 total, 3m50s;
  - run 2: exit 0, 6,647 / 0 / 0, 4m19s;
  - run 3: exit 0, 6,647 / 0 / 0, 3m37s.
  - No hang: each run's blame collector wrote "All tests finished running, Sequence file will not be generated". The one new
    case is this commit's; the base was not run on its own, 6,646 expected at `944b8c72`.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. The plan commit, the record alone, had `git diff --check`.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell changed.

No AD, EXO, Graph, SMTP, ServiceNow, Delinea, push or deployment: real SQLite stores over temp folders only. No coder
self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-ad56ea6ad87844ffa`, not merged, not pushed. Test project only, so no version bump is due at
merge.
