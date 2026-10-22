# aud-a2w: Conference Rooms - the Room Type path logs none of its refusals, a Finder-row room read that throws leaves no log line, and the row's own refusals use a third wording style

**Severity**: LOW - the aud-a2v class on the sites aud-a2v did not cover; no wrong write. (1)
`SetRoomTypeAsync` has the same three early refusals as the Finder row (no validated room, an invalid
time zone, a recipient that is not a room mailbox) plus its unconfigured-groups refusal, and logs none
of them. (2) When the Finder row's room read throws (Exchange returns a different mailbox, or none),
the service does not catch it; the page's catch audits and notifies but writes no log line. (3) The
row's time-zone and recipient refusals do not end "Nothing was written." - a third wording style,
shared with `SetRoomTypeAsync`, after aud-a2v unified the two preflights.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a56481d2c0a45a8cb`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above and the supervising session's brief. One fix commit for the finding's three parts, second of
three on branch `worktree-agent-a56481d2c0a45a8cb` (after psl-8, before aud-a2x; all three edit
`Services/ConferenceRoomService.cs`). Line numbers are the base `e9886df7`'s.

**Rule.** (1) A refusal the Room Type path returns before any write reaches the application log once, at Warning,
naming the room as typed and the refusal, where the path refuses (aud-a2v's placement for the Finder row). (2) A throw
from the Finder row's room read reaches the application log once, at Warning, naming the room and the exception's
message, and then leaves the service as it does now. (3) Every refusal the Finder row or the Type path decides before
any write says, at its end, that nothing was written - one rule for the module, the preflights' since aud-a2v.
Otherwise every outcome, message, step, audit and notification is unchanged.

1. **The Type path's refusals.** `SetRoomTypeAsync` (873): no validated room (880-885), an invalid time zone (887-896),
   the recipient refused (898-904: not a room mailbox; "not found" is defensive - the room read returns a room or throws,
   aud-a2v), the module's groups unconfigured (910-916). Each sets its message, then `_logger.LogWarning("Room Type apply
   for {Room} refused the row; nothing was written: {Refusal}", roomEmail, <the refusal>)`, then returns. Nothing has been
   read but the room, or written, before any of them. Not in `ValidateRoomRecipientAsync` (453), which the Finder row
   shares and whose refusal it logs at its own return (aud-a2v).
2. **The Finder row's room read.** `SetRoomMetadataAndListAsync` (472): `ValidateRoomRecipientAsync(room, roomEmail)`
   (505) runs in a try whose catch logs `_logger.LogWarning(ex, "Room Finder apply for {Room}: reading the room failed;
   nothing was written: {Error}", roomEmail, ex.Message)` and rethrows (`throw;`). The read throws when Exchange returns
   another mailbox or none (`ReadValidatedMailbox`) or a cmdlet error. So the page's catch (`ConferenceRooms.razor` 1084,
   untouched) and the bulk row's (`ConferenceRoomBulkProcessor`) audit and notify exactly as now, and the row's outcome is
   the same throw. The pool logs none of it (`ExoConnectionPool.RunWithRetryCoreAsync` rethrows, read), so this is the
   one line. Warning, not Error: the row fails closed before any write, as its refusals do.
3. **One suffix rule.** Each refusal below is built without " Nothing was written.", logged so (parts 1 and aud-a2v),
   and returned with it appended:
   - the Finder row: the invalid time zone (493) and the recipient refused (507-509);
   - the Type path: the invalid time zone, the recipient refused, and the unconfigured groups.
   - Not the no-validated-room refusal on either path: `ValidatedMailbox.Refusal` already says "so nothing was written"
     in its first sentence, and is shared with four other modules; a suffix would say it twice.
   - Not `ValidateRoomRecipientAsync`'s own text, which stays a refusal without the suffix - the callers append it, the
     shape `ConfirmDirectoryObject` has had since aud-a2r.

**Tests** (`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, a new section at its end, through
`ExchangeWriteHarness` with the file's `CapturingLogger`; no harness change; no directory, Exchange or network call):
- `TheTypePathsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (no validated room; an invalid time zone; a
  recipient that is not a room mailbox; the module's groups unconfigured): refused, no step, nothing written, no
  credential asked for; the message the refusal (with " Nothing was written." but for the validated-mailbox one);
  exactly one entry carries the refusal, a Warning, equal to the Type line.
- `TheRowsRoomRead_ThatThrows_IsLoggedOnce_AndStillThrows` (Exchange answering another mailbox; none): the exception
  leaves the service with its message as before; nothing written, no credential asked for; exactly one entry carries
  the message, a Warning, equal to the row's read line, with the exception attached.
- `EveryRefusalTheRowOrTheTypePathDecidesBeforeAnyWrite_SaysNothingWasWritten` (both paths, each refusal of parts 1 and
  3): the message says nothing was written exactly once, at its end for the suffixed ones.
- Changed, by part 3: aud-a2v's `TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` - its time-zone and recipient
  cases' messages gain " Nothing was written." and their line carries the refusal before it. No other existing
  assertion changes.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, touched, rebuilt, md5
matched, green; filter `ConferenceRoom`): M1a-d each Type line dropped in turn (the brief's "one Type log line
dropped", all four); M2 the read's catch removed (the brief's "the catch removed"); M2b the catch swallowing instead of
rethrowing (the outcome must not change); M3a-e each suffix dropped in turn (the brief's "one suffix dropped", all five);
M4 a second line - `ValidateRoomRecipientAsync` logging its refusal too (the "once"). Comments after the round, checked
code-identical.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, this record.
`Components/Pages/ConferenceRooms.razor` is not touched.

## Guard proof

Pending.
