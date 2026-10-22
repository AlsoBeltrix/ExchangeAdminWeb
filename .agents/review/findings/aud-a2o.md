# aud-a2o: Conference Rooms on-prem add - bulk row message omits the confirmed-membership detail, the no-op reuses the add's step label, and an unreachable error branch has no test

**Severity**: LOW - operability and dead code, no wrong write. (1) When a row succeeds only because
the read-back confirmed the membership (aud-a2n), the page's step and the audit `extra` carry the
detail but a bulk job row's message does not, so a bulk operator cannot tell a confirmed-present
row from an applied write. (2) The on-prem already-a-member no-op uses the same step label as a
real add, "Add to '<list>' (on-prem)", so the step list reads as if a write happened. (3) The
write's error-without-a-throw (`HadErrors`) branch cannot be reached under `-ErrorAction Stop`
and has no test; dead code that a later edit could make live and wrong.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a9e6dd9435998c5ff`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2m/a2n agent, 2026-10-22, candidates 3 to 5 of its hand-back)

## Evidence

At `5c116ce4`, `Services/ConferenceRoomService.cs` `AddToRoomListViaAdAsync` and its callers;
`Services/Jobs/ConferenceRoomBulkProcessor.cs` row message composition;
`Models/ConferenceRoomModels.cs` `DirectoryMembershipConfirmed`.

## Approach (to be written by the implementing agent before code)

- Bulk row message: when `DirectoryMembershipConfirmed` is set, the row message says the membership
  was confirmed present rather than written (same wording the page step uses).
- Step label: the no-op path reports a distinct step label, for example "Already on '<list>'
  (on-prem)", so the step list says what happened; audit unchanged (aud-a2g).
- Dead branch: either remove the `HadErrors` branch with a comment saying why `-ErrorAction Stop`
  makes it unreachable, or keep it and pin it with a test through a stand-in that writes a
  non-terminating error; pick the one that matches `GroupManagementService`'s handling and say so.
- Tests through `ExchangeWriteHarness` and the bulk processor tests: a confirmed-present bulk row's
  message; the no-op step label; the chosen treatment of the dead branch. Mutations at the call
  site: drop the detail from the bulk message; reuse the add's label.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above and the supervising session's brief. No page edit.

1. **Bulk row message.** `ConferenceRoomBulkProcessor.ProcessRowAsync`: the row outcome's `Message` is the
   result's `Message`, followed - when `RoomOperationResult.DirectoryMembershipConfirmed` is set - by that
   detail, the text the page shows as the on-prem step's `Error` and the audit records as
   `membershipConfirmedPresent` ("Membership confirmed present after ... No directory object is recorded as
   written: ..."). A bulk job shows each row's message and not its steps. The service's `Message` is
   unchanged, so the page's alert - the message and the steps - does not show the detail twice; audit and
   trace unchanged.
2. **Step label.** `AddToRoomListAsync`, the on-prem fallback's success: `AddToRoomListViaAdAsync` returning
   neither a written object nor a confirmed membership - `(null, null, null)`, the already-a-member no-op its
   header documents - is the step `Already in '<list>' (on-prem)`: the cloud no-op's existing label
   `Already in '<list>'` with the on-prem suffix, rather than the record's "Already on" example, so the two
   no-ops read alike. The written add and the add the read-back confirmed keep `Add to '<list>' (on-prem)`,
   the latter with its detail as the step's `Error`. Result fields and audit unchanged (aud-a2g): the no-op
   names nothing written or confirmed.
3. **Dead branch: removed, matching `GroupManagementService`**, whose two `Set-ADGroup` member writes
   (`AddMemberAsync`, `RemoveMemberAsync`) are `try { Invoke } catch { writeError = ex } finally { clear the
   commands and the error stream }` with no `HadErrors` check, the read-back deciding. The write's
   `if (ps.HadErrors) writeError = ...` goes: the write's error is the thrown one. Why it is unreachable,
   measured in a scratch runspace (pwsh 7.6.6; the tests' SDK is 7.6.1; no directory): an error a command
   writes under `-ErrorAction Stop` is thrown from `Invoke` and leaves no record in the stream. A `HadErrors`
   without a throw needs an error written past the preference - a nested command's own
   `-ErrorAction Continue`, which a compiled cmdlet such as `Set-ADGroup` does not run (*assumption, not
   probed*) - or a record an earlier command left: `HadErrors` is true while any record remains (psl-2,
   psl-3; measured), so the branch would have told a clean write as an erroring one had a later edit left a
   record before it. Every command before the write returns on `HadErrors` today, so none does. With the
   branch gone `-ErrorAction Stop` on the write is load-bearing: the stand-in's refusal is a written error,
   seen only because it is thrown.
4. Comments after the mutation round.

**Tests.**
- `ConferenceRoomBulkProcessorTests` (the fake ops): a success row whose result carries
  `DirectoryMembershipConfirmed` - its message opens with the result's and contains the detail; without
  one, the result's message as it is.
- `ConferenceRoomDirectoryAuditTests`, bulk, through the harness (`ApplyAndReadAudit` keeps the row
  outcome): the race with the room there, and the add applied then failed - the row's message contains the
  `membershipConfirmedPresent` its audit records.
- `ConferenceRoomRoomListPreflightTests`: a re-run inside the sync window - the first run adds the room
  on-prem, the second finds it already a member - the two room-list steps differ, both successful, the
  no-op's naming the list with no `Error`; one `Set-ADGroup` in all; the second names nothing written.
- The dead branch. The harness gains `SetAdGroupReportsErrorWithoutThrowing` (a message): the stand-in
  writes it with its own `-ErrorAction Continue`, so `-ErrorAction Stop` does not throw it, and carries on.
  Applied: success, the room's object written, no confirmed detail, three membership reads - the error not
  the write's, and the record it left not failing the read-back (the `finally`'s clear). Not applied
  (`SetAdGroupChangesNothing`): failed, partial, without the error's text. The harness test pins the knob:
  no throw under `-ErrorAction Stop`, `HadErrors`, the record, the add applied.

**Mutations** (md5 before and after, the named failing tests, restored, md5 matched, touched, rebuilt,
green): M1 the bulk message without the detail (`Message = r.Message`, the call site); M2 the no-op given
the add's label (the call site); M3 the `HadErrors` branch restored; M4 `-ErrorAction Stop` dropped from
the write; M5 the `finally`'s `Streams.Error.Clear()` dropped; R the service as at `501ea43e` (the fix
reverted - the tests name nothing it adds, so they compile against it); H1 the harness knob ignored;
D, a demonstration: the service as at `501ea43e` with M4 - expected green, the branch masking the
dependency M4 shows.

**Files.** `Services/ConferenceRoomService.cs`, `Services/Jobs/ConferenceRoomBulkProcessor.cs`,
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomBulkProcessorTests.cs`,
this record.

## Guard proof

Pending.
