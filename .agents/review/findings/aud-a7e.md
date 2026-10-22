# aud-a7e: The Scheduled Out of Office notice shows From/Until with no time zone

**Severity**: LOW - the write is correct; the user-facing notice is ambiguous. A mailbox owner in
another zone, or the operator later, cannot tell whether the window is mailbox time, server time,
operator time or Exchange's reading of the submitted value.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
