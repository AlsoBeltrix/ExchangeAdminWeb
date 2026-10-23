# aud-a57: The telemetry kill switch can fail open between two store reads; ModuleConfigService.ReadModuleConfig and the event log's load catches log unguarded

**Severity**: MEDIUM (part 1), LOW/MEDIUM (parts 2 and 3). (1) `UsageTelemetryService.ReadSwitch` reads
twice, `IsModuleCorrupt` then `GetValue`; `GetValue` returns an empty config on a read error, so a store read
that fails between the two reads the privacy switch as never configured, which means ON, and caches that for
up to 30 s - a fail-open on the anonymous usage switch. One `TryGetModuleConfig` read fails closed instead.
(2) `ModuleConfigService.ReadModuleConfig`'s catch logs without a guard, so a logging provider that throws
escapes `GetValue` / `GetModuleConfig` into every caller (the aud-a28q/a45 class at the config store).
(3) `AdminEventLog.razor` `LoadEvents`' audit and trace catches call `Logger.LogError` with no guard, so a
throwing provider ends the load.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
