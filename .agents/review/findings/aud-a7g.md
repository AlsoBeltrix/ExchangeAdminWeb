# aud-a7g: A repeated DST hour is accepted as one arbitrary instant, and a server-side zone failure is reported as a browser problem

**Severity**: MEDIUM - a wrong Exchange write, limited to the ambiguous DST window: a schedule
entered at `01:30` on the fall-back day in a zone that repeats that hour has two possible instants,
and `ConvertTimeToUtc` picks one without asking, so the auto-reply window can be written an hour
off from the operator's intent. The second part is LOW: when the browser returns a real IANA id
this host cannot resolve, the page says the browser zone "could not be read" and asks for a reload,
which cannot fix a server-side globalization gap.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; handed to the aud-a7f/a38j agent as its third item
**Branch**: -
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

## Guard proof

Pending.
