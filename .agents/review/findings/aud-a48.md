# aud-a48: UsageTelemetryService reports through an unguarded logger, so a throwing provider escapes into the audit path, the Home page render and a fire-and-forget task

**Severity**: LOW/MEDIUM - the aud-a28q/a45 class in the usage-telemetry switch. `UsageTelemetryService`'s
`Record` catch, `LogSwitchUnreadableOnce` (reached from `ReadSwitch`, documented "Never throws") and the
`Enqueue` task's catch each call the logger with no guard. A logging provider that throws therefore escapes
`Enabled()` and `Record` into the last statement of `AuditService.WriteAuditEvent` (`_usage?.RecordAction`)
after the operation completed, and into the Home page's render (`@if (Usage.Enabled())`); in the enqueue task
it faults a fire-and-forget task unobserved.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-ac1038e1d7ece4ae5` (from `132d6e97`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a47/a45/a46 agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `2a15e9ed`: `Services/UsageTelemetryService.cs` around 222 (the enqueue task's catch), 242 (the `Record`
catch), 257 and 265 (`LogSwitchUnreadableOnce`); `Services/AuditService.cs` around 516
(`_usage?.RecordAction(`); `Components/Pages/Home.razor` 43.

## Approach (to be written by the implementing agent before code)

- One guarded report helper (the aud-a28z `ReportSafely` shape) for every logger call in the service; the
  enqueue task's catch swallows after the guarded report so the task never faults; `Enabled()` and `Record`
  never throw whatever the provider does.
- Tests: the aud-a28q throwing-logger theory extended to `UsageTelemetryService` (`Enabled`, `Record`, the
  enqueue path, the unreadable-switch report); `WriteAuditEvent` with a telemetry service whose logger
  throws still returns. Mutation at the call site: one guard removed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Admitted from the aud-a47/a45/a46 hand-back (`24eab6c9`). One commit
`fix(usage-telemetry): ... (aud-a48)`, second of three on branch `worktree-agent-ac1038e1d7ece4ae5` (from `132d6e97`),
after aud-a51 and before aud-a49. The service is shared - every audit call and the Home page reach it - and belongs to no
module's own behaviour, so the base app version is due; the supervisor owns the bump.

Evidence, re-read at `132d6e97`:
1. `Services/UsageTelemetryService.cs` calls its logger in four places, none guarded:
   - `LogSwitchUnreadableOnce` (251-274): two `_logger.LogWarning` calls (257-261, 265-270), after the once-flag is set
     (253).
   - `Record`'s catch (240-243): `_logger.LogWarning` (242).
   - The enqueue task's catch (220-223): `_logger.LogWarning` (222).
2. `ReadSwitch` (129-158) is documented "Never throws" (128), and calls `LogSwitchUnreadableOnce` from three places. Read
   from the file:
   - From inside its try - the corrupt switch (134) and the malformed one (150). A report that throws there is caught by
     the method's own catch, whose second call finds the flag set and returns false. Those two do not escape, by accident.
   - From the catch (156), when the switch read itself throws. That report is the first, and its throw leaves
     `ReadSwitch`, and so `Enabled()` (105-126).
   - The read throws when `GetValue` does. `ModuleConfigService.ReadModuleConfig` (281-293) catches a failed store read
     and reports it through its own logger, unguarded (290). With one provider behind both loggers, as in production,
     that report throws the provider's exception out of `GetValue`. `IsModuleCorrupt` (45-58) cannot throw:
     `ModuleConfigRepository.TryReadModule` (33-45) catches everything. So the path is a store read that fails after
     `IsModuleCorrupt`'s read passed - a busy shared database, for one.
3. Where it lands:
   - `Record` (231-244) calls `Enabled()` inside its try. Its catch reports through the same logger and throws again, out
     of `RecordSessionStart`, `RecordOpen`, `RecordTheme` and `RecordAction`.
   - `Services/AuditService.cs` `WriteAuditEvent` calls `_usage?.RecordAction(` (516) as its last statement, after the row
     was written and the operation scope closed, under a comment that says "RecordAction never throws" (512). The audit
     call then throws after the operation completed - the Constitution's "Audit failure must not make an
     already-completed operation appear to have failed".
   - `Components/Pages/Home.razor` 43 calls `Usage.Enabled()` in render.
   - The enqueue task (212-225) faults when the insert fails and its report throws. Nothing observes the task.

**Rule.** No logging failure leaves `UsageTelemetryService`. `Enabled()` answers, off when the switch cannot be read; every
`Record` method returns; and the insert's task completes - whatever the provider does.

1. `Services/UsageTelemetryService.cs`:
   - New `private void ReportSafely(Action<ILogger> report)`, the aud-a28z shape: the report inside a try whose catch
     takes any exception and does nothing.
   - Every logger call in the file goes through it, its text and arguments unchanged: the two in
     `LogSwitchUnreadableOnce`, `Record`'s catch, and the insert's catch.
   - The enqueue task's body moves to new `internal void InsertQueued(UsageEvent e)`: the insert, with its failure
     reported through `ReportSafely` and swallowed. `Enqueue` becomes `_ = Task.Run(() => InsertQueued(e));`, still
     virtual for the capturing subclass the tests use. A test can then run the task's body on its own thread and see
     that it returns.
2. Not changed: `Services/AuditService.cs` (another agent's file; its comment at 512 becomes true) and
   `Components/Pages/Home.razor` (`Enabled()` no longer throws for a logging failure).
3. Comments after the mutation round, checked code-identical.

Tests, in `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`. The aud-a28q tripwire's own file,
`AuditWriterThrowFreeTests.cs`, is another agent's in this round, so the throwing-logger theory sits with the service's
own tests. A new `ThrowingLogger<T>` counts each report and throws, as aud-a28u's does.
- `TheTelemetry_GivenALoggerThatThrows_NeverThrows`, by cause:
  - The switch read fails. The config store's reads fail after the first, so `IsModuleCorrupt` passes and `GetValue`'s
    read fails. Both services' loggers throw, one provider as in production. `Enabled()` answers false, and one report is
    attempted.
  - The switch is malformed ("flase"): `Enabled()` answers false.
  - The switch is unreadable (a store that cannot be read): `Enabled()` answers false.
  - The switch itself throws (a subclass whose `Enabled()` throws). The four `Record` methods return, and their reports
    are attempted.
  - The insert fails (`InsertQueued` over a repository whose open fails): it returns, and one report is attempted.
  - No throw in any case.
- `AnAuditCall_GivenATelemetryServiceWhoseLoggerThrows_StillReturns_AndTheRowIsWritten`. An `AuditService` over a real
  JSONL writer on the test's temp root, with the telemetry service from the first cause. `LogModuleAction` returns, and
  the row is in the audit file.

Mutations at the call site (md5 before and after, the named failing test, restored, touched, rebuilt, md5 matched, green):
- M1 `LogSwitchUnreadableOnce`'s read-failure report unguarded again (the bare `_logger.LogWarning(...)`).
- Extra E1 `Record`'s catch report unguarded again.
- Extra E2 the insert's catch report unguarded again.

Files:
- `Services/UsageTelemetryService.cs`
- `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`
- this record

## Guard proof

Pending.
