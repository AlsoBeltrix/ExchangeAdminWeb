# aud-a2o: Conference Rooms on-prem add - bulk row message omits the confirmed-membership detail, the no-op reuses the add's step label, and an unreachable error branch has no test

**Severity**: LOW - operability and dead code, no wrong write. (1) When a row succeeds only because
the read-back confirmed the membership (aud-a2n), the page's step and the audit `extra` carry the
detail but a bulk job row's message does not, so a bulk operator cannot tell a confirmed-present
row from an applied write. (2) The on-prem already-a-member no-op uses the same step label as a
real add, "Add to '<list>' (on-prem)", so the step list reads as if a write happened. (3) The
write's error-without-a-throw (`HadErrors`) branch cannot be reached under `-ErrorAction Stop`
and has no test; dead code that a later edit could make live and wrong.
**Status**: Merged to master (`15de2fcf`); closed on guard proof; codex review pending (batched with aud-a2p); dev acceptance owed
**Branch**: `worktree-agent-a9e6dd9435998c5ff`
**Commit**: `df579d08` (fix, tests, comments, record); plan `e53a2e2d`; merged `15de2fcf`
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

## Approach (as implemented)

- `ConferenceRoomBulkProcessor.ProcessRowAsync` (`Services/Jobs/ConferenceRoomBulkProcessor.cs`): the row
  outcome's `Message` is `r.Message`, then a space and `r.DirectoryMembershipConfirmed` when that is set - the
  detail the page shows as the on-prem step's `Error` and the audit records as `membershipConfirmedPresent`.
  The service's message, the audit, the trace and the completion notification are unchanged; so is the page
  (1758 lines, md5 `6607b944fae10efefca43e99674e19b3` before and after, no `ClickGateRegistry` entry moved).
- `AddToRoomListAsync` (`Services/ConferenceRoomService.cs`), the on-prem fallback's success: `alreadyMember`
  is `adWritten is null && adConfirmed is null`, the `(null, null, null)` the add's header documents for the
  already-a-member no-op; its step is `Already in '<list>' (on-prem)`, every other success's
  `Add to '<list>' (on-prem)` as before. Result fields unchanged, so the audit is (aud-a2g).
- `AddToRoomListViaAdAsync`: the write's `if (ps.HadErrors) writeError = <its first record's exception> ??
  <a generic error>` is removed. The write is `try { ps.Invoke(); } catch (Exception ex) { writeError = ex; }
  finally { the commands and the error stream cleared }`, the form of `GroupManagementService.AddMemberAsync`'s
  and `RemoveMemberAsync`'s `Set-ADGroup` writes. aud-a2n's sentence that an error reported without a throw is
  "held the same way" is replaced by a comment saying why there is none and what the check risked; the
  `finally`'s comment says what its error-stream clear protects.
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`): `SetAdGroupReportsErrorWithoutThrowing`, a message
  the stand-in `Set-ADGroup` writes after the add with its own `-ErrorAction Continue`, so the caller's
  `-ErrorAction Stop` does not throw it.
- Tests: new - `ConferenceRoomBulkProcessorTests.ARowsMessage_CarriesAMembershipTheReadBackConfirmed_WhenTheResultHasOne`,
  `ConferenceRoomDirectoryAuditTests.ABulkRowConfirmedNotWritten_SaysSoInItsMessage_AsItsAuditDoes` (its
  `ApplyAndReadAudit` keeps the bulk row outcome), `ConferenceRoomRoomListPreflightTests.TheOnPremNoOp_HasAStepOfItsOwn_NotTheAddsStep`
  and `AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForItsError_TheReadBackDecides`; extended -
  `TheHarness_SetADGroup_AddsTheMember_RefusesOneAlreadyThere_AndCanDropOrBeRaced`, the knob's section.

