# aud-a64: Remaining unguarded logger calls and start-up parses - the event log's LogUndoNotRun, ShowUndoPreview and LoadUsage catches, the config store's migrations, the writers' constructor folder creation, and the telemetry report flag that never resets

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) `AdminEventLog.razor`
`LogUndoNotRun`'s catch logs unguarded though the method is documented "Never throws"; (2) the
`ShowUndoPreview` catch logs unguarded before the failure preview is assigned; (3) the `LoadUsage` catch
logs unguarded before `usageError` is set, so the Usage view shows empty tables with no error and the
exception escapes. (4) `ModuleConfigService.MigrateGraphSecretKeys` (called from `Program.cs`) and
`ImportLegacyConfig` (run by the constructor) log unguarded, so a throwing provider fails start-up or the
singleton's construction. (5) `JsonlLogService` and `ExtendedLogService` constructors still create the log
folder unguarded; now that every write creates it, a log root unavailable at start-up breaks construction
instead of each write being refused and reported. (6) `UsageTelemetryService.LogSwitchUnreadableOnce`'s
flag never resets, so a failure after a recovery is never reported.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a56/a57/a58 agent, 2026-10-23, candidates 2, 3, 4, 6, 10 and 11 of its hand-back)

## Evidence

At `3da510cb`: `Components/Pages/AdminEventLog.razor` 1263, 1050, 1644; `Services/ModuleConfigService.cs`
271, 276, 335, 339, 354, 358, 364; `Services/JsonlLogService.cs` 36; `Services/ExtendedLogService.cs` 69;
`Services/UsageTelemetryService.cs` `LogSwitchUnreadableOnce`.

## Approach (to be written by the implementing agent before code)

- Every listed logger call goes through the guarded helper of its class (aud-a28z/a57 shapes); the page
  catches set their error state before reporting.
- The two constructors stop creating the folder (the appender does, per write); the migrations report
  through the guard.
- The telemetry report flag resets when a read succeeds (one report per failure streak).
- Mutations at the call site: one guard removed per class.

### Implementation notes (fix/a64-a67, 2026-10-23), at `eb017102`

- (1) `Components/Pages/AdminEventLog.razor` 1290: `LogUndoNotRun`'s catch calls `Logger.LogError(...)` with no guard,
  though the method is documented "Never throws". Its callers are `ExecuteUndo`'s no-handler exit (1167) and its catch
  (1241), so a provider that threw there replaced the no-handler refusal with "Unexpected error", tried the record a
  second time, and took the provider's exception out of the handler. The report goes through the page's existing
  `ReportSafely` (777, aud-a57).
- (2) `AdminEventLog.razor` 1072-1077: `ShowUndoPreview`'s catch reports first (1074, unguarded), then assigns the failure
  preview. The preview is assigned first; the report goes through `ReportSafely` last.
- (3) `AdminEventLog.razor` 1666-1673: `LoadUsage`'s catch clears the tables, reports (1671, unguarded), then sets
  `usageError` (1672), so a provider that threw left the Usage view's empty tables with no error and the exception
  escaped. `usageError` is set first; the report goes through `ReportSafely` last.
- Tests: `EventLogUndoRefusalTests` +2 (an undo no module claims, with no audit writer and a logger that throws; a
  preview that fails, with a logger that throws); `EventLogDiagnosticsTests` +1 (a usage read that fails, with a logger
  that throws). Mutations: each of the three call sites back as it was at `eb017102`.
- Module version: the Admin Event Log's (its page). ClickGateRegistry has the page NotYetConverted; no ProgressRegistry
  pin moves.

## Files changed

- (1)-(3): `Components/Pages/AdminEventLog.razor`; `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`,
  `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`.

## Guard proof

Driver (session scratchpad `a64a67-agent/mutate.sh`, never committed): the file's MD5; one exact-text replacement that
must match once and must change the bytes; a Debug build of the test project, 0 errors required; the filter; the exact
bytes restored with a fresh mtime and the MD5 re-checked; a rebuild; the filter again, green.

(1)-(3). Filter `FullyQualifiedName~EventLog|FullyQualifiedName~ProgressRegistryTests|FullyQualifiedName~ClickGate|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~UndoNotificationTests`
(413 tests). Each mutant puts one call site back as it was at `eb017102`, so each failure is also the defect's
reproduction. `AdminEventLog.razor` MD5 `b34a30377865cf9c6d7b53ac0b79c833` before and after every row; 413/413 green
after each restore.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| M1 `LogUndoNotRun`'s catch unguarded again | `59902609779aa1b3c9810ec465751e81` | 1 of 413: `EventLogUndoRefusalTests.AnUndoNoModuleClaims_WhoseRecordCannotBeWritten_WithALoggerThatThrows_IsRefused_AndNothingEscapes` - `System.InvalidOperationException: the logging provider failed` escaped `ExecuteUndo` |
| M2 `ShowUndoPreview`'s catch reports first and unguarded again | `41e8730a48abfab91c28de49f44f49dd` | 1 of 413: `EventLogUndoRefusalTests.APreviewThatFails_WithALoggerThatThrows_ShowsItsFailure_AndNothingEscapes` - the provider's exception escaped the handler |
| M3 `LoadUsage`'s catch reports unguarded before `usageError` again | `c35069baa199107a2e576750f46f43e7` | 1 of 413: `EventLogDiagnosticsTests.AUsageReadThatFails_WithALoggerThatThrows_SaysSo_AndDoesNotEndTheLoad` - `System.InvalidOperationException: the logging provider failed` escaped `LoadUsage` |

After the round the preview test's page attach became an `await` (xUnit1031 under `dotnet format`); M2 was run again on
those bytes (`EventLogUndoRefusalTests.cs` MD5 `64f669cff3d46a5e251405527e20cedf`): the same single failure, and 413/413
after the restore.

## Also held (aud-a62/a63/a65 agent, 2026-10-23) - LOW

(7) `Services/Jobs/BulkJobService.cs` 66 and 68: a finite but out-of-range `BulkJobs:RetentionDays` or
`BulkJobs:StaleHeartbeatMinutes` still throws `OverflowException` out of `TimeSpan.FromDays`/`FromMinutes`
in the constructor, so the app fails to start. (8) A negative `BulkJobs:RetentionDays` puts the start-up
prune cutoff in the future (`InitializeAsync` 85; `BulkJobRepository.PruneFinishedBefore` 281-300), deleting
every finished job record at start-up (the audit rows survive). (9) A negative `BulkJobs:RecentJobLimit`
reaches SQLite as a negative `LIMIT`, which means no limit (`BulkJobRepository.GetRecentFinished` 439-450).
Fix shape: range-check the three settings in `ConfigValue` (a minimum and maximum per setting) with the
default and the once-per-value warning.
