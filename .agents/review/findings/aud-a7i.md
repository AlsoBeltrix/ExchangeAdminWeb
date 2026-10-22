# aud-a7i: Check Status names an unreadable schedule value's type but not the value, and hides the line when only an end was reported

**Severity**: LOW - no wrong write. (1) Since aud-a7h, a schedule time Exchange reports in a form the
app cannot read is shown as "reported in a form this app cannot read: System.String", but the text
itself is not shown, although showing it word for word involves no guess and would let the operator
see what Exchange said. (2) A Scheduled auto-reply whose start is not reported (null) but whose end
is shows no schedule line at all; the end is dropped silently. Pre-existing.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38k/a7h agent, 2026-10-22, candidates 2 and 4 of its hand-back)

## Evidence

At `0187257e`: `Models/LookupModels.cs` `ReportedTime` / `ShowReported(ReportedTime, zone)` (the
marker names the type only); `Components/Pages/OutOfOffice.razor`, the Check Status schedule line
(`checkResult.StartTime is { } start` gates the whole line).

## Approach (to be written by the implementing agent before code)

- The unreadable marker carries the raw text (HTML-encoded, length-capped) beside the type name:
  "reported in a form this app cannot read (System.String): <text>". No parsing, no guess.
- The schedule line renders whenever either a start or an end was reported; a missing one reads
  "(not reported)". Tests through the a7b..a7h harness: an unreadable string shows its text; an
  end-only schedule shows the end and "(not reported)" for the start. `ClickGateRegistry`: the page
  is tier 3 with no line-keyed entries; confirm. Mutations at the call site: drop the text from the
  marker; gate the line on the start again.

## Guard proof

Pending.
