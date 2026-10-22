# aud-a7f: The Check Status tab and the owner notice's Date line still show times with no zone

**Severity**: LOW - same ambiguity aud-a7e removed from the Scheduled notice, left in two other
places: the Out of Office Check Status tab renders a read schedule's start and end with no zone,
and the owner notice's "Date:" line is the server clock with no zone.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-aeb07a6371bf7b965` (forked at `36fb150d`)
**Commit**: -
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

Pending.
