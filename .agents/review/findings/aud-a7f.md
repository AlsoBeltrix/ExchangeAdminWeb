# aud-a7f: The Check Status tab and the owner notice's Date line still show times with no zone

**Severity**: LOW - same ambiguity aud-a7e removed from the Scheduled notice, left in two other
places: the Out of Office Check Status tab renders a read schedule's start and end with no zone,
and the owner notice's "Date:" line is the server clock with no zone.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
