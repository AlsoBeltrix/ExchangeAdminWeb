# aud-a7h: A read schedule of an unexpected type silently disappears from Check Status, and the repeated-time refusal says "hour" for any repeat amount

**Severity**: LOW - no wrong write. `GetOutOfOfficeAsync` reads `StartTime`/`EndTime` from
`Get-MailboxAutoReplyConfiguration` with `as DateTime?`; if the Exchange Online module hands back
another type (a `DateTimeOffset`, a string, a wrapped PSObject), the cast yields null and the
Check Status tab shows a Scheduled auto-reply with no window, as if none were set, with no sign
that a value was dropped. Separately, the aud-a7g refusal says the clocks "repeat that hour" for a
zone whose clocks go back by another amount; the check itself is correct for any amount.
**Status**: Fixed on branch; closed on guard proof; review pending
**Branch**: `worktree-agent-a6125ee40db9873ac` (forked at `4a8021a8`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38j/a7f/a7g agent, 2026-10-22, candidates 2 and 3 of its hand-back)

## Evidence

At `f18431d0`, `Services/OutOfOfficeService.cs` `GetOutOfOfficeAsync` (the `as DateTime?` reads);
`Models/LookupModels.cs` `NoInstantReason` (the "repeat that hour" wording).

## Approach (to be written by the implementing agent before code)

- Read the two times through one helper that accepts `DateTime`, `DateTimeOffset`, a parseable
  string and a wrapped PSObject, and otherwise returns a "not readable" marker that Check Status
  renders as "schedule reported in a form this app cannot read: <type name>" instead of nothing.
  The onus is on the data; nothing is guessed. Label the live module's actual type as an unverified
  assumption in the record.
- Wording: "repeat that time" or "go back and repeat it", not "hour"; the test asserts the message
  does not assume an hour (or simply that the refusal fires for a zone with a 30-minute repeat, using
  a custom `TimeZoneInfo` with such an adjustment rule).
- Tests through the a7b/a7d/a7e/a7f harness: a `DateTimeOffset` read renders as the same instant; a
  string read renders; an unreadable type renders the marker, never an empty window. Mutation at the
  call site: `as DateTime?` again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Module-scoped: Out of Office (its version bump is the supervisor's; `Modules/ModuleCatalog.cs` is not
edited here). No new JS interop, no new handler or lifecycle method. Written before the code.

**Part 1 - a read schedule of another type (LOW).**

1. `Models/LookupModels.cs`, beside `OutOfOfficeResult`: `ReportedTime` - one schedule time as
   `Get-MailboxAutoReplyConfiguration` reported it: either the time (a `DateTime`, whose Kind says
   whether it names an instant - `aud-a7f`), or the name of the type that came back in its place.
   `ReportedTime.Read(object?)` is the one reader:
   - null: null (nothing reported - the page shows no schedule line, as before);
   - a `PSObject`: read as the object it wraps;
   - `DateTime`: as is; `DateTimeOffset`: its instant, as Kind Utc;
   - a string in an ISO 8601 form - year first, `yyyy-MM-dd`, `T` or a space, `HH:mm[:ss[.fffffff]]`,
     then `Z`, an offset, or nothing - parsed with the invariant culture and `AdjustToUniversal`: with
     `Z` or an offset it is that instant (Kind Utc); with neither it names no zone (Kind Unspecified,
     shown as reported, `aud-a7f`);
   - anything else, a string in any other form included: not readable, carrying its type name - for a
     `PSObject` its first type name, which for a deserialized object says what it was.
   Nothing is guessed: a culture-formatted text such as `01/02/2026` is a different day in different
   cultures, so it is not read; the marker says what came back instead.
   `OutOfOfficeResult.StartTime`/`EndTime` become `ReportedTime?`.
   `OutOfOfficeRequest.ShowReported(ReportedTime, TimeZoneInfo)`: a readable time exactly as the
   `aud-a7f` overload shows it; an unreadable one as "reported in a form this app cannot read: <type
   name>".
2. `Services/OutOfOfficeService.cs`, `GetOutOfOfficeAsync`: both times through `ReportedTime.Read`.
3. `Components/Pages/OutOfOffice.razor`: the Check Status schedule line shows whenever Exchange
   reported a start (readable or not), each time through the new overload; an end Exchange did not
   return still renders nothing. `ReadScheduleTime` takes a `ReportedTime`.
