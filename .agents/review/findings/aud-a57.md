# aud-a57: The telemetry kill switch can fail open between two store reads; ModuleConfigService.ReadModuleConfig and the event log's load catches log unguarded

**Severity**: MEDIUM (part 1), LOW/MEDIUM (parts 2 and 3). (1) `UsageTelemetryService.ReadSwitch` reads
twice, `IsModuleCorrupt` then `GetValue`; `GetValue` returns an empty config on a read error, so a store read
that fails between the two reads the privacy switch as never configured, which means ON, and caches that for
up to 30 s - a fail-open on the anonymous usage switch. One `TryGetModuleConfig` read fails closed instead.
(2) `ModuleConfigService.ReadModuleConfig`'s catch logs without a guard, so a logging provider that throws
escapes `GetValue` / `GetModuleConfig` into every caller (the aud-a28q/a45 class at the config store).
(3) `AdminEventLog.razor` `LoadEvents`' audit and trace catches call `Logger.LogError` with no guard, so a
throwing provider ends the load.
**Status**: Merged `3da510cb`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a7b48a52dcf67eb2b` (from `a7300dee`)
**Commit**: `278dff50` (fix, tests, record); plan `c3960fe9`
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

## Fix

- `Services/UsageTelemetryService.cs` `ReadSwitch`:
  - `if (!_moduleConfig.TryGetModuleConfig(ConfigModuleId, out var values)) return LogSwitchUnreadableOnce(null);`, then
    `var configured = values.GetValueOrDefault(ConfigKey);`.
  - The rest is unchanged: blank on, not a Boolean off and logged once, a throw off and logged once.
  - The dictionary `TryReadModule` returns is case-insensitive, as `GetValue`'s lookup was.
- `Services/ModuleConfigService.cs` `ReadModuleConfig`: the `_logger.LogError(...)` sits in `try { ... } catch (Exception)
  { }`. The empty config is still returned.
- `Components/Pages/AdminEventLog.razor`:
  - New private `ReportSafely(Action<ILogger> report)`: `report(Logger)` in a try whose catch takes any exception.
  - The audit catch and the trace catch report through it, their text unchanged.
  - 1,680 lines -> 1,695. NotYetConverted in `ClickGateRegistry`, so no key moves. `ProgressRegistry`'s pins are
    unchanged.

Tests:
- `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`, +2:
  - `ASwitchTurnedOff_WhoseStoreFailsAfterOneRead_IsOff`.
  - `ASwitchThatCannotBeRead_IsOff_ForNoLongerThanTheCacheHoldsAnyAnswer`.
  - Updated, not weakened:
    - `ServiceWhoseSwitchReadFails` allows no read (`AllowReads(0)`), so the switch's one read is the read that fails. The
      theory case "the switch read fails" and `AnAuditCall_...` keep every assertion: off, one report attempted, the row
      written.
    - The read-count comment in `Switch_IsReadOnceForABurstOfRecords` says one store read per load.
- `ExchangeAdminWeb.Tests/ModuleConfigServiceTests.cs`, +2: `AReadThroughAStoreThatFails_WithALoggerThatThrows_AnswersEmpty`
  (`GetValue`, `GetModuleConfig`); new `ThrowingLogger`.
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`, +2:
  - `AFileThatCannotBeRead_WithALoggerThatThrows_DoesNotEndTheLoad` (audit, trace).
  - `PageWithDiagnostics` takes an optional logger; new `ThrowingLogger`.

Caller-visible changes, by design:
- The usage-telemetry switch is read from the store once per load, not twice. A store that fails after the token read is
  the switch unreadable, so off and reported once per process, as any unreadable switch was. It never again reads a
  switch the operator turned off as on.
- A logging provider that throws while `ModuleConfigService` reports a failed read no longer takes its exception out of
  `GetValue`, `GetModuleConfig` or `IsModuleConfigured`. They answer null or empty, as they do with a provider that works.
- A logging provider that throws while the Admin Event Log reports a file it could not read no longer ends the load. The
  page shows the error, or names the trace file, as it does with a provider that works.

## Guard proof

Reproduction: M1, M2, M3 and E1 each put one call site back as it was at `a7300dee`. Each failure is the defect as the
record states it:
- M1: the switch stored "false" read as on when the store failed after one read.
- M2: the provider's exception came out of `GetValue` and `GetModuleConfig`.
- M3 and E1: the provider's exception ended `LoadEvents`.

