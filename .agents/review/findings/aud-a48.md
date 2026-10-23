# aud-a48: UsageTelemetryService reports through an unguarded logger, so a throwing provider escapes into the audit path, the Home page render and a fire-and-forget task

**Severity**: LOW/MEDIUM - the aud-a28q/a45 class in the usage-telemetry switch. `UsageTelemetryService`'s
`Record` catch, `LogSwitchUnreadableOnce` (reached from `ReadSwitch`, documented "Never throws") and the
`Enqueue` task's catch each call the logger with no guard. A logging provider that throws therefore escapes
`Enabled()` and `Record` into the last statement of `AuditService.WriteAuditEvent` (`_usage?.RecordAction`)
after the operation completed, and into the Home page's render (`@if (Usage.Enabled())`); in the enqueue task
it faults a fire-and-forget task unobserved.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
