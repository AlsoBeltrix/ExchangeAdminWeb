# aud-a48: UsageTelemetryService reports through an unguarded logger, so a throwing provider escapes into the audit path, the Home page render and a fire-and-forget task

**Severity**: LOW/MEDIUM - the aud-a28q/a45 class in the usage-telemetry switch. `UsageTelemetryService`'s
`Record` catch, `LogSwitchUnreadableOnce` (reached from `ReadSwitch`, documented "Never throws") and the
`Enqueue` task's catch each call the logger with no guard. A logging provider that throws therefore escapes
`Enabled()` and `Record` into the last statement of `AuditService.WriteAuditEvent` (`_usage?.RecordAction`)
after the operation completed, and into the Home page's render (`@if (Usage.Enabled())`); in the enqueue task
it faults a fire-and-forget task unobserved.
**Status**: Fixed on the branch, not merged; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-ac1038e1d7ece4ae5` (from `132d6e97`)
**Commit**: `df58ee19` (fix, tests, record); comments `c2d6215d`; plan `5ed8a5a8`
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

## Fix

- `Services/UsageTelemetryService.cs`:
  - New private `ReportSafely(Action<ILogger>)`: the report in a try whose catch takes any exception and does nothing.
  - The four logger calls go through it, their text and arguments unchanged:
    - `LogSwitchUnreadableOnce`'s two (the unreadable switch, the malformed one);
    - `Record`'s catch;
    - the insert's catch.
  - New `internal void InsertQueued(UsageEvent e)`: the insert, its failure reported through `ReportSafely`. It is the
    enqueue task's old body.
  - `Enqueue` is `_ = Task.Run(() => InsertQueued(e));`, still `internal virtual`.

Tests: `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs` +6.
- `TheTelemetry_GivenALoggerThatThrows_NeverThrows`, five causes:
  - The switch read fails: `Enabled()` false, one report attempted.
  - The switch is malformed: false, one attempted.
  - The switch is unreadable: false, two attempted - the change watcher's report of the token, and the switch's.
  - The switch itself throws: the four `Record` methods return, four attempted.
  - The insert fails: `InsertQueued` returns, one attempted.
- `AnAuditCall_GivenATelemetryServiceWhoseLoggerThrows_StillReturns_AndTheRowIsWritten`: `LogModuleAction` returns, the
  switch's one report attempted, and the row in the audit file.
- Helpers:
  - `ServiceWhoseSwitchReadFails`: a real store whose second read fails, under a module-config service and a telemetry
    service whose loggers both throw.
  - `Answer`, `ThrowingSwitchTelemetry` (an `Enabled()` that throws), `ReadsFailAfterStore` and `ThrowingLogger<T>`.

Caller-visible change, by design: a logging provider that throws no longer takes its exception out of the service:
- `Enabled()` answers - off when the switch cannot be read, as with a provider that works - so the Home page renders.
- `RecordSessionStart`, `RecordOpen`, `RecordTheme` and `RecordAction` return, so an audit call whose row was written
  returns too.
- The insert's task completes.
- Nothing else changes: the reports' text, the once-per-process switch warning, and what is recorded.

## Guard proof

Reproduction: E3 below puts all three of the file's reports back as they were at `132d6e97`, keeping only the
`InsertQueued` seam the test calls. It fails exactly the four cases that reach a report that throws:
- The audit call throws "the logging provider failed" after its row was written - the Constitution's "Audit failure must
  not make an already-completed operation appear to have failed".
- So do `Enabled()` and the `Record` methods, and the insert's task body.

Same driver as aud-a51's (session scratchpad `mutate/mutate.ps1`, specs `mutate/round-a48.json` and
`mutate/round-a48-e3.json`, logs `mutate/a48-mutations.log` and `mutate/a48-e3-mutations.log`). E3 is three literal
replacements at once, each required to match exactly once. The filter (160 tests) is this service's, its wiring's and
repository's, the change watcher's, and the audit writer's and service's:
`FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~UsageTrackerWiringTests|FullyQualifiedName~UsageEventRepositoryTests|FullyQualifiedName~ConfigChangeWatcherTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests`

Every mutant built with 0 errors, and each restore matched its original MD5. After each round, a rebuild on the restored
bytes and the filter: 160/160 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 `LogSwitchUnreadableOnce`'s read-failure report unguarded again (the bare `_logger.LogWarning(...)`) | `UsageTelemetryService.cs`, `2DB82918757112613B597AF2F4E73AED` | `EC5ACF0CA9E1C9D270611BC8CEBE0AE1` | 2 of 160: `TheTelemetry_GivenALoggerThatThrows_NeverThrows` "the switch read fails" - "the logging provider failed" thrown out of `Enabled()`; `AnAuditCall_...` - 2 reports where 1, `Enabled()` having thrown into `Record`'s catch, which made its own |
| extra E1 `Record`'s catch report unguarded again | `UsageTelemetryService.cs`, `2DB82918757112613B597AF2F4E73AED` | `16491833B36E9EC989C3FD68CD8C8E41` | 1 of 160: the theory's "the switch itself throws" - the provider's exception out of `RecordSessionStart` |
| extra E2 the insert's catch report unguarded again | `UsageTelemetryService.cs`, `2DB82918757112613B597AF2F4E73AED` | `9EA8C148F0557DFD4D8CD0CE2E90C851` | 1 of 160: the theory's "the insert fails" - the provider's exception out of the task's body |
| extra E3 all three unguarded at once, as at `132d6e97` | `UsageTelemetryService.cs`, `2DB82918757112613B597AF2F4E73AED` | `82F4B68CAB94C66622656CFD7476E786` | 4 of 160: `AnAuditCall_...` - the provider's exception out of `LogModuleAction`; the theory's "the switch read fails", "the switch itself throws" and "the insert fails" |

The theory's malformed and unreadable causes pass under every mutant. A report that throws from inside `ReadSwitch`'s try
is caught by its own catch, whose second call finds the once-flag set - the accident the Plan's evidence 2 describes.
They are kept as the rule's other two answers.

Unchanged through the rounds: `UsageTelemetryServiceTests.cs` `E368B7F8E701834C6D6E17C176B96242`. The code comments are
written after all three findings' rounds, in their own commit, so the MD5s above are this commit's bytes.

Not run, by design: the Home page in a browser. `Home.razor` calls `Usage.Enabled()` (43), and the theory shows that call
answering whatever the provider does.

## Known gaps

- `ModuleConfigService.ReadModuleConfig` (281-293) reports a failed read through its own logger, unguarded (290). With a
  provider that throws, `GetValue` then throws into every caller. This service now absorbs it, but the other callers do
  not. It is another agent's file and another finding's defect, listed as a candidate in the hand-back.
- With a provider that works, the same race - `IsModuleCorrupt`'s read passes, `GetValue`'s fails - reads the switch as
  never configured, so ON (`ReadSwitch` 136-141, `ReadModuleConfig` failing open). That is a privacy switch failing
  permissive for up to the cache's 30 s. It is not a logging failure and not this finding's; it is listed as a candidate
  in the hand-back.
- `InsertQueued` is internal so a test can run the task's body. The task itself is still not observed: a fault it could
  still meet, other than a report's, would go unobserved as before. The body's catch takes every exception.

## Comments after the round

Written after all three findings' rounds, in their own commit (`c2d6215d`), and checked as aud-a51's record says. This
record's file: `Services/UsageTelemetryService.cs` `2DB82918757112613B597AF2F4E73AED` -> `9F43D9F7FDF5B881C7118B6871950426`.
It has 166 code lines, their MD5 `418CF71C620C89CE1B6D85120BF7A334` on both sides. 13 lines were added and 1 extended
(14 in, 1 out):
- the class's "cannot break the caller" rule, which now covers the reports (the extended line);
- that `Enabled()` answers whatever the provider does;
- `InsertQueued`'s purpose;
- why the reports are guarded, and the guard's empty catch.

## Branch-tip gates

See aud-a51's record for the branch's gates.

## Closeout

Fixed on branch `worktree-agent-ac1038e1d7ece4ae5`, not merged, not pushed. The service is shared - every audit call and
the Home page reach it - so the base app version is due, and no module's own behaviour changed. The bump is the
supervisor's, at merge, because `ExchangeAdminWeb.csproj` was outside this brief. No file another agent owns was touched:
`Services/AuditService.cs` and `Components/Pages/Home.razor` needed no change. So nothing is left for a follow-up under
that heading; the candidates are in the hand-back.

## Codex review

Pending.
