# Defect review request: log-folder failures, telemetry streak, bulk-job ranges, settings-page saves, busy-wait ceilings and the save tripwire (aud-a64 items 5-9, aud-a67 items 1-11, 13, 14)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `d0fc4c70` or later.
- The change is the range merged at `fe67668d`: commits `d42e146c`, `93d4c6e0`, `36245893` (aud-a64 5-9) and `2c52053f`,
  `5d89d3a3`, `0fc3dad5`, `b1661786`, `51b2f7fa`, `44d99f3e`, `41d3320f`, `3cb50d2c`, `492725b5`, `16392c2a`, `ba77f21f`,
  `0a10b484`, `7af08914`, `f08a664a` (aud-a67), on top of `620e9d5a`, whose aud-a64 (1)-(4) were already reviewed as part
  of the consolidation. Review `git diff 620e9d5a f08a664a -- . ':!.agents'` and read the COMBINED
  `Services/JsonlLogService.cs` and `Services/ExtendedLogService.cs` (built when the log folder cannot be made),
  `Services/UsageTelemetryService.cs` (one warning per failure streak), `Services/Jobs/BulkJobService.cs` and
  `Services/Jobs/BulkJobRepository.cs` with the `ConfigValue` range checks, `Services/Storage/ConfigDbMaintenance.cs` and
  `Services/BitLockerRecoveryService.cs` (command timeout), `Components/Pages/ModuleConfig.razor` (dirty cleared on load;
  one save at a time; the load takes its module first; the waiting notice), `Components/Pages/ExchangeOnlineConfig.razor`
  (the pool drain off the circuit), `Components/Pages/AdminSettings.razor` and `Services/ADAttributeEditorService.cs`
  (the allowlist busy message; the OU answer landing on its own load; the refusal labels captured), the busy-wait tests
  and the settings-save tripwire, and `README.md`. Also `d0fc4c70`, which swapped one written-out two-minute bound in
  `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs` for the shared `ConsumerWait`.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a64.md` and `aud-a67.md` (both carry every sub-item, its approach, its mutation
  table and its known gaps). Out of scope, do not re-raise: aud-a67 **(12)**, a save outlasting its 10 s bound holding its
  worker and store connection - the agent left it unstarted because closing it needs cancellation through `IConfigStore`
  and every repository, which the Constitution puts behind a written plan; the `SqliteBusyWaitTests`
  `ALockReleasedWithinTheWait_LetsTheWriteThrough` 5 s assertion (admitted by the agent as a candidate); anything already
  an open row in `.agents/review/index.md`. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes
  (1, 2, 3) and the ClickGateRegistry line-keying rule; `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat f08a664a`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Start-up and construction: with the log folder unmakeable the writers are now built - does
every later write refuse and report rather than throw, is the failure reported once rather than per write, and can a
folder that becomes available later start working without a restart; the telemetry streak flag - reset on success,
thread-safe, and is a second distinct failure after a recovery reported; the three bulk-job range checks - the bounds
chosen (1-1440 minutes, 1-1000, 1-3650 days) against what the settings mean, what a value outside them does (default plus
one warning, or refusal), whether a negative or zero value can still reach `TimeSpan.FromDays`/`FromMinutes` or a SQLite
`LIMIT`, and whether start-up still fails on an unparseable value; the command timeout on `ConfigDbMaintenance` and the
BitLocker archive connection - the measured wait is now 7.8-9.4 s where the docs claim 5 s because the open and the
statement each get one wait: is any caller's own bound now exceeded, and does a timeout surface as a clear refusal
rather than a crash; the pool drain moved off the circuit - what happens if it throws there, is the save still reported
correctly, and can a drain outlive the circuit that asked for it; one save at a time per load (items 4 and 13) - the
guard's scope (per page instance? per module? per operator?), whether a legitimate second save of a DIFFERENT setting is
now refused, and whether the refusal is visible; the load taking its module first and the OU answer landing on its own
load - any remaining route-change path where a late answer lands on the newer module; the waiting notice's wording and
whether it can persist after the save completes; the settings-save tripwire discovering saves and catches from source -
can a new save escape it, and does it fail closed when it cannot parse a page; and tests passing for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.