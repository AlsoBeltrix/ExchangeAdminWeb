# mde-2: A failed observer lookup renders as data, with nothing on the page saying so

**Severity**: MEDIUM - a column that could not be read is indistinguishable from a column that
was read and found nothing. Both print `(unavailable)`; only one of them means the operator
should stop trusting it.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `7ba8ed0`

## Evidence

`Services/DefenderEndpointDeviceService.cs:902-906` (pre-fix) incremented `observerFailedBatches`
on every failed observer request - and nothing ever read the variable. The all-posture-success
return at `:926` went straight to `Succeeded`, which carries no failure count at all.

Downstream, `ApplyEnrichment` at `:1229-1231` stamps `RecentlySeenBy` as `(unavailable)` for
every device the observer lookup did not answer for, and the page's failure banners at
`Components/Pages/DefenderEndpointDevices.razor:280-305` fire only for partial or non-success
states.

The composition is the defect. The observer query runs in its OWN request - deliberately, so it
cannot take the posture columns down with it - which means it can fail while every posture batch
succeeds. That run is `Succeeded`, so no banner renders, and the column reads `(unavailable)`
with no explanation anywhere on the page.

## Predicted observable failure

An unsupported or broken observer query (the realistic case: `SeenBy()` named wrong, or the
table not available to this tenant) produces a report where `Recently seen by` is empty for
every device and the page says the run succeeded. The operator reads "nobody has observed any
of these devices" from a query that never ran.

## Approach

Carry the count into the result and render it. The page now says: *"the observing machine could
not be looked up for some devices (N of M lookups failed)"*. That sentence is also the thing
that separates the two states the page previously could not tell apart - a lookup that FAILED
from a lookup that succeeded and found nothing. Both leave the column unhelpful; they call for
different responses.

## Files changed

- `Models/DefenderDeviceModels.cs`
- `Services/DefenderEndpointDeviceService.cs`
- `Components/Pages/DefenderEndpointDevices.razor`
- `Modules/ModuleCatalog.cs` (Defender `1.6.2` -> `1.6.3`)
- `ExchangeAdminWeb.Tests/DefenderEndpointDeviceServiceTests.cs`
- `ExchangeAdminWeb.Tests/DefenderDevicePagingTests.cs`
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` (re-anchored - the page edit shifted four entries)
- `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`

## Guard proof

`AFailedObserverLookup_CostsOnlyTheObserverColumn` in `DefenderEndpointDeviceServiceTests.cs`
plus the page-side banner assertion in `DefenderDevicePagingTests.cs`, mutated and watched to
fail.

Full suite at the fix: **3,822 passed / 0 failed / 3 skipped**; build 0 errors; format clean;
`git diff --check` clean; ASCII lint passed.

## Coder dispute

None.

## Known gaps

This fix was incomplete and round 2 found the hole: the count was carried on the SUCCEEDED path
only, so a mixed run - one posture batch failing AND a different observer lookup failing - still
dropped it. See `mde-4`. The two findings are the same mechanism caught at two different return
sites, which is worth recording: the fix was verified against the state it was written for and
not against the neighbouring one.

## Reviewer comments

`Reviewer: codex / gpt-5.5 / xhigh`
Range: `96cdb89..HEAD`. Verdict: **findings** (3). Finding 2 of 3.
Raw output: `.agents/review/out/session-review.txt`.

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master` in `7ba8ed0`, completed by `mde-4`. Not yet pushed - push policy is
ask.