**Why the dead branch was removed and not pinned.** The brief's rule: pick what matches `GroupManagementService`,
whose member writes have no `HadErrors` check - only a thrown error is the write's, the read-back deciding. The
scratch probe (`scratchpad/a2o-round/probe-errorstream.ps1`, pwsh 7.6.6, an in-process runspace, no directory)
measured: an advanced function called with `-ErrorAction Stop` whose own `Write-Error` is promoted throws from
`Invoke` and leaves 0 records in `Streams.Error`; one whose `Write-Error` carries `-ErrorAction Continue` does not
throw, `HadErrors` true, 1 record; a later clean invocation with that record still in the stream reports
`HadErrors` true (psl-2), and false once the stream is cleared. So the branch could fire only on an error written
past the preference - *assumption, not probed: a compiled cmdlet such as `Set-ADGroup` writes none* - or on a
record an earlier command left, which every command before the write prevents by returning on `HadErrors`.
Kept, it would have told a clean write as a failing one the moment a later edit left such a record; and, as D
below shows, it masked the write's dependence on `-ErrorAction Stop`, which removing it makes load-bearing and
pinned (M4).

## Guard proof

| Test | What it pins |
| --- | --- |
| `ConferenceRoomBulkProcessorTests.ARowsMessage_CarriesAMembershipTheReadBackConfirmed_WhenTheResultHasOne` (confirmed / not) | part 1, the composition: a result carrying `DirectoryMembershipConfirmed` - the row's message opens with the result's and contains the detail; without one, the result's message as it is |
| `ConferenceRoomDirectoryAuditTests.ABulkRowConfirmedNotWritten_SaysSoInItsMessage_AsItsAuditDoes` (refused, the room there; applied, then failed) | part 1 end to end, the real service and processor over the harness: the bulk row, a success, carries in its message the `membershipConfirmedPresent` its audit records |
| `ConferenceRoomRoomListPreflightTests.TheOnPremNoOp_HasAStepOfItsOwn_NotTheAddsStep` | part 2: a re-run inside the sync window - the first run's on-prem add and the second's already-a-member no-op have different room-list steps (the condition, not the wording), both successful, the no-op's naming the list with no `Error`; one `Set-ADGroup` in all; the no-op names nothing written or confirmed |
| `ConferenceRoomRoomListPreflightTests.AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForItsError_TheReadBackDecides` (applied / not) | part 3, the chosen treatment: an error the write reports without a throw is not its error - applied, the room's objectGUID written, no confirmed detail; not applied, failed and partial without that text - and the record it left does not fail the read-back (three membership reads, the third decisive) |
| `ConferenceRoomRoomListPreflightTests.TheHarness_SetADGroup_AddsTheMember_RefusesOneAlreadyThere_AndCanDropOrBeRaced` (extended) | the knob, so the test above cannot pass for want of an error: under `-ErrorAction Stop` the stand-in applies the add and reports the error without a throw - `HadErrors`, one record, its message |

