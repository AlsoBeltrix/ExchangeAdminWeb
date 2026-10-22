# aud-a7h: A read schedule of an unexpected type silently disappears from Check Status, and the repeated-time refusal says "hour" for any repeat amount

**Severity**: LOW - no wrong write. `GetOutOfOfficeAsync` reads `StartTime`/`EndTime` from
`Get-MailboxAutoReplyConfiguration` with `as DateTime?`; if the Exchange Online module hands back
another type (a `DateTimeOffset`, a string, a wrapped PSObject), the cast yields null and the
Check Status tab shows a Scheduled auto-reply with no window, as if none were set, with no sign
that a value was dropped. Separately, the aud-a7g refusal says the clocks "repeat that hour" for a
zone whose clocks go back by another amount; the check itself is correct for any amount.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
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

Pending.
