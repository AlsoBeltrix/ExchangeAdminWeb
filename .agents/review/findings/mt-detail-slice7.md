# mt-detail-slice7: selection + threshold-driven download/email of detail export (slice 7)

**Severity**: n/a — slice-landing review of the selection UI + threshold-driven download/email actions
**Status**: Verified — accepted round 1; codex CLI (gpt-5.5-dzs/xhigh/std)
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `99cf4a1` (slice 7), base `1dd74e1`

## Evidence
`Components/Pages/MessageTrace.razor` — Trace Search results gain per-row
checkboxes + a select-all box (caps at first 50), a selection-count-driven action
bar (Download details / Email details), the download path (fetch each selected
detail + shared `MessageTraceDetailReport.BuildCsv` + `downloadFile` interop), the
email path (enqueue `MessageTraceDetailJobProcessor` bulk job via
`BulkJobService`), a read-only destination display, and audit for each action. New
injects: `BulkJobService`, `IConfiguration`. No other files change.

## Predicted observable failure
Without the threshold gate at the action a >10 selection could download live
(cost blow-up / decision-4 violation); without the 50 cap select-all or manual
ticking could submit an unbounded job. Without gating the destination to the
authenticated identity + admins an operator could exfiltrate to a typed address
(decision 6). Without audit the download/email-submit would be untraced. The core
guards: `ResolveAction` disables Download above `LiveMax` AND the action re-checks
it (UI hiding is not the gate); the payload carries only `userEmail` (never a
typed address); the destination display is derived from `userEmail` + admin
config, not an input; both actions audit success and failure.

## What
Seventh slice of the Message Analysis detail work stream (plan task 7). Adds the
interactive selection + the two threshold-driven delivery paths on top of the
already-tested slice 3-5 primitives (the pure report/threshold helper, the email
method + resolver, and the bulk-job processor). 1-10 selected -> live download
(email also allowed); 11-50 -> email-only; select-all and manual ticks cap at 50.

## Approach
`selectedIndices` (HashSet<int>) tracks the ticked rows.
`MessageTraceDetailReport.ResolveAction(count)` drives which buttons are enabled;
`SelectAllCount` caps select-all at `EmailMax`; a manual tick past 50 is ignored.
`DownloadSelectedDetails` re-checks the threshold, fetches each selected detail on
demand, builds the CSV via the shared pure builder, and downloads it via interop.
`EmailSelectedDetails` builds a `MessageTraceDetailJobPayload` (Messages +
userEmail only) and enqueues a `BulkJob` for `MessageTraceDetailJobProcessor` via
`BulkJobService.Enqueue`; the zipped report is delivered off-circuit by the
processor to the authenticated user + admins (decision 6 — never operator-typed).
`DestinationDisplay()` derives the read-only recipient string from `userEmail` +
`Email:AdminNotificationEmail`, mirroring `EmailService.ResolveMessageTraceRecipients`.
Selection + banners reset on a new trace.

## Files changed
- `Components/Pages/MessageTrace.razor` — checkboxes + select-all + action bar +
  download/email handlers + destination display + audit + state (only file).

## Guard proof
UI-only slice over already-tested primitives (report/threshold helper: slice 3
tests; email/resolver: slice 4 tests; processor: slice 5 tests), so no new xUnit
here. Static guard: the threshold is enforced both in the button `disabled`
bindings AND re-checked inside `DownloadSelectedDetails`/`EmailSelectedDetails`;
the email payload carries only `userEmail` (no address input exists); the
destination display is derived, not typed. Full suite 807/807 pass; build 0
errors; `MessageTrace.razor` verified pure ASCII; `dotnet format
--verify-no-changes` and `git diff --check HEAD` clean.

## Coder dispute (if any)
None.

## Known gaps
The Blazor selection interaction itself, live EXO/on-prem detail fetch for the
download path, and real SMTP/zip delivery for the email job are
manual-validation-on-dev (no dev tenant). Module version bump (1.1.1 -> 1.2.0)
is slice 8.

## Reviewer comments

### Round 1 — accepted (codex CLI, gpt-5.5-dzs/xhigh/std), commit 99cf4a1, base 1dd74e1
Verdict `accepted`, `guard_confirmed:true`, `capability_ok:true`, SHAs match dispatch.

All mandate points confirmed against the diff:
- Exfiltration gate: destination is read-only `DestinationDisplay()` (no address
  input); the enqueued payload carries only `UserEmail = userEmail.Trim()` — no
  operator-typed value can become a recipient.
- Threshold NOT UI-only: `DownloadSelectedDetails` re-checks
  `ResolveAction(count)==LiveOrEmail` and `EmailSelectedDetails` re-checks
  `count<=EmailMax` inside the handlers, not just the button `disabled` bindings.
- Selection cap: `ToggleSelectAll` uses `SelectAllCount` (caps at EmailMax);
  `ToggleRowSelection` ignores a manual tick past EmailMax.
- Audit: download and email-job submit each audited on success and failure with
  message id(s) as target and the ticket.
- No silent drop / order: `SelectedMessages` orders selected indices in result
  order and filters to the current result set; the download builds the CSV via the
  shared `MessageTraceDetailReport.BuildCsv`.
- State hygiene: a new trace clears selection + action banners.
Capability build EXIT=0 (existing NU1903 warnings only). No findings.
