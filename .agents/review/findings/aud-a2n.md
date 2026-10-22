# aud-a2n: The value-already-there detection matches English error text, and the add's failure log still names the old cmdlet

**Severity**: LOW - on a non-English domain controller or module locale the aud-a2l
"value already exists" refusal is not recognised, so the race falls back to the old PARTIAL result
after Exchange metadata committed (no wrong write; a re-run says "already a member"). The outer
catch's log line "Add-ADGroupMember on-prem room-list write failed" names a cmdlet the code has
not used since aud-a2j, which misleads whoever reads the log.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.

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
