# sweep-d-31: Defender's hunting loop kept going after a 429 (sweep item 31)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 31: only 401 and 403 stopped the hunting
loop (`DefenderEndpointDeviceService.cs:1006-1007`, `:1056-1057`), so after a 429 every later batch was refused the same
way and each greyed 1,000 devices.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-d` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-d`, from master `6ea8963b`)
**Commit**: `b0e4aee4` (`fix(sweep-d, item 31): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- `DefenderApiResult` gains `RetryAfter` (optional, last parameter), set from a failed response's `Retry-After`
  (`DefenderApiClient.RetryAfterOf`: seconds, or a date taken relative to now; never negative).
- The posture and observer calls carry `Throttled` and `RetryAfter` out of `TryEnrichBatchAsync` /
  `TryReadObserversAsync`. `HonourThrottleAsync` (a method, not a local function, so the loop's own awaits stay the only
  continuations of `GetDiscoveryEnrichmentAsync` - the concurrency test schedules on them): a 429 whose Retry-After is
  present and at most `MaxHonouredRetryAfter` = 60 s is waited out ONCE per run (`Task.Delay` on
  `EnrichmentTimeProvider`, under the enrichment deadline's token) and the same call made again; a second 429, or a
  missing or longer Retry-After, stops the loop. The stop reason is the existing 429 sentence plus
  `DefenderDiscoveryReasons.ThrottledStop(n, afterRetry)`: "... the hunting stopped there: N device(s) were not queried
  and show (unavailable). The device list itself is complete." N is the devices from the stopping batch on; batches not
  started are marked unavailable as before.
- Logged: one Warning for the wait, one for the stop (batch, reason, count). The service gains an optional
  `ILogger<DefenderEndpointDeviceService>` (DI supplies it) and a `Logger` init seam. The run is a read; no audit row is
  added for it.
- 401 and 403 unchanged. Other failures (5xx, malformed, send failures) stay isolated per batch.
- README Defender bullet; DefenderEndpointDevices `1.6.19`.

## Where the code goes beside the fix shape

- The 60 s bound is a quarter of the four-minute enrichment deadline; a wait cancelled by the deadline ends the run as
  `DeadlineReached`.
- Five existing tests used a 429 to stand for "one batch failed, the rest carry on" (`AFailedHuntingBatch_...`,
  `APartlyFailedHuntingRun_...`, `AMixedRun_...`, the concurrency theory's "status" case, and the 429 case of
  `IsolatedFailuresAndObserver400KeepLaterEmptySuccesses`). That is exactly the behaviour this item changes, so they now
  use a 5xx (503, or 502 where the test needs a reason different from its neighbour's 503); the 429 case of the
  isolated-failure theory is removed because the 503 case beside it covers isolation.

## Guard proof (`DefenderHuntingThrottleTests`)

| Mutation | Failing tests |
|---|---|
| D1 never wait | `AShort429_IsWaitedOutOnce_AndTheBatchIsAskedAgain`, `ASecond429_AfterTheWait_StopsTheLoop_...`, `TheWaitIsUsedOncePerRun_SoALater429StopsAtOnce` |
| D2 wait on every 429 | `TheWaitIsUsedOncePerRun_SoALater429StopsAtOnce` |
| D3 a 429 does not stop | `A429WithNoRetryAfter_OrALongOne_StopsAtOnce` (2), `ASecond429_...`, `AnObserver429_StopsTheLoopToo`, `TheWaitIsUsedOncePerRun_...` |
| D4 no 60 s bound | `A429WithNoRetryAfter_OrALongOne_StopsAtOnce(61)` |
| D5 count off by one | `A429WithNoRetryAfter_...` (2), `ASecond429_...`, `AnObserver429_...`, `TheWaitIsUsedOncePerRun_...` |
| D6 stop not logged | `A429WithNoRetryAfter_...` (2), `ASecond429_...` |

(Under every mutation `ModuleCatalogTests.Catalog_HasDefenderEndpointDevicesModule` also failed: its version pin had not
yet been moved to 1.6.19 when the table was run; it was moved in the same commit.) `A401Or403_StillStopsAtOnce_...` and
`RetryAfter_IsReadAsSecondsOrAsADate_AndNeverNegative` pin behaviour around the change.

## Gates

Run once for the branch at its tip; see the sweep-d hand-back. Not run: a live hunting run against a throttled tenant.

## Manual checks for the deploy list (from the sweep)

- runHuntingQuery responses ending "- 429" in the app log; after the deploy, the two new Warnings ("throttled (429) ...
  waiting", "stopped on a 429 ... device(s) were not queried") are where a throttled run shows.
