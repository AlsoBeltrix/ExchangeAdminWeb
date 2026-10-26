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
- (5) At `620e9d5a`: `Services/JsonlLogService.cs` 40 and `Services/ExtendedLogService.cs` 72 - each constructor's last
  folder step, `Directory.CreateDirectory(_logFolder)`, unguarded. Since aud-a56 `JsonlFileAppender.Append` (76) creates
  the folder before every write (`EnsureFolder`, 230-243), and a folder that cannot be created is that write's refusal,
  reported at once (`JsonlLockRefusedException`: the JSONL writer's `FileWriteFailed` and its error; the extended log's
  consumer drops, counts and reports the entry). Each constructor's call is now best effort: in a try whose catch reports
  a warning through the class's `ReportSafely` and carries on, so a folder that cannot be made at start-up no longer
  stops either singleton being built, and each write then makes it or is refused and reported.
  - Departure from the record's bullet ("the two constructors stop creating the folder"), taken after a full run of that
    shape: 32 tests in 8 classes (`PermissionPagesSelfGrantTests`, `JsonlWriterLockTests`, `AuditWriterThrowFreeTests`,
    `ExtendedLogServiceTests`, `JsonlWriterRotationTests`, `PermissionValidatorTests`, `AuditServiceTests`) open or
    delete files in the log folder right after building a writer, before any write. The best-effort call keeps the folder
    there from start-up whenever it can be made - what those fixtures and an operator looking for the folder rely on -
    and closes the defect the same way, with a start-up warning that names the folder as a gain. No reader depends on
    the folder existing (`GetAuditLogPaths` and `GetTraceLogPaths` build paths, the Admin Event Log checks `File.Exists`
    at 626 and 659, the extended log's reads at 228 and 266, the export store `Directory.Exists`).
  - One test pinned the defect: `AuditServiceTests.LogMailboxPermission_ThrowsOnWriteFailure` asserted that building the
    audit writer under a log root that cannot be made throws. It now asserts the new contract - the writer is built and a
    mailbox-permission record under it comes back `FileWriteFailed` - renamed to say so.
  - Tests: `JsonlWriterRotationTests` +1 and `ExtendedLogServiceTests` +1 - a log folder that cannot be created at
    start-up: the writer is built and warns once naming the folder, a row or entry is refused and reported (or dropped and
    counted), and once the obstacle goes the next one is written. Mutations: each constructor's guard removed. App-wide.
- (6) `Services/UsageTelemetryService.cs` `LogSwitchUnreadableOnce` (266-288 at `620e9d5a`): the report is gated by
  `Interlocked.Exchange(ref _switchReadFailureLogged, 1) == 0` and nothing sets the flag back, so it is once per process -
  a store that failed, recovered and failed again later was never reported the second time (aud-a57's known gap). A read
  that answers - blank, the documented default (on), or a Boolean (`ReadSwitch` 151-160) - now resets it, so the report
  is once per failure streak; an unreadable store and a value that is not a Boolean are both failures, and both still
  read off. Test: `UsageTelemetryServiceTests` +1 - a switch whose store fails, recovers and fails again is reported once
  per streak, not on the reads inside one. Mutation: the reset removed. App-wide (shared telemetry): no module bump.
- (7)-(9), one cluster: `Services/Jobs/BulkJobService.cs` 66-68 at `620e9d5a` read the three settings through
  `ConfigValue.ReadDouble` / `ReadInt` (`Services/ConfigValue.cs`), which refuse only what does not parse or is not
  finite. So a finite value out of range still reached `TimeSpan.FromMinutes` / `FromDays` and threw `OverflowException`
  in the constructor (7); a negative or zero retention put the start-up prune's cutoff (`InitializeAsync` 88) at or after
  now, so `BulkJobRepository.PruneFinishedBefore` (281-300) deleted every finished job record (8); and a negative limit
  reached `GetRecentFinished` (439-450) and its siblings as `LIMIT -n`, which SQLite reads as no limit (9).
  - `ConfigValue.ReadInt` and `ReadDouble` take an optional inclusive `min` and `max`; a value that parses but is outside
    them is the default, named once per setting and value like any other unreadable value, the warning naming the range.
    Callers that pass no range read as before.
  - `BulkJobService` passes a range per setting, as named constants: `StaleHeartbeatMinutes` 1 to 1440 (a minute to a
    day), `RecentJobLimit` 1 to 1000, `RetentionDays` 1 to 3650 (a day to ten years). The bounds are this agent's choice
    of sane operating values - the record gives the shape, not the numbers - each well inside what `TimeSpan`,
    `DateTime.UtcNow - retention` and `(int)TotalDays` hold. README's paragraph on number settings names them.
  - Tests: `ConfigValueTests` - a range kept and refused for each reader; the runner given values out of range on both
    sides (one too large for `TimeSpan`) is built with its defaults and names each; `BulkJobServiceTests` - a start-up with
    a negative retention keeps a day-old finished job (8), and a negative limit shows the default 25 of 30 finished jobs
    (9). Mutations: `BulkJobService` with no ranges again (all three cases); `ReadDouble`'s range check removed. App-wide
    (the shared runner): no module bump.

## Files changed

- (1)-(3): `Components/Pages/AdminEventLog.razor`; `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`,
  `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`.
- (4): `Services/ModuleConfigService.cs`, `Services/Storage/LegacyConfigImport.cs`;
  `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`.
- (6): `Services/UsageTelemetryService.cs`; `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`.
- (7)-(9): `Services/ConfigValue.cs`, `Services/Jobs/BulkJobService.cs`, `README.md`;
  `ExchangeAdminWeb.Tests/ConfigValueTests.cs`, `ExchangeAdminWeb.Tests/BulkJobServiceTests.cs`.
- (5): `Services/JsonlLogService.cs`, `Services/ExtendedLogService.cs`; `ExchangeAdminWeb.Tests/JsonlWriterRotationTests.cs`,
  `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`, `ExchangeAdminWeb.Tests/AuditServiceTests.cs` (the test that pinned
  the defect, rewritten to the new contract).

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

(5). Filter `FullyQualifiedName~JsonlWriterRotationTests|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~JsonlWriterLockTests|FullyQualifiedName~AuditWriterThrowFreeTests`
(148 tests). Each mutant is that constructor's call as it was at `620e9d5a`: bare and unguarded. `JsonlLogService.cs` MD5
`9acc151fc92459da77f39e38047749a7`, `ExtendedLogService.cs` `71edf225678dd7c9b8818b7e3803f335`, each matched after its
row; 148/148 green after each restore. The full suite on this tree: 6,703 passed, 0 failed.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| M5a `JsonlLogService`'s constructor: the folder created unguarded again | `2a28a9bfd3ca5be84dc6d53bd57f8540` | 2 of 148: `JsonlWriterRotationTests.ALogFolderThatCannotBeCreatedAtStartUp_StillBuildsTheWriter_AndEachRowIsTheFoldersRefusal` and `AuditServiceTests.LogMailboxPermission_UnderALogRootThatCannotBeCreated_IsBuilt_AndHandsBackTheRowNotWritten` - `System.IO.IOException: Cannot create '...' because a file or directory with the same name already exists.` out of the constructor |
| M5b `ExtendedLogService`'s constructor: the same | `c0f6d93f64d66d4ebbd137c6ae1b9590` | 1 of 148: `ExtendedLogServiceTests.ALogFolderThatCannotBeCreatedAtStartUp_StillBuildsTheService_AndTheEntryItRefusesIsDroppedAndCounted` - the same `IOException` out of the constructor |

The round was run twice: first on 2026-10-23, then again on 2026-10-26 after the test moved (below), with the same
mutant MD5s and the same failures. The second round's test bytes: `ExtendedLogServiceTests.cs`
`020357d5f5479bf573b7bd036709ca55`, `JsonlWriterRotationTests.cs` `4e2c7ec8407de43c6014679bb1439c6f`,
`AuditServiceTests.cs` `5d8cfc7509ffae200ecb9eaa56659de6`.

(6). Filter `FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~UsageTrackerWiringTests|FullyQualifiedName~UsageEventRepositoryTests|FullyQualifiedName~ConfigChangeWatcherTests|FullyQualifiedName~ConfigBooleanTests`
(97 tests). `UsageTelemetryService.cs` MD5 `2bf77f42aaca15db0d34e6459c87ad3e`, matched after the row; 97/97 green after
the restore. The mutant is the method as it was at `d42e146c` (no reset at all), so its failure is the reproduction.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| M6 `Readable`'s `Interlocked.Exchange(ref _switchReadFailureLogged, 0)` removed | `a5a843f11e24308301ec9c9b5c2c7f0a` | 1 of 97: `UsageTelemetryServiceTests.ASwitchThatFailsAgainAfterARecovery_IsReportedOncePerFailureStreak` - one warning where two were expected: the second failure streak was never reported |

(7)-(9). Filter `FullyQualifiedName~ConfigValueTests|FullyQualifiedName~BulkJobServiceTests|FullyQualifiedName~BulkJobRepositoryTests|FullyQualifiedName~ConfigBooleanTests|FullyQualifiedName~Comms10kOwnedJobTests|FullyQualifiedName~MessageTraceExportListingTests`
(192 tests). `BulkJobService.cs` MD5 `54c7d93f70b43a82bbb1a4da26a32d6d`, `ConfigValue.cs` `d9d15f9e3e4897a1a67ef17323b9dddd`,
each matched after its rows; 192/192 green after every restore. Test bytes: `ConfigValueTests.cs`
`f63b1292686e034445bcac7c6ccdf535`, `BulkJobServiceTests.cs` `0d42e35c3c9d8e472e51bcedec8ac01a`. M7 is the constructor
as it was at `93d4c6e0`, so its six failures are the three defects' reproduction.

| Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|
| M7 `BulkJobService`'s constructor reads the three settings with no range again | `4eb87a30bb34a889487de54dc3a1fefc` | 6 of 192: all three `TheBulkJobRunner_GivenSettingsOutsideTheirRange_IsBuilt_WithItsDefaults` rows - `System.OverflowException: TimeSpan overflowed because the duration is too long.` out of the constructor (7), and a heartbeat of `00:00:00` / `-00:01:00` where 5 minutes was expected; `Initialize_GivenARetentionOutsideItsRange_KeepsAFinishedJob`, both rows - the day-old finished job deleted at start-up (8); `GetRecentJobs_GivenALimitOutsideItsRange_ListsTheDefaultTwentyFive` - 30 of 30 listed where 25 were expected (9) |
| extra E3 `ConfigValue.ReadDouble`'s range check removed | `3e4312ce782b2be4a332b33f1a57164e` | 8 of 192: the three out-of-range `ReadDouble` rows (0.5, -3, 1e300 each taken), and the runner's and the prune's cases above |
| extra E4 `ConfigValue.ReadInt`'s range check removed | `bdb02a9e52ddfb687279f49fc420b389` | 7 of 192: the three out-of-range `ReadInt` rows (0, -1, 11 each taken), the runner's three rows (the limit read as -7, -13 and 999999997), and `GetRecentJobs_GivenALimitOutsideItsRange_...` |

A first pass of this round left one failure on the restored tree:
`TheBulkJobRunner_GivenSettingsOutsideTheirRange_IsBuilt_WithItsDefaults(heartbeat: "0", ...)` counted two warnings, not
three. `ConfigValue` names a setting and value once per process (the `Named` dictionary), and the new
`BulkJobServiceTests` cases pass `BulkJobs:RetentionDays` "0" and "-1" too, so whichever class ran first consumed the
name - an order-dependent failure, not the mutant's. The rows were given values no other test uses (`0.125`/`-7`/`0.25`,
`-11`/`-13`/`-17`, `1e301`/`999999997`/`1e302`), which is the rule this class's own remarks already state, and the whole
round was run again on those bytes: the table above. The filter then ran three times in a row, 192/192 each time.

**Merge note (this branch forked at `eb017102`; master is ahead).** `Services/JsonlLogService.cs`,
`Services/ExtendedLogService.cs` and `JsonlWriterRotationTests.cs` are untouched on master, so those merge clean.
`ExtendedLogServiceTests.cs` is not: kd-16, kd-22 and kd-25 raised its waits to a shared `ConsumerWait` of 2 minutes and
kd-47 replaced its local `TryDelete` with `TestFolders.TryDelete`. Two things follow, both deliberate:
- This record's new case was first written just above `GetEntries_WhenAFileCannotBeRead_...`, which is inside kd-47's
  hunk; it was moved above `TheConsumer_InAFolderThatLetsItWriteButNotDelete_WritesEveryEntry` instead, between two
  master hunks and clear of both, so the merge has no conflict to resolve.
- Its wait for the consumer to count the dropped entry is `TimeSpan.FromMinutes(2)` written out, with a comment naming
  kd-16's `ConsumerWait`: the same value, since the constant does not exist on this branch. At merge it can become
  `ConsumerWait` like the rest of the file.

## Also held (aud-a62/a63/a65 agent, 2026-10-23) - LOW

(7) `Services/Jobs/BulkJobService.cs` 66 and 68: a finite but out-of-range `BulkJobs:RetentionDays` or
`BulkJobs:StaleHeartbeatMinutes` still throws `OverflowException` out of `TimeSpan.FromDays`/`FromMinutes`
in the constructor, so the app fails to start. (8) A negative `BulkJobs:RetentionDays` puts the start-up
prune cutoff in the future (`InitializeAsync` 85; `BulkJobRepository.PruneFinishedBefore` 281-300), deleting
every finished job record at start-up (the audit rows survive). (9) A negative `BulkJobs:RecentJobLimit`
reaches SQLite as a negative `LIMIT`, which means no limit (`BulkJobRepository.GetRecentFinished` 439-450).
Fix shape: range-check the three settings in `ConfigValue` (a minimum and maximum per setting) with the
default and the once-per-value warning.
