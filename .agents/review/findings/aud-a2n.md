# aud-a2n: The value-already-there detection matches English error text, and the add's failure log still names the old cmdlet

**Severity**: LOW - on a non-English domain controller or module locale the aud-a2l
"value already exists" refusal is not recognised, so the race falls back to the old PARTIAL result
after Exchange metadata committed (no wrong write; a re-run says "already a member"). The outer
catch's log line "Add-ADGroupMember on-prem room-list write failed" names a cmdlet the code has
not used since aud-a2j, which misleads whoever reads the log.
**Status**: Merged to master (`5c116ce4`); closed on guard proof; codex review pending; dev acceptance owed; the live AD error-code probe is an owner item
**Branch**: `worktree-agent-a76a90b485c4fa8ce`
**Commit**: `e223e131` (fix, tests, comments, record); plan `bd961140`; merged `5c116ce4`
**Reviewer**: implementer (surfaced by the aud-a2k/a2l agent, 2026-10-22, candidates 1 and 3 of its hand-back)

## Evidence

At `28263a6c`, `Services/ConferenceRoomService.cs`, `AddToRoomListViaAdAsync`: the duplicate
check matches the thrown error's message against "already a member", "already exists" and
`ENTRY_EXISTS`. The ActiveDirectory module surfaces `ADException` with an `ErrorCode` (the LDAP
result code; `ENTRY_EXISTS` is 68 / `0x44`) and, for linked-attribute adds, an
"attribute or value exists" error from the DC that carries the Win32 code 8305
(`ERROR_DS_ATTRIBUTE_OR_VALUE_EXISTS`) or the LDAP code 20 (`attributeOrValueExists`). The text is
localised; the codes are not. Which code the live module raises for this case is an assumption until
a dev probe confirms it (owner go required for any AD probe).

## Approach (to be written by the implementing agent before code)

- Decide by error code, not text: read the `ErrorRecord`'s exception chain for an `ADException`
  `ErrorCode` (LDAP 20 `attributeOrValueExists`, 68 `entryAlreadyExists`) or a Win32 8305, and treat
  the text match only as a secondary signal. Record the assumption about which code the live module
  raises and name the dev probe that would confirm it; the aud-a2l read-back already makes the
  outcome correct whenever the error is recognised, and a miss is still a safe failure.
- Correct the outer catch's log text to name `Set-ADGroup` and the member attribute.
- Tests through `ExchangeWriteHarness`: a stand-in raising an error with the LDAP code and a
  non-English message is settled by the read-back; a stand-in raising an unrelated code with a
  message that happens to contain "already exists" stays a failure (the code wins). Mutation at the
  call site: match text only.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above, the two folded-in items below and the supervising session's brief. The
