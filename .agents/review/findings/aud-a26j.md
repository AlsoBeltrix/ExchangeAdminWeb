# aud-a26j: The attribute editor's Set-ADUser write is not confirmed by reading the attributes back

**Severity**: LOW - no wrong object is written. Since aud-a26g the write's success rests on the
absence of a throw; `PerformSave` does not read the attributes back after `Set-ADUser`, so a write
the directory accepted without applying (or a value a server normalised differently) is recorded as
a success with the proposed values as "new", and the Undo button offers to reverse a change that
did not happen as recorded. Known Failure Class 2; the Conference Rooms twin was fixed in aud-a2p.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a762d0851374626dc`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28l/a26g/a26i agent, 2026-10-22, candidate 6 of its hand-back)

## Evidence

At `f0cccea8`, `Services/ADAttributeEditorService.cs` `PerformSave`: `Set-ADUser` in
try/catch/finally (aud-a26g), then the success record with the proposed values; no `Get-ADUser`
of the written attributes after the write. Compare aud-a2p's `ReadBackSyncedAttributes` /
`UnconfirmedSyncedAttributes`.

## Approach (to be written by the implementing agent before code)

- After the write, re-read the written attributes by objectGUID on the same `-Server` (the write
  already targets the bound object, aud-a2d), compare ordinal to what was sent (trim as the write
  trims; empty equals absent; a one-element collection reads as its element), and on a mismatch or a
  failed read record a failed `ADAttributeEditor_Update` naming the attribute, the value read and the
  value written (no old/new pairs, so no Undo is offered for a change not confirmed). The success
  record's new values are the values read back.
- Reuse the aud-a2p comparison helper if it can be shared without widening visibility carelessly;
  otherwise a sibling. Mutation at the call site: drop the read-back.
- Tests through `AttributeEditorHarness`: a `Set-ADUser` that applies (success, values confirmed);
  one that throws nothing and applies nothing (failed record, no undo); a read-back that fails
  (failed record); a server that normalises a value (the record carries the read value).

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28l/a26g/a26i hand-back
(`8f3aa99d`). One commit `fix(ad-attribute-editor): ... (aud-a26j)`, second of two on this branch.
Behaviour: AD Attribute Editor (module version; the supervisor owns the bump). Evidence re-read at
`762c774c`, `Services/ADAttributeEditorService.cs` PerformSave 1325-1459: the re-read before the write, by DN
(1355-1360), returns the object whose objectGUID is checked against the lookup's (1373-1380); the write
`Set-ADUser -Identity <DN> -Credential -ErrorAction Stop [-Replace] [-Clear]` (1418-1424) has no `-Server`
and runs in try/catch/finally (1432-1454, aud-a26g); the success record with the proposed values follows
(1456). Nothing reads the attributes back.

**Rule.** A save is done when every attribute it wrote reads back as written from the object it wrote (Known
Failure Class 2; aud-a2p's rule for the Conference Rooms twin). No retry: the operator saves again.

1. **The read-back.** After the write's finally, `ConfirmWrite` reads and compares, returning the failure
   message or the changes as read:
   - `Get-ADUser -Identity <objectGUID> -Properties <the written attribute names> -Credential -ErrorAction
     Stop`, with no `-Server`: the write sends none, so the read goes where the write went - the module's
     default for this session; the brief's "on the same -Server". The editor routes no call: its lookup, its
     re-read and its write are all unrouted. By the objectGUID the re-read returned for the DN written, so it
     reads that object and no other. In the shape of aud-a2p's read: a throw, an error record (HadErrors), or
     anything but exactly one object with that objectGUID is a failed read; so is a re-read that carried no
     objectGUID.
   - `UnconfirmedWrites(changes, read)`, internal static and pure: each change's NewValue (null for a clear)
     against the value read, ordinal. Nothing is trimmed, because the write trims nothing (the brief's "trim as
     the write trims"). "" and absent are equal; a one-element collection reads as its element, several are
     joined - aud-a2p's ReadDirectoryString, as a sibling here because ConferenceRoomService's is private and
     that file is not edited. Returns each difference - `<name> (<label>) is '<read>'` or `is not set`, then
     `, not '<written>' as written` or `, not cleared as written` - and the changes with NewValue the value read.
   - Not confirmed: "Set-ADUser on <upn> was not confirmed: read back by objectGUID <guid>, <each>." The read
     failed: "Set-ADUser on <upn> was sent but could not be confirmed: reading <names> back by objectGUID
     <guid> failed: <error>". Each logs a warning and is recorded as `LogAudit(target, [], ..., false,
     <message>)` - a failed ADAttributeEditor_Update with `targetObjectGuid` and no old/new pair, the shape of
     the pre-write failures - so no Undo is offered (CanUndo needs Success and a pair). SaveAsync announces it,
     as it announces every result, as a failure.
   - Confirmed: the success record and the result carry the changes as read. Under the ordinal rule a
     confirmed value is the value sent; taken from the read, the record states what the directory holds rather
     than what was proposed.
   - A server that stores a value in another form - a DN in its canonical case, a Boolean's or an integer's own
     text - is not confirmed, and the failed record names the value read and the value written: loud, never a
     false success. Recorded as a known gap.
2. **The write's finally becomes load-bearing**: the read runs on the same pipeline, so the write must not stay
   queued in front of it, and a record the write left past -ErrorAction Stop must not fail the read's error
   check (psl-2). No code change; its comment, which says nothing runs after the write, is corrected after the
   round.
3. Comments after the mutation round.

Tests, through `AttributeEditorHarness`. LocalAd gains, all off by default: `SetChangesNothing` (Set-ADUser
takes the call and applies nothing), `SetStoresAs` (Set-ADUser stores each -Replace value as this returns
it), `ReadBackFailure` (a Get-ADUser after a Set-ADUser in the same session fails, terminating) and `Reads`
(each Get-ADUser's identity and -Properties). ADAttributeEditorSaveTests: (a) a write that applies - success,
a Get-ADUser by the objectGUID after the Set-ADUser asking for exactly the written attributes, the record's
and the result's new values those the directory holds; (b) one that raises nothing and applies nothing -
failed, the message naming the attribute, the value read and the value written, one Failed
ADAttributeEditor_Update with no old/new pair, the undo service's CanUndo false on it, one failure notice;
(c) a read-back that fails - failed, the read's error named, the write sent, the same record; (d) a server
that normalises the value - failed, the record's error carrying the value read and the value written; (e)
pure - ordinal, "" and absent equal, a clear against "", a one-element collection, a value not set; (f) a
harness self-test of the three knobs under -ErrorAction Stop. The existing happy-path test's exact call list
gains the read-back.

Mutations at the call site (as aud-a28m): M4 the read-back dropped - `ConfirmWrite` not called, the proposed
values recorded as before; extra E3 the write's finally no longer clearing the error stream.

Files: `Services/ADAttributeEditorService.cs`, `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, this record.

