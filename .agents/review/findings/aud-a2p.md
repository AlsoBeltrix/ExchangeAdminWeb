# aud-a2p: The on-prem City/State/Country write is not confirmed by a read-back, keeps a dead HadErrors branch, and an unused success message claims a write

**Severity**: LOW - no wrong object is written. `SetSyncedAttributesViaAdAsync` (the Room Finder
City/State/Country `Set-ADUser` on a synced room) treats "no error" as done with no read-back, the
aud-a2l shape Known Failure Class 2 forbids; it also keeps an error-without-a-throw (`HadErrors`)
branch that cannot fire under `-ErrorAction Stop` (it fails closed, so the harm is only dead code).
Separately, `AddToRoomListAsync` still builds "Membership was written on-prem ..." for the no-op and
the confirmed-present rows; the caller replaces success messages today, so it is unused, but wrong if
ever shown.
**Status**: Merged to master (`28ffde25`); closed on guard proof; codex a2o-a2p clean (one MEDIUM folded into aud-a2q); dev acceptance owed
**Branch**: `worktree-agent-a88f21a61c9cda81b`
**Commit**: `0bbd302f` (fix, tests, comments, record); plan `8ce77ddf`; merged `28ffde25`
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

## Approach (as implemented)

- `SetSyncedAttributesViaAdAsync` (`Services/ConferenceRoomService.cs`): the room's object is read with
  `ConfirmDirectoryObject`'s DN overload; `server` is `GroupManagementService.ServerFromDn(<its DN>)` (its text in the
  messages; "the default domain controller" when null). `Set-ADUser -Identity <objectGUID> -Replace <attrs> -Credential
  -ErrorAction Stop` gains `-Server <server>` when there is one, and runs in `try { ps.Invoke(); } finally {
  ps.Commands.Clear(); ps.Streams.Error.Clear(); }`; the `if (ps.HadErrors) return (FirstError(ps) ?? ..., null);`
  after it is removed, and a thrown error reaches the method's existing catch, its message and log unchanged. Then
  `ReadBackSyncedAttributes`, a throw caught as the read's error:
  - the read fails: `("Set-ADUser on the AD object <guid> for <room> could not be confirmed: reading l, st, co back from
    <server> failed: <error>", null)`, logged as a warning;
  - attributes not as written: `("Set-ADUser on the AD object <guid> for <room> was not confirmed: read back from
    <server>, <each>.", null)`, each `<attribute> (City|State|Country) is '<read>'` or `is not set`, then `, not
    '<written>' as written`, joined by "; "; logged as a warning;
  - all as written: `(null, objectGuid)`, written (aud-a2g).
  The row's handling is unchanged: an error is the partial "Partial: Building/Capacity/Floor were set in Exchange Online,
  but City/State/Country failed: <error> Re-run this row after fixing the cause.", the step `Set-ADUser
  (City/State/Country)` failed with it, `WrittenDirectoryObject` null, nothing after it run.
- `ReadBackSyncedAttributes` (private static): `Get-ADUser -Identity <objectGUID> -Properties l,st,co [-Server <server>]
  -Credential -ErrorAction Stop`; an error record returns its first error; exactly one object whose objectGUID is the one
  written must answer ("<n> object(s) answered, ObjectGUID <guid or none>" otherwise); then
  `UnconfirmedSyncedAttributes(written, <the object>)`.
- `UnconfirmedSyncedAttributes(IReadOnlyDictionary<string, object> written, PSObject read)` (internal static, pure): for
  each of `l`, `st`, `co` the write's `-Replace` holds, its value's text against the value read (`ReadDirectoryString`:
  the property's value unwrapped - a string as it is; a collection's one element, none as absent, several joined by "; ";
  anything else its text), "" taken as absent on both sides, compared `StringComparison.Ordinal`.
- `AddToRoomListAsync`, the on-prem fallback's success: `result.Message = adWritten is not null ?
  BuildRoomListAddedMessage(list, viaOnPrem: true) : BuildOnPremRoomListNotWrittenMessage(list, confirmedAfterWrite:
  adConfirmed is not null)`. `BuildOnPremRoomListNotWrittenMessage` (internal static, pure): "Already in on-prem room list
  '<list>': nothing was written." and "In on-prem room list '<list>', confirmed by reading its group back after this row's
  add was refused or reported an error: not recorded as written by this row.". `AddToRoomListAsync` and `RoomListTarget`
  are `internal` (the test seam).
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`): `SetAdUserChangesNothing`,
  `SetAdUserReportsErrorWithoutThrowing`, `AttributeReadBackFailure` and `AttributeReadBackAnswersAs`, passed to the
  directory runspace when it opens. The stand-in `Set-ADUser` takes `-Server`, refuses an object `DirectoryObjects` does
  not hold (Write-Error then return), applies `-Replace` to the one it does unless told to change nothing, and, told to,
  then reports an error with its own `-ErrorAction Continue`; `Failures['Set-ADUser']` still throws before anything is
  applied. The stand-in `Get-ADUser` takes `-Server`; a read asking for `-Properties` after a `Set-ADUser` call fails with
  `AttributeReadBackFailure`, or answers with `AttributeReadBackAnswersAs` as its objectGUID. Neither routes by `-Server`
  (the room's object answers on any); the tests pin what is passed.
- Tests: new - `ConferenceRoomWriteIdentityTests.TheCityStateAndCountryWrite_IsConfirmedByReadingItsAttributesBack_FromTheServerItWroteTo`,
  `ACityStateAndCountryWriteThatChangesNothing_IsAFailedStep_NotAWrite`, `ACityStateAndCountryReadBackThatFails_IsAFailedStep_NotAWrite`,
  `ACityStateAndCountryReadBackAnsweredByAnotherObject_IsAFailedStep_NotAWrite`,
  `AnErrorTheCityStateAndCountryWriteReportsWithoutThrowing_IsNotTakenForItsError_TheReadBackDecides` (applied / not),
  `TheReadBack_ComparesWhatWasWritten_Ordinal_AnEmptyWriteAsAnAbsentAttribute` and
  `TheHarness_SetADUser_AppliesItsReplace_AndCanChangeNothing_ReportAnError_OrFailItsReadBack`;
  `ConferenceRoomDirectoryAuditTests.ASetADUserTheReadBackDoesNotShow_IsNotRecordedAsWritten` (page, bulk);
  `ConferenceRoomRoomListPreflightTests.TheOnPremAddsOwnMessage_ClaimsAWriteOnlyWhenItsWriteAddedTheRoom` (already a member,
  confirmed after a refused add, written) and `TheOnPremAddsNotWrittenMessages_NameTheList_AndClaimNoAdd`. 14 cases. No
  existing test changed; the five that write City/State/Country and expect it written now pass through the read-back
  (H1 below shows they rest on it).

## Guard proof

| Test | What it pins |
| --- | --- |
| `TheCityStateAndCountryWrite_IsConfirmedByReadingItsAttributesBack_FromTheServerItWroteTo` | the normal case: success, not partial, the room's objectGUID written, the object holding Dublin/Leinster/Ireland; a `Get-ADUser` after the `Set-ADUser`, by the objectGUID, `-Properties l,st,co`; the write and the read both `-Server contoso.test`, the domain the room's DN names |
| `ACityStateAndCountryWriteThatChangesNothing_IsAFailedStep_NotAWrite` | the finding: `Set-ADUser` raises nothing and changes nothing, an older City there - failed, partial, nothing written, the step `Set-ADUser (City/State/Country)` failed naming the object, the server read and each attribute ("l (City) is 'Cork', not 'Dublin' as written", st and co "is not set"), its text in the row's message, the timezone not run |
| `ACityStateAndCountryReadBackThatFails_IsAFailedStep_NotAWrite` | the write applied, its read-back failing: failed, partial, nothing written, "could not be confirmed", the server and the read's error |
| `ACityStateAndCountryReadBackAnsweredByAnotherObject_IsAFailedStep_NotAWrite` | another object answering the read-back: failed, partial, nothing written, that object named |
| `AnErrorTheCityStateAndCountryWriteReportsWithoutThrowing_IsNotTakenForItsError_TheReadBackDecides` (applied / not) | part 2: an error reported without a throw is not the write's - applied, success and written; not applied, the not-confirmed failure; its text in neither - and the record it left does not fail the read-back (the `finally`'s clear) |
| `TheReadBack_ComparesWhatWasWritten_Ordinal_AnEmptyWriteAsAnAbsentAttribute` | the comparison, pure: as written, confirmed (`c`, `countryCode` not read); a case difference and a trailing space are differences; a written value absent (no property, null) is not confirmed; an unwritten attribute is not compared; an empty write equals an absent attribute and "", not a value; a one-element collection is its element, two values are not one |
| `TheHarness_SetADUser_AppliesItsReplace_AndCanChangeNothing_ReportAnError_OrFailItsReadBack` | the stand-ins, so the tests above cannot pass for the wrong reason: `-Replace` applied; a `Failures` entry throws before applying; told to, changes nothing; told to, applies and reports an error that `-ErrorAction Stop` does not throw (`HadErrors`, one record); the read-back failure only for a `-Properties` read after a `Set-ADUser`; another object answering only that read |
| `ConferenceRoomDirectoryAuditTests.ASetADUserTheReadBackDoesNotShow_IsNotRecordedAsWritten` (page, bulk) | the audit, both surfaces: Failed, no `writtenDirectoryObject` |
| `ConferenceRoomRoomListPreflightTests.TheOnPremAddsOwnMessage_ClaimsAWriteOnlyWhenItsWriteAddedTheRoom` (already a member / confirmed after a refused add / written) | part 3, by a direct call: the no-op's and the confirmed add's messages are the not-written ones, not the add's; the written add's is `BuildRoomListAddedMessage(viaOnPrem: true)` |
| `ConferenceRoomRoomListPreflightTests.TheOnPremAddsNotWrittenMessages_NameTheList_AndClaimNoAdd` | the two messages, pure: different, each naming the list, neither "Added to" nor "Membership was written" |

