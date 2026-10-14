# mde-7: Mixed and failed upload states still cannot reach the errors CSV

**Severity**: MEDIUM - it re-opens `mde-5`, which re-opened `mde-3`. The same "recorded but
unreachable" shape, now at the two states the previous fix's gate excluded.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `acf99e9`

## Evidence

Not a Defender finding - the third Comms-10k one in this stream, same provenance as `mde-3`
and `mde-5`.

`mde-5`'s fix (`ac71855`) added the download under this gate
(`Components/Pages/Comms10k.razor:107` pre-fix):

```razor
@if (parsedEmails.Count == 0 && rejectedBeforeLookup.Count > 0 && resolveResult == null)
```

`parsedEmails.Count == 0` is the problem. It is true only for a file in which EVERY row is
unusable. Two states have rejections and do not satisfy it:

1. **A mixed file before Validate is pressed.** One good address and one blank or no-`@` row
   gives `parsedEmails.Count == 1`, so the Validate path at `:94` renders and this block does
   not. The operator sees a Validate button and no mention of the rejected row.
2. **A validation that failed.** `resolveResult != null && !resolveResult.Success` - AD
   credentials or configuration unavailable - fails this gate on the third clause, and the
   other download lives inside `resolveResult.Success` at `:116`. Neither block renders.

In both, `rejectedBeforeLookup` holds the rows and nothing can reach them.

## Predicted observable failure

Upload a workbook with one real address and one blank row. The page offers Validate and says
nothing about the blank. Press Validate while AD is unreachable: the error renders, the
rejected row is still recorded, and there is no way to download the errors CSV from that page
state. The operator is told the upload is fine, or that AD failed, and never that their file
was short a row.

## Approach

**Stop gating on state.** Round 2 fixed one state and round 3 found two more, which is the
third time in this stream a fix was written for the state in front of it and not the one
beside it. So the condition is no longer a state at all:

```razor
@if (AllRejections.Count > 0 && (resolveResult == null || !resolveResult.Success))
```

Read as: are there rejections, and is the validated-result panel not already offering them.
`AllRejections` is `[.. rejectedBeforeLookup, .. resolveResult?.Rejections ?? []]`, so it is
the complete set in every state by construction. The alert's wording branches three ways -
validation failed, nothing usable, or a mixed file with rows still to validate - but the
REACHABILITY does not branch at all.

Still exactly two `DownloadRejections` call sites: this one, and the contextual one inside the
validated summary.

## Files changed

- `Components/Pages/Comms10k.razor`
- `Modules/ModuleCatalog.cs` (Comms-10k `1.4.2` -> `1.4.3`)
- `ExchangeAdminWeb.Tests/Comms10kRejectionReportTests.cs`

## Guard proof

Two tests. `AFileWithNoUsableRowsStillOffersTheErrorsReport` (mde-5's) moved with the condition
- it now pins the all-invalid BRANCH rather than the old gate, so that case stays covered. The
new `TheErrorsReportIsReachableFromEveryStateThatHasRejections` pins the SHAPE: the exact
widened condition, the absence of the old narrow gate, and that the offer renders before and
outside the success panel.

| Mutation | Result |
| --- | --- |
| restore the round-2 gate `parsedEmails.Count == 0 && rejectedBeforeLookup.Count > 0 && resolveResult == null` | FAIL; restored -> passes |

Page restored byte-identical (md5 `9ed4532b...`) and touched.

**A real compile error was caught on the way and is worth recording**: `<text> </text>` followed
by bare prose inside an `@if` block does not parse - Razor reads the prose as C# and emits
`CS0246: The type or namespace name 'can' could not be found`. The trailing sentence has to sit
INSIDE the `<text>` element. The first test run after that mistake reported a stale PASS/FAIL
from the previous binary because the build had failed and `--no-build` ran the old DLL; the
error was only visible once the build output was grepped for `error CS` rather than tailed.

## Coder dispute

None.

## Known gaps

Source-text guard over a `.razor` file, not behaviour - same limit as `mde-5` and `mde-6`, and
the reviewer named this class explicitly. It proves the condition and the placement; it does
not prove the alert renders.

The wording branches are not individually pinned. A future edit could garble the mixed-file
sentence without failing anything. That is deliberate - pinning prose is what made `mde-3`'s
first guard useless - but it is a gap, not an absence of one.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Round 3 of the stream, over `96cdb89..b3099fe`. Verdict: **findings** (2). Finding 2 of 2.
Capability proof: both the file read and `git diff --stat ac71855..b3099fe` succeeded.
Raw output: `.agents/review/out/mde-r3.txt`.

The reviewer also named the guards it believed would pass against broken code, including
`AFileWithNoUsableRowsStillOffersTheErrorsReport`, and it was right - that test counted two
download call sites and could not see which states reach them.

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master`. Round 3 was not clean, so round 4 is owed. Not yet pushed - push
policy is ask.