## Fix

`Services/ADAttributeEditorService.cs`, PerformSave: after the write's try/catch/finally,
`ConfirmWrite(ps, target, reReadGuid, changes, credential)`. Its failure message is recorded as
`LogAudit(target, [], performedBy, ip, ticket, false, <message>)` and returned as the failed result;
otherwise the success record and the result carry the changes as read. New, beside it:

- `ConfirmWrite` (private): runs the read-back, a throw caught as the read's error. A failed read - "Set-ADUser
  on <upn> was sent but could not be confirmed: reading <names> back by objectGUID <guid> failed: <error>";
  differences - "Set-ADUser on <upn> was not confirmed: read back by objectGUID <guid>, <each>." Each logs a
  warning.
- `ReadBackWrittenAttributes` (private static): `Get-ADUser -Identity <objectGUID> -Properties <the written
  names> -Credential -ErrorAction Stop`, no `-Server`; no objectGUID to read by, an error record, or anything
  but exactly one object with that objectGUID is a failed read.
- `UnconfirmedWrites` (internal static, pure): each change's NewValue against the value read, ordinal, nothing
  trimmed, "" and absent equal; returns the differences - `<name> (<label>) is '<read>'` or `is not set`, then
  `, not '<written>' as written` or `, not cleared as written` - and the changes with NewValue as read.
- `ReadDirectoryString`, `EmptyAsAbsent`, `Unwrapped` (private static): aud-a2p's, as siblings - a one-element
  collection read as its element, several joined by "; ".

The write, its catch and its finally are unchanged.

