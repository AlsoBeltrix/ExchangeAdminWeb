# aud-a63: The event log's ExecuteUndo catch and the config store's post-write logging are unguarded, so a throwing provider leaves an undo unaudited and reported as success, or makes a saved setting look failed

**Severity**: MEDIUM - the aud-a28q/a45 class at two places where it changes an outcome. (1)
`AdminEventLog.razor` `ExecuteUndo`'s catch calls `Logger.LogError` unguarded, before `undoError` is set and
before `LogUndoNotRun`; with a throwing provider an undo that failed before its module was called leaves no
audit row, the `finally`'s `activity.Complete(undoError == null, ...)` reports success, and the exception
reaches the circuit. (2) `ModuleConfigService` calls `LogInformation` after the committed write, unguarded,
inside the lock and before `ConfigSaved?.Invoke` (three sites); a throwing provider makes a saved change
look failed and skips this instance's `ConfigSaved` subscribers.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); in progress, plan committed
**Branch**: `worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a56/a57/a58 agent, 2026-10-23, candidates 1 and 5 of its hand-back)

## Evidence

At `3da510cb`: `Components/Pages/AdminEventLog.razor` around 1209 (the `ExecuteUndo` catch; the page is
NotYetConverted for `ClickGateRegistry`); `Services/ModuleConfigService.cs` 103, 127, 223 (the
post-write `LogInformation` calls), the `ConfigSaved` invocation that follows each.

## Approach (to be written by the implementing agent before code)

- `ExecuteUndo`'s catch sets `undoError` and writes `LogUndoNotRun` FIRST, then reports through the page's
  guarded `ReportSafely` (aud-a57); the activity completes with the failure; nothing reaches the circuit.
- The three post-write logs go through a nested guard (the aud-a28u shape) so a saved change is reported
  saved and `ConfigSaved` is raised whatever the provider does.
- Tests: `EventLogUndoRefusalTests` with a throwing logger (an undo that fails before its module: one
  `LogUndoNotRun` row, activity failed, no throw); `ModuleConfigServiceTests` with a throwing logger (the
  save returns saved, `ConfigSaved` raised). Mutations at the call site: the unguarded log before
  `undoError` again; one post-write guard removed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"), within the 2026-10-23 stopping rule (MEDIUM). Admitted from the
aud-a56/a57/a58 hand-back, candidates 1 and 5. One commit `fix(event-log,config-store): ... (aud-a63)`, the second fix on
branch `worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`), after aud-a62 and before aud-a65. Two layers change:
- The Admin Event Log's undo is that module's behaviour, so its module version is due.
- The module-config writes are shared, so the base app version is due.

The supervisor owns both bumps.

Evidence, re-read at `4a7a202e`:
1. `Components/Pages/AdminEventLog.razor` `ExecuteUndo` (1098-1223):
   - The catch (1207-1216) calls `Logger.LogError(ex, "Failed to execute undo for operation {OperationId}",
     target.OperationId)` first (1209), with no guard. Then it sets `undoError` (1210), then calls `LogUndoNotRun` when the
     module was not called (1214-1215).
   - The finally (1217-1222) completes the activity from `undoError`: `activity.Complete(undoError == null, undoError)`
     (1219).
   - So with a provider that throws, an undo that threw before its module - the auth state, the re-check, the level check
     (aud-a28m's exits) - leaves no audit row. The activity completes as a success, and the provider's exception leaves
     the handler for the circuit.
   - The page's guarded `ReportSafely(Action<ILogger>)` (753-763, aud-a57) is the shape to use.
   - `ClickGateRegistry` has the page NotYetConverted ("tier 4, not approved"), so no line key moves. `ProgressRegistry`
     pins `handler.ExecuteUndoAsync` in `ExecuteUndo`, which does not move.
2. `Services/ModuleConfigService.cs`, each inside `lock (_writeLock)`, after the committed write and before
   `ConfigSaved?.Invoke(moduleId)`:
   - `SaveModuleConfig` 103 (`ConfigSaved` 106).
   - `UpdateModuleValues` 127 (`ConfigSaved` 130, then `return update`).
   - `DecideChangedValues` 222-223, the save `SaveChangedValues` makes (`ConfigSaved` 226-227, then the outcome).
   - A provider that throws at any of them takes its exception out of a write that committed. The caller reports a saved
     change as failed, and this instance's `ConfigSaved` subscribers never hear of it. The `lock` statement releases the
     lock either way.
3. Held as aud-a64 (LOW, the 2026-10-23 stopping rule), so not touched here:
   - `LogUndoNotRun`'s own catch (1263), the preview's catch (1050) and the usage read's catch (1644);
   - `ModuleConfigService`'s migration and legacy-import reports (271, 276, 335-364).

**Rule.** Reporting a failure never decides the outcome. An undo that failed is recorded, and its activity completed as
failed, whatever the provider does, and the report cannot throw. A config write that committed is reported saved, and
`ConfigSaved` is raised, whatever the provider does.

1. `AdminEventLog.razor` `ExecuteUndo`'s catch, in this order:
   - `undoError`;
   - `LogUndoNotRun` when the module was not called, unchanged;
   - the report, through `ReportSafely`, its text unchanged.
   - The finally then completes the activity with the failure, and nothing reaches the circuit.
2. `ModuleConfigService`:
   - New private `ReportSafely(Action<ILogger>)`, the aud-a28z shape. `ReadModuleConfig`'s inline guard (aud-a57) is
     left as it is.
   - The three post-write `LogInformation` calls go through it, their text unchanged, still inside the lock.
3. Comments after the mutation round, checked code-identical.

Tests:
- `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`:
  `AnUndoThatThrowsBeforeItsModuleIsCalled_WithALoggerThatThrows_IsRecordedAndFailed_AndNothingEscapes`, a theory over the
  two faults of `AnUndoThatThrowsBeforeItsModuleIsCalled_LeavesOneFailureRecord`.
  - The page's `Logger` throws on every call, and its `Progress` is a recorder of how each activity completed.
  - `ExecuteUndo` returns without an exception, and `undoError` carries the fault.
  - One audit row, the event log's own and Failed, as the existing test asserts it.
  - The undo's activity completed once, failed, with `undoError`. One report attempted.
- `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`:
  `ACommittedWrite_WithALoggerThatThrows_IsReportedSaved_AndRaisesConfigSaved`, a theory over `SaveModuleConfig`,
  `UpdateModuleValues` and `SaveChangedValues`.
  - No exception, and `ConfigSaved` raised once with the module id. The value is read back.
  - `UpdateModuleValues` returns its update, and `SaveChangedValues` its status `Saved`. One report attempted.

Mutations at the call site (md5 before and after, the named failing test, restored from the index, touched, rebuilt,
md5 matched, green):
- M1 the unguarded `Logger.LogError(...)` before `undoError` again: the catch as at `4a7a202e` (the record's).
- M2 one post-write guard removed: `UpdateModuleValues`' bare `_logger.LogInformation(...)` again (the record's).
- Extra E1 the catch's new order kept, its report unguarded (`Logger.LogError(...)` after `LogUndoNotRun`). It shows the
  guard is load-bearing, not only the order.
- Extra E2 `SaveModuleConfig`'s guard removed.
- Extra E3 `DecideChangedValues`' guard removed.

Filter:
`FullyQualifiedName~EventLog|FullyQualifiedName~ModuleConfig|FullyQualifiedName~Undo|FullyQualifiedName~WholeStoreWriterTripwireTests`.
`ClickGate*` and `ProgressRegistryTests` are run alone as well.

Files:
- `Components/Pages/AdminEventLog.razor`
- `Services/ModuleConfigService.cs`
- `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`
- `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`
- this record

## Guard proof

Pending.
