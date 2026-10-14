# mde-4: The observer failure count is dropped on a mixed partial run

**Severity**: MEDIUM - it re-opens `mde-2` through the neighbouring return site. A column that
could not be read again renders as data with no banner.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `c7e64b1`

## Evidence

`mde-2`'s fix (`7ba8ed0`) carried `observerFailedBatches` into the result on ONE path. The
increment is at `Services/DefenderEndpointDeviceService.cs:903`; the partial-success return at
`:916` passed only `observerUnavailable` into `PartiallySucceeded`, because that factory had no
`observerFailedBatches` parameter at all (`Models/DefenderDeviceModels.cs:327`).

So the count reached the page when every posture batch succeeded, and was discarded when any
posture batch failed. The page's observer banner at
`Components/Pages/DefenderEndpointDevices.razor:322` is driven by that count, so it did not
render.

## Predicted observable failure

One posture batch throttles (HTTP 429) and a DIFFERENT batch's observer lookup fails. The run
is `PartiallySucceeded`, the posture banner renders, `Recently seen by` reads `(unavailable)`
for the devices the observer lookup missed - and nothing says why. The operator sees one
explained failure and one unexplained blank column, which reads as "no observer data exists for
those devices".

## Approach

Add the parameter to `PartiallySucceeded` and pass the count there too. One parameter, one
argument.

## Files changed

- `Models/DefenderDeviceModels.cs`
- `Services/DefenderEndpointDeviceService.cs`
- `Modules/ModuleCatalog.cs` (Defender `1.6.3` -> `1.6.4`)
- `ExchangeAdminWeb.Tests/DefenderEndpointDeviceServiceTests.cs`
- `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`

## Guard proof

`AMixedRun_CountsTheObserverFailuresAsWellAsThePostureOnes` in
`DefenderEndpointDeviceServiceTests.cs`: batch 1's posture fails (so its observer request is
skipped entirely), batch 2's posture answers and its observer request fails. It pins
`PartiallySucceeded` with `DiscoveryFailedBatches == 1` AND `ObserverFailedBatches == 1`.

| Test | Mutation | Result |
| --- | --- | --- |
| `AMixedRun_CountsTheObserverFailuresAsWellAsThePostureOnes` | drop the `observerFailedBatches` argument from the partial return | FAIL - `Assert.Equal() Expected: 1, Actual: 0`; restored -> passes |

Mutation run this session, not inherited from the record. Full Release suite at the fix:
**3,826 passed / 0 failed / 3 skipped**; build 0 errors; format clean; `git diff --check` clean;
ASCII lint passed.

## Coder dispute

None.

## Known gaps

Nothing in this proves the observer query returns data in this tenant - `SeenBy()` has never
returned a row here. This guard is about how a failure is reported, not about whether the
lookup works. See `.agents/state.md`, unproven environmental fact 2.

## Reviewer comments

`Reviewer: codex / gpt-5.5 / xhigh`
Round 2 of the Defender location stream, over the session range with `835a195` at its head.
Capability proof in the envelope: `git show --stat 835a195` succeeded; the full-file read of
`DefenderEndpointDeviceService.cs` was truncated by the terminal budget and followed with
targeted line reads.
Verdict: **findings** (2) - this one and `mde-5`. The reviewer confirmed `mde-1` closed
(`ApplySites` carries `false` into `CompleteRun`, the page renders the Site warning, and the
matching happens after the catch) and reported `git diff --check` clean.
Raw output: `.agents/review/out/round2.txt`.

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master` in `c7e64b1`. Round 2 was not clean either, so round 3 is owed. Not
yet pushed - push policy is ask.
