# mt-detail-slice2: per-message delivery-detail service (slice 2)

**Severity**: n/a — slice-landing review of a new service method + its tests
**Status**: Round-1 fix landed - awaiting re-dispatch (round 2)
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `1f0af9c` (slice), fix commit pending

## Evidence
`Services/MessageTraceService.cs` — new `GetMessageDetailAsync` and its backend
routing / mapping seams (`ClassifyDetailBackend`, `BuildOnPremDetail`,
`MapOnPremDetailEvents`, `BuildCloudDetail`, `MapCloudDetailEvents`,
`IsOutdatedModuleError`, `UnknownBackendDetail`).
`ExchangeAdminWeb.Tests/MessageTraceDetailTests.cs` — 18 tests.

## Predicted observable failure
Without this slice, an operator cannot retrieve a single message's full per-hop
delivery trail: the summary path collapses every event to one row and drops the
reason fields. The core guard is `OnPremDetail_PreservesEveryEventRow_NoCollapse`
(+ ordering + reason-field tests): reintroducing a collapse
(`.Take(1)` in `MapOnPremDetailEvents`) makes those tests FAIL. Guards:
`dotnet build ExchangeAdminWeb.slnx -c Release` (0 errors), `dotnet test
ExchangeAdminWeb.slnx --filter FullyQualifiedName~MessageTraceDetailTests`
(18/18).

## What
Second slice of the Message Analysis detail work stream (plan task 2). Adds the
service method that fetches one message's full delivery trail: on-prem re-runs
`Get-MessageTrackingLog` scoped to the one message with the reason fields
(`Source`, `SourceContext`, `RecipientStatus`) and NO collapse (every event row,
ordered by timestamp); cloud calls `Get-MessageTraceDetailV2` keyed by
`MessageTraceId` + `RecipientAddress`. Both fail-soft.

## Approach
`GetMessageDetailAsync(MessageTraceResult, CancellationToken)` routes by
`ClassifyDetailBackend`. Cloud runs through `RunPooledQueryAsync(allowRetry:true)`
and catches the shared `IsOutdatedModuleError` predicate (the summary path's
duplicated inline check was consolidated onto it) plus a generic fail-soft catch.
On-prem uses the existing throttle + fresh-runspace + `ConnectOnPrem` +
`RemoveOnPremSession` path already used by the summary query. The live PS work is
un-unit-hostable (sealed pool / on-prem runspace), so the routing and PSObject
mapping are extracted into `internal static` seams and the tests exercise those
directly (BlockedSenders precedent). Aged-out cloud and empty on-prem set an
explanatory `Error` with empty `Events`; an unknown backend returns an error, not
a throw.

## Files changed
- `Services/MessageTraceService.cs` — new detail method + seams; summary path's
  outdated-module catch consolidated onto `IsOutdatedModuleError`.
- `ExchangeAdminWeb.Tests/MessageTraceDetailTests.cs` — 18 tests (new file).

## Guard proof
`MessageTraceDetailTests.OnPremDetail_PreservesEveryEventRow_NoCollapse` (and
`OnPremDetail_OrdersEventsByTimestamp`). Reverting the fix — changing
`MapOnPremDetailEvents`'s final `.OrderBy(e => e.Date).ToList()` to
`.OrderBy(e => e.Date).Take(1).ToList()` — makes both FAIL (verified: 2 failed,
16 passed); restoring makes all 18 PASS. Build 0 errors, ASCII exit 0, format
exit 0, `git diff --check HEAD` clean.

## Coder dispute (if any)
None.

## Known gaps
The live on-prem `Get-MessageTrackingLog` re-query and the cloud
`Get-MessageTraceDetailV2` call are manual-validation-on-dev only (no dev tenant /
on-prem transport server) — a standing repo gap. The reviewer should grade the
mapping/routing seams and the fail-soft contract, not the live PS transport.

## Reviewer comments

**Round 1 - codex CLI (codex-cli, codex exec, default), 2026-07-27 - REOPENED.**
Guard confirmed (revert `.OrderBy(...).ToList()` -> `.Take(1).ToList()`: 2 failed / 16
passed; restore: 18 passed), capability_ok (read repo file + built the solution),
`reviewed_sha` 1f0af9c, `base_sha` d8dd2466 - both match dispatch. Two substantive
fail-soft gaps (mandate item 3, "never throws into the caller"):

1. `Services/MessageTraceService.cs:131` (cloud) - the catch is inside the
   `RunPooledQueryAsync` delegate, but EXO borrow/config/pool/connect failures occur
   before the delegate runs and propagate out of `GetMessageDetailAsync` instead of
   returning a `MessageTraceDetail` with `Error` set and `Events` empty.
2. `Services/MessageTraceService.cs:173` (on-prem) - `ThrottledAsync` can throw a
   throttle timeout before the `Task.Run` body runs, bypassing the inner catch, so
   `GetMessageDetailAsync` can still throw.

Both accepted. The summary path already solves the identical problem with the outer
`RunMessageTraceBackendAsync` wrapper (line 313); the detail path was missing the
analogous outer guard. Fix: wrap the two live detail calls in `GetMessageDetailAsync`
in an outer try/catch that returns a fail-soft detail, mirroring the summary path.

**Round-1 fix (landed).** `GetMessageDetailAsync` now routes the backend switch
through a new pure `internal static RunDetailBackendAsync(message, query, onError)`
seam that catches any throw - including the pre-delegate EXO borrow/config/pool/connect
failures (finding #1) and the on-prem `ThrottledAsync` pre-`Task.Run` throttle-timeout
throw (finding #2) - and returns a fail-soft detail (`Error` set, `Events` empty). The
seam is extracted (not an inline try/catch) so it is unit-testable without a live pool /
runspace, mirroring `RunWithRetryCoreAsync`. Two new tests
(`RunDetailBackend_ThrowingQuery_ReturnsFailSoftDetail_NeverThrows`,
`RunDetailBackend_SucceedingQuery_PassesResultThrough`) cover it. Guard proved
non-vacuous: replacing the catch body with `throw;` makes the throwing test FAIL
(1 failed / 19 passed); restoring makes all 20 pass. Full suite 768/768, build 0 errors,
format exit 0, `git diff --check HEAD` clean, both files ASCII-clean.
