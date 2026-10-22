# aud-a7f: The Check Status tab and the owner notice's Date line still show times with no zone

**Severity**: LOW - same ambiguity aud-a7e removed from the Scheduled notice, left in two other
places: the Out of Office Check Status tab renders a read schedule's start and end with no zone,
and the owner notice's "Date:" line is the server clock with no zone.
**Status**: Merged to master (`f18431d0`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-aeb07a6371bf7b965` (forked at `36fb150d`)
**Commit**: `58ccceac` (merged `f18431d0`)
**Reviewer**: implementer (surfaced by the aud-a38i/a7e agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At the aud-a7e branch tip `9b3080ee`, `Components/Pages/OutOfOffice.razor`, the Check Status tab
renders `StartTime`/`EndTime` from `Get-MailboxAutoReplyConfiguration` as bare clock times;
`Services/EmailService.cs`, the owner notice template writes `Date: <DateTime.Now>` (server local,
unzoned). aud-a7e defined the schedule zone at the write boundary (the operator's browser zone,
converted to UTC instants) and named it in the Scheduled notice.

## Approach (to be written by the implementing agent before code)

- Check Status: convert the read schedule (Exchange returns UTC-kind instants; label any
  assumption about the cmdlet's kind as such) into the operator's browser zone already captured by
  aud-a7e and render it with the zone's name, the same way the Scheduled notice does.
- Owner notice Date line: render the instant with its zone (UTC with an explicit "UTC", or the
  operator's zone captured for the operation, whichever the record argues is less surprising for
  the mailbox owner; state the choice).
- Tests through the aud-a7b/a7d/a7e harness: the Check Status rendering names the zone and shows
  the converted time; the Date line names its zone. Mutation at the call site: drop the zone from
  each rendering.
- `ClickGateRegistry`: the page is tier 3 with no line-keyed entries; confirm. `ProgressRegistry`
  pins unchanged.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Module-scoped: Out of Office (its version bump is the supervisor's; `Modules/ModuleCatalog.cs` is not
edited here). No new JS interop. Written before the code.

**Check Status: the operator's browser zone**, the `scheduleZone` aud-a7e already reads on the first
render.

1. `Models/LookupModels.cs`, beside `ZoneName`/`OffsetAt`: one helper that shows a time Exchange
   reported in a zone, by what its `Kind` says:
   - `Utc`, or `Local` (in .NET, a time in the server's own zone - converted with `ToUniversalTime`):
     each names an instant, shown as the wall-clock time it reads in the zone with
     `(<ZoneName>, UTC<offset at that instant>)` - the notice's form. The offset is taken from the
     instant, not from the wall clock (`OffsetAt`), so the two 01:30s of a repeated hour read
     `UTC-04:00` and `UTC-05:00`. The offset text comes from the formatter `OffsetAt` uses, so there
     is one definition of it.
   - `Unspecified`: shown as reported with "(time zone not reported)", no zone and no offset. An
     unzoned value names no instant; reading it as UTC would print a confident label that is wrong by
     the zone's offset whenever that guess is wrong - the ambiguity aud-a7e removed from the write.
   - **Assumption, unverified (no live Exchange call):** the `Kind` that
     `Get-MailboxAutoReplyConfiguration`'s `StartTime`/`EndTime` reach `OutOfOfficeService` with,
     through the Exchange Online module. The fix depends on no answer to it: each `Kind` is shown for
     what it states, and the one that states nothing is not given a zone.
2. `Components/Pages/OutOfOffice.razor`: the Check Status schedule line renders both times through
   the helper. With the browser's zone not known (not yet read, or unreadable) the instant is shown
   in UTC, labelled `UTC` - a named reference for a known instant, not a guessed zone (aud-a7e's
   no-guess rule is about input, where the zone is what names the moment).
3. **Owner notice `Date:` line: UTC, with the literal "UTC".** `EmailService.BuildOofNotification`
   takes the change instant as a `DateTimeOffset` (an instant whatever offset it carries);
   `SendOofNotificationAsync` hands it `DateTimeOffset.UtcNow`; the line prints the time in UTC
   followed by `UTC`, in the notice's existing date format. Chosen over the operator's zone: the
   reader is the mailbox owner, who may be anywhere and has no tie to the operator's browser, and the
   request carries a zone only when it is Scheduled (aud-a7e, `TheRequestKeepsOnlyWhatItsStateWrites`),
   so Enabled and Disabled notices would have none.
4. **Tests** through the aud-a7b/a7d/a7e harness (`OutOfOfficeRequestCaptureTests`): its
   `Get-MailboxAutoReplyConfiguration` stub returns a schedule each test sets. Check Status renders
   the converted times with zone and offset (New York across the DST end, Kolkata, and the two
   instants of New York's repeated hour); a `Local` read renders the same instant; an `Unspecified`
   read is shown as reported with no zone and no offset; with no browser zone, UTC. The notice's Date
   line converts an instant handed over at another offset and names UTC.
5. **Mutations at the call site**: the Check Status line drops the zone (its pre-fix rendering); the
   Date line drops the zone (its pre-fix `{changedAt:...}`). Also: the helper reads `Unspecified` as
   UTC; the offset taken from the wall clock instead of the instant.
6. `ClickGateRegistry`: the page is tier 3 with no line-keyed entries - confirmed by running the
   ClickGate tests after the edit. `ProgressRegistry`: no handler or lifecycle method is added; its
   pins are unchanged.
7. **Gates**: Release build; the full suite with `--blame-hang`; `dotnet format --verify-no-changes`;
   `git diff --check HEAD`; ASCII lint.

## Guard proof

Built as the Plan. **Check Status: the operator's browser zone** (aud-a7e's `scheduleZone`); UTC,
labelled `UTC`, when the browser has not given one. **What each `Kind` gets**: `Utc` and `Local` are
shown as the instants they name; `Unspecified` is shown as reported, followed by "(time zone not
reported)", with no zone and no offset. **Owner notice `Date:` line: UTC, with the literal "UTC"**.
Each choice is argued in the Plan.

- `Models/LookupModels.cs`: `OutOfOfficeRequest.ShowReported(reported, zone)` - the wall clock the
  instant reads in the zone, `yyyy-MM-dd HH:mm` (invariant culture), then `(<ZoneName>, UTC<offset at
  that instant>)`; `OffsetAt` and it share one offset formatter.
- `Components/Pages/OutOfOffice.razor`: the Check Status schedule line renders both times through
  `ReadScheduleTime`, which hands `ShowReported` the browser's zone or `TimeZoneInfo.Utc`. An end time
  Exchange did not return still renders nothing, as before. No new JS interop.
- `Services/EmailService.cs`: `BuildOofNotification` takes the change instant as a `DateTimeOffset`;
  `SendOofNotificationAsync` hands it `DateTimeOffset.UtcNow`; the line prints its UTC time and
  `UTC`, HTML-encoded like the window's lines.

| Test (`ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs`) | Pins |
| --- | --- |
| `TheStatusCheck_ShowsAReadScheduleInTheBrowsersZone_WithTheZoneAndEachTimesOffset` (3 cases) | **the finding, Check Status**: the page's real `CheckStatus` over the real service, the stub reporting Kind-Utc times; the rendered tab shows each in the browser's zone with zone and offset - New York across the DST end (`UTC-04:00`, `UTC-05:00`), New York's repeated hour (05:30Z and 06:30Z both read 01:30, `UTC-04:00` then `UTC-05:00`), Kolkata (`UTC+05:30`); literal expectations |
| `AReadScheduleOfKindLocal_IsShownAsTheSameInstant` | a `Local` read is the same instant |
| `AReadScheduleThatNamesNoZone_IsShownAsReported_AndIsGivenNone` | `Unspecified`: the times as reported, and neither the zone id nor "UTC" anywhere on the tab |
| `WithoutTheBrowsersZone_AReadScheduleIsShownInUtc_AndSaysSo` | no browser zone: `(UTC, UTC+00:00)` |
| `TheOwnersNotice_DatesTheChangeInUtc_AndSaysSo` (Scheduled, Enabled, Disabled) | **the finding, Date line**: an instant handed over at UTC-04:00 prints as its UTC time followed by `UTC` |
| the harness's `Get-MailboxAutoReplyConfiguration` stub (changed) | reports what each test sets; by default what it reported before |

**Against the unfixed code** - the page and `Models/LookupModels.cs` checked out at `HEAD`, the tests
as changed: 45 `OutOfOffice`/`OofSchedule` tests, 5 failed (the three Check Status cases, the `Local`
case, the no-zone case); restored, touched, rebuilt, 45 passed. Two new tests cannot fail there by
construction: the `Unspecified` one passes (the old line named no zone either; F3 is its proof), and
the Date-line one needs the new `DateTimeOffset` signature to compile (F2 restores the old line).

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the
solution rebuilt (Release) and the `OutOfOffice`/`OofSchedule` tests run, the file restored from its
saved bytes, touched (a restored copy keeps its old mtime and MSBuild would skip the rebuild), md5
re-confirmed equal, rebuilt, and the tests re-run green. First round on the pre-comment bytes (LF:
page `ca90bc1aee89110155a5c2f113d261e4`, `Services/EmailService.cs`
`34f80e24ab662e58dcd8a2b0873ecf19`, `Models/LookupModels.cs` `4d2eabde638f15a80a924f06ca7549a1`);
re-run on the final bytes with the same results - the md5s below are the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| F1: the Check Status line drops the zone (its pre-fix rendering) | page `251247b6985d7f7c3d8a86b0a37f9624` | `775cb8370da9aba65038eda3b269b9a2` | 5: the three Check Status cases, `Local`, no browser zone |
| F2: the Date line drops the zone (its pre-fix `{changedAt:...}`) | EmailService `1603445a512d3db84a1a5291871a9e51` | `5d48dd21fad10869d374cc7dae7dab33` | 3: `TheOwnersNotice_DatesTheChangeInUtc_AndSaysSo`, all three states |
| F3: a time of Kind `Unspecified` read as UTC | models `1780ba8440e9ed6484d2a7111a9778b4` | `92bd3cc2e5f8cce3f447513d4152ad6c` | 1: `AReadScheduleThatNamesNoZone_IsShownAsReported_AndIsGivenNone` |
| F4: the offset taken from the wall clock (`OffsetAt`), not the instant | models | `2792786c1ddfa9144cb9027cd6a3298c` | 1: the repeated-hour case (05:30Z labelled `UTC-05:00`) |

F1 and F2 are the two the Approach names. Comments were written after the first round. Final test
file md5 (LF): `OutOfOfficeRequestCaptureTests.cs` `d464d91db8fbe941bc2a6a0599df9423`.

**Pins.** `ClickGateRegistry`: the page is `"tier 3, not approved"` with no line-keyed entries, so the
added lines move nothing - confirmed by the `ClickGate*` tests passing in the full suite.
`ProgressRegistry`: no handler or lifecycle method was added; its pins are unchanged and its tests
pass. `ValidatedWriteCallSite` unchanged and passing.

## Gates

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings - the count aud-a7e
  recorded; none in a changed file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`, on
  the final bytes: 5133 passed, 3 skipped (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable`
  cases), 0 failed - 5136 in all; no hang. Run on top of the `aud-a38j` commit.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add`.

## Files changed

- `Models/LookupModels.cs` - `ShowReported`; `OffsetAt` on a shared offset formatter.
- `Components/Pages/OutOfOffice.razor` - the Check Status schedule line; `ReadScheduleTime`.
- `Services/EmailService.cs` - `BuildOofNotification(..., DateTimeOffset)`; the Date line in UTC.
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs` - the read stub reports a set schedule;
  five tests (nine cases).
- This record.

## Known gaps

- **Assumption, unverified (no live Exchange call):** which `Kind` the Exchange Online module gives
  `StartTime`/`EndTime`. If it is `Unspecified`, Check Status shows the times as reported with "(time
  zone not reported)" - honest, but no zone; one live read would settle it. aud-a7e's assumption about
  Kind-Utc writes is the same unknown from the other side.
- **No browser ran**; the browser's zone came from the harness's fake `IJSRuntime`, as in aud-a7e.
- The notice's Date line keeps the server's culture for the month name and AM/PM, as its From and
  Until lines always have.
- Out of Office module version bump is the supervisor's.

## Closeout

Fixed on branch; not merged, not pushed.