**Mutations** (`scratchpad/a2o-round/round.ps1`, the a2m/a2n runner copied, with `rows-a2o.ps1`; log
`round-a2o.log` beside them; 2026-10-22 11:09-11:14). Each row ran `ConferenceRoomRoomListPreflightTests`,
`ConferenceRoomDirectoryAuditTests` and `ConferenceRoomBulkProcessorTests` (131 cases), the test project rebuilt
each time. md5 before = restored: `Services/ConferenceRoomService.cs` `203da093825347e4705b742bbdae628a` (M2-M5, R, D);
`Services/Jobs/ConferenceRoomBulkProcessor.cs` `84247b3918670ff4631905d6509eba24` (M1);
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` `b0d12c62618d08f789be80ca0fcd1adf` (H1). Each restored file was
touched, the next row rebuilding from it. `Audit.` is `ConferenceRoomDirectoryAuditTests`; other names are in
`ConferenceRoomRoomListPreflightTests` unless qualified.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1 | the brief's, at the call site: the bulk row's message without the detail (`Message = r.Message`) | `609a928950bb150112961f7e9e225cdc` | `ConferenceRoomBulkProcessorTests.ARowsMessage_...`; `Audit.ABulkRowConfirmedNotWritten_...` | FAIL, 3 of 131: the confirmed case (the detail not in "Room metadata and room list configured.") and both end-to-end rows (the audit's detail not in the row's message). The mutated md5 is the processor as at `501ea43e`: M1 is also the processor's revert |
| M2 | the brief's, at the call site: the no-op given the add's label | `327730fe7983ab8001a1585d4990ede9` | `TheOnPremNoOp_HasAStepOfItsOwn_NotTheAddsStep` | FAIL, 1: both steps "Add to 'Building 1 Conference Rooms' (on-prem)" |
| M3 | the dead branch restored (the `HadErrors` lines as at `501ea43e`) | `4d594cfdefef89d98ffe0f37adfda357` | `AnErrorTheWriteReportsWithoutThrowing_...` | FAIL, 2: applied - nothing written (taken for a write error, then confirmed); not applied - the reported text in the message ("... and Set-ADGroup reported an error (...)") |
| M4 | `-ErrorAction Stop` dropped from the write | `0308f4517c4ebc8c75c26bb6b46f1d16` | `AnOnPremAddRefusedAsAValueAlreadyThere_IsSettledByTheReadBack`; `AValueAlreadyThereRefusal_IsKnownByItsCode_InAnyLanguage`; `AWriteErrorWithAnotherCode_...`; `Audit.AnOnPremAddRefusedAsAValueAlreadyThere_IsNeverRecordedAsWritten`; `Audit.ABulkRowConfirmedNotWritten_...` | FAIL, 7: every refusal the stand-in writes - the race, the 68 and 20 refusals and the 8224 error in their error-record form - no longer an error: the room's objectGUID reported written, no confirmed detail, on page and bulk. PASS: the thrown forms (`Failures`, a `throw`), which do not depend on the preference |
| M5 | the write's `finally` no longer clears the error stream (the commands still cleared) | `7f97a8760fa149b7e7a542e4057759b2` | `AnErrorTheWriteReportsWithoutThrowing_...` | FAIL, 2: the record the write left makes the read-back's `HadErrors` true, so the read "fails" with the write's text - applied, a failed row ("... reading its group ... back from contoso.test failed: A non-terminating error ..."); not applied, the text in the message. psl-2 measured in this path. PASS: every other test - a thrown error leaves no record, as the probe measured |
| R | the revert: the service as at `501ea43e` (with M1, the processor's revert, the whole fix) | `e3281e44212efdf6898e631381f1aa68` (= `501ea43e`) | `TheOnPremNoOp_...`; `AnErrorTheWriteReportsWithoutThrowing_...` | FAIL, 3: the two steps alike; applied - nothing written; not applied - the reported text in the message |
| H1 | harness: `SetAdGroupReportsErrorWithoutThrowing` ignored (the stand-in reports nothing) | `a3ae2cb844754a2521883db1118eb877` | the harness test | FAIL, 1: the harness test (`HadErrors` false). *Demonstration:* the dead-branch test PASSES both ways - with no error reported it pins nothing - the wrong-reason pass only the harness test stops |
| D | *demonstration*: the service as at `501ea43e`, the `HadErrors` branch present, with M4 | `5927100f65ee3613f287e9720d09b760` | the 7 tests M4 fails | PASS, all 7: the branch takes each written refusal from the stream, so while it is there no test notices `-ErrorAction Stop` gone. The 3 that fail are R's - the service before this fix - failing as in R |

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 238 `ConferenceRoom*`
cases green.

**Comments after the round.** The round ran on the fix with aud-a2n's now-wrong sentence on the write removed and
none of the new explanatory comments; they were written afterwards and checked by `scratchpad/a2o-round/comment_only.ps1`
(the a2m/a2n checker: blank, `//`, `///` and PowerShell `#` lines dropped from both versions, the code lines identical
in order): service `203da093825347e4705b742bbdae628a` -> `90b7e62d6c261c1b5637337911a1bf97` (code lines 1558 = 1558;
comment and blank lines 747 -> 758); processor `84247b3918670ff4631905d6509eba24` -> `f542d878dcbe400c54d03ea8dd88d303`
(171 = 171; 90 -> 94); harness `b0d12c62618d08f789be80ca0fcd1adf` -> `2bbdc41d779c523ebc887f3d8767ad2d` (605 = 605;
322 -> 330). Unchanged since the round: the three test files, whose comments were written with the tests
(`ConferenceRoomRoomListPreflightTests.cs` `1a7675ac98e7fc5331f38d61483fdf59`, `ConferenceRoomDirectoryAuditTests.cs`
`ea7af855bfbe2e92f7753b5f9a110cbb`, `ConferenceRoomBulkProcessorTests.cs` `81857a2b80b80d5dda2b7c27ef60eb01`), and the page.

