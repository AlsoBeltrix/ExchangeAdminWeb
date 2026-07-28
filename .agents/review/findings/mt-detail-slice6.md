# mt-detail-slice6: message-trace per-row Details drill-in + inline trail (slice 6)

**Severity**: n/a — slice-landing review of a UI-only per-row detail drill-in (live path) + its audit
**Status**: Verified — accepted round 2 (r1 fix `6960887`); codex CLI (gpt-5.5-dzs/xhigh/std)
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `68ab114` (slice 6), base `a5f3fa7`; r1 fix `6960887`

## Evidence
`Components/Pages/MessageTrace.razor` — Trace Search results table gains a per-row
**Details** action; clicking calls `MessageTraceService.GetMessageDetailAsync`
(the seam covered by slice 2), renders the trail inline as an expandable
sub-table (Date, Event, Action/Source, Detail), one row open at a time, and
audits the live fetch via `Audit.LogLookupAction` (action `MessageTrace_Detail`).
No other files change.

## Predicted observable failure
Without fail-soft handling a fetch error would blank the results list (Known
Failure Class #2); without the inline Error render an operator would see nothing
on failure. Without the audit call the live detail fetch would be untraced,
violating the Constitution's auditing invariant (every user-facing lookup
audited). The drill-in binds the per-row index by value (`var index = i`) inside
a `@for` loop so each row's button targets its own message, not the loop's final
element.

## What
Sixth slice of the Message Analysis detail work stream (plan task 6). Adds the
live per-row screen drill-in: an operator clicks Details on any result row and
sees that message's full per-hop delivery trail (never collapsed), fetched on
demand (one cloud `Get-MessageTraceDetailV2` call per click; on-prem free).
Available at any result-set size (decision 2). This is the interactive
counterpart to the off-circuit bulk export (slices 4-5); the checkbox/select-all
+ threshold-driven download/email controls are slice 7.

## Approach
A `@for` loop over `response.Results` (index captured by value) renders each
summary row plus, when `expandedIndex == index`, an inline detail sub-row.
`ToggleDetail(index, msg)` collapses if already open, else sets
`expandedIndex`/`detailLoading`, awaits `GetMessageDetailAsync` (fail-soft: the
seam sets `Error` rather than throwing; a thrown exception is still caught and
mapped to a detail with `Error`), and audits success/failure with the message id
as target and the ticket. A new trace run resets `expandedIndex`/`detail`. The
detail sub-table renders `Action` when present else `Source` (cloud vs on-prem),
so both backends display in one column set.

## Files changed
- `Components/Pages/MessageTrace.razor` — per-row Details action + inline trail +
  ToggleDetail + reset-on-new-trace + state fields (only file).

## Guard proof
UI-only slice over the slice-2 seam; no new service logic, so no new xUnit here
(the seam's routing/no-collapse/error-path guards live in the slice-2 tests). The
audit + fail-soft behavior is exercised on live dev only (no dev tenant —
standing gap). Build 0 errors; `tools/Test-AsciiOnly.ps1` EXIT=0; `dotnet format
--verify-no-changes` EXIT=0; `git diff --check HEAD` clean.

## Coder dispute (if any)
None.

## Known gaps
The checkbox + select-all (cap 50) + threshold-driven action controls (download
vs email-only) + email-job submit are slice 7. Live EXO/on-prem detail fetch and
the Blazor interaction itself are manual-validation-on-dev (no dev tenant).

## Reviewer comments

### Round 1 — reopened (codex CLI, gpt-5.5-dzs/xhigh/std), commit 68ab114, base a5f3fa7
Verdict `reopened`, `guard_confirmed:true`, `capability_ok:true`, SHAs match dispatch.

Guard proofs confirmed (both hold in the diff): the catch branch maps a thrown
detail-fetch exception to an inline `MessageTraceDetail { Error = ex.Message }`
and never assigns `response` (results not blanked); both success and catch
branches call `Audit.LogLookupAction` (action `MessageTrace_Detail`, message-id
target, ticket carried). Per-row binding correct (`@for` + `var index = i`);
cloud-cost on-demand (fetch only from ToggleDetail); state reset on new trace.
Build 0 errors, ASCII + diff-check clean.

**F1 (real, accepted): stale-response race.** Only the currently
expanded/loading row's button is disabled, so a second row can be opened while
the first fetch is still awaiting. `ToggleDetail` writes into the shared
`detail` field with no request/index guard: if the earlier request completes
after a later row is opened, the later row renders the WRONG message's trail,
and the earlier request's `finally` clears the newer request's spinner. Violates
the one-open-at-a-time / opening-another-replaces-it invariant under normal
double-click / slow-cloud latency.

Fix (round 1): disable ALL Details buttons while any fetch is in flight
(`detailLoading`), and guard the post-await writes with a monotonic request
token so a late response for a superseded row is discarded (state only applied
when the awaited request is still the current one).

### Round 2 — accepted (codex CLI, gpt-5.5-dzs/xhigh/std), remediation commit 6960887, base 68ab114
Verdict `accepted`, `guard_confirmed:true`, `capability_ok:true`, SHAs match dispatch.

F1 confirmed closed: every Details button now keys `disabled="@detailLoading"`
(global in-flight flag, not per-row); `ToggleDetail` captures `detailRequestToken`
before the await and gates both the success and failure detail/detailLoading
writes on `token == detailRequestToken`, so a superseded response writes no trail
and does not clear the newer spinner. A new trace run clears loading state and
increments the token, staling any in-flight reply against the new result set.
Audit still fires on both branches (the lookup happened); the fail-soft no-blank
behavior is preserved (only `detail` state written, never `response`). Capability
build EXIT=0, ASCII/diff-check clean. No comments beyond the confirming notes.
