# aud-a2p: The on-prem City/State/Country write is not confirmed by a read-back, keeps a dead HadErrors branch, and an unused success message claims a write

**Severity**: LOW - no wrong object is written. `SetSyncedAttributesViaAdAsync` (the Room Finder
City/State/Country `Set-ADUser` on a synced room) treats "no error" as done with no read-back, the
aud-a2l shape Known Failure Class 2 forbids; it also keeps an error-without-a-throw (`HadErrors`)
branch that cannot fire under `-ErrorAction Stop` (it fails closed, so the harm is only dead code).
Separately, `AddToRoomListAsync` still builds "Membership was written on-prem ..." for the no-op and
the confirmed-present rows; the caller replaces success messages today, so it is unused, but wrong if
ever shown.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a88f21a61c9cda81b`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2o agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `15de2fcf`, `Services/ConferenceRoomService.cs`: `SetSyncedAttributesViaAdAsync` (the `Set-ADUser`
write by the room's objectGUID on its own domain, aud-a2d; no `Get-ADUser` read of the written
attributes after it; `if (ps.HadErrors)` after an `-ErrorAction Stop` invoke); `AddToRoomListAsync`,
`BuildRoomListAddedMessage(viaOnPrem: true)` on the no-op and confirmed-present paths.

## Approach (to be written by the implementing agent before code)

- After `Set-ADUser`, read the three attributes back (`Get-ADUser -Identity <objectGUID> -Properties
  l,st,co -Server <same DC>`) and compare to what was written (ordinal, trimmed as the write trims);
  a mismatch or a failed read is a failed step naming the attribute and the server; the audit's
  `writtenDirectoryObject` (aud-a2g) is recorded only when the read-back matches. Same shape as
  aud-a2l for the room list.
- Remove the dead `HadErrors` branch the way aud-a2o did for the group write (try/catch/finally; the
  read-back decides), with the comment saying why.
- The no-op and confirmed-present paths build the message that describes them (no "was written"
  claim); or remove the unused message if the caller never shows it, with a test pinning that the
  no-op's result message does not claim a write.
- Tests through `ExchangeWriteHarness`: a `Set-ADUser` that applies (read-back matches, success,
  written object recorded); one that raises no error and changes nothing (failed step, nothing
  recorded as written); a read-back that fails (failed step); the dead branch gone (a stand-in
  writing a non-terminating error is not taken for the write's error). Mutations at the call site:
  drop the read-back; restore the `HadErrors` branch; restore the "was written" message on the no-op.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above and the supervising session's brief. No page edit; the model, the
bulk processor and both audit surfaces unchanged.

**Rule.** The City/State/Country write is done when the attributes it wrote read back as written, from the
server it wrote to (Known Failure Class 2); only then is its object reported written (aud-a2g). No retry: the
operator re-runs.

1. **Routing.** `SetSyncedAttributesViaAdAsync` reads the room's object with `ConfirmDirectoryObject`'s DN
   overload (as `AddToRoomListViaAdAsync` does) and sends the write and the read-back to
   `server = GroupManagementService.ServerFromDn(<that DN>)`, the room object's own domain: `Set-ADUser` gains
   `-Server <server>`. The brief's "-Server <the same DC>" is read as the write's own `-Server`, which the write
   had none of - there was no server for a read to match. The domain does not move: the read before the write
   has no `-Server`, so it finds the object only in the module's default domain (*documented AD module default,
   not probed*), which the object's DN then names; and the write is by objectGUID, so a wrong server could only
   fail it, never write another object. The routing aud-a2j gives the group write and `GroupManagementService`
   every write (fsr-1). A DN naming no domain leaves both without `-Server`, the precedent's fallback; the
   messages then say "the default domain controller". *Assumption, aud-a2l's, carried:* a domain's DNS name in
   `-Server` reaches a domain controller holding the write; one DC is not pinned.
2. **The read-back.** After the write, `ReadBackSyncedAttributes`: `Get-ADUser -Identity <objectGUID>
   -Properties l,st,co -Server <server> -Credential -ErrorAction Stop`; in the shape of the file's other read
   helpers an error record fails it, and exactly one object, the objectGUID written, must answer.
   `UnconfirmedSyncedAttributes(written, read)`, internal static: each of `l`, `st`, `co` in the write's
   `-Replace` (`BuildSyncedUserAttributes`'s values, trimmed as written) compared ordinal with the value read;
   an empty value and an absent attribute (null, no property, "") are equal; an attribute the row left blank is
   not written, so not compared; a one-element collection reads as its element (*assumption*: the module
   returns a single-valued attribute as its value - `ADAttributeEditorService` reads its attributes through
   `Properties[..].Value` - and a collection-shaped answer must not be a false failure). `c` and `countryCode`
   go in the same `-Replace` as `co` and are not read (the brief's three; *assumption*: one `-Replace` is one
   LDAP modify, applied whole).
   - All match: `(null, objectGuid)`, written (aud-a2g).
   - Any differs: `("Set-ADUser on the AD object <guid> for <room> was not confirmed: read back from <server>,
     l (City) is '<read>' (or: is not set), not '<written>' as written[; ...].", null)`.
   - The read fails - thrown, an error record, or no single object with that objectGUID answering:
     `("Set-ADUser on the AD object <guid> for <room> could not be confirmed: reading l, st and co back from
     <server> failed: <error>", null)`.
   - Each failure logs a warning; the row turns it into the existing partial ("Partial: Building/Capacity/Floor
     were set in Exchange Online, but City/State/Country failed: ... Re-run this row after fixing the cause."),
     the step `Set-ADUser (City/State/Country)` failed with it, no `WrittenDirectoryObject`, no later step run.
3. **The dead branch.** The write's `if (ps.HadErrors) return (FirstError(ps) ?? ..., null);` is removed; the
   write is `try { ps.Invoke(); } finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`. Its error is the
   one it throws, and that reaches the method's existing catch - the message and log as before ("Failed to
   write City/State/Country on the AD object: ..."), nothing read back, nothing reported written: aud-a2l's
   shape, which the approach names. Not aud-a2o's try/catch/finally: aud-a2o holds the error for a read-back
   that runs after any write error (aud-a2n); here none runs after a thrown error, so a catch would have nothing
   to hold it for. Reading back after a thrown error, as aud-a2n does for the membership, would make a row whose
   values are found a success naming no written object, needing a "confirmed, not written" detail on the result
   and both audit surfaces - not this record's; a known gap. The `finally`: the commands cleared, so the
   read-back's `Get-ADUser` does not run piped after the write run again; the error stream cleared, so a record
   the write left - an error written past `-ErrorAction Stop` - does not fail the read-back's error check
   (psl-2; aud-a2o's M5).
4. **The no-write messages.** `AddToRoomListAsync`, the on-prem fallback's success: the already-a-member no-op
   (`(null, null, null)`) and the add the read-back confirmed (`adConfirmed`) build
   `BuildOnPremRoomListNotWrittenMessage(list, confirmedAfterWrite)` - "Already in on-prem room list '<list>':
   nothing was written." and "In on-prem room list '<list>', confirmed by reading its group back after this
   row's add was refused or reported an error: not recorded as written by this row." - and only the add whose
   write put the room there keeps `BuildRoomListAddedMessage(list, viaOnPrem: true)`. Described, not removed:
   the method's failure messages are shown (in the caller's partial message), so its messages stay, each true.
   The cloud no-op's "Added to room list '<list>'." is the same class on the cloud path, outside this record:
   reported, not changed.
5. **Test seam.** `AddToRoomListAsync` and `RoomListTarget` become `internal` (the test assembly sees
   internals): the list add's message reaches only its caller, which replaces it on success, so only a direct
   call can pin it.
6. Comments after the mutation round.

**Tests** (through `ExchangeWriteHarness`; no directory, Exchange or network call).
- Harness: the stand-in `Set-ADUser` takes `-Server` and applies `-Replace` to its object in `DirectoryObjects`,
  so a read sees it, unless `SetAdUserChangesNothing`; `SetAdUserReportsErrorWithoutThrowing` (a message) is
  written after applying with its own `-ErrorAction Continue` (aud-a2o's knob, on this write). The stand-in
  `Get-ADUser` takes `-Server`; a read asking for `-Properties` after a `Set-ADUser` fails with
  `AttributeReadBackFailure`, or answers as the object `AttributeReadBackAnswersAs`. A harness test pins each,
  under `-ErrorAction Stop`.
- `ConferenceRoomWriteIdentityTests`: applied - success, the room's objectGUID written, a `Get-ADUser` after
  the `Set-ADUser` by the objectGUID with `-Properties l,st,co`, both `-Server contoso.test`, the object holding
  the values; changes nothing, an older City there - failed, partial, nothing written, the step failed naming
  each attribute, the value read, the value written and the server, no later step run; the read-back fails -
  failed, partial, "could not be confirmed", the server and the read's error; another object answers - failed,
  partial, naming it; an error reported without a throw (applied / not) - applied: success, written, its text
  nowhere; not: the not-confirmed failure, without its text; pure - `UnconfirmedSyncedAttributes` (equal; a case
  difference; an empty write against an absent attribute and against ""; an unwritten attribute not compared; a
  one-element collection; a written value absent).
- `ConferenceRoomDirectoryAuditTests`, page and bulk: a `Set-ADUser` the read-back does not show - Failed, no
  `writtenDirectoryObject` (`CityWritten_...` and `AWriteThatSucceeded_...` now pass through the read-back).
- `ConferenceRoomRoomListPreflightTests`: the on-prem add's own message, by a direct call - already a member,
  confirmed after a refused add, written - each the message for what happened, the two that wrote nothing not
  the add's; pure - the two not-written messages name the list and claim no add.

**Mutations** (md5 before and after, the named failing tests, restored, md5 matched, touched, rebuilt, green):
M1 the brief's, at the call site - the read-back dropped, the write's lack of an error deciding as before; M2
the brief's - the `HadErrors` branch restored after the write; M3 the brief's, at the call site - the add's "was
written" message on the no-op path (M3b: on the confirmed path); M4 the `finally`'s error-stream clear dropped;
M5 the read-back without the write's `-Server`; M6 the write without its `-Server`; M7 the read-back's
objectGUID check dropped; H1 harness - the stand-in no longer applies `-Replace`; H2 harness -
`SetAdUserReportsErrorWithoutThrowing` ignored.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`, this record.

## Guard proof

Pending.
