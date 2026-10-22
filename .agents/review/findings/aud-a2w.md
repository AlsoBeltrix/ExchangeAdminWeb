# aud-a2w: Conference Rooms - the Room Type path logs none of its refusals, a Finder-row room read that throws leaves no log line, and the row's own refusals use a third wording style

**Severity**: LOW - the aud-a2v class on the sites aud-a2v did not cover; no wrong write. (1)
`SetRoomTypeAsync` has the same three early refusals as the Finder row (no validated room, an invalid
time zone, a recipient that is not a room mailbox) plus its unconfigured-groups refusal, and logs none
of them. (2) When the Finder row's room read throws (Exchange returns a different mailbox, or none),
the service does not catch it; the page's catch audits and notifies but writes no log line. (3) The
row's time-zone and recipient refusals do not end "Nothing was written." - a third wording style,
shared with `SetRoomTypeAsync`, after aud-a2v unified the two preflights.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2v agent, 2026-10-22, candidates 1, 3 and 4 of its hand-back)

## Evidence

At `6ac190f7`: `Services/ConferenceRoomService.cs` `SetRoomTypeAsync` (the early returns), the Finder
row's room read in `SetRoomMetadataAndListAsync` (no try around it), the row's time-zone and recipient
refusal texts; `Components/Pages/ConferenceRooms.razor` (the page catch - line-keyed by
`ClickGateRegistry`, 1758 lines, edit in place only).

## Approach (to be written by the implementing agent before code)

- `SetRoomTypeAsync` logs each early refusal once at Warning, the aud-a2v shape, naming the room and the
  reason.
- The row's room read is caught in the service: one Warning log line naming the room and the exception's
  message; the outcome and the page's audit/notification path unchanged.
- The row's and the Type path's refusals end " Nothing was written." - one rule for the module.
- Tests through `ConferenceRoomWriteIdentityTests`' `Service` seam; mutations at the call site: one Type
  log line dropped; the catch removed; one suffix dropped.

## Guard proof

Pending.
