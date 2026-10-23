# aud-a63: The event log's ExecuteUndo catch and the config store's post-write logging are unguarded, so a throwing provider leaves an undo unaudited and reported as success, or makes a saved setting look failed

**Severity**: MEDIUM - the aud-a28q/a45 class at two places where it changes an outcome. (1)
`AdminEventLog.razor` `ExecuteUndo`'s catch calls `Logger.LogError` unguarded, before `undoError` is set and
before `LogUndoNotRun`; with a throwing provider an undo that failed before its module was called leaves no
audit row, the `finally`'s `activity.Complete(undoError == null, ...)` reports success, and the exception
reaches the circuit. (2) `ModuleConfigService` calls `LogInformation` after the committed write, unguarded,
inside the lock and before `ConfigSaved?.Invoke` (three sites); a throwing provider makes a saved change
look failed and skips this instance's `ConfigSaved` subscribers.
**Status**: Complete; merged `a4fc0e29`; codex attr-batch-17 clean
**Branch**: `worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`)
**Commit**: `44827a38` (fix, tests, record); plan `46dacfdd`
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

## Fix

- `Components/Pages/AdminEventLog.razor` `ExecuteUndo`'s catch:
  - `undoError` is set first. `LogUndoNotRun` follows when the module was not called, unchanged. The report goes last,
    through the page's `ReportSafely` (aud-a57), its text unchanged.
  - A three-line comment sits above it. The page is NotYetConverted in `ClickGateRegistry`, so no key moves, and
    `ProgressRegistry`'s pin in `ExecuteUndo`, `handler.ExecuteUndoAsync`, is unchanged.
- `Services/ModuleConfigService.cs`:
  - New private `ReportSafely(Action<ILogger> report)`: `report(_logger)` in a try whose catch takes any exception, under
    a three-line comment.
  - The three post-write `LogInformation` calls, in `SaveModuleConfig`, `UpdateModuleValues` and `DecideChangedValues`, go
    through it, still inside the lock, their text unchanged.

Tests:
- `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`, +2:
  - `AnUndoThatThrowsBeforeItsModuleIsCalled_WithALoggerThatThrows_IsRecordedAndFailed_AndNothingEscapes`, for
    "EventLog" and "ADAttributeEditorLevel3".
  - `PageConfirming` and `Unattached` take an optional logger and progress channel; without them they are as before.
  - New `ThrowingLogger<T>`, and `RecordingProgress`, an `IActivityProgress` that keeps how each activity ended: the real
    one drops the outcome, because its frame is live only.
- `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`, +3:
  `ACommittedWrite_WithALoggerThatThrows_IsReportedSaved_AndRaisesConfigSaved`, for `SaveModuleConfig`,
  `UpdateModuleValues` and `SaveChangedValues`.

Caller-visible changes, by design:
- With a logging provider that throws, an undo that throws before its module is called is recorded - the event log's own
  Failed row - and shown, and its activity completes as failed. Nothing reaches the circuit. Before, there was no row,
  the activity completed as a success, and the circuit got the provider's exception.
- With a provider that works, only the order changes: the error line is logged after the audit row, not before.
- With a provider that throws, a module-config write that committed returns as saved and raises `ConfigSaved`. Before,
  the provider's exception came out of the write.

## Guard proof

Reproduction:
- M1 puts the catch back as it was at `4a7a202e`; its mutant is the page at `4a7a202e` byte for byte (MD5
  `42108BF7BAD63A85971342344664F1B8`).
- Run by hand on that mutant, the new test's three checks each failed on their own, for both faults:
  - the provider's `InvalidOperationException` escaped `ExecuteUndo`;
  - the audit file held 0 rows where 1;
  - the activity completed `("Undoing the selected action", True, null)` where `(..., False, <the error shown>)`.
  - That is the defect as the record states it: no audit row, the activity a success, and the exception out to the
    circuit.
- M2 puts back `UpdateModuleValues`' bare `_logger.LogInformation(...)`: the provider's exception came out of a write
  that had committed, and `ConfigSaved` was not raised.

Driver: the aud-a62 round's (session scratchpad `w-a62a63a65/mutate.ps1`), spec `w-a62a63a65/round-a63.ps1`, log
`w-a62a63a65/a63-mutations.log`. The round was run twice. The first run (`a63-mutations.first-run.log`, the same five
mutants, the same failures) was on a draft of the page test that checked the three halves one after another. The test was
then made to report each half on its own (`Assert.Multiple`), and the round was run again on the final bytes. The table
is the second run.

