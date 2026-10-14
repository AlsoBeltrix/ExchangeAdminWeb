# mde-6: A whole-run enrichment failure makes the seen-by filter empty the table

**Severity**: MEDIUM - a valid 26,000-device inventory is silently hidden by a filter the app
could not evaluate, and the page says "No devices matched" directly under a banner saying the
column is unavailable. The two statements contradict each other and the operator has no way to
tell which is true.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `4e0c586`

## Evidence

`Components/Pages/DefenderEndpointDevices.razor:666` (pre-fix) judged the seen-by filter against
the raw `d.RecentlySeenBy` value, with one escape: a device whose value is exactly
`(unavailable)` is kept. That escape is `111bfa2`'s fix for the Site column and it only helps
when something STAMPED the marker.

Nothing stamps it on a whole-run failure. `Services/DefenderEndpointDeviceService.cs:1212`:

```csharp
var state = enrichment.State;
if (state != DefenderDiscoveryEnrichmentState.Succeeded
    && state != DefenderDiscoveryEnrichmentState.PartiallySucceeded)
{
    return;
}
```

`ApplyEnrichment` returns before the stamping loop, so `RecentlySeenBy` stays BLANK. The
escape never fires, `"".Contains(seenBy)` is false for any non-empty filter, and every device
in the tenant is excluded.

`NotAttempted` is not an exotic state. Its own XML doc lists the triggers: *"the Include
Discovery Sources switch is off, the module's credentials were unavailable, no devices matched,
or the listing itself refused."* **The hunting switch being off is an ordinary operator
action.**

## Predicted observable failure

Turn Include Discovery Sources off (or run while Graph returns 403/429/timeout, or with every
hunting batch failing), type anything into "Seen by this machine", press Search. The banner
says the observer column is unavailable; the table says "No devices matched"; the CSV export is
empty because `ExportableDevices` follows the filtered set. The devices are all there and none
of them is shown.

## Approach

Do not judge a column that was never evaluated. The filter now computes
`seenByWasEvaluated` from the run state and passes every device when it is false:

```csharp
var seenByWasEvaluated =
    result.DiscoveryEnrichment is DefenderDiscoveryEnrichmentState.Succeeded
        or DefenderDiscoveryEnrichmentState.PartiallySucceeded;
```

**Site deliberately does NOT get the same treatment**, and the asymmetry is the point.
`ApplySites` stamps `(unavailable)` on its own failure path, so the marker always arrives; and a
BLANK site is a real answer - no subnet in the forest contains that address. Only the seen-by
column has a state in which blank means "never evaluated".

`ExportEnrichmentCell` at `:1169` already drew exactly this line for the CSV - the same two
states, the same reasoning, written out in its own remarks. The filter was the one place that
did not.

## Files changed

- `Components/Pages/DefenderEndpointDevices.razor`
- `Modules/ModuleCatalog.cs` (Defender `1.6.4` -> `1.6.5`)
- `ExchangeAdminWeb.Tests/DefenderDevicePagingTests.cs`
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` (`ExpectedLineCount` 1200 -> 1213)
- `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`

## Guard proof

`AWholeRunEnrichmentFailureDoesNotEmptyTheSeenByFilter` in `DefenderDevicePagingTests.cs`. Two
mutations, both run this session, both bit:

| Mutation | Result |
| --- | --- |
| delete the `\|\| !seenByWasEvaluated` disjunct | FAIL; restored -> passes |
| widen the binding with `or ...NotAttempted` | FAIL; restored -> passes |

The second mutation is the one that matters. A guard that only checked the disjunct exists
would pass against a flag defined as `true`, or against one widened until it means nothing, so
the test pins the binding to exactly the two states `ApplyEnrichment` stamps on and asserts the
other two are absent.

Page restored byte-identical after both mutations (md5 `f69646cd...`) and touched, so MSBuild
could not skip the rebuild and hand the test a stale binary.

## Coder dispute

None. The reviewer also rated the severity correctly in my view, though I would note it is
reachable by a switch the operator can flick rather than only by an outage.

## Known gaps

**The guard is a source-text assertion over the `.razor` file, not behaviour.** It proves the
disjunct and the binding are present and correctly scoped; it does not prove the filter returns
the right set, because `FilteredDevices` is a private property in a `@code` block and this repo
has no bUnit harness. The reviewer named the predecessor test as one that "would pass broken
code" and this one is stronger but the same kind. Honest statement of the limit rather than a
claim it is closed.

The other enrichment columns (`DiscoverySources`, `DeviceType`, `DeviceCategory`, `Vendor`,
`Model`) have the same blank-on-whole-run-failure shape but no filter, so there is nothing to
exclude. If a filter is ever added for one of them it needs this same clause.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Round 3 of the Defender location stream, over `96cdb89..b3099fe`. No T1 sensitive path matched,
so standard tier; no escalation triggers fired.
Capability proof: reading `.agents/review/findings/mde-4.md` succeeded; `git diff --stat
ac71855..b3099fe` succeeded. The reviewer also independently checked that local `HEAD` was
`b3099fe` while `origin/master` was still `96cdb895`, and said so.
Verdict: **findings** (2) - this one and `mde-7`. It did not rerun the build or suite and said
so.

What it cleared, and it was asked to: all `DefenderDiscoveryEnrichment` construction sites carry
the observer count where they should (`Failed`/`NotAttempted` intentionally carry none); the
Site path is consistent across screen, filter, CSV and paging; subnet matching uses longest
prefix, returns blank on no match, and resolves equal-prefix duplicates stably.

Raw output: `.agents/review/out/mde-r3.txt`. Prompt is machine-local
(`.gitignore:66` excludes `.agents/review/*.prompt.txt`).

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master`. Round 3 was not clean, so round 4 is owed. Not yet pushed - push
policy is ask.
