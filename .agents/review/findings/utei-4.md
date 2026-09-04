# utei-4: Changing the date range in the Usage view leaves the Events table stale

**Severity**: MEDIUM - the Events table, its count and its CSV export keep showing the
OLD date range while the date inputs above them show the new one. The operator is
reading audit evidence for a period that is not the one on screen.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/utei-4.md`

## Evidence

`Components/Pages/AdminEventLog.razor:46,50` - both date inputs call
`OnDateRangeChanged` via `@bind:after`. `Components/Pages/AdminEventLog.razor:1224-1233`
- `OnDateRangeChanged` calls `LoadUsage()` when `showUsage` is true and `LoadEvents()`
otherwise, so an edit made in the Usage view never reloads events.
`Components/Pages/AdminEventLog.razor:1212` - `ShowEventsView()` is
`showUsage = false;` and nothing more. The date inputs are rendered OUTSIDE the
`@if (!showUsage)` block (`:43-51`), so they are editable in both views.

## Predicted observable failure

Open Event Log (Events view loads, say, the last 7 days). Click Usage. Change Start
Date to 90 days ago - the usage aggregates refresh correctly. Click Events. The date
inputs read 90 days ago, but the event table, the result count and the CSV export are
still the 7-day set, and stay that way until some other event-side filter fires
`LoadEvents`. An operator exporting for an investigation gets a CSV whose range does
not match the range displayed.

## What

The view toggle was treated as pure presentation, but the two views share the date
inputs and each keeps its own loaded data. Switching back has to reconcile the data
with the controls.

## Approach

TBD - fix commit will fill this in.

## Files changed

TBD

## Guard proof

TBD

## Coder dispute (if any)

None. Verified against the cited lines; the symmetric direction (change dates in
Events, switch to Usage) is already safe because `ShowUsageView` calls `LoadUsage`.

## Known gaps

TBD

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as utei-1)
Harness: codex-cli 0.153.2. Reviewed `e33b201~1..dea899b`, verdict `findings`,
capability_ok true. Envelope `.agents/review/ute-impl.result.json`.
