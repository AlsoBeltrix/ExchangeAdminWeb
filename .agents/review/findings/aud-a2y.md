# aud-a2y: Conference Rooms and credential wording leftovers - the Type path's room read that throws is not logged, "Check the DelineaSecretId configuration" at seven sites in other modules, and two refusals outside the service lack "Nothing was written."

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1)
`ConferenceRoomService.SetRoomTypeAsync`: when the Type path's room read throws, the service neither
catches nor logs it (aud-a2w part 2's gap on the Type path). (2) "Check the DelineaSecretId configuration"
is shown whatever the actual reason at six `SelfServiceGroupService` sites and one `GroupManagementService`
site - the aud-a2x defect in other modules; they can now call `ModuleCredentialService.LookupCredentialsAsync`.
(3) Refusals outside the service do not end "Nothing was written.": `ConferenceRoomBulkProcessor` "Invalid
type" and `ConferenceRooms.razor` "Invalid room type" (line-keyed page).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-8/a2w/a2x agent, 2026-10-23, candidates 1, 7 and 8 of its hand-back)

## Evidence

At `9293b77a`: `Services/ConferenceRoomService.cs` 931; `Services/SelfServiceGroups/SelfServiceGroupService.cs`
88, 165, 242, 468, 738, 791; `Services/GroupManagementService.cs` 107; `Services/Jobs/ConferenceRoomBulkProcessor.cs`
205; `Components/Pages/ConferenceRooms.razor` 1400 (line-keyed by `ClickGateRegistry`, 1758 lines, edit in
place only).

## Approach (to be written by the implementing agent before code)

- The Type path's room read gets the aud-a2w Warning-and-rethrow.
- The seven credential refusals choose their clause from `LookupCredentialsAsync`'s failure (the aud-a2x
  shape).
- The two refusals end " Nothing was written." (the page edit in place).
- Mutations at the call site: the catch removed; one site's single text again; one suffix dropped.

### As written before each item's code (branch `fix/psl10-a2y`, forked from master `3e43b35b`)

Line numbers are the base's unless an item says otherwise. One item per commit, in order.

**(1) The Type path's room read.** `Services/ConferenceRoomService.cs` `SetRoomTypeAsync` (1066), its read of the
validated room at 1099 - `var (_, recipientError) = await ValidateRoomRecipientAsync(room, roomEmail);`, in no try - runs in
a try whose catch logs `_logger.LogWarning(ex, "Room Type apply for {Room}: reading the room failed; nothing was written:
{Error}", roomEmail, ex.Message)` and rethrows with `throw;`: the Finder row's read since aud-a2w (624-633), word for word but
the path's name. It is the path's first call: the no-room and time-zone refusals before it read nothing, and nothing is written
before it. The outcome is the same throw, so the page's catch (`Components/Pages/ConferenceRooms.razor`
1436-1441, untouched) and the bulk row's (`Services/Jobs/ConferenceRoomBulkProcessor.cs` `ProcessRowAsync`, untouched) audit
and notify as before; neither writes a log line, and the pool rethrows without one, so this is the one line. Test in
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` beside the Finder row's (`TheRowsRoomRead_ThatThrows_...`),
through the same `Service` seam and `CapturingLogger`. Mutation: the catch removed (the fix reverted). Conference Rooms
2.7.0 -> 2.8.0, the module's one minor step for this branch (kd-5 and item (3) land under it).

## Guard proof

Each row: the file's md5 with the fix, the mutation applied (the fix reverted to the commit before it, or one exact
replacement at the call site), the projects rebuilt, the filter run, the failures read from the TRX; then the fixed bytes
copied back with a fresh mtime, the md5 checked equal, rebuilt, the filter run again. Scripts and logs in the session
scratchpad (`round/prove.sh`, `round/<row>.log`); no directory, Exchange or network call in any of them.

**(1) The Type path's room read** - `ConferenceRoomWriteIdentityTests.TheTypePathsRoomRead_ThatThrows_IsLoggedOnce_AndStillThrows`
(another mailbox; no mailbox): the read still throws with Exchange's message ("... to a different mailbox ...", "...
couldn't be found ..."); nothing written; no credential asked for; exactly one log entry carries the message, a Warning,
equal to "Room Type apply for <room>: reading the room failed; nothing was written: <message>", with the thrown exception.

| # | Mutation | md5 fixed = restored | md5 mutated | Filter `ConferenceRoomWriteIdentityTests` (101) | Restored |
| --- | --- | --- | --- | --- | --- |
| 1 | the fix reverted - the read in no try, as at `3e43b35b` (the brief's "the catch removed") | `e79e2bf881012163891c3274223095d6` | `3ab67aa6b221ae3c912a8427adf6a422` | FAIL, 2 of 101: both cases, "Assert.Single() Failure: The collection did not contain any matching items" - no log entry carries the message; the throw and its message as before | PASS, 101 of 101 |
