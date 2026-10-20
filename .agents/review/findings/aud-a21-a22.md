# aud-a21 / aud-a22: an ARC pass masked a real auth failure, and a spam finding never fired

**Severity**: HIGH for A21 - a forged or forwarded message can be reported as passing SPF,
DKIM and DMARC when the receiving system said all three failed. MEDIUM for A22.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Findings 21 and 22 of the 2026-10-16 audit, verified against the code.

### A21 - ARC overwrote the authoritative verdict

`Services/HeaderAnalysisService` routed two different headers into one parser:

```csharp
case "authentication-results":
case "arc-authentication-results":
    ParseAuthenticationResults(headerValue, headers);
```

and that parser keeps the **first** value it sees:

```csharp
if (spfMatch.Success && headers.Authentication.SPF!.Result == "none")
```

`Authentication-Results` is what the receiving system concluded. `ARC-Authentication-Results`
is what an **intermediary** reported, and intermediaries **prepend** their headers - so the
ARC block normally sits above the real one. First-writer-wins therefore means **ARC wins**.

An ARC pass recorded before a forwarding break masked the `spf=fail` the receiving system
wrote underneath it. The page then displayed PASS for all three mechanisms.

### A22 - the spam thresholds tested for a key the parser never writes

The analyser stores these scores under **expanded** names - `result.SpamScores["Spam
Confidence Level"]`, `["Bulk Complaint Level"]` - and the thresholds tested
`score.Key.Contains("SCL")` and `Contains("BCL")`. "Bulk Complaint Level" contains no "BCL".

**The mutation run refined this, and the audit's wording was slightly too broad.** Reverting
the fix left the SCL finding still working and only BCL broken: the parser also stores every
raw header under its own name, and `X-MS-Exchange-Organization-SCL` does contain "SCL", so
that one matched by accident through the duplicate entry. `BCL` arrives parsed out of
`X-Microsoft-Antispam: BCL:8`, so no raw key carries it, and that finding never fired at all.

## Predicted observable failure

An analyst pastes the headers of a suspicious forwarded message. The page reports SPF, DKIM
and DMARC all PASS, because an upstream relay said so before the break - while this tenant's
own mail system failed all three. The analyst concludes the message is authentic.

## Approach

**A21:** the two headers are routed separately and the parser takes an `isArc` flag. The
direct header **always overwrites**; ARC may only fill a slot nothing authoritative has
written. ARC is not discarded - with no direct header its values are the best available and
are still reported.

**A22:** an `IsScore` helper matches either the abbreviation or the expanded name, so both
spellings work - the expanded keys the parser writes, and the raw ones that reach
`SpamScores` from other headers.

## Files changed

- `Services/HeaderAnalysisService.cs`
- `ExchangeAdminWeb.Tests/HeaderAnalysisMaskingTests.cs`

## Guard proof

**Behaviour, through the real `AnalyzeHeaderText`** - raw header text in, analysed result
out, nothing stubbed. That is the same path the page uses.

| Test | What it pins |
| --- | --- |
| `AnArcPassAboveARealFailure_DoesNotHideTheFailure` | the defect, with ARC above the direct header as it really arrives |
| `ArcStillFillsWhatTheDirectHeaderDidNotSay` | ARC is not simply ignored |
| `ADirectHeaderBelowArc_IsStillReadWhenItAgrees` | the ordinary agreeing case still works |
| `AHighSpamConfidenceLevel_RaisesItsFinding` / `AHighBulkComplaintLevel_RaisesItsFinding` | the two findings fire |
| `ScoresBelowTheThreshold_RaiseNothing` | a fix that always raised them would not pass |

| Mutation | Result |
| --- | --- |
| `mayOverwrite = false` (ARC wins again) and both thresholds back to the abbreviation | FAIL - `AnArcPassAboveARealFailure_...` and `AHighBulkComplaintLevel_...` |

**SCL survived that mutation**, which is how the correction above was found. Source restored
byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4380 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. `MessageTrace` 1.5.9 -> 1.6.0 - minor, because the analyser's
verdict changes for messages carrying ARC.

## Known gaps

- Only the three mechanisms are covered. The parser pulls other fields out of the same header
  (`smtp.mailfrom`, client IP) and those follow the same first-writer rule; they now follow
  the same override, but are not separately tested.
- Multiple ARC sets (`i=1`, `i=2`, ...) are not distinguished from each other. The fix stops
  ARC beating the direct header; it does not reason about the ARC chain's own validity.
- Nothing here validates an ARC seal. A forged ARC header is still believed when no direct
  header exists.

## Closeout

Fixed directly on `master`. Independent review pending.
