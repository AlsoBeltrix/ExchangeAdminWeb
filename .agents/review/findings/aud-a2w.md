# aud-a2w: Conference Rooms - the Room Type path logs none of its refusals, a Finder-row room read that throws leaves no log line, and the row's own refusals use a third wording style

**Severity**: LOW - the aud-a2v class on the sites aud-a2v did not cover; no wrong write. (1)
`SetRoomTypeAsync` has the same three early refusals as the Finder row (no validated room, an invalid
time zone, a recipient that is not a room mailbox) plus its unconfigured-groups refusal, and logs none
of them. (2) When the Finder row's room read throws (Exchange returns a different mailbox, or none),
the service does not catch it; the page's catch audits and notifies but writes no log line. (3) The
row's time-zone and recipient refusals do not end "Nothing was written." - a third wording style,
shared with `SetRoomTypeAsync`, after aud-a2v unified the two preflights.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; fixed on branch, not merged, not pushed; review pending
**Branch**: `worktree-agent-a56481d2c0a45a8cb`
**Commit**: `06c68f89` (fix, tests, record); plan `ee4c8c5d`; comments `4e0f61a1` (shared with psl-8, aud-a2x)
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

## Fix

- `Services/ConferenceRoomService.cs`, on top of psl-8 (`f5e14730`):
  - `SetRoomTypeAsync`: each of its four refusals before any write - no validated room, an invalid time zone, the
    recipient refused, the module's groups unconfigured - is followed by `_logger.LogWarning("Room Type apply for {Room}
    refused the row; nothing was written: {Refusal}", roomEmail, <the refusal>)` before its return. The time-zone, recipient
    and groups refusals are built without " Nothing was written." (a local `refusal`, or `recipientError` as
    `ValidateRoomRecipientAsync` returned it), logged so, and returned with it appended; the no-validated-room refusal is
    `ValidatedMailbox.Refusal`, as before.
  - `SetRoomMetadataAndListAsync`: the room read (`ValidateRoomRecipientAsync`) runs in a try whose catch logs
    `_logger.LogWarning(ex, "Room Finder apply for {Room}: reading the room failed; nothing was written: {Error}",
    roomEmail, ex.Message)` and rethrows with `throw;`. The time-zone and recipient refusals are built, logged and returned
    as the Type path's: the line carries the refusal, the message adds " Nothing was written.".
  - Nothing else: `ValidateRoomRecipientAsync`, the page, the bulk processor and every later step are unchanged.
