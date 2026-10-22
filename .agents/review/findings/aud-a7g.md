# aud-a7g: A repeated DST hour is accepted as one arbitrary instant, and a server-side zone failure is reported as a browser problem

**Severity**: MEDIUM - a wrong Exchange write, limited to the ambiguous DST window: a schedule
entered at `01:30` on the fall-back day in a zone that repeats that hour has two possible instants,
and `ConvertTimeToUtc` picks one without asking, so the auto-reply window can be written an hour
off from the operator's intent. The second part is LOW: when the browser returns a real IANA id
this host cannot resolve, the page says the browser zone "could not be read" and asks for a reload,
which cannot fix a server-side globalization gap.
**Status**: Merged to master (`f18431d0`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-aeb07a6371bf7b965` (forked at `36fb150d`; this record exported from master `a77b9f77`)
**Commit**: `9f3f8e39` (merged `f18431d0`); plan/record export `1e0ef195`
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (ops-batch-6, `prompt-ops-batch-6.md`, 2026-10-22)

## Evidence

At `5e9b2c79`: `Models/LookupModels.cs` around line 198, the aud-a7e `ToUtc(wallClock, zone)`
helper returns null for a time the zone skips (`IsInvalidTime`) but not for one it repeats
(`IsAmbiguousTime`), so the page and the service accept it and `ConvertTimeToUtc` resolves it to
one of the two instants. `Components/Pages/OutOfOffice.razor` around line 260: one error path
covers both the JS read failing and `TimeZoneInfo.FindSystemTimeZoneById` throwing, with a message
about the browser and a reload.

## Approach (to be written by the implementing agent before code)

- `ToUtc` (or a sibling) refuses an ambiguous wall-clock time the same way it refuses a skipped one,
  with its own reason; the page validation and the service refusal (aud-a7e's write boundary) both
  carry it, with a message that names the time, the zone, and that the hour occurs twice, and
  tells the operator to pick a time outside the repeated hour (the onus is on the user; no guess, no
  silent choice of offset).
- Split the two failure paths on the page: a JS read failure says the browser did not report a zone
  (reload); a resolution failure shows the id the browser returned and says this server cannot
  resolve it, naming the administrator remediation (ICU / time-zone data on the host). Both still
  refuse scheduling.
- Tests through the aud-a7b/a7d/a7e harness: an ambiguous time is refused at the page validation
  and at the service, nothing written, notified or audited; a skipped time still refused; a time
  outside the repeated hour on the same day still written as the right UTC instant; a fake
  `IJSRuntime` returning an unresolvable id produces the resolution message, a throwing one the
  browser message. Mutations at the call site: drop the ambiguity check; collapse the two messages
  back into one.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it and
handed to this agent on 2026-10-22, to land after `aud-a38j` and `aud-a7f`. Module-scoped: Out of
Office (its version bump is the supervisor's; `Modules/ModuleCatalog.cs` is not edited here). Written
before the code.

**Part 1 - the repeated hour (MEDIUM).**

1. `Models/LookupModels.cs`: `ToUtc` returns null for a wall-clock time that names no single instant
   - one the zone skips (as now) or one it repeats (`zone.IsAmbiguousTime`) - so no caller is handed
   one of two instants without asking. A sibling, `NoInstantReason(wallClock, zone)`, says which, in
   one place: the skipped wording unchanged ("does not exist in <zone>: the clocks skip it"); the
   repeated one names the zone, says the time occurs twice because the clocks go back and repeat that
   hour, and tells the operator to choose a time outside the repeated hour. No guess and no offset
   chosen: the onus is on the operator.
2. Page, `ValidateOofSchedule`: the refusal names the time (`yyyy-MM-dd HH:mm`, invariant culture) and
   carries the reason, so an ambiguous schedule is refused before anything is read, audited, notified
   or written.
3. Service, `SetOutOfOfficeAsync` (aud-a7e's write boundary): refuses it again, with the time and the
   reason, before a connection is borrowed - it does not rely on the page.
4. Checked on this host (.NET 10.0.12, `America/New_York`): on 2026-11-01, 01:00 to 01:59:59 is
   ambiguous and 00:59:59 and 02:00 are not; `ConvertTimeToUtc` reads 01:30 as 06:30Z (standard
   time), which is the silent choice this removes.

**Part 2 - the zone-resolution message (LOW).**

5. Page, `ReadScheduleZoneAsync`: the one catch becomes two paths. The browser read failing (the JS
   call throws, or answers with no id) keeps the browser message (reload). `FindSystemTimeZoneById`
   failing for an id the browser did give shows that id, says this server cannot resolve it - so a
   reload will not help - and names the administrator's remedy: the server's time-zone data (.NET
   resolves these names through ICU, which must be present and not switched off by invariant
   globalization or NLS mode). Both leave the zone null and scheduling refused; Enable and Disable
   still work. Same JS call; no new interop.

**Tests** through the aud-a7b/a7d/a7e harness and the existing unit tests:

- `OofScheduleValidationTests`: an ambiguous start, and an ambiguous end, are refused, naming the time
  and the zone, with a reason other than the skipped-time one; the skipped-time case is still refused.
- `OutOfOfficeWriteIdentityTests.AScheduleThatNamesNoInstant_WritesNothing_AndBorrowsNoConnection`
  gains the ambiguous case.
- Harness: an ambiguous time entered on the page is refused by validation - nothing read, written,
  audited or notified; a window on the same day outside the repeated hour (00:30 to 02:30, New York,
  2026-11-01) is written as 04:30Z and 07:30Z.
- Harness: a fake `IJSRuntime` answering an id this host cannot resolve shows the resolution message
  carrying that id; one that throws, or answers blank, shows the browser message; the two differ.

**Mutations at the call site**: drop the ambiguity check; collapse the two messages back into one.
Also: the refusal stops naming the time. md5 before and after, failing tests named, restore, md5
re-confirmed, touch, rebuild, rerun green. `ClickGateRegistry` (tier 3, no line-keyed entries) and
`ProgressRegistry` (no handler or lifecycle method added) unchanged.

## Guard proof

Built as the Plan.

- `Models/LookupModels.cs`: `ToUtc` converts only when `NoInstantReason` is null. `NoInstantReason`
  returns, for a skipped time, "does not exist in <zone>: the clocks skip it" (unchanged); for a
  repeated time (`zone.IsAmbiguousTime`), "occurs twice in <zone>: the clocks go back and repeat that
  hour, so it could mean either of two moments; choose a time outside the repeated hour"; otherwise
  null. `ClockText` prints a wall-clock time as `yyyy-MM-dd HH:mm`, invariant culture.
- Page, `ValidateOofSchedule`: "The start time, 2026-11-01 01:30, occurs twice in America/New_York: ...".
  It still runs before the first await in `SetOof`, so nothing is read, audited, notified or written.
- Service, `SetOutOfOfficeAsync`: "The schedule for <mailbox> has a time, <time>, that <reason>.
  Nothing was written." - returned before `RunAsync`, so no connection is borrowed.
- Page, `ReadScheduleZoneAsync`: the JS call failing, or answering blank, sets the browser message
  (unchanged text, now one constant). `FindSystemTimeZoneById` failing for a non-blank id sets: "Your
  browser reports its time zone as '<id>', but this server cannot resolve that name, so a schedule
  cannot be set here, and reloading the page will not change that. An administrator must fix the
  server's time-zone data: .NET resolves these names through ICU, which must be present on the server
  and not switched off (invariant-globalization or NLS mode), with this zone in its data. Enable and
  Disable still work." Each path logs its own warning; both leave the zone null. Same JS call, no new
  interop.

| Test | Pins |
| --- | --- |
| `OutOfOfficeRequestCaptureTests.ATimeTheClocksRepeat_IsRefusedByThePage_BeforeAnythingIsReadWrittenAuditedOrNotified` | **the finding, at the page**: New York 2026-11-01 01:30 refused by validation, naming the time and the zone; nothing read, written, audited or sent; form usable |
| `OofScheduleValidationTests.Scheduled_AtATimeTheZoneRepeats_ReturnsError` (repeated start; repeated end at 01:00) | refused, naming the time and the zone, carrying the repeated reason - which is not the skipped one - and `ToUtc` gives no instant for it |
| `OofScheduleValidationTests.Scheduled_OnTheDayTheClocksGoBack_OutsideTheRepeatedHour_ReturnsNull` | 00:30 to 02:30 that day is a valid window |
| `OutOfOfficeWriteIdentityTests.AScheduleThatNamesNoInstant_WritesNothing_AndBorrowsNoConnection` (new case: the repeated hour) | **the finding, at the write boundary**: no call, no connection |
| `OutOfOfficeWriteIdentityTests.ATimeTheClocksRepeat_IsRefusedAtTheWrite_NamingTheTimeAndTheZone` | the service's own refusal (a repeated end) names the time and the zone |
| `AScheduleEnteredInTheBrowsersZone_IsWrittenAsTheSameInstant_InUtc` (new case) | the same day, either side of the repeated hour, written as 04:30Z and 07:30Z |
| `AZoneThisServerCannotResolve_IsToldApartFromABrowserThatGaveNone` (browser call fails; browser answers blank) | **part 2**: an id this host cannot resolve (precondition asserted: `TimeZoneNotFoundException`) gets a message carrying the id and different from the browser's; neither settles on a zone |
| `WithoutTheBrowsersZone_AScheduleIsRefused_BeforeAnythingIsAuditedNotifiedOrWritten` (now also for the unresolvable id) | either failure: no schedule inputs, the reason shown, a Scheduled submit refused before anything happens |
| (unchanged) `Scheduled_AtATimeTheZoneSkips_ReturnsError`, the skipped-time service case | a skipped time is still refused, with its old wording |

**Against the unfixed code** - the page and the service checked out at `HEAD`, the model as changed
(the tests call `NoInstantReason`, so the old model cannot compile against them; G1 below is the old
model's behaviour): 55 tests, 6 failed - both validation cases, the service's naming test, both
zone-message cases and the page refusal; restored, touched, rebuilt, 55 passed.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the
solution rebuilt (Release) and the `OutOfOffice`/`OofSchedule` tests run, the file restored from its
saved bytes, touched, md5 re-confirmed equal, rebuilt, and the tests re-run green. First round on the
pre-comment bytes (LF: `Models/LookupModels.cs` `4f94d3a303237bc1ed528b6ea120b5b7`, page
`e3b2989040632174bed2347b88fa8ef7`, `Services/OutOfOfficeService.cs`
`109db3dbb3716ec0037fc8d1ab7a4c7e`); re-run on the final bytes with the same results - the md5s
below are the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| G1: the ambiguity check dropped (the old `ToUtc`: a repeated time converts to standard time) | models `232cfd8f9a47600cc06ae8789f5327ce` | `6f683b5d30336d16d77d65409b12f668` | 5: both validation cases, the page refusal, the service's repeated-hour case and its naming test |
| G2: the two zone messages collapsed back into one (the resolution path shows the browser's) | page `61b5a1ee124a386d51f4813d801b2e19` | `fbddcc50a0fcf4ffc9292086e25039e4` | 2: `AZoneThisServerCannotResolve_IsToldApartFromABrowserThatGaveNone`, both cases |
| G3: the page's refusal stops naming the time | page | `470e2adda28d3901b7b9d4e5f654bc98` | 3: both validation cases, the page refusal |
| G4: the write boundary's refusal stops naming the time | service `424455662d03b665656e8b58a49b2ce4` | `c847b500f00aff20610f2f5a4816c084` | 1: `ATimeTheClocksRepeat_IsRefusedAtTheWrite_NamingTheTimeAndTheZone` |

G1 and G2 are the two the Approach names. Comments were written after the first round. Final test
file md5s (LF): `OutOfOfficeRequestCaptureTests.cs` `651e45df139995ceb005a7f87b0e3943`,
`LookupValidationTests.cs` `a4aa3c99d77192073450f01ccf811d26`, `OutOfOfficeWriteIdentityTests.cs`
`af3e971504bdebb4ea0a61e5f424c33a`.

**Pins.** `ClickGateRegistry`: the page is `"tier 3, not approved"` with no line-keyed entries -
confirmed by the `ClickGate*` tests passing in the full suite. `ProgressRegistry`: no handler or
lifecycle method was added (`ReadScheduleZoneAsync` is called from the exempt `OnAfterRenderAsync`);
pins unchanged, tests passing. The `aud-a7b` capture scan and the `aud-a7c` CheckStatus scan still
bound their methods as before.

## Gates

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings (unchanged
  count); none in a changed file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`, on the final bytes (all
  three findings): 5143 passed, 3 skipped (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable`
  cases), 0 failed - 5146 in all; no hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- Pester, on the same bytes (Pester 6.2.0 imported before the analyzer): `Invoke-Pester tests/ps` 464
  passed, 0 failed, 0 skipped.
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `Models/LookupModels.cs` - `ToUtc` on `NoInstantReason`; `ClockText`.
- `Components/Pages/OutOfOffice.razor` - `ValidateOofSchedule`'s refusal; `ReadScheduleZoneAsync`'s two
  paths and `BrowserZoneUnread`.
- `Services/OutOfOfficeService.cs` - the write boundary's refusal.
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs`, `LookupValidationTests.cs`,
  `OutOfOfficeWriteIdentityTests.cs`.
- This record.

## Known gaps

- **The resolution failure was produced with an id no host resolves**, not on a host whose ICU is
  missing or switched off; that the remedy named is the one such a host needs is the reading of .NET's
  documented behaviour (IANA ids on Windows go through ICU), not observed here. Not run.
- The repeated-hour reason says "hour"; a zone whose clocks go back by another amount (30 minutes, for
  one) gets the same words. The check itself is `IsAmbiguousTime`, whatever the amount.
- The default start time is "now" in the browser's zone; opened during a repeated hour, it is refused
  on submit until the operator moves it - deliberate (no guess), noted.
- Out of Office module version bump is the supervisor's.

## Closeout

Fixed on branch; not merged, not pushed.