The filter (181 tests) covers every `EventLog` suite, every module-config suite, every undo suite and the whole-store
writer tripwire:
`FullyQualifiedName~EventLog|FullyQualifiedName~ModuleConfig|FullyQualifiedName~Undo|FullyQualifiedName~WholeStoreWriterTripwireTests`

Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
bytes and the filter: 181/181 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the unguarded `Logger.LogError(...)` before `undoError` again: the catch as at `4a7a202e` | `AdminEventLog.razor`, `A495A18F2A2C0852B5CCC3EF1C8F3A03` | `42108BF7BAD63A85971342344664F1B8` | 2 of 181: `AnUndoThatThrowsBeforeItsModuleIsCalled_WithALoggerThatThrows_IsRecordedAndFailed_AndNothingEscapes`, "EventLog" and "ADAttributeEditorLevel3" - `Assert.Multiple`: the exception escaped, no row, the activity a success |
| M2 `UpdateModuleValues`' post-write guard removed | `ModuleConfigService.cs`, `855939A89025D866F8C7852A3A36AFC3` | `3D17F1DA2BA2D406BCCBEF007C0B4DAF` | 1 of 181: `ACommittedWrite_WithALoggerThatThrows_IsReportedSaved_AndRaisesConfigSaved(writer: "UpdateModuleValues")` - "the logging provider failed" out of the write |
| extra E1 the new order kept, the report unguarded (`Logger.LogError(...)` after `LogUndoNotRun`) | `AdminEventLog.razor`, `A495A18F2A2C0852B5CCC3EF1C8F3A03` | `5EBB6822925309AE70BCF55823DEDE49` | 2 of 181: the same test, both faults - the exception escaped. The row and the failed activity held: the order alone is not the fix |
| extra E2 `SaveModuleConfig`'s post-write guard removed | `ModuleConfigService.cs`, `855939A89025D866F8C7852A3A36AFC3` | `EE99903750BC72E672A92F33F02E547B` | 1 of 181: `ACommittedWrite_...(writer: "SaveModuleConfig")` - the same |
| extra E3 `DecideChangedValues`' post-write guard removed | `ModuleConfigService.cs`, `855939A89025D866F8C7852A3A36AFC3` | `C366E87C785E7F06F1FB1134A1A84013` | 1 of 181: `ACommittedWrite_...(writer: "SaveChangedValues")` - the same |

Unchanged through the round:
- `EventLogUndoRefusalTests.cs` `A6A41AD98A3481D356594184C33825BA`
- `ModuleConfigServiceTests.cs` `A4571F8170A8F9E94CB3E82A12D40B23`

`ClickGate*` and `ProgressRegistryTests`, run alone on the restored tree (Release): 351/351.

Not run, by design: the Admin Event Log in a browser with a logging provider that throws. The page's own `ExecuteUndo` is
driven on a renderer's dispatcher, as the aud-a28l and aud-a28m tests drive it.

## Comments after the round

None were needed. The fix's comments were written before the round and are in the bytes measured above.

## Known gaps

- `LogUndoNotRun`'s own catch (1263 at `4a7a202e`) still logs with no guard, although the method is documented "Never
  throws". It is reached only when the audit call throws, which aud-a28q's tripwire keeps from happening. It is aud-a64's,
  held under the stopping rule, with the preview's and the usage read's catches.
- `ModuleConfigService`'s migration and legacy-import reports are still unguarded; aud-a64's too.
- A `ConfigSaved` subscriber that throws would still take its exception into the saver. That is a subscriber's failure,
  not a report's, and not this record's. The one subscriber today, `PermissionValidator`'s (70-77), only invalidates two
  caches.

## Branch-tip gates

See aud-a62's record for the branch's gates.

## Closeout

Fixed on branch `worktree-agent-a4c254a086c7d1d55`, not merged, not pushed. Two bumps are due, both the supervisor's at
merge, since `ExchangeAdminWeb.csproj` and `Modules/ModuleCatalog.cs` were outside this brief:
- the base app version, for `ModuleConfigService`, which is shared;
- the Admin Event Log's module version, for its undo.
