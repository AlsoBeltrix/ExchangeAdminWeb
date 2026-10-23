# aud-a74: Five test classes call SqliteConnection.ClearAllPools, which disposes every other test's pooled connection mid-use and makes the suite flaky

**Severity**: MEDIUM - the gate's trustworthiness. `SqliteConnection.ClearAllPools()` is process-global:
with xUnit running classes in parallel, a call from one class disposes a pooled connection another class is
using, and that class's `SqliteConnectionFactory.Open()` fails with `ObjectDisposedException` ("Cannot
access a disposed object. Object name: 'SQLitePCL.sqlite3'") in a PRAGMA. Observed once on the merged tree
at `579c0875` (`ModuleConfigServiceTests.ACommittedWrite_WithALoggerThatThrows_IsReportedSaved_AndRaisesConfigSaved`
failed; the class passes alone). Callers: `ConfigBooleanTests` 42, `ConfigChangeWatcherTests` 240,
`ConfigDbCliTests` 178 and 228, `ConfigValueTests` 34, `EventLogDiagnosticsTests` 57. The per-database
`ClearPool(connection)` the other classes use is safe.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); not started
**Branch**: -
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

## Guard proof

Pending.