**Mutations** (`scratchpad/a2p-round/round.ps1`, the a2m/a2n/a2o runner copied, with `rows-a2p.ps1`; log
`round-a2p.log` beside them; 2026-10-22 12:09-12:17). Each row ran `ConferenceRoomWriteIdentityTests`,
`ConferenceRoomDirectoryAuditTests` and `ConferenceRoomRoomListPreflightTests` (143 cases), the test project rebuilt each
time. md5 before = restored: `Services/ConferenceRoomService.cs` `c57d105deba334041abc2809abf362ed` (M rows);
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` `2cea5cbde7bfc0c5a14fa109af513456` (H rows). Each restored file was
touched, the next row rebuilding from it. Test names are in `ConferenceRoomWriteIdentityTests` unless qualified (`Audit.`
is `ConferenceRoomDirectoryAuditTests`, `Preflight.` `ConferenceRoomRoomListPreflightTests`).

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1 | the brief's, at the call site: the read-back dropped - the write's lack of an error decides, as before | `80fa221d911f3f30442880a51e5e875f` | the normal, changes-nothing, read-back-fails and another-object tests; the error-without-a-throw test; `Audit.ASetADUserTheReadBackDoesNotShow_...` | FAIL, 7 of 143: no `Get-ADUser` after the `Set-ADUser`; changes-nothing, read-back-fails, another-object and not-applied each a success; both audit surfaces "Success" |
| M2 | the brief's: the dead `HadErrors` branch restored after the write (the lines as at `e1dc552a`, inside the `try`) | `0226ad28f4e6266e3f6cb570d68dd7b3` | the error-without-a-throw test | FAIL, 2: applied - not a success, the reported error taken for the write's; not applied - its text in the message ("... City/State/Country failed: A non-terminating error ...") |
| M3 | the brief's, at the call site: the add's "was written" message on the on-prem no-op path | `75067c8c84e3fc06844416c3fcb8e7b1` | `Preflight.TheOnPremAddsOwnMessage_...` (already a member) | FAIL, 1: "Added to on-prem room list 'Building 1 Conference Rooms'. Membership was written on-prem ..." |
| M3b | at the call site: the same on the confirmed-present path | `5a89fdb673f7d97da9d1983af1202227` | `Preflight.TheOnPremAddsOwnMessage_...` (confirmed after a refused add) | FAIL, 1: the add's message |
| M4 | the write's `finally` no longer clears the error stream (the commands still cleared) | `5ed145118bde8d7e4849f5890b1f872d` | the error-without-a-throw test | FAIL, 2: the record the write left fails the read-back's error check ("... reading l, st, co back from contoso.test failed: A non-terminating error ..."): applied a failed row; not applied its text in the message. psl-2 in this path |
| M5 | the read-back without the write's `-Server` | `92fb054cc7fb3793076b4bfbddd19ec6` | the normal test | FAIL, 1: the read-back's `Server` null, not `contoso.test` |
| M6 | the write without its `-Server` (as at `e1dc552a`) | `2f76ccddbec35aafb02a51a5c5a1eb60` | the normal test | FAIL, 1: the write's `Server` null |
| M7 | the read-back's objectGUID check dropped | `f5b331a8874b47676207af95a9ce7e64` | the another-object test | FAIL, 1: a success - another object's attributes taken for the room's |
| M8 | the comparison `OrdinalIgnoreCase` | `641ff15f41ce2135b4c498b80fa5e308` | the comparison test | FAIL, 1: the case difference not reported |
| M9 | an empty write no longer equal to an absent attribute | `99697dc65c5c98a7f732032ba838bb0f` | the comparison test | FAIL, 1: "l (City) is not set, not '' as written" |
| M10 | a collection read no longer taken as its element | `771a04f75d391e649164280304876938` | the comparison test | FAIL, 1: "co (Country) is 'System.Collections.ArrayList', ..." |
| H1 | harness: the stand-in `Set-ADUser` no longer applies its `-Replace` | `59eee6447249778e8b4ee58b25e7e866` | every test whose write must apply | FAIL, 11: the normal, read-back-fails, error-without-a-throw (applied) and harness tests, and the existing tests that write City and expect it written - `CityStateAndCountry_AreWrittenToTheProvenDirectoryObject_NotAUserPrincipalNameMatch`, `Audit.CityWritten_...` and `Audit.AWriteThatSucceeded_...` (page, bulk), `Preflight.AProvableRow_ForASyncedRoomList_...` and `Preflight.TheRoomList_IsReadOnce_...` - each now "... was not confirmed: read back from contoso.test, l (City) is not set ..." |
| H2 | harness: `SetAdUserReportsErrorWithoutThrowing` ignored (the stand-in reports nothing) | `9f184d753ecf0a8a47fc35706acb5180` | the harness test | FAIL, 1: `HadErrors` false. *Demonstration:* the error-without-a-throw test PASSES both ways - with no error reported it pins nothing - the wrong-reason pass only the harness test stops |
| H3 | harness: `SetAdUserChangesNothing` ignored | `8a6f269d39b432fabf14840d2244cb49` | the changes-nothing tests | FAIL, 5: the changes-nothing test, `Audit.ASetADUserTheReadBackDoesNotShow_...` (page, bulk), the error-without-a-throw test (not applied), the harness test |
| H4 | harness: `AttributeReadBackFailure` ignored | `37fa7e23ea972563cf5f08798526a947` | the read-back-fails test | FAIL, 2: it and the harness test |
| H5 | harness: `AttributeReadBackAnswersAs` ignored | `a4cb7c4bba4c3d18821910e46ac463cd` | the another-object test | FAIL, 2: it and the harness test |
| R | the revert (`scratchpad/a2p-round/revert-row.ps1`, log `revert-a2p.log`, 12:25-12:27): the service as at `e1dc552a`; so the tests compile against it, the three test methods naming members this fix adds removed - the comparison test from `ConferenceRoomWriteIdentityTests.cs` (`c2cf5c873fcd7686d0926a97116c67be` -> `f32f6a2d8c369584b24697be6d26b1e2`) and both message tests from `ConferenceRoomRoomListPreflightTests.cs` (-> `1a7675ac98e7fc5331f38d61483fdf59`, the file as at `e1dc552a`); the harness as fixed | `90b7e62d6c261c1b5637337911a1bf97` (= `e1dc552a`) | every behavioural test this record adds | FAIL, 8 of 138: no read-back in the normal test; changes-nothing, read-back-fails and another-object successes, and on both audit surfaces; the error-without-a-throw test both ways, the reported error taken for the write's. Restored: the service `f6f542c2997d3bd4caffd20825294a6a`, the test files `c2cf5c873fcd7686d0926a97116c67be` and `4b2d55f2e38f2d5233e24582d9c4fc59`, each = before, touched; rebuilt, 281 green |

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 281 `ConferenceRoom*` and
`*WriteIdentityTests` cases green.

**Comments after the round.** The round ran on the fix with none of its new explanatory comments (the method header's
"once Set-ADUser has succeeded" still standing); they were written afterwards and checked by
`scratchpad/a2p-round/comment_only.ps1` (the a2m/a2n checker: blank, `//`, `///` and PowerShell `#` lines dropped from both
versions, the code lines identical in order): service `c57d105deba334041abc2809abf362ed` ->
`f6f542c2997d3bd4caffd20825294a6a` (code lines 1655 = 1655; comment and blank lines 770 -> 822); harness
`2cea5cbde7bfc0c5a14fa109af513456` -> `440fed65e8c1a719a48389be1ef62072` (621 = 621; 334 -> 365). Unchanged since the
round: the three test files, whose comments were written with the tests (`ConferenceRoomWriteIdentityTests.cs`
`c2cf5c873fcd7686d0926a97116c67be`, `ConferenceRoomDirectoryAuditTests.cs` `6e4fd2d74d3f887d939c9f0b014a7bb7`,
`ConferenceRoomRoomListPreflightTests.cs` `4b2d55f2e38f2d5233e24582d9c4fc59`), and the page.