## Known gaps

- The no-op is known by the add's two nulls - its documented `(null, null, null)` - not by a flag of its own. A later
  change returning that shape for anything else would label it "Already in"; the result fields and the audit would
  not be affected.
- *Assumption, not probed* (no directory call is made here): the real `Set-ADGroup`, a compiled cmdlet, writes no
  error that `-ErrorAction Stop` leaves unthrown. Were one ever reported, it is now left unread like
  GroupManagementService's: the read-back decides, and a room found is reported written - aud-a2n's "nothing
  recorded as written after a write error" does not fire for an error that never reached the code.
- The probe ran on pwsh 7.6.6; the tests' SDK is 7.6.1. The round's M5 and the harness test show the same
  behaviour on the SDK.
- The bulk row's message is long when it carries the detail (the group's DN, the server read and the write's error
  text) - the text the page's step already shows.
- The single-room page's alert shows the detail on the step only, as before; the admin notifications and the
  operation traces carry the message without it, unchanged.

**Operator-visible changes.**

- A bulk job's row for a room-list membership the on-prem read-back confirmed after a refused or erroring write now
  says so: "Room metadata and room list configured. Membership confirmed present after ..." instead of the bare
  success message.
- A room already in its synced list's on-prem group, not yet on Exchange's copy, shows the step
  "Already in '<list>' (on-prem)" instead of "Add to '<list>' (on-prem)".
- Nothing else: audits unchanged; no outcome a real cmdlet is known to produce changes with the removed branch.

**Dev acceptance owed** with aud-a2f, a2i, a2j, a2k, a2l, a2m and a2n: a Room Finder bulk job on a synced list,
one row's membership confirmed by the read-back, reading its row's message; a re-run of a row inside the sync
window, reading its steps. Not run here: no directory or Exchange call was made.

**Page:** not edited; 1758 lines; no `ClickGateRegistry` entry moved.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a9e6dd9435998c5ff`. Independent review pending.

For the reviewer:

- The no-op's label is "Already in '<list>' (on-prem)", not the record's example "Already on": the cloud no-op is
  already "Already in '<list>'", so the two no-ops read alike.
- The dead branch is removed, not pinned, per the brief's "what matches `GroupManagementService`"; the test pins the
  removal's behaviour (an error reported without a throw is left unread, the read-back deciding) and the `finally`'s
  error-stream clear (M5), and the harness test pins the knob (H1). M4 and D together show what the branch hid.
- Outside the Conference Rooms service, processor and their tests, this fix touches only the harness (test-only,
  additive: one knob, default off).

## Closeout

Fixed on branch, not merged, not pushed: the plan `e53a2e2d`, then this fix, tests, comments and record in one
commit. A module-scoped behaviour change (the bulk row's message, the step label): the Conference Rooms module
version bump is due at merge - not made here, `Modules/ModuleCatalog.cs` being outside this brief. No shared code
changed - the processor is Conference Rooms' own, the harness test-only - so no base app bump.

Gates, run on the final files this commit holds (the code unchanged since; the record is not built):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5181 passed, 0 failed, 3 skipped (the known
  AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5184 total, 5 m 25 s; nothing hung, no
  Sequence file. This record's new cases are 7 (2 bulk-processor, 2 audit, 3 preflight; the extended harness test is an
  existing case); the 316 `ClickGate*` cases are among those passed.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the harness's stand-ins are PowerShell inside a
C# string, run by the tests).