folded-in item 2 supersedes the a2l rule that every write error but the duplicate stays a failure
without a read-back (aud-a2l, the brief's rule then); "stays a failure" in the approach above becomes
"is not taken for a duplicate, and is read back like every other error".

**Rule.** Every `Set-ADGroup` outcome on the on-prem room-list add is settled by reading the group's
`member` attribute back: the room there is a success, absent or unreadable a failed row carrying the
write's error. A success that only the read-back produced - the write refused or erroring - names no
written directory object and says why. Whether a refusal means "the value is already there" is
decided by its error code; the text decides only when no code is carried.

1. **By code.** `IsValueAlreadyPresentRefusal(Exception)`, internal static. It walks every exception
   the error carries: itself, its inner exceptions, and each `ErrorRecord` PowerShell attached
   (under `-ErrorAction Stop` the cmdlet's own exception sits in the thrown exception's `ErrorRecord`,
   not in its `InnerException`). An exception whose type, or a base type, is named
   `Microsoft.ActiveDirectory.Management.ADException` - by name, as `Comms10kReplaceWriter.IsRetryable`
   matches it, the assembly being referenced nowhere - contributes its `ErrorCode`, read by
   reflection; 0 is no code. Any code carried decides, the text ignored: a duplicate when one is LDAP
   20 (`attributeOrValueExists`), LDAP 68 (`entryAlreadyExists`), Win32 1378 (`ERROR_MEMBER_IN_ALIAS`,
   the extended error `00000562 ... ENTRY_EXISTS` a domain controller pairs with LDAP 68 for a member
   value already present), 8205 (`ERROR_DS_ATTRIBUTE_OR_VALUE_EXISTS`), 8305 or 8323
   (`ERROR_DS_ATT_VAL_ALREADY_EXISTS`). No code carried: aud-a2l's text match, `IsDuplicateMemberValue`,
   over the same exceptions. *For the reviewer, from winerror.h as recalled, not verified here:* the
   brief and the approach pair 8305 with `ERROR_DS_ATTRIBUTE_OR_VALUE_EXISTS`, which is 8205; 8305 is
   `ERROR_DS_OBJ_STRING_NAME_EXISTS` ("a name that is already in use"). Both are in the set, and 1378
   and 8323, the forms the a2l text list already names, are added so a coded refusal in those forms is
   not demoted by the code-wins rule. A code in the set by mistake costs a mislabelled detail only: the
   read-back decides the row.
2. **Every error read back.** `AddToRoomListViaAdAsync`: the write in `try` / `catch (Exception)` /
   `finally` (commands and error stream cleared), a non-thrown `HadErrors` taken the same way (its first
   error record's exception), then the a2i read-back on the group's own domain whatever the write
   reported. No error and the room there: `(null, roomGuid)`, written (aud-a2g), as now. An error and
   the room there: success, (3). Absent: failed, naming the group, the server read and the write's
   error. The read fails: failed, "could not be confirmed", with the read's and the write's errors.
3. **Not written.** That success returns no written directory object, and a detail: "Membership
   confirmed present after Active Directory refused the add as a value already there (<error>) ..." or
   "Membership confirmed present after Set-ADGroup reported an error (<error>) ...", naming the group
   and the server read - the two openings `internal const` on the service, the path a test can assert.
   The detail is the on-prem step's `Error` (the step `Add to '<list>' (on-prem)`, successful) and
   `RoomOperationResult.DirectoryMembershipConfirmed`, which both audit surfaces record in `extra` as
   `membershipConfirmedPresent` (`ConferenceRoomService.MembershipConfirmedAuditKey`,
   `WithMembershipConfirmed(extra, detail)`): the page's `AuditFinderAction`, edited in place so the
   page keeps its 1758 lines and no `ClickGateRegistry` entry moves, and the bulk processor's `Audit`.
   `ValidatedMailbox.WithWrittenIdentity` is unchanged. The row's message is unchanged.
4. **The log.** The outer catch's line names `Set-ADGroup` and the member attribute; since (2) it sees
   only the steps around the write.

*Assumption, recorded, not probed - the owner's standing instruction is not to probe Active Directory:*
which code the live ActiveDirectory module raises when `Set-ADGroup -Add @{ member = <DN> }` names a DN
the group already holds. Expected, from the DC's documented behaviour: an `ADException` whose
`ErrorCode` is 1378 (or the LDAP 68 it pairs with), its message the localised "The specified account
name is already a member of the group." Possible instead: no error at all - Comms10k measured `-Add` of
a member already present as a silent no-op (`Comms10kReplaceWriter.IsRetryable` remarks), which would
leave the refusal path dormant live and report a raced add as written, the write having raised
nothing. **The dev probe that would settle it (owner go required):** on the dev host, under the module
credential, against a test group it may write and a DN already in that group, `Set-ADGroup -Identity
<group objectGUID> -Add @{ member = <that DN> } -Server <group's domain> -ErrorAction Stop`, recording
whether it throws and, if so, the exception's type name, `ErrorCode`, `ServerErrorMessage`,
`InnerException` and the error record's `FullyQualifiedErrorId`.

**Tests** (`ExchangeWriteHarness`). The test double `Microsoft.ActiveDirectory.Management.ADException`
(`AdManagementExceptionDoubles.cs`, Comms10k's) gains `ErrorCode`. The stand-in `Set-ADGroup` refuses a
DN already there with a coded error (`DuplicateMemberValueRefusal`, an `ADException` with 1378 and the
1378 text, Write-Error then return, so the code sits in the error record); `Failures['Set-ADGroup']`
may be an exception, thrown as it is; `SetAdGroupFailsAfterApplying` applies the add and then throws;
`MemberReadBackFailure` fails the member reads made after a `Set-ADGroup`. In
`ConferenceRoomRoomListPreflightTests`:
- a refusal carrying the LDAP code under a non-English message, the room there: success, not partial,
  no written object, the detail opening "after Active Directory refused";
- a code outside the set whose message says "already exists", the room there: success by the
  read-back, the detail opening "after Set-ADGroup reported an error" - not taken for a duplicate;
- `Set-ADGroup` applies the add, then throws a timeout: success, the timeout in the detail, no written
  object; throws without applying: failed, partial, the timeout in the message, the read-back made;
- the read-back fails after a write error: failed, both errors in the message;
- the race (aud-a2l's): success, no written object, the refusal's detail; absent: failed, as before;
- the normal write: written, no detail; `AnyOtherWriteError_StaysAFailure_EvenWithTheRoomThere`
  becomes the reconciled success;
- pure: the decision by code (each code in the set; codes outside it; the code in the error record, in
  an inner exception; text only when no code).
In `ConferenceRoomDirectoryAuditTests`, page and bulk: the race - Success, no `writtenDirectoryObject`,
`membershipConfirmedPresent`; applied then failed - the same, the error in it; the normal on-prem add
- `writtenDirectoryObject`, no `membershipConfirmedPresent`.

**Mutations, at the call site:** (2) text only - the call asks `IsDuplicateMemberValue` again; (3) only
duplicate errors read back - the catch filtered again; (4) `roomGuid` returned on the refused-duplicate
path. Each: md5 before and after, the named failing tests, restored, md5 matched, touched, rebuilt,
green. Comments after the round.

**Files.** `Services/ConferenceRoomService.cs`, `Models/ConferenceRoomModels.cs`,
`Components/Pages/ConferenceRooms.razor`, `Services/Jobs/ConferenceRoomBulkProcessor.cs`,
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, `ExchangeAdminWeb.Tests/AdManagementExceptionDoubles.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`, this record.

## Approach (as implemented)

- `AddToRoomListViaAdAsync` (`Services/ConferenceRoomService.cs`) returns `(error, written, confirmed)`.
  The write, aud-a2j's and unchanged, runs in `try` / `catch (Exception)` / `finally`: the thrown
  exception - or, not thrown, the first error record's exception when `HadErrors` - is held as
  `writeError`, and the `finally` clears the commands and the error stream (aud-a2l's). `refusedAsPresent`
  is `IsValueAlreadyPresentRefusal(writeError)`. Then aud-a2l's read-back, unchanged, whatever the write
  reported:
  - a read error: "Adding <room> to on-prem room list '<list>' could not be confirmed: reading its group
    <DN> back from <server> failed: <read error>", then " Before that, Active Directory refused the add as a
    value already there (<error>)." or " Before that, Set-ADGroup reported an error (<error>)." when the
    write erred; logged;
  - the room absent: aud-a2l's "... was not confirmed: its group <DN>, read back from <server>, does not
    hold the room as a member", ending "." with no write error, ", though Active Directory refused the add
    as a value already there (<error>)." for a refusal (aud-a2l's words) and ", and Set-ADGroup reported an
    error (<error>)." for any other error; logged;
  - the room there and no write error: `(null, roomGuid, null)`, written, as before;
  - the room there after a write error: `(null, null, <detail>)`, logged as a warning - `ConfirmedAfterRefusal`
    or `ConfirmedAfterWriteError`, then " (<error>): its group <DN>, read back from <server>, holds the room.
    No directory object is recorded as written: " and "this row's add did not put it there." or "whether
    this row's add put it there is not known.".
  The outer catch now sees only the steps around the write; its log line is "On-prem room-list member add
  (Set-ADGroup on the group's member attribute) failed for {Room} on {List}", its message unchanged.
- `AddToRoomListAsync` puts the detail in `RoomOperationResult.DirectoryMembershipConfirmed` and in the
  on-prem step's `Error` (the step still `Add to '<list>' (on-prem)`, successful);
  `SetRoomMetadataAndListAsync` carries it to the row beside `WrittenDirectoryObject`. The row's message
  is unchanged.
- `IsValueAlreadyPresentRefusal(Exception)`, internal static: the codes of the exceptions
  `ExceptionsCarriedBy` yields - the exception, each `InnerException`, and each `IContainsErrorRecord`'s
  `ErrorRecord.Exception`, each once - read by `DirectoryErrorCode` from a type, or a base type, named
  `Microsoft.ActiveDirectory.Management.ADException` (its `ErrorCode` by reflection; 0, or a read that
  throws, is no code). Any code: true when one is in `ValueAlreadyPresentCodes` {20, 68, 1378, 8205, 8305,
  8323}. None: `IsDuplicateMemberValue(Exception)`, aud-a2l's text match, which now walks the same
  exceptions; its markers are unchanged.
- Audit: `ConferenceRoomService.MembershipConfirmedAuditKey` (`membershipConfirmedPresent`) and
  `WithMembershipConfirmed(extra, detail)`, which adds the key only for a detail. The page's
  `AuditFinderAction` takes `membershipConfirmed` and wraps the `extra` it built
  (`Components/Pages/ConferenceRooms.razor`: three lines edited in place, 1758 lines before and after); the
  bulk processor's `Audit` the same (`Services/Jobs/ConferenceRoomBulkProcessor.cs`); the model gains
  `DirectoryMembershipConfirmed` (`Models/ConferenceRoomModels.cs`).
- Harness: the `Microsoft.ActiveDirectory.Management.ADException` double (`AdManagementExceptionDoubles.cs`,
  Comms10k's) gains a `(message, errorCode)` constructor and `ErrorCode`. `ExchangeWriteHarness`:
  `DuplicateMemberValueRefusal` (by default that double, 1378 and the 1378 text), with which the stand-in
  `Set-ADGroup` refuses a DN already there (`Write-Error -Exception` then `return`, so under `-ErrorAction
  Stop` the exception sits in the thrown exception's error record); `SetAdGroupFailsAfterApplying`, thrown
  after the add is applied; `MemberReadBackFailure`, with which the stand-in `Get-ADGroup` fails a read of
  `member` made after any `Set-ADGroup` call. `Failures['Set-ADGroup']` may be an exception: the stand-in
  throws it as it is, so its code is the `InnerException` of what PowerShell throws.
- Tests (`ConferenceRoomRoomListPreflightTests`): new - `AValueAlreadyThereRefusal_IsKnownByItsCode_InAnyLanguage`
  (68 German, both ways; 20 French; 1378 Dutch), `AWriteErrorWithAnotherCode_IsNotTakenForAValueAlreadyThere_ThoughItsTextSaysAlreadyExists`
  (both ways), `ASetADGroupThatAppliedTheAddThenFailed_IsASuccess_WithTheErrorInTheDetail_NothingRecordedAsWritten`,
  `ASetADGroupThatFailedWithoutApplying_StaysAFailedRow_CarryingTheWriteError`,
  `AReadBackThatFailsAfterAWriteError_IsAFailedRow_CarryingBothErrors`,
  `AValueAlreadyThereRefusal_IsDecidedByItsCode_TheTextOnlyWhenItCarriesNone` (twelve: each code, the code
  directly, as an inner exception, in an error record and on a derived type; codes outside the set under
  duplicate text; no code) and `AnErrorCodeOnAnyOtherExceptionType_IsNotADirectoryCode`. Changed -
  `AnOnPremAddRefusedAsAValueAlreadyThere_IsSettledByTheReadBack` (never written; the refusal's detail when
  the room is there), `AnyOtherWriteError_StaysAFailure_EvenWithTheRoomThere` renamed
  `AnyOtherWriteError_WithTheRoomThere_IsSettledByTheReadBack_NotRecordedAsWritten` (its outcome inverted, as
  the folded-in item 2 requires), `TheOnPremAdd_IsConfirmedByReadingTheGroupsMemberAttributeBack` (no detail)
  and `TheHarness_SetADGroup_AddsTheMember_RefusesOneAlreadyThere_AndCanDropOrBeRaced` (the refusal's
  exception in the error record; the two new knobs). `ConferenceRoomDirectoryAuditTests`, page and bulk:
  `AnOnPremAddRefusedAsAValueAlreadyThere_IsRecordedAsWrittenOnlyWhenTheReadBackShowsTheRoom` renamed
  `AnOnPremAddRefusedAsAValueAlreadyThere_IsNeverRecordedAsWritten`; new
  `AnOnPremAddThatAppliedThenFailed_IsASuccess_RecordedAsConfirmed_NotAsWritten`; `TheOnPremRoomListAdd_IsRecordedAsWritten`
  also pins no `membershipConfirmedPresent`.

## Guard proof

| Test | What it pins |
| --- | --- |
| `AValueAlreadyThereRefusal_IsKnownByItsCode_InAnyLanguage` (68 German, thrown and in the error record; 20 French; 1378 Dutch) | the finding: a raced add refused with a value-already-there code under a message no English marker matches - success, not partial, nothing written, the detail opening `ConfirmedAfterRefusal`, the read-back made (three membership reads) |
| `AWriteErrorWithAnotherCode_IsNotTakenForAValueAlreadyThere_ThoughItsTextSaysAlreadyExists` (both ways) | the code wins: 8224 under "... already exists ..." with the room there - a success by the read-back, nothing written, the detail opening `ConfirmedAfterWriteError`, not the refusal's |
| `ASetADGroupThatAppliedTheAddThenFailed_IsASuccess_WithTheErrorInTheDetail_NothingRecordedAsWritten` | folded-in item 2: the add applied, then a timeout - success, not partial, nothing written, the room on the group; the detail opens `ConfirmedAfterWriteError`, carries the timeout's text and is the on-prem step's `Error` |
| `ASetADGroupThatFailedWithoutApplying_StaysAFailedRow_CarryingTheWriteError` | the same timeout, nothing applied: failed, partial, nothing written, no detail, the timeout in the message - and the read-back made |
| `AReadBackThatFailsAfterAWriteError_IsAFailedRow_CarryingBothErrors` | a write error and a read-back that cannot be made: failed, partial, both errors in the message |
| `AnyOtherWriteError_WithTheRoomThere_IsSettledByTheReadBack_NotRecordedAsWritten` (was aud-a2l's `AnyOtherWriteError_StaysAFailure_EvenWithTheRoomThere`) | folded-in item 2 on the race: a timeout with another actor's add of the room first - success, nothing written, the timeout in the detail, three membership reads (two before) |
| `AnOnPremAddRefusedAsAValueAlreadyThere_IsSettledByTheReadBack` (there / not; changed) | folded-in item 1: the refusal with the room there - success and nothing written (aud-a2l named the room), the refusal's detail; absent - failed, no detail |
| `TheOnPremAdd_IsConfirmedByReadingTheGroupsMemberAttributeBack` (changed) | the normal applied write still names the room's objectGUID, and carries no detail |
| `AValueAlreadyThereRefusal_IsDecidedByItsCode_TheTextOnlyWhenItCarriesNone` (twelve) | the decision, pure: every code in the set under a German message, directly, as an inner exception, in an error record and on a type derived from the double; codes 8224, 50 and 1722 under duplicate text - not duplicates; no code (0) - the text decides |
| `AnErrorCodeOnAnyOtherExceptionType_IsNotADirectoryCode` | an `ExternalException`'s `ErrorCode` is not read as a directory code; the text decides |
| `TheHarness_SetADGroup_AddsTheMember_RefusesOneAlreadyThere_AndCanDropOrBeRaced` (changed) | the stand-in's refusal is the coded exception, in the thrown exception's error record, and recognized; `SetAdGroupFailsAfterApplying` applies the add, then throws; `MemberReadBackFailure` fails a member read after a write and not one before |
| `ConferenceRoomDirectoryAuditTests.AnOnPremAddRefusedAsAValueAlreadyThere_IsNeverRecordedAsWritten` (page, bulk x there / not) | the audit: no `writtenDirectoryObject` either way; Success and `membershipConfirmedPresent` opening `ConfirmedAfterRefusal` when the room is there, Failed and no such key when not |
| `ConferenceRoomDirectoryAuditTests.AnOnPremAddThatAppliedThenFailed_IsASuccess_RecordedAsConfirmed_NotAsWritten` (page, bulk) | the audit: Success, no `writtenDirectoryObject`, `membershipConfirmedPresent` opening `ConfirmedAfterWriteError` with the timeout's text |
| `ConferenceRoomDirectoryAuditTests.TheOnPremRoomListAdd_IsRecordedAsWritten` (page, bulk; changed) | the normal add: `writtenDirectoryObject` the room's objectGUID, no `membershipConfirmedPresent` |

**Mutations** (`scratchpad/a2m-a2n-round/round.ps1` with `rows-a2n.ps1`, log `round-a2n.log` beside them,
2026-10-22 10:26-10:30; the a2m runner, the same rules). Each row ran `ConferenceRoomRoomListPreflightTests`
and `ConferenceRoomDirectoryAuditTests` (105 cases). M rows: `Services/ConferenceRoomService.cs`, md5 before =
restored = `8f395bc14f788447df038eca197d34cf`. H rows: `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, before =
restored = `63b19f297430449041690e72eac78911`. Test names are in `ConferenceRoomRoomListPreflightTests` unless
qualified (`Audit.` is `ConferenceRoomDirectoryAuditTests`). No revert row: the tests name members this fix
adds, so the file before it does not compile against them; M3 and M4 are the two halves of that revert.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M2 | the brief's: the call site decides by text only (`IsDuplicateMemberValue(writeError)`) | `4311a64887f00d051f92137b7b830078` | `AValueAlreadyThereRefusal_IsKnownByItsCode_InAnyLanguage`; `AWriteErrorWithAnotherCode_...` | FAIL, 6 of 105: the four foreign-language refusals told as a write error ("... after Set-ADGroup reported an error"); the 8224 "already exists" error, both ways, told as a refusal |
| M2b | the code-wins rule dropped: the text consulted when a code is carried | `74f1d806f042b714b7d663a1281ae322` | `AWriteErrorWithAnotherCode_...`; `AValueAlreadyThereRefusal_IsDecidedByItsCode_TheTextOnlyWhenItCarriesNone` | FAIL, 5: the 8224 row both ways (a refusal); the three codes outside the set under duplicate text, decided true |
| M3 | the brief's: only a duplicate read back again - the catch filtered by `IsValueAlreadyPresentRefusal`, every other error to the outer catch, as in aud-a2l | `64163e659dd1707467c7f8eac2b2ab83` | the applied-then-failed, failed-without-applying, read-back-fails, other-code and timeout-with-the-room tests; `Audit.AnOnPremAddThatAppliedThenFailed_...` | FAIL, 8: every non-duplicate error partial, "Failed to add the room to the on-prem room list on the AD object: ..." - the applied-then-failed and timeout-with-the-room rows (and both audit surfaces) not successes; the failed-without-applying row two membership reads, not three; the read-back's error absent |
| M4 | the brief's: the room's objectGUID returned on the refused-duplicate path | `7282a59a871a0e34793223e4f2771655` | `AnOnPremAddRefusedAsAValueAlreadyThere_IsSettledByTheReadBack`; `AValueAlreadyThereRefusal_IsKnownByItsCode_InAnyLanguage`; `Audit.AnOnPremAddRefusedAsAValueAlreadyThere_IsNeverRecordedAsWritten` | FAIL, 7: the race with the room there and the four coded refusals report `5a5a5a5a-...0001` written; on both audit surfaces `writtenDirectoryObject` present. PASS: the other-error rows - not the refused path |
| H2 | harness: the stand-in refusal back to the bare 1378 text (no exception, no code) | `196f54042e88d44bf868e26cb62a0719` | the harness test | FAIL, 2: the harness test (`Assert.Same` - no exception in the error record); the 8224 row in its error-record form, now English text with no code. *Demonstration:* the coded refusals in their error-record form PASS - the bare text is still a duplicate - the wrong-reason pass only the harness test stops |
| H3 | harness: `SetAdGroupFailsAfterApplying` throws before the add is applied | `656ecc08e0b238b9ed8deca69e6a73c8` | the harness test; the applied-then-failed test; `Audit.AnOnPremAddThatAppliedThenFailed_...` | FAIL, 4: all of them - not applied, the read-back misses |
| H4 | harness: `MemberReadBackFailure` ignored | `8ccc4ea523e770a9dc42182aad38af0f` | the harness test; `AReadBackThatFailsAfterAWriteError_...` | FAIL, 2: both |

Every restored md5 equal to "before"; the restored tree rebuilt and the 232 Conference Rooms cases green.

**Comments after the round.** The round ran on the fix with aud-a2l's now-wrong comment on the write
removed and none of the new explanatory comments; they were written afterwards and checked by
`scratchpad/a2m-a2n-round/comment_only.ps1` (blank, `//`, `///` and PowerShell `#` lines dropped from both
versions, the code lines identical in order): service `8f395bc14f788447df038eca197d34cf` ->
`e3281e44212efdf6898e631381f1aa68` (code lines 1560 = 1560; comment and blank lines 688 -> 749);
`Models/ConferenceRoomModels.cs` `0856d89ca9b21c41e693b170c9c37b9f` -> `d2f823149de0236f3d46b1982a8ef371` (157 =
157; 50 -> 56); `Services/Jobs/ConferenceRoomBulkProcessor.cs` `a385d344fbe6b5dc63fef3f875f794fb` ->
`609a928950bb150112961f7e9e225cdc` (171 = 171; 88 -> 90); harness `63b19f297430449041690e72eac78911` ->
`5649d2e335f5b9a11f23b9c207b2fe94` (602 = 602; 317 -> 321). Unchanged since the round: the page
(`6607b944fae10efefca43e99674e19b3`), `AdManagementExceptionDoubles.cs`, `ConferenceRoomRoomListPreflightTests.cs`
and `ConferenceRoomDirectoryAuditTests.cs`, whose comments were written with the tests.

## Known gaps

- *Assumption, recorded, not probed* (the Plan's paragraph and its dev probe): which code, if any, the live
  module raises for a value already present. If it raises a code outside the set, the race is still settled
  by the read-back and succeeds, but its detail says "after Set-ADGroup reported an error" rather than "after
  ... refused" - a mislabel, not a wrong outcome. If it raises nothing (the silent success Comms10k measured),
  a raced add is reported written, the write having reported nothing; that is aud-a2l's residual, unchanged.
- *Assumption, the documented API, not probed:* the real `ADException` exposes the server's code as `ErrorCode`
  (and 0 when there is none). Were it named otherwise, every error would carry no code and the text would
  decide, as before this finding.
- LDAP and Win32 codes share one number space in `ErrorCode`; Win32 20 and 68 are unrelated errors that would
  read as duplicates. Only the detail's wording is at stake.
- A localised refusal that carries no code is still told as another write error - the residual of the
  approach's "text only as a secondary signal". The read-back decides the row either way.
- The `HadErrors`-without-a-throw branch of the write is taken like a thrown error, but no test reaches it:
  under `-ErrorAction Stop` no stand-in (and, as far as is known, no real cmdlet) reports an error without
  throwing.
- A success after a write error now names no written object even when that write is what put the room
  there (a timeout after the commit) - by design: nothing tells the two apart, and aud-a2g names only what a
  write is known to have produced.
- The bulk job's row shows the row's message, which does not mention the confirmed membership; the detail is
  in the step (shown on the page) and in the audit.

**Operator-visible changes.**

- A `Set-ADGroup` that reported any error - a timeout, a transport error - but whose membership the read-back
  shows is now a success; it used to be partial. The page's on-prem step shows "Membership confirmed present
  after ..." with the error's text.
- A write error with the room absent, or a read-back that cannot be made, is a failed row whose message now
  also carries the write's error.
- A refusal of a value already there - in any language, when it carries its code - completes the row as
  before (aud-a2l), but is no longer audited as a write: `writtenDirectoryObject` is absent and
  `membershipConfirmedPresent` says why.
- The log line of an unexpected failure around the add names `Set-ADGroup` and the member attribute.
- Nothing else: an add that raises no error and is confirmed is reported, written and audited as before.

**Dev acceptance owed** with aud-a2f, a2i, a2j, a2k, a2l and a2m: the Room Finder on-prem add on a synced
list, confirmed by the read-back; and, with the owner's go for an AD probe, the Plan's probe of the duplicate
refusal's code. Not run here: no directory or Exchange call was made.

**Page:** three lines edited in place (the `AuditFinderAction` call, its signature's last line and its
body); 1758 lines before and after, so no `ClickGateRegistry` entry moved.

## Folded in: two LOW candidates from the codex review of aud-a2k/a2l (2026-10-22)

Both belong to the add's error handling this record already owns (`prompt-a2k-a2l.md`,
`a2k-a2l.result.txt`):

1. **A refused duplicate must not be audited as this operation's AD write.** When `Set-ADGroup`
   is refused as "already there" and the read-back finds the room, the row succeeds and returns
   `roomGuid`, so the page and bulk audit record `writtenDirectoryObject` although this operation's
   write was refused (aud-a2g rule: name only what a write produced). Keep the row successful;
   return no written directory object for a refused write; add a "membership confirmed present"
   detail instead. Test: the race case's audit carries no `writtenDirectoryObject`.
2. **Every write error is reconciled by the read-back, not only the duplicate.** A `Set-ADGroup`
   that applied the change and then raised a timeout or transport error is reported PARTIAL even
   though the membership is present; `GroupManagementService` reconciles all write outcomes by
   read-back. Catch the write error, clear the pipeline, read back on the group's own domain, decide
   the row from the read-back, and keep the original error text in the step and audit detail. A
   read-back that fails or misses stays a failed row carrying the write error. Test: a stand-in
   whose `Set-ADGroup` applies the add and then throws a non-duplicate error is settled as success
   with the error recorded in the detail; one that throws without applying stays a failure.

Mutations at the call site for both: return `roomGuid` on the refused-duplicate path; read back
only duplicate errors again.

Implemented as above, both items in this record's fix: the refused duplicate is no longer audited as a
write (M4 pins it), and every write error is reconciled by the read-back (M3 pins it).

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a76a90b485c4fa8ce`. Independent review pending.

For the reviewer:

- The code set goes beyond the brief's three numbers: 1378 and 8323 (the forms aud-a2l's text list already
  names) and 8205 (the number of the symbolic name the brief gives to 8305) are added, so a coded refusal in
  those forms is not demoted to "another write error" by the code-wins rule. The Plan says why; the
  winerror.h numbers are recalled, not verified here.
- Outside the Conference Rooms service, tests and harness, this fix touches `Models/ConferenceRoomModels.cs`
  (the result's new property), `Components/Pages/ConferenceRooms.razor` and
  `Services/Jobs/ConferenceRoomBulkProcessor.cs` (the two audit surfaces: a success row's audit has no
  `error`, so the detail can reach it only through `extra`), and the shared test double
  `ExchangeAdminWeb.Tests/AdManagementExceptionDoubles.cs` (Comms10k's; additive - its 404 Comms10k cases pass).
  `ValidatedMailbox` is not touched.
- aud-a2l's mutation M5 ("every write error read back, the read-back deciding") is this record's intended
  behaviour; its test `AnyOtherWriteError_StaysAFailure_EvenWithTheRoomThere` is inverted and renamed here.

## Closeout

Fixed on branch, not merged, not pushed: the plan `bd961140` (with aud-a2m's), aud-a2m's fix `8441f609`,
then this fix, tests, comments and record in one commit. A module-scoped behaviour change: the Conference
Rooms module version bump is due at merge (one bump covers a2m and a2n). No shared code changed - the model,
page and processor are Conference Rooms' own, and the harness and the double are test-only - so no base app
bump from this finding.

Gates on this commit: every class that uses the harness (`ConferenceRoom*`, `*WriteIdentityTests`) and
`GroupMemberListingTests`, 282 passed, 0 failed; the `Comms10k*` classes (the double's other user) 404 passed,
0 failed; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean; `git diff --check`
clean; `tools/Test-AsciiOnly.ps1` after `git add` clean.

Gates on the branch tip (aud-a2m and aud-a2n both in), run on the final files this commit holds:

- `dotnet build ExchangeAdminWeb.slnx -c Release` (and `--no-incremental`): 0 errors, 61 warnings - the
  baseline count - none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5146 passed, 0 failed, 3 skipped
  (the known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5149 total,
  5 m 32 s; nothing hung, no Sequence file generated. Over aud-a2l's 5111: aud-a28e/a26d's 6 (merged before
  this branch) and these two records' 32 (aud-a2m's 8, this record's 24).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed.
