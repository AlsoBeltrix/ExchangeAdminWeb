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

`OnDateRangeChanged` now sets a `eventsRangeStale` flag in its `showUsage` branch, and
`ShowEventsView` consumes it: on the way back to Events it clears the flag and calls
`LoadEvents()` once, then behaves as before.

The reload is deliberately conditional rather than unconditional on every toggle.
`LoadEvents` re-reads every JSONL log file across the whole selected span - a 90-day
range is 90 file reads - and it also resets `expandedRow` to -1 and `currentPage` to 1.
Reloading on every Events click would make an idle Usage/Events/Usage flick pay that
scan repeatedly and would silently collapse an expanded row and throw away the
operator's page position for no reason. The flag makes the cost land exactly when the
data is actually wrong.

The flag is set rather than the events reloaded in place, for the same reason: an
operator dragging the date inputs while reading Usage aggregates should not be paying
a log scan for a table they are not looking at.

The symmetric direction needed nothing: `ShowUsageView` already calls `LoadUsage`
unconditionally, and the usage aggregates are three SQL aggregates rather than a file
scan.

## Files changed

- `Components/Pages/AdminEventLog.razor` - `eventsRangeStale` field;
  `OnDateRangeChanged` sets it in the usage branch; `ShowEventsView` grows a body that
  consumes it. Each carries the reasoning as a comment.
- `ExchangeAdminWeb.Tests/UsageTrackerWiringTests.cs` - one test plus a `MethodBody`
  helper.
- `ExchangeAdminWeb.csproj` - base app version 2.20.0 -> 2.20.1, carrying the whole
  utei batch (utei-1..4); `Modules/ModuleCatalog.cs` - AdminEventLog module version
  1.2.0 -> 1.2.1 for this page-scoped behaviour change. Two independent rules, both
  fired (Constitution, Deployment And Versioning).

## Guard proof

Test: `EventLog_ReloadsEventsWhenTheRangeMovedInTheUsageView`
(`ExchangeAdminWeb.Tests/UsageTrackerWiringTests.cs`). A source-text guard, matching
the rest of that class: the repo has no bUnit dependency and the plan forbids adding
one.

The assertions are anchored INSIDE the method that has to carry each statement, using
a new `MethodBody` helper that brace-matches a method out of the page source. Loose
`Assert.Contains` calls over the whole 1300-line file would pass on a page that merely
mentions `eventsRangeStale` somewhere - the false-coverage trap recorded on blr-4. So
the test proves: the usage branch of `OnDateRangeChanged` (the text before its `else`)
sets the flag; `ShowEventsView` still clears `showUsage`, gates on
`if (eventsRangeStale)`, and has both `LoadEvents();` and the flag reset positioned
after that gate.

Mutation probe (non-vacuity): `ShowEventsView` reverted to the pre-fix
`showUsage = false;` body - exactly this test fails, the other 12 in the class pass.
Restored from a copy outside the working tree, not by `git checkout`, and re-stamped
(`(Get-Item $path).LastWriteTime = Get-Date`) so MSBuild actually rebuilt.

Verification after restore: `dotnet build ExchangeAdminWeb.slnx -c Release` succeeded
(0 errors; only the pre-existing CS8604 and the NU1903 package advisories);
`dotnet test ExchangeAdminWeb.slnx` 2385 passed / 0 failed / 3 skipped; `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; `git diff --check HEAD`
exit 0. No `.ps1`/`.psm1` touched, so ScriptAnalyzer and Pester are not in this gate.
The one non-ASCII hit in `AdminEventLog.razor` (the up/down arrows at :284) predates
this change and is untouched; the ASCII rule covers `.cs`/`.ps1`/`.psm1`.

## Coder dispute (if any)

None. Verified against the cited lines; the symmetric direction (change dates in
Events, switch to Usage) is already safe because `ShowUsageView` calls `LoadUsage`.

## Known gaps

The guard is a source-text assertion, so it proves the code SHAPE, not the rendered
behaviour: a refactor that keeps the flag and the gate but breaks the toggle some other
way would still pass. That is the standing limitation of every razor guard in this
repo, not something new here.

The flag is one-way and coarse: any range edit made in Usage marks Events stale, even
if the operator sets the dates back to what they were. The cost of that is one extra
log scan on the next Events click, which is the pre-fix behaviour for a real change
anyway.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as utei-1)
Harness: codex-cli 0.153.2. Reviewed `e33b201~1..dea899b`, verdict `findings`,
capability_ok true. Envelope `.agents/review/ute-impl.result.json`.
