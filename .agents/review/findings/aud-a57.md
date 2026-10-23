# aud-a57: The telemetry kill switch can fail open between two store reads; ModuleConfigService.ReadModuleConfig and the event log's load catches log unguarded

**Severity**: MEDIUM (part 1), LOW/MEDIUM (parts 2 and 3). (1) `UsageTelemetryService.ReadSwitch` reads
twice, `IsModuleCorrupt` then `GetValue`; `GetValue` returns an empty config on a read error, so a store read
that fails between the two reads the privacy switch as never configured, which means ON, and caches that for
up to 30 s - a fail-open on the anonymous usage switch. One `TryGetModuleConfig` read fails closed instead.
(2) `ModuleConfigService.ReadModuleConfig`'s catch logs without a guard, so a logging provider that throws
escapes `GetValue` / `GetModuleConfig` into every caller (the aud-a28q/a45 class at the config store).
(3) `AdminEventLog.razor` `LoadEvents`' audit and trace catches call `Logger.LogError` with no guard, so a
throwing provider ends the load.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan written)
**Branch**: `worktree-agent-a7b48a52dcf67eb2b` (from `a7300dee`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a51/a48/a49 agent, 2026-10-22, candidates 3, 4 and 5 of its hand-back)

## Evidence

At `81809833`: `Services/UsageTelemetryService.cs` 137-145 (`ReadSwitch`); `Services/ModuleConfigService.cs`
281-293 (`GetValue`'s empty config on error) and 290 (the unguarded log); `Components/Pages/AdminEventLog.razor`
629 and 664.

## Approach (to be written by the implementing agent before code)

- `ReadSwitch` makes one `TryGetModuleConfig` read; a failed read is "switch unreadable" and telemetry is OFF
  (fail closed on the privacy switch) with the once-per-streak report aud-a48 guards; the 30 s cache holds
  the unreadable answer no longer than a readable one.
- `ReadModuleConfig`'s catch logs through a nested guard (the aud-a28u shape); `GetValue`'s empty-config
  contract is unchanged.
- The two page catches report through a guarded helper; a logging failure never ends the load.
- Tests: the throwing-logger theory extended to `ModuleConfigService` and the page's load; a switch read that
  fails between the two reads is OFF. Mutations at the call site: the two-read switch again; one guard
  removed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Admitted from the aud-a51/a48/a49 hand-back, candidates 3, 4 and
5. One commit `fix(usage-telemetry,config-store,event-log): ... (aud-a57)`, second of three on branch
`worktree-agent-a7b48a52dcf67eb2b` (from `a7300dee`), after aud-a56 and before aud-a58. Two layers change:
- The usage-telemetry switch and the module-config reads are shared, so the base app version is due.
- The Admin Event Log's load is that module's behaviour, so its module version is due.

The supervisor owns both bumps.

Evidence, re-read at `a7300dee`:
1. `Services/UsageTelemetryService.cs` `ReadSwitch` (133-162) reads the store twice.
   - `IsModuleCorrupt` (137) is one read, through `ModuleConfigRepository.TryReadModule`.
   - `GetValue` (140) is a second read, through `ModuleConfigService.ReadModuleConfig`. That read returns an empty config
     when the store fails (281-293).
   - An empty config is a value never configured, which is the documented default, on (144-145).
   - So a read that fails between the two - a busy shared database, the other instance writing - reads a switch the
     operator turned off as on.
   - `Enabled` (109-130) caches that answer until the change token moves or 30 s pass.
2. `Services/ModuleConfigService.cs` `ReadModuleConfig`'s catch (288-292) calls `_logger.LogError` with no guard (290).
   `GetValue` (60-64), `GetModuleConfig` (66-69) and `IsModuleConfigured` (78-87) read through it. A provider that throws
   there takes its exception into every caller instead of the empty config.
3. `Components/Pages/AdminEventLog.razor` `LoadEvents` (551-):
   - The audit files' catch (626-630) sets `loadError`, then calls `Logger.LogError` with no guard (629).
   - The trace files' catch (659-665) names the file in `unreadableDiagnostics`, then calls `Logger.LogError` with no
     guard (664).
   - A provider that throws there ends `LoadEvents` with that exception. The rest of the range is never read, and the
     exception reaches the circuit.
4. `ProgressRegistry` pins `ReadLinesShared(` at both its occurrences in `LoadEvents` (the record's evidence 1 for
   aud-a49). `ClickGateRegistry` has the page as NotYetConverted, so no line key moves.

**Rule.** The privacy switch is read from the store once per load. A read that fails means the switch cannot be read,
and telemetry is off. No logging failure leaves a module-config read or the event log's load.

1. `Services/UsageTelemetryService.cs` `ReadSwitch`:
   - One `_moduleConfig.TryGetModuleConfig(ConfigModuleId, out var values)`. False is the switch unreadable,
     `LogSwitchUnreadableOnce(null)`, off.
   - Otherwise the value comes from that same read, and the rest is unchanged: blank is on, a value that is not a
     Boolean is off and logged once.
   - The report stays as aud-a48 left it: guarded, at most once per process. That is the record's "once-per-streak
     report aud-a48 guards".
   - The cache is unchanged. An unreadable answer is cached exactly as a readable one is: until the change token moves
     or 30 s pass. The token is still read before the switch (scd-2).
2. `Services/ModuleConfigService.cs` `ReadModuleConfig`: the log call sits in a nested try whose catch takes any
   exception (the aud-a28u shape). The empty config is returned as before.
3. `Components/Pages/AdminEventLog.razor`:
   - New private `ReportSafely(Action<ILogger>)`, the aud-a28z shape.
   - The two catches report through it. Their text, `loadError` and the unreadable list are unchanged.
4. Comments after the mutation round, checked code-identical.

Tests:
- `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`:
  - `ASwitchTurnedOff_WhoseStoreFailsAfterOneRead_IsOff`: the switch is stored "false", and the store fails every read
    after the first. `Enabled()` is false and nothing is recorded. This is the defect.
  - `ASwitchThatCannotBeRead_IsOff_ForNoLongerThanTheCacheHoldsAnyAnswer`: the switch is stored "true", and its one read
    fails. It is off, and off again inside the 30 s even with the store readable again. Past the 30 s it is on.
  - The existing helper `ServiceWhoseSwitchReadFails` allows no read, so the switch's one read is the read that fails.
    Its theory case and `AnAuditCall_...` keep their assertions: off, and one report attempted. The comment in
    `Switch_IsReadOnceForABurstOfRecords` is corrected to one store read per load.
- `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`:
  `AReadThroughAStoreThatFails_WithALoggerThatThrows_AnswersEmpty`, for `GetValue` and `GetModuleConfig`. Neither throws:
  the answer is null or empty, and one report is attempted.
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`:
  `AFileThatCannotBeRead_WithALoggerThatThrows_DoesNotEndTheLoad`, for the audit file and the trace file, each held with
  no sharing.
  - `LoadEvents` returns, and one report is attempted.
  - The page says what it could not read: `loadError` for the audit file, the trace file named in the diagnostics warning.

Mutations at the call site (md5 before and after, the named failing test, restored from the index, touched, rebuilt,
md5 matched, green):
- M1 the two-read switch again (`IsModuleCorrupt`, then `GetValue`) in `ReadSwitch`.
- M2 `ReadModuleConfig`'s guard removed.
- M3 the page's audit catch unguarded again.
- Extra E1 the page's trace catch unguarded again.

Filter:
`FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~UsageTrackerWiringTests|FullyQualifiedName~UsageEventRepositoryTests|FullyQualifiedName~ConfigChangeWatcherTests|FullyQualifiedName~ModuleConfigServiceTests|FullyQualifiedName~EventLog|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~AuditWriterThrowFreeTests`.
`ClickGate*` and `ProgressRegistryTests` are run alone as well.

Files:
- `Services/UsageTelemetryService.cs`
- `Services/ModuleConfigService.cs`
- `Components/Pages/AdminEventLog.razor`
- `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`
- `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`
- this record

## Guard proof

Pending.