Harness (`ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`, `LocalAd`), all default off: `SetChangesNothing`
(Set-ADUser records the call and applies nothing), `SetStoresAs` (Set-ADUser stores each -Replace value as
this returns it), `ReadBackFailure` (a Get-ADUser after a Set-ADUser in the same session -
`$global:AttributeWriteSent`, set by the stand-in Set-ADUser - fails terminating) and `Reads` (each
Get-ADUser's identity and -Properties, through `NoteRead`).

Tests (`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, 6 cases):
`AWriteThatApplies_IsConfirmedByReadingItBack_AndRecordedAsRead`,
`AWriteThatRaisesNothingAndAppliesNothing_IsAFailedSave_ThatOffersNoUndo`,
`AReadBackThatFails_IsAFailedSave_ThoughTheWriteWasSent`,
`AServerThatStoresTheValueInAnotherForm_IsNotConfirmed_AndTheRecordCarriesTheValueRead`, the pure
`TheReadBack_IsOrdinal_AnEmptyValueIsAnAbsentOne_AndAOneElementCollectionIsItsElement` and the knob self-test
`TheHarness_SetADUser_CanApplyNothingOrNormalise_AndAReadAfterItCanFail`. The existing
`AnOrdinaryEdit_IsWrittenOnceToTheBoundObject_AndAudited` pins the exact call list, which gains
`Get-ADUser|<objectGUID>|` after the write.

Caller-visible change, by design: a save whose attributes do not read back as written - nothing applied, a
value stored in another form, or a read-back that cannot be made - is a failed save. The operator is shown the
message, administrators get a failure notice, and its `ADAttributeEditor_Update` is Failed with no old/new
pair, so the event log offers no Undo. A confirmed save is recorded, returned and announced as before, its
values those read. One more Get-ADUser for each save that writes. The undo path's save runs the same
read-back, so an unconfirmed reversal is a failed undo, recorded by the undo service as any failed save is.

## Guard proof

No separate pre-fix reproduction: M4 is the save as at `762c774c` - the read-back dropped at its call site,
the proposed values recorded - and it fails the applies-nothing test that names the defect.

Same driver and conditions as aud-a28m (`a26j-mutations.log`), filter
`FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ADAttributeEditorNotificationTests|FullyQualifiedName~ADAttributeEditorUndoRefusalTests|FullyQualifiedName~ADAttributeEditorLookupTests|FullyQualifiedName~ADAttributeEditorPatternTests|FullyQualifiedName~ADAttributeEditorServiceTests|FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~UndoIdentityTests|FullyQualifiedName~EventLogUndoRefusalTests`
(175 tests). Every mutant built with 0 errors; each restore matched its original MD5. After the round, a
rebuild on the restored bytes and the filter: 175/175 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M4 the read-back dropped - `ConfirmWrite` not called, the proposed values recorded as before | `ADAttributeEditorService.cs`, `3842B3F4A1784D880ADB215BBBFF3C27` | `A6841725581DF45645944044802E92EE` | `AWriteThatRaisesNothingAndAppliesNothing_IsAFailedSave_ThatOffersNoUndo` (a success reported with nothing applied), `AReadBackThatFails_IsAFailedSave_ThoughTheWriteWasSent`, `AServerThatStoresTheValueInAnotherForm_IsNotConfirmed_AndTheRecordCarriesTheValueRead`, and `AWriteThatApplies_IsConfirmedByReadingItBack_AndRecordedAsRead` with `AnOrdinaryEdit_IsWrittenOnceToTheBoundObject_AndAudited` (no read-back call) |
| extra E3 the write's finally no longer clearing the error stream | `ADAttributeEditorService.cs`, `3842B3F4A1784D880ADB215BBBFF3C27` | `098AA25F7502BA0D045BF446290DAD80` | `AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForAFailedWrite` only - the record the write left past `-ErrorAction Stop` fails the read-back's error check, so an applied write is reported unconfirmed; the aud-a26g test now pins the finally |

Unchanged through the round: `AttributeEditorHarness.cs` `B81DDFCE7A6B045D93F2A52771E5FFA5`,
`ADAttributeEditorSaveTests.cs` `56833579FD65B29DD5B695A266CA5AD3`. The code comments are written after the
round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline
  count - none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,324 passed / 3 skipped (the three known AD skips) / 0 failed, 5,327 total, 5m8s;
  nothing hung, no Sequence file. aud-a28m's tree: 5,318 / 3 / 0, 5,321 total; the six new cases are this
  record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review verdict
is claimed.

## Known gaps

- *Assumption, not probed* (no directory call is made here): with no `-Server`, the read-back is answered where
  the module's default sends this session, as the write was; one domain controller is not pinned. One that has
  not yet seen the write reports a landed write as not confirmed - a loud failed save with no Undo; saving
  again re-reads the directory first. A false success would need the server read to hold the written values
  already.
- *Assumption, not probed*: the module answers `-Properties <name>` with a single-valued attribute as its value
  through `Properties[..].Value`, as the editor's own reads take it; a one-element collection is taken too.
  Were it otherwise every save that writes would read back unconfirmed - loud, never a false success.
- A value the directory stores in another form - a DN in its canonical case, a Boolean's or an integer's own
  text - is not confirmed, by the ordinal rule: a loud false failure for such an attribute, never a false
  success. An allowlist that offers one should offer its values as the directory returns them.
- An unconfirmed save's record carries no old/new pair, as the brief asks, so it does not hold the value the
  attribute had before the write; its error holds the value read and the value written. Where the directory
  took the write in another form, the pre-write value is in neither this record nor its trace step.
- A write that threw stays the failure it was (aud-a28c) and is not read back.
- Cost: one more Get-ADUser for each save that writes.

## Comments after the round

Written after both rounds, in their own commit with aud-a28m's; the same check (only comment and blank lines
changed in `git diff 73eeb7da -U0`). This record's files: `Services/ADAttributeEditorService.cs`
`3842B3F4A1784D880ADB215BBBFF3C27` -> `4A514533B0E7CC877475CF8EBDA1D458` (why the save is read back, why by the
objectGUID and with no -Server, why the comparison is ordinal and untrimmed, the read-shape assumption; and the
write's finally, whose comment said nothing runs on the pipeline after the write, now says why the read-back
needs it cleared); `ADAttributeEditorSaveTests.cs` `56833579FD65B29DD5B695A266CA5AD3` ->
`778D0113A2426E9BE7E32D023AF9CBFA`, the happy path's call-list comment included. `AttributeEditorHarness.cs` is
unchanged (`B81DDFCE7A6B045D93F2A52771E5FFA5`): its knobs were documented with the fix.