- `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`: `CapturingLogger` records the exception with each entry;
  `RefusalLine` gains the Type path's line; aud-a2v's `TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` takes the
  suffix (below); a new section at the end - `AssertSaysNothingWasWrittenOnce`,
  `TheTypePathsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (4), `TheRowsRoomRead_ThatThrows_IsLoggedOnce_AndStillThrows`
  (2). 6 new cases; the plan's third test, `EveryRefusalTheRowOrTheTypePathDecidesBeforeAnyWrite_SaysNothingWasWritten`, is
  folded into the two refusal theories as `AssertSaysNothingWasWrittenOnce`, which every case of both runs - the same
  assertion over the same refusals, without a third copy of their set-up. On the fix the 362 `ConferenceRoom*` cases pass.

**Operator-visible changes.**

- A Room Type apply refused before any write - no validated room, an invalid time zone, a recipient that is not a room
  mailbox, the module's groups unconfigured - leaves one warning in the application log: "Room Type apply for <room>
  refused the row; nothing was written: <refusal>". Before, only the result and its audit carried it.
- A Room Finder row whose room read throws (Exchange answering with another mailbox, or none, or an error) leaves one
  warning naming the room and the error, with the exception: "Room Finder apply for <room>: reading the room failed;
  nothing was written: <error>". The row still fails as before - the page's catch audits and notifies the same message.
- The row's and the Type path's invalid-time-zone, not-a-room-mailbox and (Type) unconfigured-groups refusals end "
  Nothing was written.", as both preflights' do; so do their audit rows and admin notifications, which carry the message.
- Nothing else: no outcome, step, write, audit field or notification changes.

## Guard proof

| Test (`ConferenceRoomWriteIdentityTests`) | What it pins |
| --- | --- |
| `TheTypePathsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (no validated room, an invalid time zone, a recipient that is not a room mailbox, the module's groups unconfigured) | parts 1 and 3 on the Type path: refused, not partial, no step, `Writes` empty, no credential asked for; the message the refusal, then " Nothing was written." but for the validated-mailbox one; that said once; exactly one entry carries the refusal, a Warning, equal to "Room Type apply for <room> refused the row; nothing was written: <refusal>" |
| `TheRowsRoomRead_ThatThrows_IsLoggedOnce_AndStillThrows` (another mailbox, none) | part 2: the row still throws, its message Exchange's ("... to a different mailbox ...", "... couldn't be found ..."); nothing written, no credential asked for; exactly one entry carries the message, a Warning, equal to "Room Finder apply for <room>: reading the room failed; nothing was written: <message>", with the thrown exception |
| `TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (aud-a2v's; changed) | part 3 on the Finder row: its time-zone and recipient messages now end " Nothing was written.", said once, and the line carries the refusal before it; the validated-mailbox one unchanged |
| `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` (aud-a2d's, unchanged) | still green: the row's read of another mailbox throws before any write, as it allows |

**Mutations** (`scratchpad/round/round.ps1`, rows `rows-a2w.ps1`, log `round-a2w.log`; 2026-10-22 18:50-18:59), filter
`ConferenceRoom` (362 cases), the projects rebuilt each row, failures read from the TRX. `Services/ConferenceRoomService.cs`
md5 before = after every row: `14f03d1420b2cf172b039ba0090ba9fa` (restored from the git index, the fix staged, and touched).

| # | Mutation (call site) | md5 mutated | Named test failing | Result |
| --- | --- | --- | --- | --- |
| M1a | the brief's "one Type log line dropped": no validated room | `db18efabde0f82a00ad349680d93de20` | `TheTypePathsOwnRefusal_...` (no validated room) | FAIL, 1 of 362: no entry carries the refusal |
| M1b | the same, an invalid time zone | `39fb195b236651d0b13a1d95f2594942` | the same (an invalid time zone) | FAIL, 1 |
| M1c | the same, the recipient refused | `dae3689c57cf2354c63949c64c461049` | the same (a recipient that is not a room mailbox) | FAIL, 1 |
| M1d | the same, the groups unconfigured | `390015f6fc63eb4676f118e28f4305a5` | the same (the module's groups unconfigured) | FAIL, 1 |
| M2 | the brief's "the catch removed": the read called bare | `e5f90ff538b06fca565fba5a1dd04635` | `TheRowsRoomRead_ThatThrows_...` (both) | FAIL, 2: no entry carries the message |
| M2b | the catch logging and swallowing - the read's message taken for the row's refusal - instead of rethrowing | `b838e56598d46628b448b3f79157d6ab` | the same (both) | FAIL, 2: no exception was thrown - the outcome changed, which the rethrow is there to prevent |
| M3a | the brief's "one suffix dropped": the Finder row's time zone | `6a0ab83e244aad7e86c0ebba6cc4ffc9` | `TheRowsOwnRefusal_...` (an invalid time zone) | FAIL, 1: the message without " Nothing was written." |
| M3b | the same, the Finder row's recipient | `7e2c06c207f4425782e21972cde92751` | the same (a recipient that is not a room mailbox) | FAIL, 1 |
| M3c | the same, the Type path's time zone | `40235660bc24ae4891845847eb134a56` | `TheTypePathsOwnRefusal_...` (an invalid time zone) | FAIL, 1 |
| M3d | the same, the Type path's recipient | `88dde070486e0a276c9dbdd37dbcd350` | the same (a recipient that is not a room mailbox) | FAIL, 1 |
| M3e | the same, the Type path's groups | `302361813cb24497f3f7303898aa8df4` | the same (the module's groups unconfigured) | FAIL, 1 |
| M4 | a second line: `ValidateRoomRecipientAsync` logs its not-a-room refusal too | `49ea2f16fde43a3b926f9d42c0c037ed` | `TheRowsOwnRefusal_...` and `TheTypePathsOwnRefusal_...` (a recipient that is not a room mailbox) | FAIL, 2: two entries carry the refusal |
| REV | the fix reverted: the service whole as at `f5e14730` (psl-8), the new tests kept (they name nothing the fix adds) | `50b71ae398e1227243a24cd7b2de6b8c` | the four Type cases, both read cases, aud-a2v's two changed cases | FAIL, 8 of 362; the 354 others pass |

After the last row the restored tree rebuilt and `ConferenceRoom*`, `DirectoryModuleImportTests`, `ClickGate*`,
`ProgressRegistryTests` and `ValidatedWriteCallSiteTests` passed, 758/758. `ConferenceRoomWriteIdentityTests.cs` at the
round: `a50b7ea381091fa922529d381ed4a94c` (its comments written with the tests).

## Known gaps

- Not changed, outside this record: `SetRoomTypeAsync`'s own room read (`ValidateRoomRecipientAsync`) that throws is not
  caught or logged by the service either - the same gap as part 2, on the Type path, which the approach does not name;
  a candidate in the hand-back.
- The Type path's refusals outside the service do not end "Nothing was written.": the page's "Invalid room type: <type>"
  (`ConferenceRooms.razor`, line-keyed) and the bulk processor's "Invalid type: <type>" (`ConferenceRoomBulkProcessor`).
  Both refuse before the service is called; neither is the service's, which the approach's "one rule for the module"
  covers.
- The recipient refusal's "not found" stays unreachable through a test (aud-a2v): the room read returns a room or throws.

## Closeout

Fixed on branch `worktree-agent-a56481d2c0a45a8cb`, not merged, not pushed. A module-scoped behaviour change (Conference
Rooms: four warning lines on the Type path, one on the Finder row's read, and the words of five refusals): the Conference
Rooms module version bump is due at merge - not made here, `Modules/ModuleCatalog.cs` being outside this brief. No shared
code changed.

## Comments, branch-tip gates and the merge check

Shared by the branch's three findings, recorded once in `psl-8.md` ("Comments after the round", "Branch-tip gates"): the
comments commit `4e0f61a1`, checked code-identical - this record's are the Type path's and the row's notes in
`ConferenceRoomService` and `ValidateRoomRecipientAsync`'s remark; the Release build, the full suite (5,758 passed, 0
skipped, 0 failed at `4e0f61a1`), this commit's tree less the live-directory classes (`06c68f89`: 5,696 / 0 / 0), format,
`git diff --check`, the ASCII lint, and the merge with master `909ec552` (clean).