## Known gaps

- *Assumption, aud-a2l's, carried, not probed:* the read-back, sent to the room's domain by its DNS name as the write was,
  is answered by a domain controller that has the write; one DC is not pinned. One the write has not reached reports a
  landed write as not confirmed - partial and loud; the re-run writes the same values and is confirmed. A false success
  would need the server read to hold the written values already, the directory then holding them.
- *Assumption, not probed:* the module answers `-Properties l,st,co` with each single-valued attribute as its value
  through `Properties[..].Value`, as `ADAttributeEditorService` reads its attributes (a one-element collection is taken
  too). Were it otherwise, every live City/State/Country write would read back "not set" - a loud partial on every such
  row, never a false success. The dev acceptance below shows it at once.
- *Assumption, not probed:* one `-Replace` is one LDAP modify, applied whole, so `co` read back as written vouches for `c`
  and `countryCode`, which are not read (the brief's three).
- A write that threw stays the failure it was even when its values are in place (a timeout after the commit): partial, as
  before, and the re-run is confirmed. Reading back after a thrown error, aud-a2n's shape for the membership, would need
  a "confirmed, not written" detail on the result and both audit surfaces - not in this record.
- *Documented AD module default, not probed:* the read before the write (no `-Server`) finds the object only in the
  module's default domain, which its DN names, so the write's new `-Server` does not move its domain. Were the DN to name
  another, the write would be refused there - by objectGUID, a loud failure, never another object.
- Unrouted as before: the preflight's object read and the write's own confirming read (no `-Server`). A DN naming no
  domain leaves the write and the read-back unrouted too, and the messages name "the default domain controller".
- Cost: one more `Get-ADUser` for each row that writes City/State/Country.
- The list add's messages are pinned by a direct call through the `internal` seam; nothing shows a success's today.
- The cloud no-op's "Added to room list '<list>'." claims an add the no-op did not make - the same class on the cloud
  path, outside this record: reported, not changed.

**Operator-visible changes.**

- A City/State/Country write that raised no error and changed nothing - or left another value - is a failed (partial) row,
  its step naming each attribute not as written, what it reads, what was written and the server read; it used to be
  reported done and audited with `writtenDirectoryObject`.
- A read-back that cannot be made, or that another object answers, is a failed (partial) row, "could not be confirmed".
- The write is sent with `-Server <the room's domain>`, the domain it already reached; two warning log lines for the
  failures above.
- Nothing else: a confirmed write reads, writes and audits as before; the room-list add's own messages are not shown.

**Dev acceptance owed** with aud-a2f, a2i, a2j, a2k, a2l, a2m, a2n and a2o: a Room Finder row with City/State/Country on a
synced room - expected a success, its audit row naming the room's objectGUID, no "not confirmed" warning in the log
(the read-shape assumption above). Not run here: no directory or Exchange call was made.

**Page:** not edited; 1758 lines; md5 `6607b944fae10efefca43e99674e19b3` before and after; no `ClickGateRegistry` entry
moved.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on `worktree-agent-a88f21a61c9cda81b`.
Independent review pending.

For the reviewer:

- "-Server <the same DC>" (the brief) is read as the write's own `-Server`, which the write gains here - it had none -
  the room's DN's domain; not one pinned DC, aud-a2l's assumption carried (Plan 1).
- try/finally, not aud-a2o's try/catch/finally (Plan 3): no read-back follows a thrown write error, so a catch would hold
  nothing; the thrown error reaches the method's existing catch exactly as before.
- The new read keeps the error-record check the file's other reads have (failing closed, for a read); the write's was
  the dead one. M4 shows the `finally`'s clear is what keeps it from failing a clean write's read-back.
- `AddToRoomListAsync` and `RoomListTarget` are `internal`: a test seam, no behaviour change.
- Outside the service and its tests this fix touches only the harness: test-only and additive - four knobs, default off;
  `Get-ADUser` and `Set-ADUser` take `-Server`; `Set-ADUser` applies its `-Replace` and refuses an object it does not hold.

## Closeout

Fixed on branch, not merged, not pushed: the plan `8ce77ddf`, then this fix, tests, comments and record in one commit. A
module-scoped behaviour change (the City/State/Country write confirmed by its read-back, routed to the room's domain, its
new failure messages): the Conference Rooms module version bump is due at merge - not made here, `Modules/ModuleCatalog.cs`
being outside this brief. No shared code changed (`GroupManagementService.ServerFromDn` reused as it is; the harness
test-only), so no base app bump.

Gates, run on the final files this commit holds (the revert row ran after the suite and restored every file
byte-identical, md5s above; the record is not built):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5214 passed, 0 failed, 3 skipped (the known AD
  skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5217 total, 5 m 6 s; nothing hung, no Sequence
  file. This record's new cases are 14; the 316 `ClickGate*` cases are among those passed (and, run alone on the restored
  build, 316 passed).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the harness's stand-ins are PowerShell inside a C#
string, run by the tests).