Same driver as aud-a56's (session scratchpad `mutate/mutate.ps1`, spec `mutate/round-a57.ps1`, log
`mutate/a57-mutations.log`). The filter (218 tests) covers the telemetry service, its wiring and repository, the change
watcher, the module-config service, every `EventLog` suite, and the audit service and writer:
`FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~UsageTrackerWiringTests|FullyQualifiedName~UsageEventRepositoryTests|FullyQualifiedName~ConfigChangeWatcherTests|FullyQualifiedName~ModuleConfigServiceTests|FullyQualifiedName~EventLog|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~AuditWriterThrowFreeTests`

Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
bytes and the filter: 218/218 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the two-read switch again (`IsModuleCorrupt`, then `GetValue`, in `ReadSwitch`) | `UsageTelemetryService.cs`, `81B61F941AD2166B7F83F4B8116EF1AF` | `AC306CB8785C2EDBBAD900A0774B09B9` | 1 of 218: `ASwitchTurnedOff_WhoseStoreFailsAfterOneRead_IsOff` - `Enabled()` true where false |
| M2 `ReadModuleConfig`'s guard removed (the bare `_logger.LogError(...)` again) | `ModuleConfigService.cs`, `E02AB650708844814D6A1CC289CABCAD` | `66B974E1AF6EFFDE2A1D91779E1BEBF0` | 2 of 218: `AReadThroughAStoreThatFails_WithALoggerThatThrows_AnswersEmpty`, `GetValue` and `GetModuleConfig` - "the logging provider failed" thrown out of the read |
| M3 the page's audit catch unguarded again (`Logger.LogError(ex, "Failed to read audit log file {Path}", filePath);`) | `AdminEventLog.razor`, `42108BF7BAD63A85971342344664F1B8` | `D9B4BD2B28C3E12BEE39E7EEF42F4459` | 1 of 218: `AFileThatCannotBeRead_WithALoggerThatThrows_DoesNotEndTheLoad(stream: "audit")` - the provider's exception out of `LoadEvents` |
| extra E1 the page's trace catch unguarded again | `AdminEventLog.razor`, `42108BF7BAD63A85971342344664F1B8` | `730F5456D4D0003EAEDFD84FE2B930C2` | 1 of 218: `AFileThatCannotBeRead_WithALoggerThatThrows_DoesNotEndTheLoad(stream: "trace")` - the same |

Under M1 the theory case "the switch read fails" and `AnAuditCall_...` still pass. With no read allowed, the merged probe
also finds the store unreadable, and that was never the defect. The race needs the probe's read to pass, and only the new
test sets that up. `ASwitchThatCannotBeRead_IsOff_ForNoLongerThanTheCacheHoldsAnyAnswer` passes under every mutant too: it
pins the cache's contract, which this record keeps rather than changes.

Unchanged through the round:
- `UsageTelemetryServiceTests.cs` `7B50C670FAD3700F3AFFB4C31A99E3CD`
- `ModuleConfigServiceTests.cs` `F309D5D50C6B580F3CE54A77E850E826`
- `EventLogDiagnosticsTests.cs` `CAD18C19EE998614CB431A0508B0CDC9`

`ClickGate*` and `ProgressRegistryTests` passed on this tree, in the same run as the filter before the round (569 of 569).
They are run alone again at the branch tip.

Not run, by design: the Admin Event Log in a browser with a logging provider that throws. The page's own `LoadEvents`
and render tree are driven as the aud-a46 and aud-a49 tests drive them.

## Comments after the round

None were needed. This record's comments were written before its round and are in the bytes measured above. The branch's
one comments-only commit touches `Services/JsonlLogService.cs` only (aud-a56's record).

## Known gaps

- The unreadable-switch warning is still at most once per process (`LogSwitchUnreadableOnce`'s flag is never reset), as
  aud-a48 left it and as the record's "once-per-streak report" refers to. A store that fails, recovers and fails again
  later is not reported the second time; the switch reads off each time.
- `IsModuleCorrupt` still reads the store on its own for its other callers. Only the switch stopped using it as a probe.
- `ModuleConfigService`'s other reports are still unguarded: the success logs after a write, the Graph secret-key remap's
  and the legacy import's (listed in the hand-back as candidates). This record guards the read path only.
- The Admin Event Log's other `Logger` calls are still unguarded: the undo preview's catch, the undo's catch, the audit
  of an undo not run and the usage read's catch (listed in the hand-back as candidates). This record guards `LoadEvents`
  only.

## Branch-tip gates

See aud-a56's record for the branch's gates.

## Closeout

Fixed on branch `worktree-agent-a7b48a52dcf67eb2b`, not merged, not pushed. Two bumps are due, both the supervisor's at
merge, since `ExchangeAdminWeb.csproj` and `Modules/ModuleCatalog.cs` were outside this brief:
- the base app version, for the usage-telemetry switch and the module-config read, which are shared;
- the Admin Event Log's module version, for its load.
