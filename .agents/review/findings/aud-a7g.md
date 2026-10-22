# aud-a7g: A repeated DST hour is accepted as one arbitrary instant, and a server-side zone failure is reported as a browser problem

**Severity**: MEDIUM - a wrong Exchange write, limited to the ambiguous DST window: a schedule
entered at `01:30` on the fall-back day in a zone that repeats that hour has two possible instants,
and `ConvertTimeToUtc` picks one without asking, so the auto-reply window can be written an hour
off from the operator's intent. The second part is LOW: when the browser returns a real IANA id
this host cannot resolve, the page says the browser zone "could not be read" and asks for a reload,
which cannot fix a server-side globalization gap.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; handed to the aud-a7f/a38j agent as its third item; plan written, in progress
**Branch**: `worktree-agent-aeb07a6371bf7b965` (forked at `36fb150d`; this record exported from master `a77b9f77`)
**Commit**: -
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

Pending.