4. **Assumption, unverified (no live Exchange call):** the type the Exchange Online module hands
   `StartTime`/`EndTime` back as. The old reads assumed `DateTime`; this fix depends on no answer -
   each accepted form is shown for what it states, and anything else is named on the tab.

**Part 2 - the repeated-time wording (LOW).**

5. `NoInstantReason`: "occurs twice in <zone>: the clocks go back and repeat that time, so it could
   mean either of two moments; choose a time the clocks do not repeat". The check
   (`zone.IsAmbiguousTime`) is unchanged; the skipped-time wording is unchanged.

**Tests.**

- Harness (`OutOfOfficeRequestCaptureTests`, the read stub reporting what each test sets; New York as
  the browser's zone; literal expectations): a `DateTimeOffset` read (handed over at +05:30) renders as
  the same instant as the `aud-a7f` Kind-Utc case; `PSObject`-wrapped `DateTimeOffset` and `DateTime`
  render the same; an ISO string with `Z` and with an offset renders as its instant, one with neither
  as reported with "(time zone not reported)"; an `Int32`, a culture-formatted string and a
  `PSCustomObject` carrying a deserialized type name each render the Schedule line with the marker
  naming that type - never an empty window.
- `OofScheduleValidationTests`: a custom `TimeZoneInfo` whose clocks go back 30 minutes (02:00 to 01:30
  on 1 November): 01:30 and 01:59 are refused, naming the time and the zone, with a reason that says no
  "hour"; 01:15 and 02:00 that day are accepted; `ToUtc` gives no instant for the repeated time.

**Mutations at the call site**, each by exact-text replacement asserted to match once, md5 before and
after, the solution rebuilt (Release), the `OutOfOffice`/`OofSchedule` tests run, the file restored,
touched, md5 re-confirmed, rebuilt, rerun green:

- H1 (the one the Approach names): the service reads both times `as DateTime?` again (through the same
  reader).
- H2: the reason says "repeat that hour" again.
- H3: the reader reads any string the invariant culture's general parse accepts (a culture guess).
- H4: the page renders nothing for an unreadable time.

`ClickGateRegistry` (tier 3, no line-keyed entries) and `ProgressRegistry` (no handler or lifecycle
method added) unchanged - confirmed by their tests in the full suite. **Gates**: Release build; full
suite with `--blame-hang`; `dotnet format --verify-no-changes`; `git diff --check HEAD`; ASCII lint.
Comments written after the mutation round.

## Guard proof

Built as the Plan.

- `Models/LookupModels.cs`: `ReportedTime` - `Time` (a `DateTime`, its Kind as read) or `UnreadableType`,
  and `ReportedTime.Read(object?)`, the one reader; `OutOfOfficeResult.StartTime`/`EndTime` are
  `ReportedTime?`; `OutOfOfficeRequest.ShowReported(ReportedTime, TimeZoneInfo)` shows a readable time
  through the `aud-a7f` overload and an unreadable one as "reported in a form this app cannot read:
  <type name>"; `NoInstantReason`'s repeated-time wording.
- `Services/OutOfOfficeService.cs`: both reads through `ReportedTime.Read`.
- `Components/Pages/OutOfOffice.razor`: the Check Status schedule line shows whenever Exchange reported a
  start (`checkResult.StartTime is { } start`); `ReadScheduleTime` takes a `ReportedTime`. The markup's
  line count is unchanged; the code block gains two comment lines.

| Reported as | Read as | Check Status shows (browser zone New York) |
| --- | --- | --- |
| `DateTime`, any Kind | as is | as `aud-a7f` showed it (unchanged) |
| `DateTimeOffset` | its instant, Kind Utc | e.g. "2026-10-30 09:00 (America/New_York, UTC-04:00)" |
| a `PSObject` | what it wraps | as that |
| ISO 8601 text with `Z` or an offset | that instant, Kind Utc | as the instant |
| ISO 8601 text with neither | Kind Unspecified | "2026-10-30 13:00 (time zone not reported)" |
| anything else, text in another form included | not readable: its type name (a `PSObject`'s first type name) | "reported in a form this app cannot read: System.Int32" |

The repeated-time reason now reads "occurs twice in <zone>: the clocks go back and repeat that time, so
it could mean either of two moments; choose a time the clocks do not repeat". The check is unchanged.

**Assumption, unverified (no live Exchange call):** the type the Exchange Online module hands
`StartTime`/`EndTime` back as through `Get-MailboxAutoReplyConfiguration`. The old reads assumed
`DateTime` (and the `aud-a7f` assumption about its Kind stands beside this one); whether a real session
ever delivers a `DateTimeOffset`, text or a wrapped value - and wrapped or bare - was not observed. The fix
depends on no answer: each accepted form is shown for what it states, anything else is named on the tab.
One live read would settle it. Checked on this host instead: a value placed in the harness's read stub
reaches the service as itself - a `PSObject` stays a `PSObject` (so `as DateTime?` gave null for it, as for
a `DateTimeOffset`, an `Int32` or a string), a bare `DateTime` stays bare.

| Test | Pins |
| --- | --- |
| `OutOfOfficeRequestCaptureTests.AReadScheduleOfAnotherType_IsShownAsTheSameInstant` (`DateTimeOffset`; `PSObject` wrapping a `DateTimeOffset`; `PSObject` wrapping a `DateTime`; ISO 8601 text with `Z` and with `-05:00`) | **the finding**: the page's real `CheckStatus` over the real service, the stub reporting each form; the tab shows the `aud-a7f` New York window, "2026-10-30 09:00 (America/New_York, UTC-04:00) - 2026-11-06 17:00 (America/New_York, UTC-05:00)" (the page's em dash). The offsets handed over (+05:30, -05:00) are not the browser's, so only a converted instant matches. Literal expectation |
| `AReadScheduleAsTextThatNamesNoZone_IsShownAsReported_AndIsGivenNone` | ISO text with no zone: "(time zone not reported)" on both, and no zone named |
| `AReadScheduleThisAppCannotRead_IsShownAsAMarkerNamingWhatCameBack_NeverAsAnEmptyWindow` (an `Int32`; text in a culture's format, `10/30/2026 1:00:00 PM`; a `PSObject` property bag typed `Deserialized.Contoso.Data.ExDateTime`) | **never an empty window**: the Schedule line is there, each time the marker naming `System.Int32`, `System.String`, `Deserialized.Contoso.Data.ExDateTime` |
| `OofScheduleValidationTests.Scheduled_AtATimeAZoneRepeatsForThirtyMinutes_ReturnsError_InWordsThatNameNoHour` (01:30; 01:59) | **part 2**: a custom `TimeZoneInfo` whose clocks go back thirty minutes (02:00 to 01:30 on 1 November); the page's validation refuses, naming the time and the zone, carrying a reason with no "hour"; `ToUtc` gives no instant |
| `OofScheduleValidationTests.Scheduled_OnTheDayAZoneGoesBackThirtyMinutes_OutsideTheRepeat_ReturnsNull` (01:15; 02:00) | either side of the thirty minutes is one instant and accepted - the check is the zone's own rule, not an hour-wide window |
| (unchanged) every `aud-a7f` Check Status test, the `aud-a7g` repeated-hour tests | a `DateTime` read, and the New York refusal, as before |

**Against the unfixed code** - `Models/LookupModels.cs`, `Services/OutOfOfficeService.cs` and the page
checked out at `HEAD` (LF md5s `232cfd8f9a47600cc06ae8789f5327ce`, `424455662d03b665656e8b58a49b2ce4`,
`61b5a1ee124a386d51f4813d801b2e19` - the `aud-a7g` finals), the tests as changed (they compile there: the
harness hands the stub plain objects and the unit tests call `NoInstantReason`): 67
`OutOfOffice`/`OofSchedule` tests, 10 failed - the four other-type cases, the no-zone text, the three
unreadable cases (each with no Schedule line at all) and both thirty-minute cases ("hour" in the reason).
The two outside-the-repeat cases pass there by construction: the check did not change. Restored from the
saved bytes, touched, md5s re-confirmed, rebuilt, 67 passed.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the
solution rebuilt (Release) and the 67 `OutOfOffice`/`OofSchedule` tests run, the file restored from its
saved bytes, touched (a restored copy keeps its old mtime and MSBuild would skip the rebuild), md5
re-confirmed equal, rebuilt, and the tests re-run green. First round on the pre-comment bytes (LF:
models `6ac269e7802e7c264f02492e2943a640`, service `aee8e69d6201e4a1f1eb65f7d7ebf156`, page
`308a658b850700b0a6b984b16caf8a9f`); re-run on the final bytes with the same results - the md5s below are
the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| H1 (the one the Approach names): both reads `as DateTime?` again (`ReportedTime.Read(... ?.Value as DateTime?)`) | service `cf7c5afe2c2056273221125b5e678c6c` | `61c8f916b2b73a16f7e73e4ee571d945` | 8: the four other-type cases, the no-zone text, the three unreadable cases |
| H2: the reason says "repeat that hour ... outside the repeated hour" again | models `9e44af4fd9707e5c5d72df012235f1f9` | `842d0b94d0bf3c4b106d702f2e266866` | 2: both thirty-minute cases |
| H3: any text the invariant culture's general parse accepts is read (`DateTime.TryParse`) | models | `9286d57b73f409063db3b896d3121fc3` | 1: the culture-formatted text case (it read as an unzoned 13:00) |
| H4a: the page shows the line only for a readable start (`is { Time: not null } start`) | page `c4204ad17885b5934569fe0e0a09d5f8` | `d6c9600aa4ae70b93e80536f9d34af37` | 3: the three unreadable cases |
| H4b: an unreadable time renders as nothing (`: ""`) | models | `a597360556ef8187a599b1d3f8000ca5` | 3: the three unreadable cases |

Comments were written after the first round. Final test-file md5s (LF):
`OutOfOfficeRequestCaptureTests.cs` `f39905f6396ba1f4b303890126af0b68`, `LookupValidationTests.cs`
`fe0ad6a507b2fe44ecbc10375679ccee`.

**Pins.** `ClickGateRegistry`: the page is `"tier 3, not approved"` with no line-keyed entries, and the
markup's line count did not change - confirmed by the `ClickGate*` tests passing in the full suite.
`ProgressRegistry`: no handler or lifecycle method was added (`ReadScheduleTime` is a render helper); its
pins are unchanged and its tests pass. `ValidatedWriteCallSite`, the `aud-a7b` capture scan and the
`aud-a7c` CheckStatus scan pass unchanged.

## Gates

On the final bytes of both findings (this commit on top of `aud-a38k`):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings (the count
  `aud-a7f`/`a7g` recorded); none in a changed file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5205 passed, 3 skipped (the
  known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5208 in all, 12 of them
  new cases; no hang. The `ClickGate`, `ProgressRegistry`, `ValidatedWriteCallSite` and
  `GlobalProgressWiring` classes: 412 passed.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- Pester, on the same bytes (Pester 6.2.0): `Invoke-Pester tests/ps` 474 passed, 0 failed, 0 skipped.
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`. The page's one
  non-ASCII character is its existing em dash between the two times (`.razor` is outside the lint).

## Files changed

- `Models/LookupModels.cs` - `ReportedTime` and its reader; `OutOfOfficeResult`'s two times;
  `ShowReported(ReportedTime, TimeZoneInfo)`; `NoInstantReason`'s wording; comments.
- `Services/OutOfOfficeService.cs` - both reads through the reader.
- `Components/Pages/OutOfOffice.razor` - the Check Status schedule line; `ReadScheduleTime`.
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs` - `ReadBack` takes any object; three tests
  (eight cases).
- `ExchangeAdminWeb.Tests/LookupValidationTests.cs` - the thirty-minute zone; two tests (four cases).
- This record.

## Known gaps

- **Assumption, unverified (no live Exchange call):** the type the live module reports (above).
- **Unreadable text is named, not shown.** A string in a culture's format is reported as "System.String";
  what it said is not on the tab. Showing it verbatim would guess nothing, but it was not in the approved
  approach. Candidate.
- **The same `as DateTime?` read elsewhere**: `Services/MigrationService.cs` (`CreationDateTime`,
  `StartDateTime`, `CompletionDateTime`, `CompleteAfter`, `LastSyncTime`) and
  `Services/RecipientLookupService.cs` (`WhenCreated`, `LastLogonTime`) drop any other type the same way.
  Not this finding's module. Candidates.
- A Scheduled auto-reply whose start Exchange did not report at all (null) still shows no schedule line,
  as before; an end it did not report still renders nothing.
- **No browser ran**; the browser's zone came from the harness's fake `IJSRuntime`, as in `aud-a7e`/`a7f`.
- Out of Office module version bump is the supervisor's.

## Closeout

Fixed on branch; not merged, not pushed.
