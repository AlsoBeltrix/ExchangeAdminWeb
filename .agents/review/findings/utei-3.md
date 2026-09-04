# utei-3: The kill-switch read happens on the caller's path, so telemetry can delay an audited operation

**Severity**: MEDIUM - the plan's non-interference rule ("a telemetry write runs off
the caller's path... must not change, delay or mask the operation that triggered it")
is broken before the fire-and-forget hand-off: every audited operation pays a
synchronous config-store read, and a locked shared database can stall it for up to the
5-second SQLite busy timeout.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/utei-3.md`

## Evidence

`Services/AuditService.cs:434-440` calls `_usage?.RecordAction(...)` synchronously at
the end of every audit write. `Services/UsageTelemetryService.cs:167-174` (`Record`)
calls `Enabled()` BEFORE `Enqueue`, and only `Enqueue` uses `Task.Run`.
`Services/UsageTelemetryService.cs:70-90` (`Enabled`) calls
`_moduleConfig.IsModuleCorrupt` and `_moduleConfig.GetValue`, which read the config
store (`ModuleConfigService` has no cache). `Services/Storage/SqliteConnectionFactory.cs:74-79`
sets `PRAGMA busy_timeout=5000` on each connection. Since `SharedConfigDb` landed, that
store is the file the OTHER instance also writes.

## Predicted observable failure

Prod performs an audited mailbox-permission change while dev is mid-write on the shared
config database (or a deploy-time backup holds the file). The operator's action does not
return until the telemetry kill-switch read acquires the lock - up to 5 seconds added per
audited operation, and once per audited operation in a bulk loop. Nothing has failed and
nothing is logged; the app is simply slower because of a feature that is required to be
invisible.

## What

Two reads were treated as free because they are "just config": the corruption check and
the value read. They are SQLite connections on the request path, and the shared-database
decision made contention across processes a real case.

## Approach

`Enabled()` now caches its answer instead of reading the config store on every call,
using the pattern this app already uses for exactly this problem: a
`ConfigChangeWatcher` over the config store plus a TTL backstop. Four other services
(`PermissionValidator`, `ProtectedPrincipalService`, `ExtendedLogService`,
`ADAttributeEditorService`) hold the same shape, and the watcher was built for the
shared-database case - `SqliteConfigStore.Write` bumps a change token inside every
write transaction, so a switch flipped by the OTHER instance is picked up here without
a restart, within the watcher's own two-second throttle.

The uncached read moved into a private `ReadSwitch()`, unchanged, so utei-2's
fail-closed semantics are preserved exactly. The token is read BEFORE the value (the
scd-2 protocol documented on `ConfigChangeWatcher`), so a write racing the load is
caught on the next check rather than being cached as fresh. `SwitchCacheTtl` is 30
seconds, matching the other config readers, and only matters when the token itself
cannot be read.

Cost on the operator's thread drops from two SQLite reads per audited action to a
lock, a compare and a clock read, plus at most one throttled token read every two
seconds. A test seam (`internal Func<DateTime> UtcNow`) mirrors the one
`ConfigChangeWatcher` already exposes.

The gate was deliberately NOT moved inside the `Task.Run` body. That would have
removed the last read from the caller's thread but left a background config read per
audited action - a thousand of them in a bulk loop, contending on the same shared
database the operator's own work uses. Caching removes the reads outright.

## Files changed

- `Services/UsageTelemetryService.cs` - `Enabled()` caches behind a
  `ConfigChangeWatcher` + TTL; the read body moved to `ReadSwitch()`; cache fields, a
  `UtcNow` test seam and an `internal ChangeWatcher` accessor added; constructor
  creates the watcher.
- `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs` - two tests, a
  `CountingConfigStore` decorator and a `ConfigOver(IConfigStore)` fixture; the
  existing `MalformedSwitch_LogsOnce` now steps the cache clock so it stays
  non-vacuous.

## Guard proof

Tests (`ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`):

- `Switch_IsReadOnceForABurstOfRecords` (:340) - 200 `RecordAction` calls through a
  `CountingConfigStore`; asserts all 200 events are queued and that the burst costs
  0-4 store reads. Uncached the same burst costs 400+.
- `Switch_ReloadsWhenTheOtherInstanceTurnsItOff` (:367) - a second
  `ModuleConfigService` over the SAME store writes `false`, standing in for the other
  instance; after the watcher's throttle clock is advanced, `Enabled()` is false and
  records stop. Guards against the cache going sticky.
- `MalformedSwitch_LogsOnce` (:313) - amended to step the cache clock past the TTL
  between its three calls, so it still exercises three real reads.

Mutation probes (non-vacuity), each run separately and reverted:

1. Cache-hit branch replaced by `if (false)` (the pre-fix behaviour):
   `Switch_IsReadOnceForABurstOfRecords` fails, 31 pass.
2. `&& !_changeWatcher.HasChangedSince(_switchToken)` replaced by `&& true`:
   `Switch_ReloadsWhenTheOtherInstanceTurnsItOff` fails, 31 pass.
3. `Interlocked.Exchange(ref _switchReadFailureLogged, 1) == 0` replaced by `true`:
   `MalformedSwitch_LogsOnce` fails, 31 pass - confirming the clock stepping kept it
   biting.

Each probe was reverted from a copy outside the working tree, not by `git checkout`.
Note for future probes: restoring with `Copy-Item` carries the backup's original
timestamp back onto the source, so MSBuild skips the rebuild and the next run silently
tests the still-mutated assembly. Stamp the file
(`(Get-Item $path).LastWriteTime = Get-Date`) after restoring.

Verification after restore: `dotnet build ExchangeAdminWeb.slnx -c Release` succeeded
(0 errors; only the pre-existing NU1903 package advisories); `dotnet test
ExchangeAdminWeb.slnx` 2383 passed / 0 failed / 3 skipped; `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean; ASCII scan and
`git diff --check HEAD` clean.

## Coder dispute (if any)

None. Note the fix must keep the fail-closed reading from utei-2 and must keep the
Home disclosure consistent with what is actually collected.

## Known gaps

A cache miss - the first call, a TTL expiry, or a token that moved - still reads the
config store on the caller's thread, so the worst case is one blocking read per 30
seconds per process rather than one per audited action. That residual is identical to
every other caching config reader in this app and was accepted when the change watcher
was designed; a config database locked continuously for seconds is already stalling
the permission validator and protected-principal checks, so telemetry is no longer a
distinctive contributor.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as utei-1)
Harness: codex-cli 0.153.2. Reviewed `e33b201~1..dea899b`, verdict `findings`,
capability_ok true. Envelope `.agents/review/ute-impl.result.json`.
