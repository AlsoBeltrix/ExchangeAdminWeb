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

## Guard proof

Pending.

## Also held (aud-a62/a63/a65 agent, 2026-10-23) - LOW

(7) `Services/Jobs/BulkJobService.cs` 66 and 68: a finite but out-of-range `BulkJobs:RetentionDays` or
`BulkJobs:StaleHeartbeatMinutes` still throws `OverflowException` out of `TimeSpan.FromDays`/`FromMinutes`
in the constructor, so the app fails to start. (8) A negative `BulkJobs:RetentionDays` puts the start-up
prune cutoff in the future (`InitializeAsync` 85; `BulkJobRepository.PruneFinishedBefore` 281-300), deleting
every finished job record at start-up (the audit rows survive). (9) A negative `BulkJobs:RecentJobLimit`
reaches SQLite as a negative `LIMIT`, which means no limit (`BulkJobRepository.GetRecentFinished` 439-450).
Fix shape: range-check the three settings in `ConfigValue` (a minimum and maximum per setting) with the
default and the once-per-value warning.
