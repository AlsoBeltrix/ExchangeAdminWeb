# aud-a7e: The Scheduled Out of Office notice shows From/Until with no time zone

**Severity**: LOW - the write is correct; the user-facing notice is ambiguous. A mailbox owner in
another zone, or the operator later, cannot tell whether the window is mailbox time, server time,
operator time or Exchange's reading of the submitted value.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress
**Branch**: `worktree-agent-ab28c8810a2e397d6` (forked at `1049f839`)
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (ops-batch-5, `prompt-ops-batch-5.md`, 2026-10-22)

## Evidence

At `9364341c`, `Services/EmailService.cs`, `BuildOofNotification` (around line 416) renders the
Scheduled state as `From: <clock time>` / `Until: <clock time>` from the captured
`OutOfOfficeRequest` (aud-a7b) with no zone named. The page binds `DateTime` values from the
operator's browser form; `Set-MailboxAutoReplyConfiguration -StartTime/-EndTime` receives them
through the Exchange Online session, which interprets an unzoned `DateTime` in the session's own
zone (UTC for the remote session), not the operator's or the mailbox's.

So the ambiguity is not only in the notice: the zone of the schedule is undefined at the write
boundary too, which is why the notice cannot state one.

## Approach (to be written by the implementing agent before code)

- Define the schedule zone once, at the write boundary: the request carries a `DateTimeOffset`
  (or a `DateTime` plus an explicit `TimeZoneInfo`) and the write converts to what the cmdlet
  expects deterministically. The page states the zone next to the schedule fields (the operator
  must know what they are entering; we do not guess). The record must say which zone was chosen
  and why; the recommended choice is the operator's browser zone captured by the page, stated on
  the page and in the notice, with the write converting to UTC for the session.
- The notice renders From/Until with the zone's name or offset.
- Tests through the aud-a7b/a7d harness: the notice names the zone; a schedule entered in one zone
  is written as the equivalent instant; the page label and the notice agree. Mutation at the call
  site: drop the zone from the rendered text; write the unconverted value.
- No live Exchange call. The cmdlet's exact interpretation of an unzoned value is an assumption
  to be labelled as such in the record; the fix removes the dependence on it.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Module-scoped: Out of Office (its version bump is the supervisor's), plus one global JS helper in
`Components/App.razor` - shared by location, used only by this page (the supervisor's call whether
the base version moves too). Written before the code.

**Zone: the operator's browser time zone** (an IANA id), read once per page through JS interop.
Feasible without a large page change - one injected `IJSRuntime`, one first-render read, one label,
the schedule fields shown only once the zone is known - so the server-zone fallback is not used.

1. `Components/App.razor`: `getBrowserTimeZone()` returns
   `Intl.DateTimeFormat().resolvedOptions().timeZone`, beside `getTheme` and `downloadFile` (the
   app's existing interop pattern).
2. Page: `@inject IJSRuntime JS`. `OnAfterRenderAsync(firstRender)` awaits `ReadScheduleZoneAsync()`
   then `StateHasChanged()`. `ReadScheduleZoneAsync` resolves the id with
   `TimeZoneInfo.FindSystemTimeZoneById` into `scheduleZone` and re-bases the default start and end
   to that zone's wall clock; any failure (JS, an id this server cannot resolve) leaves the zone
   null with `scheduleZoneError` - fail closed, no fallback guess. The start/end fields render only
   with a known zone, beside the label "Times are in your browser's time zone, <id>."; otherwise
   the reason shows instead. `SetOof` passes `scheduleZone` into its one capture (`aud-a7b`) and
   validates with it.
3. `OutOfOfficeRequest` gains `TimeZoneInfo? ScheduleZone`, kept only when Scheduled (like the
   schedule); `FromForm` stores the times as wall clock (`DateTimeKind.Unspecified`). Static helpers
   beside it: `ToUtc(wallClock, zone)` (null for a time the zone skips), `ZoneName(zone)` (the id -
   the one name both the label and the notice print), `OffsetAt(wallClock, zone)` (`UTC+05:30`).
4. `ValidateOofSchedule(state, start, end, zone)`: a schedule also needs a zone and times the zone
   does not skip; end after start is compared as instants. Existing messages kept.
5. `OutOfOfficeService.SetOutOfOfficeAsync`: a Scheduled request is converted to UTC before a
   connection is borrowed; with no zone, or a time it cannot convert, it refuses and writes nothing
   (the write boundary does not trust the page to have checked). `-StartTime`/`-EndTime` receive
   `DateTime` values of Kind Utc.
6. `EmailService.BuildOofNotification`: From and Until print the time as entered plus
   `(<zone id>, UTC<offset at that time>)`.
7. `ProgressRegistry`: the page's new `OnAfterRenderAsync` gets an `Exempt` entry with its reason
   (one JS call answered by the browser; no server-side remote, disk or directory work). The four
   Reported pins, `Email.SendOofNotificationAsync(` included, are unchanged. `ClickGateRegistry`
   lists the page as tier 3 with no line-keyed entries - confirmed by running its tests.
8. Tests, through the `aud-a7b`/`a7d` harness with a fake `IJSRuntime` answering an IANA id: the
   notice names the zone and each time's offset; a schedule entered in a zone is written as the
   same instant in UTC (a fixed-offset zone and a DST-straddling one); the page's rendered label and
   the notice name the same zone, the captured one; with no readable browser zone a Scheduled
   submit refuses before anything is audited, notified or written. Service: a Scheduled request
   with no zone writes nothing and borrows no connection. Validation: no zone; a skipped time. The
   harness's existing written-time assertions become the UTC instant; the `aud-a7b` scan adds
   `scheduleZone` to the inputs captured once.
9. Mutations at the call site: the notice drops the zone; the write passes the unconverted wall
   clock value; the page label names the server's zone instead of the captured one.

**Assumption (unverified - no live Exchange call):** how `Set-MailboxAutoReplyConfiguration` reads a
`DateTime` with no zone (the Evidence's reading: the session's zone, UTC). After the fix it is never
handed one: the value is the explicit UTC instant. That the Exchange Online module carries a Kind-Utc
`DateTime` through as that instant is itself an assumption, also unverified here.

## Guard proof

Pending.
