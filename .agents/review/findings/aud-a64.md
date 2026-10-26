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
- (4) `Services/ModuleConfigService.cs`, at `0b22d0e3`:
  - `MigrateGraphSecretKeys` (242-279), called once by `Program.cs` 434 at start-up outside any try: the remap's catch
    (271) and the summary (276) call `_logger` with no guard, so a provider that threw stopped the app starting. Both go
    through the class's `ReportSafely` (308, aud-a63).
  - `ImportLegacyConfig` (324-381), run by the constructor (21): the per-file catch (350), the single-file catch (373)
    and the outer catch (379) the same - a provider that threw at 350 or 373 reached the outer catch, threw again there,
    and the singleton could not be built.
  - 354 and 369 hand `_logger` to `LegacyConfigImport.ArchiveFile` (`Services/Storage/LegacyConfigImport.cs` 18-32),
    documented "Never throws on a failed archive", whose catch logs with no guard (30). The guard goes there, inside
    the helper, so its other callers' imports get the documented contract too (ModuleAdminService,
    ModuleEnablementService, ProtectedPrincipalService, SectionAccessService, ADAttributeEditorService, ExtendedLogService).
  - Tests: `ModuleConfigServiceTests` - the remap with a logger that throws (a remap that writes, one whose write fails);
    construction with a logger that throws (an unparseable per-module file, an unparseable single file, a legacy file
    whose archive fails). Mutations: the remap's catch unguarded again; the per-file catch unguarded again; the helper's
    guard removed; extras for the summary and the single-file catch. App-wide (the shared config store): no module bump.

## Files changed

- (1)-(3): `Components/Pages/AdminEventLog.razor`; `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`,
  `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`.
- (4): `Services/ModuleConfigService.cs`, `Services/Storage/LegacyConfigImport.cs`;
  `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`.

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

(4). Filter `FullyQualifiedName~ModuleConfigServiceTests|FullyQualifiedName~GraphSecretKeyMigrationTests|FullyQualifiedName~ModuleAdminService|FullyQualifiedName~ModuleEnablementService|FullyQualifiedName~SectionAccessService|FullyQualifiedName~ExtendedLogServiceTests`
(130 tests; the legacy importers' suites beside the config store's). `ModuleConfigService.cs` MD5
`5d4a592a855522d5e99fcae7b58bbb3e`, `LegacyConfigImport.cs` `61b5f720bf696b97e23ccfd98eb1e876`, each matched after its
rows; 130/130 green after every restore. M4a, M4b and M4c together are the code as it was at `0b22d0e3`.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| M4a `MigrateGraphSecretKeys`' catch unguarded again | `3a4a4fb05c020bda61cc98ae16f63e12` | 1 of 130: `TheGraphSecretKeyRemap_WithALoggerThatThrows_Returns(cause: "a remap whose write fails")` - `System.InvalidOperationException: the logging provider failed` out of the remap |
| M4b `ImportLegacyConfig`'s three reports unguarded again | `66decf1a8c4bb0176f0713812afeafee` | 2 of 130: `Construction_WithALoggerThatThrows_IsBuilt_AndImportsWhatItCan`, "an unparseable per-module file" and "an unparseable single file" - the provider's exception out of the constructor |
| M4c `LegacyConfigImport.ArchiveFile`'s guard removed | `816630970ca561a7e17a7a8811e409c2` | `Construction_WithALoggerThatThrows_IsBuilt_AndImportsWhatItCan(cause: "a legacy file whose archive fails")` - the next file's value null where "db-legacy" was expected: the import ended at the failed archive's report |
| extra E1 the remap's summary unguarded again | `4974ae783c740280e1293255082a68be` | 1 of 130: `TheGraphSecretKeyRemap_WithALoggerThatThrows_Returns(cause: "a remap that writes")` - the provider's exception out of the remap |
| extra E2 the per-file catch alone unguarded again | `03e4dc4a7995434c43ff1cf4cd7904f4` | 1 of 130: `Construction_..._AndImportsWhatItCan(cause: "an unparseable per-module file")` - the next file not imported |

Under M4c a second test failed in that run, `ExtendedLogServiceTests.Dispose_GivesTheQueueTimeInProportionToItsLength`
(40 entries dropped where 0 were expected). It is timing-bound - a 100 ms-per-entry dispose allowance over writes that
sleep 5 ms each - and the machine was shared with other agents' builds and test runs; it passed on the restored tree and
in every other run, and the mutant does not reach it (the extended log's legacy file is absent there, so its import never
archives). Recorded as an incidental timing failure, not a result of the mutant.

## Also held (aud-a62/a63/a65 agent, 2026-10-23) - LOW

(7) `Services/Jobs/BulkJobService.cs` 66 and 68: a finite but out-of-range `BulkJobs:RetentionDays` or
`BulkJobs:StaleHeartbeatMinutes` still throws `OverflowException` out of `TimeSpan.FromDays`/`FromMinutes`
in the constructor, so the app fails to start. (8) A negative `BulkJobs:RetentionDays` puts the start-up
prune cutoff in the future (`InitializeAsync` 85; `BulkJobRepository.PruneFinishedBefore` 281-300), deleting
every finished job record at start-up (the audit rows survive). (9) A negative `BulkJobs:RecentJobLimit`
reaches SQLite as a negative `LIMIT`, which means no limit (`BulkJobRepository.GetRecentFinished` 439-450).
Fix shape: range-check the three settings in `ConfigValue` (a minimum and maximum per setting) with the
default and the once-per-value warning.
