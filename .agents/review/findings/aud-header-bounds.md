# aud-header-bounds: the routing list was uncapped, and the caps that did exist were silent

**Severity**: MEDIUM. Not a security boundary - the input is already behind module
authorization - but a pasted message with thousands of `Received:` lines did unbounded work
on a Blazor Server circuit, and an analyst could not tell a complete routing path from a
truncated one.
**Status**: Verified and fixed
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

From the lower-priority Wave 4 list of the 2026-10-16 audit: *"Header-processing bounds:
capped raw headers still feed uncapped derived routing collections; large-input runtime
impact unmeasured."*

**The audit was right, and for a reason that is easy to miss.** `EmailHeaders.AddHeader`
enforces `MaxTotalHeaders = 500` and `MaxValuesPerHeader = 50`, so the `Headers` dictionary
is bounded. But `ReceivedHeaders` is a **separate list, filled directly by
`ProcessSpecialHeaders`**, which never calls `AddHeader`. None of those ceilings touch it.

Measured, not argued: a pasted message with 5,000 `Received:` lines produced **5,000**
parsed hops, 5,000 routing rows and 5,000 regex passes.

**The second half was not in the audit at all**: the caps that did exist dropped headers
*silently*. A message cut off at 500 headers or 50 values rendered as a complete one, and
the earliest surviving `Received` hop reads as the origin when it is not - which is the
single thing an analyst uses this screen to determine.

## Predicted observable failure

An analyst pastes a message crafted with thousands of forged `Received:` headers - the
classic way to bury a real origin. The page does thousands of regex passes and renders
thousands of rows on the circuit. Worse, if the parser's own ceilings have trimmed the
header set, the hop the analyst reads as the origin is simply the oldest one that survived
truncation, with nothing on screen to say so.

## Approach

**A ceiling on the Received list itself**, where none existed: `MaxReceivedHeaders = 250`.
Far above anything real - RFC 5321 suggests refusing mail after 100 hops - because it is
there to bound work, not to second-guess the operator.

**Every truncation is reported.** `EmailHeaders.HeadersTruncated` is set wherever
`AddHeader` drops something, `ReceivedHeadersTruncated` where the Received list fills, and
both are carried to `HeaderAnalysisResult.RoutingPathTruncated` /
`InterestingHeadersTruncated`. `MessageTrace.razor` renders a warning saying in terms that
the earliest hop shown is **not** the origin.

**Runtime is measured**, which the audit noted it was not: a 20,000-hop message is asserted
to analyse in under ten seconds. The threshold is loose on purpose - the claim is that the
work is bounded, not that it hits a number on this host.

## Files changed

- `Services/HeaderAnalysisService.cs` - the Received ceiling, propagation of both flags
- `Models/MessageForensicsModels.cs` - `HeadersTruncated`, `ReceivedHeadersTruncated`,
  `RoutingPathTruncated`, `InterestingHeadersTruncated`
- `Components/Pages/MessageTrace.razor` - two warnings (ClickGate-exempt, "tier 4")
- `ExchangeAdminWeb.Tests/HeaderBoundsTests.cs`

Module-scoped; no shared code. MessageTrace's module version bumped.

## Guard proof

**Behavioural, and gratifyingly so** after a long run of source scans: `HeaderAnalysisService`
parses text and needs nothing else, so these drive real messages through the real analyser.

| Test | What it pins |
| --- | --- |
| `AnAbsurdNumberOfReceivedHopsIsBounded` | 5,000 lines in, at most 250 hops out |
| `TruncationIsReported_NotSilent` | both flags set - the defect the audit did not name |
| `AnOrdinaryMessageIsNotReportedAsTruncated` | a warning that cried wolf would be ignored |
| `TheParserItselfReportsWhatItDropped` | `MaxTotalHeaders` is no longer silent |
| `AParserWithinItsLimitsReportsNothing` | the flag is not simply always true |
| `RepeatingOneHeaderPastItsLimitIsAlsoReported` | `MaxValuesPerHeader`, the bound that catches a flood under one key |
| `ALargeMessageAnalysesInReasonableTime` | the "unmeasured" half, measured |

| Mutation | Result |
| --- | --- |
| the Received ceiling removed | FAIL - `AnAbsurdNumberOfReceivedHopsIsBounded` |
| `HeadersTruncated` not set by `AddHeader` | FAIL - `TheParserItselfReportsWhatItDropped` |

Both sources restored byte-identical (md5).

## This finding nearly got un-fixed, and the test is why it did not

A first pass added the Received ceiling. A boundary test then failed, and I read that as
proof the ceiling was redundant behind `MaxValuesPerHeader` - so I **deleted it**, wrote a
confident note that the audit's premise was half wrong, and moved on.

The next run said `got 5000`. The boundary test had failed for an unrelated reason, and the
Received list was never bounded by anything. The deletion would have shipped a record
claiming a protection existed where none did.

Source-scanning guards would not have caught that; only running the real parser over a real
5,000-hop message did. It is the clearest case this stream has produced for driving
behaviour wherever a seam allows it.

## Known gaps

- **250 is a judgement, not a measurement.** No real message approaches it; nothing says it
  is the right number if one ever does.
- **The warnings are not tested for rendering.** There is no bUnit harness, so that
  `MessageTrace.razor` shows them is read, not driven.
- **`MaxSplitValues = 100`** bounds comma/semicolon-separated values but has no test of its
  own; it is exercised only incidentally.
- **The runtime assertion is machine-dependent** and will be worthless on a very slow CI
  agent, where it would fail loudly rather than silently - the better direction.

## Closeout

Fixed on `master`. Independent review not yet run on this finding.
