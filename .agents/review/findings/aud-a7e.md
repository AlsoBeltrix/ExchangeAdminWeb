# aud-a7e: The Scheduled Out of Office notice shows From/Until with no time zone

**Severity**: LOW - the write is correct; the user-facing notice is ambiguous. A mailbox owner in
another zone, or the operator later, cannot tell whether the window is mailbox time, server time,
operator time or Exchange's reading of the submitted value.
**Status**: Merged to master (`5e9b2c79`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-ab28c8810a2e397d6` (forked at `1049f839`)
**Commit**: `9b3080ee` (merged `5e9b2c79`)
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

Built as the Plan. **Zone: the operator's browser time zone** - capture was feasible with a
moderate page change (one injected `IJSRuntime`, one first-render read, one label, the schedule
fields gated on a known zone), so the server-zone fallback was not needed. The page states it
beside the fields as "Times are in your browser's time zone, <IANA id>."; the request carries the
wall-clock times plus that `TimeZoneInfo`; the service converts to UTC before borrowing a
connection; the notice prints each time as `<time> (<IANA id>, UTC<offset at that time>)`. If the
browser cannot say, or this server cannot resolve its id, scheduling is refused with the reason on
the page - never a guessed zone.

The zone was resolved on this host before relying on it: under .NET 10.0.12,
`TimeZoneInfo.FindSystemTimeZoneById('America/New_York')` returns Id `America/New_York`,
`HasIanaId` true; `Asia/Kolkata` resolves at +05:30; New York reads 2026-10-30 09:00 as UTC-04:00
and 2026-11-06 17:00 as UTC-05:00; 2026-03-08 02:30 there is an invalid (skipped) time, which
`ConvertTimeToUtc` would throw on - hence `ToUtc` returns null for it and both the page and the
service refuse it.

| Test | Pins |
| --- | --- |
| `OutOfOfficeRequestCaptureTests.TheOwnersNotice_NamesTheZone_AndTheOffsetOfEachTime` (Asia/Kolkata; America/New_York across the DST end) | **the finding**: From and Until each carry the zone and that time's offset (`UTC-04:00` then `UTC-05:00` for New York) |
| `AScheduleEnteredInTheBrowsersZone_IsWrittenAsTheSameInstant_InUtc` (same two cases) | **the write boundary**: `-StartTime`/`-EndTime` are bound with the literal UTC instants, Kind Utc |
| `ThePageLabelAndTheNotice_NameTheSameZone_TheOneTheTimesWereEnteredIn` | the page's rendered label (its compiled render tree) names the browser's zone, two schedule inputs are offered, and the notice's From and Until name the same zone |
| `WithoutTheBrowsersZone_AScheduleIsRefused_BeforeAnythingIsAuditedNotifiedOrWritten` | an unreadable browser zone: no schedule inputs, the reason shown, a Scheduled submit refused by validation - nothing read, written, audited or notified, form usable |
| `AnEditDuringTheOperation_ChangesNeitherWhatIsWrittenNorWhatIsRecorded` (auth, read, write; changed) | now asserts the UTC instants; the edit also moves `scheduleZone`, so the write must use the captured zone |
| `TheRequestKeepsOnlyWhatItsStateWrites` (changed) | the zone is kept only when Scheduled |
| `OutOfOfficeWriteIdentityTests.AScheduleThatNamesNoInstant_WritesNothing_AndBorrowsNoConnection` (no zone; a skipped time) | the service refuses at the write boundary without the page: no call, no connection |
| `OofScheduleValidationTests.Scheduled_WithNoTimeZone_ReturnsError`, `Scheduled_AtATimeTheZoneSkips_ReturnsError` | the page's validation; the five existing cases pass a zone and keep their expectations |
| `OutOfOfficeTargetCaptureTests.SetOof_CapturesEveryBoundInputOnce_BeforeTheFirstAwait_AndNeverRereadsOne` (changed) | `scheduleZone` joins the inputs captured once, before the first await, and never re-read |

The existing `aud-a7d` test (`TheMailboxOwnerIsToldWhatWasWritten_AScheduleWithItsWindow`) passes
unchanged: the time it looks for is still printed, now followed by its zone.

Mutations at the call sites, each by exact-text replacement asserted to match once, md5 recorded,
the solution rebuilt (Release) and the `OutOfOffice` tests run (`OofSchedule` too for M7), the file
restored from a saved copy, touched (a restored copy keeps its old mtime and MSBuild would skip the
rebuild), md5 re-confirmed equal; after the last one the solution was rebuilt and 448 targeted tests
(`OutOfOffice`, `OofSchedule`, `ProgressRegistry`, `ClickGate`, `GlobalProgressWiring`,
`ValidatedWriteCallSite`) passed. Pre-comment md5s (LF bytes): page
`9aff4ed144eac2389783b02a7cd0b390`, `Services/EmailService.cs` `8be96ce3e885a1df4221158272190d29`,
`Services/OutOfOfficeService.cs` `9222c9270a2c81b8d66fe6929299e864`.

| Mutation | File | md5 after mutation | Failed |
| --- | --- | --- | --- |
| M1: the notice drops the zone (`When` prints the bare time) | EmailService | `e8fd7373f967ce83b445ab386a5d5f2e` | 3: `TheOwnersNotice_NamesTheZone_...` (both), `ThePageLabelAndTheNotice_...` |
| M2: the write passes the unconverted wall clock (`startTime!.Value` for `startUtc.Value`) | service | `8e9709146be77500ab47800769316ca5` | 5: `AScheduleEnteredInTheBrowsersZone_...` (both), `AnEditDuringTheOperation_...` (auth, read, write) |
| M3: the label names `TimeZoneInfo.Local` (the server's zone) | page | `08ada95944bb467bf979cb08fc3f9cdc` | 1: `ThePageLabelAndTheNotice_...` |
| M4: the capture passes no zone into the request | page | `005b7d3db8fdcf4ed53fc96c5fdd7d62` | 12: every Scheduled harness test (validation now refuses them) and the `aud-a7b` capture scan |
| M5: an unreadable browser zone falls back to the server's | page | `3684e8866cb117fdce65d0381fc3b0f8` | 1: `WithoutTheBrowsersZone_...` |
| M6: the service reads a zoneless schedule as UTC instead of refusing | service | `d6d5bdd8bbf30310175fddc12a86fd1d` | 1: `AScheduleThatNamesNoInstant_...` (no zone) |
| M7: validation lets a zoneless schedule through (the service still refuses) | page | `2ead194fa925732a45a23843df07b0f5` | 2: `Scheduled_WithNoTimeZone_ReturnsError`; `WithoutTheBrowsersZone_...` - the refusal arrives only after the mailbox is read, and is audited and notified |

M1 and M2 are the two the Approach names; M3 to M7 pin the zone's single definition and the fail-
closed paths. Comments were written after the round. Final md5s (LF bytes): page
`b3f35dbce309c72f47efb1b36686a5a7`, `Services/EmailService.cs` `09f1bf8c92d6ab103758813f244c16ab`,
`Services/OutOfOfficeService.cs` `4d3edb308a4d1e5403891d3e9cc071f8`, `Models/LookupModels.cs`
`6268e56b6660d924ed219d1f40beb4c7`, `Components/App.razor` `0df37b820d09296b2b038f21933406d3`.

**Pins.** `ProgressRegistry`: the four Reported calls on `SetOof`, `Email.SendOofNotificationAsync(`
included, are unchanged; the page's new `OnAfterRenderAsync` is an `Exempt` entry with its reason,
as condition 4 requires of every lifecycle method. `ClickGateRegistry`: the page is `"tier 3, not
approved"` with no line-keyed entries, so its added lines move nothing - confirmed by the
`ClickGate*` tests passing after the edit. `ValidatedWriteCallSite` (the write and the read still
take `protectedValidation.Mailbox`) passes.

## Gates

- `dotnet build ExchangeAdminWeb.slnx -c Release` (non-incremental): 0 errors, 61 warnings - the
  count `aud-a7b`/`aud-a7d` recorded; none in a changed file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`, on the final bytes:
  5090 passed, 3 skipped (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases),
  0 failed - 5093 in all, 10 of them new cases; no hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `Models/LookupModels.cs` - `OutOfOfficeRequest.ScheduleZone`; `FromForm` keeps it (and wall-clock
  times) only when Scheduled; `ToUtc`, `ZoneName`, `OffsetAt`.
- `Services/OutOfOfficeService.cs` - converts to UTC before borrowing; refuses no zone or a skipped
  time.
- `Services/EmailService.cs` - From and Until with zone and offset.
- `Components/Pages/OutOfOffice.razor` - `IJSRuntime`; `OnAfterRenderAsync` / `ReadScheduleZoneAsync`;
  the label; fields gated on a known zone; the zone in the capture and the validation.
- `Components/App.razor` - `getBrowserTimeZone()`.
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs`, `OutOfOfficeWriteIdentityTests.cs`,
  `LookupValidationTests.cs`, `OutOfOfficeTargetCaptureTests.cs`, `ProgressRegistry.cs`.
- This record.

## Known gaps

- **Assumption, unverified (no live Exchange call):** that the Exchange Online module carries a
  Kind-Utc `DateTime` to the service as that instant. The fix no longer depends on how an unzoned
  value is read, because it never sends one.
- **No browser ran.** `getBrowserTimeZone` and the first-render read were driven through a fake
  `IJSRuntime`. `Intl.DateTimeFormat().resolvedOptions().timeZone` is standard (ECMA-402); a browser
  without it, or a host whose .NET cannot resolve IANA ids (ICU absent, NLS mode), gets scheduling
  refused with the reason - Enable and Disable still work.
- An hour the clocks repeat (the autumn change) is read as standard time - one of its two instants.
  Documented on `ToUtc`; not separately refused.
- The Check Status tab still prints a read schedule's times with no zone (the read path - not this
  finding). Candidate.
- The notice's "Date:" line is the server's clock with no zone. Candidate.
- `Components/App.razor` is shared by location; the helper is called only by this page. Module
  version bump (Out of Office) is the supervisor's; whether the base version moves too is the
  supervisor's call.

## Closeout

Fixed on branch; not merged, not pushed. Out of Office module version bump due at merge.
