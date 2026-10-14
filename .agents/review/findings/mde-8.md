# mde-8: A blank device id leaves every enrichment cell blank, so the filter hides the device

**Severity**: MEDIUM - `mde-6`'s class reached per row instead of per run. A device the
unfiltered inventory retains disappears from a filtered table and from the CSV, with no marker
and no banner naming it.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `107cfe4`

## Evidence

`Services/DefenderEndpointDeviceService.cs:865` splits the ids before building the KQL batches:

```csharp
// An id this module cannot vouch for is never interpolated into KQL. It is dropped here
// and lands in unavailable below, so the device says so rather than showing a blank.
var safeIds = deviceIds.Where(IsSafeDeviceId).ToList();
var unsafeIds = deviceIds.Where(id => !IsSafeDeviceId(id)).ToList();
```

The comment states the intent exactly: *"so the device says so rather than showing a blank."*
`IsSafeDeviceId` rejects a blank or whitespace id, so such a device goes into `unavailable`.

`ApplyEnrichment` then never consults that set for it (`:1220` pre-fix):

```csharp
foreach (var device in devices)
{
    if (string.IsNullOrWhiteSpace(device.Id))
        continue;

    if (enrichment.UnavailableDeviceIds.Contains(device.Id))
    { ... }
```

The blank-id guard runs FIRST, so the device leaves the loop with every enrichment cell blank -
the one outcome the split was written to prevent.

That alone would be a cosmetic defect. What makes it a MEDIUM is the composition with `mde-6`:
an unsafe id on its own makes the run `PartiallySucceeded`, and
`Components/Pages/DefenderEndpointDevices.razor:663` treats `PartiallySucceeded` as evaluated,
so `seenByWasEvaluated` is TRUE. The seen-by filter at `:678` then judges the blank, and
`"".Contains(seenBy)` is false.

## Predicted observable failure

Defender returns one machine record with a blank or whitespace `id` alongside normal ones, with
hunting enabled. The unfiltered table shows that device. Type anything into "Seen by this
machine" and it vanishes - not marked `(unavailable)`, not counted in a banner, simply absent
from the table and from the CSV, which follows the filtered set.

## Approach

Stamp it rather than skip it. A blank id IS the unvouchable case - it is what put the device in
the unavailable set - so it takes the same marker:

```csharp
if (string.IsNullOrWhiteSpace(device.Id))
{
    StampUnavailable(device);
    continue;
}
```

The six assignments were factored into `StampUnavailable` because there are now two callers that
must agree, and a blank cell is precisely what `mde-6`'s filter cannot judge. If they drift, this
finding comes back.

## Files changed

- `Services/DefenderEndpointDeviceService.cs`
- `Modules/ModuleCatalog.cs` (Defender `1.6.5` -> `1.6.6`)
- `ExchangeAdminWeb.Tests/DefenderEndpointDeviceServiceTests.cs`
- `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`

## Guard proof

`ADeviceWhoseIdIsBlankReadsUnavailableRatherThanBlank`. **This one is BEHAVIOURAL, not a source
scan** - the service is reachable from the suite, unlike the page properties `mde-6` and `mde-7`
had to settle for. It lists a machine with a whitespace id beside a normal one, runs the real
enrichment path, and asserts three cells on the blank-id device read `(unavailable)` and that
the normal device is untouched.

| Mutation | Result |
| --- | --- |
| restore the bare `if (string.IsNullOrWhiteSpace(device.Id)) continue;` | FAIL; restored -> passes |

Service file restored byte-identical (md5 `1efae8e7...`) and touched.

## Coder dispute

None.

## Known gaps

The guard pins the service. It does not pin the PAGE half of the composition - that a blank
cell would be filtered out - because `FilteredDevices` is private in a `@code` block. `mde-6`'s
guard covers the filter's shape; nothing proves the two together.

Whether Defender ever returns a blank `id` has not been observed. The finding stands on the
mechanism and on the fact that the code already has a branch for unsafe ids: something thought
it possible enough to write the split.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Round 4 of the stream, over `96cdb89..ec2bdc8`. Verdict: **findings** (2) - this one and
`mde-9`.
Capability proof: reading `.agents/review/findings/mde-7.md` succeeded; `git diff --stat
4e0c586..ec2bdc8` succeeded (6 files, 341 insertions, 34 deletions). It also ran
`git diff --check 96cdb89..ec2bdc8`, clean, and did not rerun the suite.

**What it cleared, having been asked to attack it:** the `mde-6` argument that Site needs no
equivalent clause holds - directory exceptions stamp every current device, no devices are
appended after `ApplySites`, an empty result renders no table, and a blank Site from a blank or
unmatched IP is a real "no site can be derived" answer. And the `mde-7` enumeration holds across
all nine page states it listed; the two download buttons cannot render together, and a second
upload clears the rejection fields first.

Raw output: `.agents/review/out/mde-r4.txt`.

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master`. Round 4 was not clean, so round 5 is owed. Not yet pushed - push
policy is ask.
