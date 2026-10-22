# aud-a2d: The Exchange write re-resolves the operator's string instead of writing the mailbox the check validated

**Severity**: MEDIUM - after aud-a2b and aud-a2c the check names the right object, but every write
hands Exchange the typed string again, so check and write can still name different objects.
**Status**: Merged to master; codex review 2026-10-21 found no remaining typed-string target; two Conference Rooms follow-ons admitted as aud-a2f and aud-a2g (fix in progress); dev acceptance check owed (grant/revoke FullAccess and Send As, calendar set/remove, OOF set/disable, Room Finder with and without City/State/Country, room type)
**Branch**: `worktree-agent-ab74b77ad109ec5f3`
**Commit**: `fb449b5e` (plumbing + Mailbox), `6241bb20` (Calendar), `73787287` (Out of Office), `404c856a` (Conference Rooms); records `3dcb15e4`, `32a09ace`, `7ae55b9a` (merged)
**Reviewer**: implementer (gap recorded in aud-a2b; admitted by the supervisor 2026-10-21)

## Evidence

Recorded as the open half of aud-a2b ("What 'the object written' still means", Known gaps) and
handed on by aud-a2c ("What aud-a2d can rely on"). At `3562cf08`:

The check resolves and proves an object, then returns only a verdict -
`Services/PermissionValidator.cs:169-174`:

```
169    public sealed record TargetValidation(string? Error, string? ServicedNote)
171        public static readonly TargetValidation Ok = new(null, null);
```

and the room gate hands its write only the servicer note -
`Services/ConferenceRoomProtectionGate.cs:94-99`:

```
94        Func<string?, Task<TResult>> onAllowed)
99        return await onAllowed(outcome.ServicedAuditDetail);
```

Every write then passes the operator's string, which Exchange resolves again by its own rules -
`Services/MailboxPermissionService.cs:51-52` (and :65-66, :100-101, :113-114, :384-401, :447-464),
`Services/CalendarPermissionService.cs:30-37` (the folder identity is built from the typed string's
primary address), `Services/OutOfOfficeService.cs:60-61`, `Services/ConferenceRoomService.cs:408-409`
(and every Set-* in `SetRoomTypeAsync`, :678-838):

```
51                    ps.AddCommand("Add-MailboxPermission")
52                      .AddParameter("Identity", targetMailbox)
```

The Conference Rooms Active Directory writes do the same through a different door: City, State,
Country and the on-premises room-list membership are written by objectGUID, but the objectGUID is
found by matching the typed string against `userPrincipalName` (`:1463-1468`), not taken from the
object the gate proved owns the mailbox.

## Predicted observable failure

The check validates mailbox A; between the check and the write - or through any difference in how
the two resolve a string - Exchange resolves the same string to mailbox B and the change lands on
B, which nothing checked. The audit row records an allowed change to A. Narrower than aud-a2b/a2c
(those were stable bypasses; this needs the directory to change, or a resolution rule the check
does not share), but it is the last place the typed string decides what is written.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21).

**Rule.** The identity the check validated flows to the write. No Exchange write in the four
modules passes the operator's string as the target; a write with no validated identity is refused
and audited, and never falls back to the string.

**Shared plumbing.**

- New `Services/ValidatedMailbox.cs`: `ExchangeGuid` (the value aud-a2c proved for a synced
  mailbox, Exchange's own for cloud-only) and `DirectoryObjectGuid` (the AD objectGUID aud-a2c
  proved owns it; null when no on-premises object was proven). Built only from a check's result
  (`From(ResolvedDirectoryPrincipal)`, `From(ResolvedRecipient)`), never from a string; all-zero
  and absent GUIDs give null. `Identity` is the ExchangeGuid in "D" form - the `-Identity` every
  Exchange write passes. Audit helper adds `writtenIdentity` (and `writtenDirectoryObject`) to
  `extra`; the row's `target` stays the operator's typed string.
- `PermissionValidator.TargetValidation` gains `Mailbox`; an allow cannot be built without one.
  Central config: the mailbox is `ResolveExchangeTargetAsync`'s principal. No central resolution:
  the Exchange recipient the legacy list checked, or one Exchange lookup when the list did not
  look (nothing configured, or matched by string). No mailbox (no recipient, no mailbox GUID,
  Exchange not answering) DENIES, audited by every caller's existing denial path. Central and
  legacy lookups naming different mailboxes DENIES. Protected verdicts still take precedence.
- `ExchangeServiceBase`: `BindValidatedMailbox` reads the mailbox back by its GUID and refuses
  unless Exchange returns exactly that ExchangeGuid (the read most writes already made, now
  checking what it gets); `GetMailboxLocationAsync(ValidatedMailbox?)` (null is "Unknown");
  `ConnectOnPrem` and `GetModuleCredentialsAsync` become `virtual` test seams (no behaviour
  change) so the on-prem writes can be driven in-process.
- Every service write method takes `ValidatedMailbox?` and refuses null before borrowing a
  connection or asking for a credential: no cmdlet runs, the caller audits the failed result.

**Write call sites, before -> after** (line numbers at `3562cf08`):

| # | Call site | Before | After |
| --- | --- | --- | --- |
| M1 | `MailboxPermissionService.AddMailboxPermissionsAsync` read `Get-Mailbox` (:37) | typed | GUID, must return that ExchangeGuid |
| M2 | `Add-MailboxPermission -Identity` (:51-52) | typed | GUID |
| M3 | `Add-RecipientPermission -Identity` (:65-66) | typed | GUID |
| M4 | `RemoveMailboxPermissionsAsync` read `Get-Mailbox` (:86) | typed | GUID, checked |
| M5 | `Remove-MailboxPermission -Identity` (:100-101) | typed | GUID |
| M6 | `Remove-RecipientPermission -Identity` (:113-114) | typed | GUID |
| M7 | on-prem `Add-MailboxPermission -Identity $Identity` (:384-388) | typed | GUID |
| M8 | on-prem `Add-ADPermission -Identity $Identity` (:397-401) | typed | `Get-Mailbox -Identity <GUID> \| Add-ADPermission` (Add-ADPermission documents only DN or name for `-Identity`; the piped mailbox object binds it) |
| M9 | on-prem `Remove-MailboxPermission` (:447-451) | typed | GUID |
| M10 | on-prem `Remove-ADPermission` (:460-464) | typed | piped from `Get-Mailbox -Identity <GUID>` |
| M11 | `ProcessMailboxPermissionsCsvAsync` -> M1-M6 (:238-286) | `row.Target` | that row's validated mailbox |
| M12 | page `SubmitSingle` location read (:461) | typed | GUID |
| M13 | page `SubmitSingle` -> M1-M6 (:487-489) | typed | the validation's mailbox |
| M14 | page `ExecuteOnPrem` -> M7-M10 (:707-709) | typed | the re-validation's mailbox |
| C1 | `CalendarPermissionService.SetCalendarPermissionAsync` read `Get-Mailbox` (:30) | typed | GUID, checked |
| C2 | `Get-MailboxFolderStatistics` (:33, base :596) | typed's primary SMTP | GUID |
| C3 | `Set-MailboxFolderPermission -Identity` (:36-37) | `<primary of typed>:\<folder>` | `<GUID>:\<folder>` |
| C4 | `Add-MailboxFolderPermission` fallback (:49-50) | same | same |
| C5 | `RemoveCalendarPermissionAsync` read, folder lookup, `Remove-MailboxFolderPermission` (:68-75) | same | same |
| C6 | on-prem `Set-/Add-MailboxFolderPermission "$Identity:\Calendar"` (:269-279) | typed | GUID |
| C7 | on-prem `Remove-MailboxFolderPermission` (:317-323) | typed | GUID |
| C8 | `ProcessCalendarPermissionsCsvAsync` -> C1-C5 (:119-169) | `row.Target` | row's mailbox |
| C9 | page `SubmitSingle` location read and C1-C5 (:431, :462, :468); `ExecuteOnPrem` -> C6-C7 (:661, :663) | typed | the (re-)validation's mailbox |
| O1 | `OutOfOfficeService.SetOutOfOfficeAsync` `Set-MailboxAutoReplyConfiguration -Identity` (:60-61) | `request.Mailbox` | GUID, as a second parameter; the `OutOfOfficeRequest` record is unchanged |
| O2 | page `SetOof` previous-state read (:381) | typed | GUID |
| O3 | page `SetOof` -> O1 (:388) | request only | request and the validation's mailbox |
| R1 | `ConferenceRoomProtectionGate.GuardThenRunAsync` `onAllowed` (:90-100) | servicer note | note and the mailbox; NotFound, and a principal with no ExchangeGuid, now DENY |
| R2 | `SetRoomMetadataAndListAsync` room read `Get-Mailbox`/`Get-Place`/`Get-MailboxRegionalConfiguration` (:346-353, :72-123) | typed | GUID, must return that ExchangeGuid |
| R3 | AD preflight, `Get-ADUser -Filter "UserPrincipalName -eq '<typed>'"` (:1310, :1463) | typed | `Get-ADUser -Identity <proven objectGUID>`; refused when none was proven |
| R4 | `Set-Place -Identity` (:408-409) | typed | GUID |
| R5 | `Set-ADUser -Identity` (:1355-1366) | objectGUID matched from the typed UPN | proven objectGUID |
| R6 | `Set-MailboxRegionalConfiguration`, `Set-MailboxCalendarConfiguration` (:453-463) | typed | GUID |
| R7 | `AddToRoomListAsync` `Add-DistributionGroupMember -Member` and its already-member test (:544-557) | typed | GUID; already-member compared with the primary address Exchange returned for the GUID |
| R8 | AD fallback `Add-ADGroupMember -Members` (:1418-1445) | objectGUID matched from the typed UPN | proven objectGUID |
| R9 | `SetRoomTypeAsync`: `Get-Mailbox` (:678), `Get-MailboxFolderStatistics` (:688), `Get-/Remove-MailboxFolderPermission` (:722-736), `Set-CalendarProcessing` (:751), `Set-Mailbox` (:779), `Add-/Set-MailboxFolderPermission` (:1203-1214), `Set-MailboxRegionalConfiguration`/`Set-MailboxCalendarConfiguration` (:828-838) | typed, then its primary SMTP | GUID; folder `<GUID>:\<folder>`; the read must return that ExchangeGuid |
| R10 | `ConferenceRoomBulkProcessor` `onAllowed` -> R2-R9 (:116-200) | `row.Email` | the gate's mailbox |
| R11 | page `SetupSingleRoom` old-values read and R2-R8 (:1059, :1072) | typed | the gate's mailbox |
| R12 | page `SetSingleRoomType` -> R9 (:1418) | typed | the gate's mailbox |

**Deliberately left on the typed string:**

- `-User` / `-Trustee` and `ValidateRecipient(user)`: the principal being GIVEN access, not the
  object written; it is not a protected-principal target. `ValidateSelfGrantAsync` compares typed
  strings and is a different check.
- Read-only lookups that no write follows: Out of Office `CheckStatus`; Conference Rooms CSV
  previews (`GetRoomInfoAsync(row.Email)`, `ResolveRoomListAsync`) - the apply goes through the
  bulk job and the gate.
- Values that are not the target: folder-permission `-User` groups and the arbiter (module config),
  the room-list group (`Get-/New-DistributionGroup`, `Add-DistributionGroupMember -Identity <list>`,
  `Get-ADGroup` by list name) - named from the building, not from the operator's target.
- `Set-RemoteMailbox` is not called (decommissioned stub, `ConferenceRoomService.cs:1243`).

**Tests.** In-process PowerShell harness (real `ExoConnectionPool`, connect seam, stand-in
functions recording bound parameters), typed string deliberately different from the GUID - and,
where it helps, resolving to a different mailbox: each write cmdlet above receives the GUID; a
null mailbox is refused with no cmdlet run and no connection borrowed; a read-back returning a
different ExchangeGuid refuses before any write; bulk rows write their own row's mailbox and a
row with none is denied and audited; on-prem paths driven through the two seams; the validator's
and the gate's no-mailbox denials; the Out of Office capture harness asserts the GUID written is
the captured mailbox's, whatever the box says. Audit rows keep `target` = typed and carry
`writtenIdentity`.

**Mutations, at the call sites.** Pass the typed string again at each write's `-Identity` (and the
on-prem argument lists, the folder identity, the AD objectGUID); drop each service's null refusal
(fall back to the string); drop the validator's and the gate's no-mailbox denial; drop the
read-back GUID comparison. A named test fails for each; files restored byte-identical, touched,
rebuilt green.

**Files.** `Services/ValidatedMailbox.cs` (new), `Services/PermissionValidator.cs`,
`Services/ExchangeServiceBase.cs`, `Services/ExchangeIdentityResolver.cs` (GUID reader shared),
`Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs`,
`Services/OutOfOfficeService.cs`, `Services/ConferenceRoomService.cs`,
`Services/ConferenceRoomProtectionGate.cs`, `Services/Jobs/IConferenceRoomBulkOperations.cs`,
`Services/Jobs/ConferenceRoomBulkProcessor.cs`, the four pages, their tests, and this record.
`ClickGateRegistry` re-anchored by the playbook if a line-keyed page changes length.

**Commits.** Shared plumbing + Mailbox Permissions; Calendar Permissions; Out of Office;
Conference Rooms. Gates before each.

## Approach (as implemented)

### Shared plumbing

- `Services/ValidatedMailbox.cs` as planned. `WithWrittenIdentity` records `writtenIdentity`
  always and `writtenDirectoryObject` only when the caller says its operation wrote Active
  Directory by that object (Conference Rooms) - an Exchange-only write records only what it wrote.
  (Correction: every Room Finder caller said so, whether or not Active Directory was written; since
  aud-a2g the key records only an object the service reports written.)
- `PermissionValidator`: `TargetValidation` is a record with a private constructor and three
  factories; `Allow` and `Serviced` require a mailbox, `Deny` carries none. Every allowing exit -
  the central check serviced, the typed-literal check serviced, the legacy list serviced, and the
  plain allow - goes through one `BindWriteTargetAsync`: central check ran, so the mailbox is its
  resolved principal's (and if the legacy list also asked Exchange and got a different mailbox,
  refused); no central check, so the recipient the legacy list put to Exchange, or one lookup when
  it did not ask; nothing usable, refused (`NoMailboxMessage`; Exchange not answering, "could not
  be identified"). `CheckExclusionAsync` now returns the recipient it looked up; the lookup itself
  is one shared `LookUpRecipientAsync`. Protected and failed verdicts return before the binding, so
  they keep their messages and warnings.
- `ExchangeServiceBase.BindValidatedMailbox`: `Get-Mailbox -Identity <GUID>`, exactly one result,
  its `ExchangeGuid` (parsed by the same `ExchangeIdentityResolver.ReadExchangeGuid`, now
  `internal`) equal to the validated one, else it throws before any write. Returns the primary
  address for messages. `ValidateMailbox(string)` - the read by the typed string - is removed with
  its last caller (Calendar commit). `GetMailboxLocationAsync(ValidatedMailbox?)`; null is "Unknown".
  `ConnectOnPrem` and `GetModuleCredentialsAsync` are `virtual`, no behaviour change.

### Mailbox Permissions

- Cloud grant/revoke: null refused before a connection is borrowed; read-back by GUID; the four
  cmdlets take `mailbox.Identity`. `targetMailbox` remains for the result and the OWA link only.
- On-prem: null refused before a credential is requested. `Add-/Remove-MailboxPermission` take the
  GUID; Send As is `Get-Mailbox -Identity <GUID> -ErrorAction Stop | Add-ADPermission ...` (and
  `Remove-ADPermission`), one pipeline, the same shape `OnPremRecipientExists` already sends through
  the restricted endpoint. `Add-ADPermission` documents `-Identity` as a DN or unique name only.
- CSV: each row hands its own `targetValidation.Mailbox`; the row audit adds `writtenIdentity`.
- Page: the location read, the single grant/revoke and the on-prem pair all take their own
  validation's mailbox; both audit sites add `writtenIdentity`. Line count unchanged (808), so no
  ClickGate entry moved.

### Calendar Permissions

- Cloud set/remove: null refused before a connection is borrowed; read-back by GUID; the folder
  identity is `GetCalendarFolderName(ps, <GUID>)` - the folder NAME is read from the validated
  mailbox too, so a localized "Kalender" can only come from the mailbox being written.
  `Set-MailboxFolderPermission`, its `Add-` fallback and `Remove-MailboxFolderPermission` take
  `<GUID>:\<folder>`. The result message names the mailbox's primary address (from the read-back)
  and the identity written; it used to show `<primary of the typed string>:\Calendar`.
- On-prem set/remove: null refused before a credential; the remote script's `$Identity` is the
  GUID, so it writes `<GUID>:\Calendar`.
- CSV and page as for Mailbox Permissions. `ValidateMailbox(string)` removed - it had no caller
  left. Line count unchanged (762).

### Out of Office

- `SetOutOfOfficeAsync(OutOfOfficeRequest request, ValidatedMailbox? mailbox)`: the aud-a7b request
  record is unchanged and still the only source of what is written; the mailbox is a second
  parameter because it exists only after the check, while the request is captured before any
  await. `Set-MailboxAutoReplyConfiguration -Identity` takes the GUID; null refuses before a
  connection. The message still names the address as typed.
- `GetOutOfOfficeAsync(ValidatedMailbox?)`: the page's previous-state read for the audit is of the
  mailbox about to be written. The status check (`CheckStatus`) stays on the typed string - it is
  a read-only lookup no write follows.
- Page: `SetOof` passes `protectedValidation.Mailbox` to both; the audit `extra` gains
  `writtenIdentity` beside `previousState`/`newState`. The page is not ClickGate-registered.

### Conference Rooms

- Gate: `ProtectionOutcome` carries `Room`; both allow factories require one. The room is bound
  after the protection verdict (`ValidatedMailbox.From(resolved)`), so a protected room keeps its
  refusal; an ordinary or serviced allow with no mailbox GUID - NotFound included - is refused
  ("cannot be bound"). `onAllowed` is now `(servicedNote, room)` (and `room` on the no-user
  overload). NotFound used to be an allow; the test that pinned that is rewritten to the opposite.
- `GetRoomInfoAsync(ValidatedMailbox)`: `ReadValidatedMailbox` (the read-back, factored out of
  `BindValidatedMailbox` in `ExchangeServiceBase`) then `Get-Place` / `Get-MailboxRegionalConfiguration`
  by the GUID. The string overload stays for previews and is unchanged in behaviour.
- `SetRoomMetadataAndListAsync` and `SetRoomTypeAsync` take the room: null refused before any read;
  every Exchange write takes the GUID; the Type calendar folder is `<GUID>:\<folder>`.
  `AddToRoomListAsync` (now private) adds `-Member <GUID>` and compares the list's members with the
  primary address Exchange returned for the GUID (it compared the typed string).
- Active Directory steps: City/State/Country (`Set-ADUser`) and the on-prem room-list fallback
  (`Add-ADGroupMember -Members`) write the objectGUID the gate PROVED owns the mailbox
  (`ValidatedMailbox.DirectoryObjectGuid`). `ResolveAdObjectGuid` - a `UserPrincipalName -eq
  '<typed>'` search - is replaced by `ConfirmDirectoryObject` (`Get-ADUser -Identity <objectGUID>`,
  exactly that object or refuse). A cloud-only room has no proven object: a row with
  City/State/Country, or one needing the on-prem list fallback, is refused before any write and
  before a credential is fetched. (Correction: true of City/State/Country only - the list-fallback
  refusal came after Set-Place; see aud-a2f.) The removed comment's safety argument ("userPrincipalName ==
  email in this environment") rested on this environment's shape (Architectural Invariant 7).
  `OpenDirectoryRunspace` is a new `virtual` test seam, no behaviour change.
- Bulk processor: each row's operation is handed the gate's room for that row; its audit adds
  `writtenIdentity` (and `writtenDirectoryObject` on Finder rows, which write AD by it).
- Page: both handlers' `onAllowed` pass the room to the write and to the old-values read; both
  audit helpers add the identity. Line count unchanged (1758); no ClickGate entry moved.

## Guard proof

| Test | What it pins |
| --- | --- |
| `MailboxPermissionWriteIdentityTests.AGrant_WritesTheValidatedMailbox_NotTheStringTyped` | the read-back and both grant cmdlets take the GUID; the typed alias, which Exchange resolves to a different (protected) mailbox, reaches no cmdlet |
| `ARevoke_WritesTheValidatedMailbox_NotTheStringTyped` | the same for both revoke cmdlets |
| `WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed` (grant, revoke) | null refuses with `ValidatedMailbox.Refusal`, no cmdlet runs, no connection opened |
| `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` | Exchange answering the GUID with another mailbox stops the write |
| `AnOnPremGrant_SendsTheValidatedMailbox_AndBindsSendAsToTheMailboxItNames`, `AnOnPremRevoke_...` | both remote scripts get the GUID; Send As binds to the object `Get-Mailbox` returned for it |
| `WithoutAValidatedMailbox_TheOnPremPathAsksForNoCredential_AndRunsNothing` (grant, revoke) | the on-prem refusal comes before the credential |
| `TheBulkLoop_WritesEachRowsOwnValidatedMailbox_AndRefusesARowWithNone` | a CSV row writes its own mailbox and records `writtenIdentity` with `target` typed; a row with no mailbox is denied and audited as `BulkAdd_ExcludedTarget` |
| `PermissionValidatorTests.TheAllowCarriesTheMailboxTheCheckResolved_NotTheStringTyped` | the allow carries the resolved principal's GUID and proven objectGUID |
| `AResolvedTargetWithNoMailbox_IsRefused_RatherThanWrittenByItsName` | the no-recipient branch is denied, not allowed |
| `AProtectedTargetWithNoMailbox_IsStillRefusedAsProtected` | binding runs after the protection verdict |
| `AServicedAllow_CarriesTheMailbox_AndWithoutOneIsRefused` (2) | the serviced allow is bound too |
| `NothingConfigured_TheTargetIsStillBoundToTheMailboxExchangeNames`, `NothingConfigured_ATargetWithNoMailbox_IsRefused` (2), `NothingConfigured_ExchangeCannotBeAsked_IsRefused` | no protection configured still binds through one Exchange lookup, and refuses without a mailbox or an answer |
| `LegacyList_TheAllowCarriesTheMailboxTheListChecked_AskingExchangeOnce` | the legacy list's own recipient is the identity; Exchange is not asked twice |
| `CentralAndLegacyLookupsNamingDifferentMailboxes_AreRefused` (2) | two checks naming two mailboxes refuse; one mailbox twice is allowed |
| `ValidatedWriteCallSiteTests` | each page write takes its own validation's mailbox; each audit site records the identity (source-level, see Known gaps) |

**Existing tests changed** (fixtures only, as aud-a2c did): `PermissionValidatorTests` allow cases
give their target a mailbox GUID - `ValidateTargetMailbox_ExclusionExchangeDoesNotKnow_KeepsLiteralMatch`,
`LegacyList_AnUnrelatedMailbox_IsAllowed`, `LegacyList_AFailedLoad_...`, `ValidateTargetMailbox_EmptyExclusions_AllowsOperations`,
`ValidateTargetMailbox_AppsettingsOnlyExclusion_IsNotProtected`, `ValidateTargetMailbox_InitSucceeds_WhenExchangeKnowsNoEntry`,
`AddressOnlyConfig_AllowsAnUnprotectedTarget`, and the scripted default principal; their assertions
are unchanged. `OutOfOfficeRequestCaptureTests`' validator now has a resolver (it asks Exchange for
the mailbox even with nothing protected).

**Mutations, round 1** (shared plumbing + Mailbox Permissions). Each applied by
`mutlib.py` only if the old text occurs exactly once and the md5 changes; test project rebuilt;
the named test run; the file restored from the saved bytes and touched; md5 re-checked. Every
restored md5 equals "before"; final rebuild and every named test green. Files as committed.

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- | --- |
| MP1 | `Add-MailboxPermission -Identity` typed string | `MailboxPermissionService.cs` `627e11937f4d352afbbac0b1f234cafb` | `458a624c540c809fc6e637d504cf31b2` | `AGrant_WritesTheValidatedMailbox_NotTheStringTyped` | FAIL |
| MP2 | `Add-RecipientPermission -Identity` typed string | same | `27c0e6fbb7c0465346c70a2570ef5a7f` | same | FAIL |
| MP3 | `Remove-MailboxPermission -Identity` typed string | same | `6116dc7539811140dfada5b17a16fb68` | `ARevoke_WritesTheValidatedMailbox_NotTheStringTyped` | FAIL |
| MP4 | `Remove-RecipientPermission -Identity` typed string | same | `cef136df9172ec0508b5cf522351c4cd` | same | FAIL |
| MP5 | read-back goes back to `ValidateMailbox(typed)` | same | `69c7b5b3ae5e3656ad9ec67dc8923404` | `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` | FAIL |
| MP6 | grant: null refusal dropped | same | `20a4c2790d30fc1b1f907dec2be307eb` | `WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed` | FAIL |
| MP7 | revoke: null refusal dropped | same | `3f657acf82a3c91255cf8958b42c03d6` | same | FAIL |
| MP8 | on-prem FullAccess grant argument typed string | same | `5ecb0a36899bef6c61f6912c734f4dfa` | `AnOnPremGrant_SendsTheValidatedMailbox_AndBindsSendAsToTheMailboxItNames` | FAIL |
| MP9 | on-prem Send As grant argument typed string | same | `88bf0152e40a58ba4aa6b2c1fb9f8cce` | same | FAIL |
| MP9b | on-prem Send As back to `Add-ADPermission -Identity <string>` | same | `dbd300424c12b969187d1f451233e61e` | same | FAIL |
| MP10 | on-prem FullAccess revoke argument typed string | same | `cef1db2946b0dbbb1502b9557a1f2c4e` | `AnOnPremRevoke_SendsTheValidatedMailbox_AndBindsSendAsToTheMailboxItNames` | FAIL |
| MP11 | on-prem Send As revoke argument typed string | same | `6aaad9560a9366887d72ff5ec8edec66` | same | FAIL |
| MP12 | on-prem grant: null refusal dropped | same | `21bfd17e90f865de3386f618636c9ee3` | `WithoutAValidatedMailbox_TheOnPremPathAsksForNoCredential_AndRunsNothing` | FAIL |
| MP13 | on-prem revoke: null refusal dropped | same | `b75ce16b270134c649022ab006e43972` | same | FAIL |
| MP14 | CSV row hands the write null instead of its mailbox | same | `81099f737350d65a8437018ed266025a` | `TheBulkLoop_WritesEachRowsOwnValidatedMailbox_AndRefusesARowWithNone` | FAIL |
| MP15 | CSV row audit drops `writtenIdentity` | same | `e774f88de59920ad7b324e6577423628` | same | FAIL |
| MB1 | read-back stops comparing the ExchangeGuid | `ExchangeServiceBase.cs` `3a2fe91ea9673f8202861ae81594488b` | `d96551ca1d78d82ee57e9ea1d82e2a33` | `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` | FAIL |
| V1 | validator: no-mailbox refusal dropped | `PermissionValidator.cs` `8e28690f2adc523a18a25d868c9237ab` | `48aeb107122d1d6d6bd932f20c2925eb` | `AResolvedTargetWithNoMailbox_IsRefused_RatherThanWrittenByItsName` | FAIL |
| V2 | validator: central/legacy disagreement allowed | same | `b515ddf5c92891dbe8e8c148020b6221` | `CentralAndLegacyLookupsNamingDifferentMailboxes_AreRefused` | FAIL |
| V3 | validator: binds to a fresh lookup of the string, not the resolved principal | same | `c0cf5064a08540083875de3649ae0eb4` | `TheAllowCarriesTheMailboxTheCheckResolved_NotTheStringTyped` | FAIL |
| V4 | validator: legacy path asks Exchange a second time | same | `6f9a40fef9a44efc71dc37fee6aa12a2` | `LegacyList_TheAllowCarriesTheMailboxTheListChecked_AskingExchangeOnce` | FAIL |
| P1 | page: single grant handed null | `MailboxPermissions.razor` `322eac0eaf4bf507d5a97715353e7c25` | `deca13674b95ca2250f122bb360d819d` | `ValidatedWriteCallSiteTests.EveryPageWrite_IsBoundToItsOwnValidation` | FAIL |
| P2 | page: on-prem grant handed null | same | `ba84f08cfb51fee1fa224e652288474f` | same | FAIL |
| P3 | page: location read by the typed string | same | `b8d21ab61740102dfd3d99152f04e71b` | same | FAIL |

A page cannot be mutated to pass the typed string to a write at all: no write method takes a
string identity any more, so that mutation does not compile. P3 is the one page call that still
can, because the string overload of the location read remains for read-only callers.

**Calendar tests** (`CalendarPermissionWriteIdentityTests`; the typed alias names a different
mailbox whose calendar folder is "Kalender"):

| Test | What it pins |
| --- | --- |
| `ASet_WritesTheValidatedMailboxsCalendar_NotTheStringTyped` | read-back, folder lookup and `Set-MailboxFolderPermission` all use the GUID; the identity is `<GUID>:\Calendar` |
| `ASetThatFallsBackToAdd_AddsOnTheValidatedMailboxsCalendar` | the `Add-` fallback takes the same folder identity |
| `ARemove_WritesTheValidatedMailboxsCalendar_NotTheStringTyped` | the remove path |
| `WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed` (2) | null refuses before a connection |
| `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` | read-back mismatch stops the write |
| `AnOnPremSet_SendsTheValidatedMailbox_AndWritesItsCalendar`, `AnOnPremRemove_...` | the remote script receives the GUID and writes `<GUID>:\Calendar` |
| `WithoutAValidatedMailbox_TheOnPremPathAsksForNoCredential_AndRunsNothing` (2) | on-prem refusal before the credential |
| `TheBulkLoop_WritesEachRowsOwnValidatedCalendar_AndRefusesARowWithNone` | the CSV row's own mailbox, `writtenIdentity` audited; a row with none denied as `BulkSetCalendar_ExcludedTarget` |

**Mutations, round 2** (Calendar Permissions):

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- | --- |
| C1 | set: folder identity built from the typed string | `CalendarPermissionService.cs` `1ae332eb5ac0b62fc754e355e58779e4` | `c0c1b3676e6c1a38007fef78e7520ac2` | `ASet_WritesTheValidatedMailboxsCalendar_NotTheStringTyped` | FAIL |
| C2 | `Set-MailboxFolderPermission -Identity <typed>:\Calendar` | same | `9a4a36bc81a97616ae1f4cdc7666f522` | same | FAIL |
| C3 | `Add-MailboxFolderPermission` fallback `-Identity <typed>:\Calendar` | same | `bf1434ccc58537571a02b958bafe7342` | `ASetThatFallsBackToAdd_AddsOnTheValidatedMailboxsCalendar` | FAIL |
| C4 | remove: folder identity built from the typed string | same | `6b80c2fbe434ae33201850e8014a22d7` | `ARemove_WritesTheValidatedMailboxsCalendar_NotTheStringTyped` | FAIL |
| C5 | `Remove-MailboxFolderPermission -Identity <typed>:\Calendar` | same | `f0deaf48fac99d28065a7baa638b969d` | same | FAIL |
| C6 | set: null refusal dropped | same | `f6bda17f6ddd7b7b128527c26396375a` | `WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed` | FAIL |
| C7 | remove: null refusal dropped | same | `f7cd8ca68b809a18f540d570194bbbb7` | same | FAIL |
| C8 | set: read-back skipped | same | `07b69980a79ff5c8d4289f934975644d` | `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` | FAIL |
| C9 | on-prem set argument typed string | same | `e6396c45c7579be05e35e1bb019d1a36` | `AnOnPremSet_SendsTheValidatedMailbox_AndWritesItsCalendar` | FAIL |
| C10 | on-prem remove argument typed string | same | `433a099a48482949f868b49d50ca5e57` | `AnOnPremRemove_SendsTheValidatedMailbox_AndWritesItsCalendar` | FAIL |
| C11 | on-prem set: null refusal dropped | same | `0360697f916ead5492ac73adc3fdee31` | `WithoutAValidatedMailbox_TheOnPremPathAsksForNoCredential_AndRunsNothing` | FAIL |
| C12 | on-prem remove: null refusal dropped | same | `f46a9197d2b87b1916b9f2f398a29b9a` | same | FAIL |
| C13 | CSV row hands the write null | same | `c42532d5d49973b007dbefffcc3eb14a` | `TheBulkLoop_WritesEachRowsOwnValidatedCalendar_AndRefusesARowWithNone` | FAIL |
| C14 | CSV row audit drops `writtenIdentity` | same | `5d1a7f3ba04db37d4474d4659be6384d` | same | FAIL |
| CP1 | page: single set handed null | `CalendarPermissions.razor` `52f4ddb3415520f5126a918489777a50` | `8e9397e089d974b6eb41e902a1d41cff` | `ValidatedWriteCallSiteTests.EveryPageWrite_IsBoundToItsOwnValidation` | FAIL |
| CP2 | page: on-prem remove handed null | same | `5354cb61bb2589102940e0b84e36bb2f` | same | FAIL |
| CP3 | page: location read by the typed string | same | `2b36a605c73fa4b318ffa5f2780b76f2` | same | FAIL |
| CP4 | page: single-write audit drops `writtenIdentity` | same | `4ce60521af1611d4efd2494026ef6cd9` | `ValidatedWriteCallSiteTests.EveryPageWriteAudit_RecordsTheIdentityWritten` | FAIL |

**Out of Office tests:**

| Test | What it pins |
| --- | --- |
| `OutOfOfficeWriteIdentityTests.TheWrite_IsBoundToTheValidatedMailbox_NotTheStringTyped` (Enabled, Disabled) | `Set-MailboxAutoReplyConfiguration -Identity` is the GUID; the typed alias (another mailbox) is not written |
| `WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed` | null refuses before a connection |
| `ThePreviousStateRead_IsOfTheValidatedMailbox_AndWithoutOneReadsNothing` | the audit's previous state is read from the GUID; no mailbox reads nothing |
| `OutOfOfficeRequestCaptureTests.AnEditDuringTheOperation_ChangesNeitherWhatIsWrittenNorWhatIsRecorded` (auth, read, write) | **existing harness, still passing**: an edit to every bound field mid-operation (the box moves to another mailbox with its own GUID) changes nothing - the write, the previous-state read and the audit's `writtenIdentity` are the captured mailbox's GUID; `target` stays the typed address. Assertion changed from the typed string to the GUID, which is the point of the finding |
| `ASecondSubmitWhileOneIsInFlight_WritesNothing` | unchanged guard; the one write is the captured mailbox's GUID |

**Mutations, round 3** (Out of Office):

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- | --- |
| O1 | `Set-MailboxAutoReplyConfiguration -Identity` typed string | `OutOfOfficeService.cs` `a2a033ab4ca8fa38660197a7bd88a14d` | `3aba8bd5825ecb08f28e955e9d30e333` | `OutOfOfficeWriteIdentityTests.TheWrite_IsBoundToTheValidatedMailbox_NotTheStringTyped` | FAIL (both cases) |
| O1b | the same mutation | same | same | `OutOfOfficeRequestCaptureTests.AnEditDuringTheOperation_...` | FAIL (all three holds) |
| O2 | null refusal dropped | same | `05462c61c3317fa0ba5c1fc8d5478872` | `WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed` | FAIL |
| O3 | previous-state read not of the validated mailbox | same | `76ed1ca2cf78081dbde46014955e7cc3` | `ThePreviousStateRead_IsOfTheValidatedMailbox_AndWithoutOneReadsNothing` | FAIL |
| OP1 | page: the write handed null | `OutOfOffice.razor` `0af66f3898ed894f03a548698aaf6d7b` | `b0da1605c3363c8d492cc3a81c9949e7` | `OutOfOfficeRequestCaptureTests.AnEditDuringTheOperation_...` | FAIL (all three holds) |
| OP2 | page: previous-state read by the typed string | same | `64b2397512a86eb08ce18794fdf41f30` | same | FAIL (all three holds) |
| OP3 | page: audit drops `writtenIdentity` | same | `082b6337533c9e36248ca79ccd3cbea9` | same | FAIL (all three holds) |

**Conference Rooms tests** (`ConferenceRoomWriteIdentityTests` drive the real service through the
harness, with the credential and the AD runspace replaced by stand-ins; the typed string is an
alias of a DIFFERENT room):

| Test | What it pins |
| --- | --- |
| `TheFinderWrites_AreBoundToTheValidatedRoom_NotTheStringTyped` | every room read, `Set-Place`, both timezone cmdlets and `Add-DistributionGroupMember -Member` take the GUID |
| `CityStateAndCountry_AreWrittenToTheProvenDirectoryObject_NotAUserPrincipalNameMatch` | `Set-ADUser` writes the proven objectGUID; every AD read is `-Identity <objectGUID>`, none a `-Filter` |
| `ACloudOnlyRoomWithLocationFields_IsRefusedBeforeAnyWrite` | no proven AD object: refused before Set-Place, no credential fetched |
| `ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain` | the already-member test uses the validated room's address |
| `TheOnPremRoomListFallback_AddsTheProvenDirectoryObject` | the on-prem fallback adds the proven objectGUID |
| `TheTypeWrites_AreBoundToTheValidatedRoom_NotTheStringTyped` (2) | read-back, folder lookup, `Set-CalendarProcessing`, `Set-Mailbox`, every folder permission (`<GUID>:\Calendar`), the existing-permission removal and both timezone cmdlets |
| `WithoutAValidatedRoom_NothingIsWritten_AndNothingIsAsked` (Finder, Type) | null refuses before any read, connection or credential |
| `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` (Finder, Type) | read-back mismatch stops both operations before a write |
| `ConferenceRoomProtectionGateTests.TheWrite_IsHandedTheRoomTheGateResolved` | `onAllowed` receives the resolved principal's GUID and proven objectGUID |
| `NotFound_HasNoRoomToBindTheWriteTo_WriteNotInvoked` | **rewritten from `NotFound_TreatedAsAllow_InvokesWrite`** - NotFound is now refused |
| `AResolvedTargetWithNoMailboxGuid_IsDenied_WriteNotInvoked` | an AD object Exchange has no mailbox for is refused |
| `ConferenceRoomServicerTests.AServicedAllow_IsBoundToTheRoomTheGateResolved`, `AServicedTargetWithNoMailbox_IsRefused` | the serviced allow is bound too |
| `ConferenceRoomBulkProcessorTests.ARow_IsWrittenToTheRoomTheGateResolvedForIt_AndTheAuditRecordsIt` (Finder, Type) | each bulk row hands its room operation the gate's room and audits `writtenIdentity` with the typed target |
| `ARowNeitherDirectoryKnows_HasNoRoomToBindTo_IsDeniedAndNotApplied` | a NotFound row is denied and audited, never applied |

Existing tests changed: the gate, servicer and bulk-processor fakes give the resolved principal a
mailbox GUID (an allow now needs one); the bulk fake's default status moved from NotFound to
Resolved for the same reason; the processor's happy-path audit assertion accepts the new `extra`.

**Mutations, round 4** (Conference Rooms):

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- | --- |
| R1 | `Set-Place -Identity` typed string | `ConferenceRoomService.cs` `91926ed347c6a362349979d1a0ea0b68` | `484d587457ad6874ccae416679735c0a` | `TheFinderWrites_AreBoundToTheValidatedRoom_NotTheStringTyped` | FAIL |
| R2 | Finder `Set-MailboxRegionalConfiguration` typed string | same | `efbff938c3225de39a2136a0d0f01185` | same | FAIL |
| R3 | Finder `Set-MailboxCalendarConfiguration` typed string | same | `65e3123cd2efa7b62bc95f45077518bf` | same | FAIL |
| R4 | `Add-DistributionGroupMember -Member` typed string | same | `15b763851ce0a6da42ae4904ef3cfc2c` | same | FAIL |
| R5 | already-member compared with the typed string | same | `534e4a1e65c9c6bfa720970c96ce6af7` | `ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain` | FAIL |
| R6 | `Set-ADUser -Identity` typed string | same | `5d4986b769b65633ee0b294b7b37d39f` | `CityStateAndCountry_AreWrittenToTheProvenDirectoryObject_NotAUserPrincipalNameMatch` | FAIL |
| R7 | AD object found by a UPN match of the typed string again | same | `f9712f05a7e2024ab9202a83e92c36e1` | same | FAIL |
| R8 | preflight falls back to the typed string with no proven object | same | `e499e96fe4793344a197969b687053d6` | `ACloudOnlyRoomWithLocationFields_IsRefusedBeforeAnyWrite` | FAIL |
| R9 | `Add-ADGroupMember -Members` typed string | same | `a7309b1cb3949bb0bdca2b7a131d0d1f` | `TheOnPremRoomListFallback_AddsTheProvenDirectoryObject` | FAIL |
| R10 | Finder: null refusal dropped | same | `45947a17be2b14db5740a6dce14e6dc8` | `WithoutAValidatedRoom_NothingIsWritten_AndNothingIsAsked` (Finder) | FAIL |
| R11 | Type: null refusal dropped | same | `23b45de46841525b35fbabd4b9a0af7e` | same (Type) | FAIL |
| R12 | room read by the typed string instead of the GUID | same | `4f1764dd6eb33d72dfb0ba46ddbc2a29` | `TheFinderWrites_AreBoundToTheValidatedRoom_NotTheStringTyped` | FAIL |
| R14 | Type: folder looked up by the typed string | same | `ae3d574df2d55826f8f125da5e4d5dd1` | `TheTypeWrites_AreBoundToTheValidatedRoom_NotTheStringTyped` | FAIL (both) |
| R15 | Type: folder identity built from the typed string | same | `ab1d5b1aa4f94187a84ad2e9e8ef6c43` | same | FAIL (both) |
| R16 | `Set-CalendarProcessing -Identity` typed string | same | `ab69fe6c53faf1cadb3dc1809eb0e0f2` | same | FAIL (both) |
| R17 | `Set-Mailbox -Identity` typed string | same | `bcb5150d34000746172a9596cfc13254` | same | FAIL (both) |
| R18 | Type `Set-MailboxRegionalConfiguration` typed string | same | `971bfe16a57a94497ceee4c8741b97ba` | same | FAIL (both) |
| R19 | gate: no-room refusal dropped | `ConferenceRoomProtectionGate.cs` `0aa3f7d67829af0812abb10d1fcd58ec` | `34bb23ff0f433bd51fffd1adde5ac74f` | `NotFound_HasNoRoomToBindTheWriteTo_WriteNotInvoked` | FAIL |
| R21 | gate: the resolved room not carried | same | `261473b4ee227be8e1a2f1623934871c` | `TheWrite_IsHandedTheRoomTheGateResolved` | FAIL |
| R22 | bulk row handed no room | `ConferenceRoomBulkProcessor.cs` `12d4e2e71c36546d7cb6d0bb374b7b69` | `3b74d2b1f9132c3d6766024155c33ff0` | `ARow_IsWrittenToTheRoomTheGateResolvedForIt_AndTheAuditRecordsIt` | FAIL (Finder; the mutation is on the Finder branch) |
| R23 | bulk row audit drops `writtenIdentity` | same | `58ede47d3698ec3e05c9b23c1951dea5` | same | FAIL (both) |
| R24 | page: old-values read by the typed string | `ConferenceRooms.razor` `de5ae5ce52362f4ea943fa2494993a99` | `da673853beff33f034c18e6f9c931245` | `ValidatedWriteCallSiteTests.EveryPageWrite_IsBoundToItsOwnValidation` | FAIL |
| R25 | page: Finder write handed null | same | `30880e759fab2be11053d1165f557f6f` | same | FAIL |
| R26 | page: Finder audit drops `writtenIdentity` | same | `c051de1a41b005eb9e23430007aeee4f` | `ValidatedWriteCallSiteTests.EveryPageWriteAudit_RecordsTheIdentityWritten` | FAIL |

**Round 5 - the shared read-back, re-proved on the final file.** `ExchangeServiceBase.cs` changed
after round 1 (`ValidateMailbox` removed in the Calendar commit; the read factored into
`ReadValidatedMailbox` in the Conference Rooms commit), so MB1 was re-run against it:

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named tests | Result |
| --- | --- | --- | --- | --- | --- |
| MB1-final | `ReadValidatedMailbox` stops comparing the ExchangeGuid | `ExchangeServiceBase.cs` `e29a5f47c4941483a53f9cbe07b60834` | `3fab2d5a92b755c6b57a46d4c02d5b70` | `AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite` in Mailbox, Calendar and Conference Rooms (Finder, Type) | FAIL (all 4) |

In all five rounds: 74 mutations, 74 named-test failures, every restored md5 equal to "before",
every final rebuild and named-test run green. Each md5 is of the file as it was committed in that
round's commit - comments were written before the rounds, not after; only `ExchangeServiceBase.cs`
changed again later, which round 5 covers.

## Known gaps

**Source-level and fake-driven, not live.** Probing Exchange, Active Directory or Graph is
forbidden without a direct owner go. The stand-ins bind parameters the way the cmdlets declare
them; they do not prove how the real cmdlets resolve a GUID string. Each assumption below fails
CLOSED - the cmdlet reports the identity "couldn't be found" and nothing is written - and each
needs the dev acceptance check before this ships:

- *Assumption:* MailboxIdParameter resolves a mailbox's ExchangeGuid string, in Exchange Online
  and on-premises: `Get-Mailbox`, `Add-/Remove-MailboxPermission`, `Get-/Set-MailboxAutoReplyConfiguration`,
  `Set-CalendarProcessing`, `Set-Mailbox`, `Set-MailboxRegionalConfiguration`,
  `Set-MailboxCalendarConfiguration`, `Get-MailboxFolderStatistics`, and the mailbox part of
  `<mailbox>:\<folder>` for `Set-/Add-/Remove-/Get-MailboxFolderPermission`. Microsoft documents
  "GUID" for all of them without saying which GUID; aud-a2c's design rests on the same assumption.
  The read-back (`BindValidatedMailbox`) at least proves that what `Get-Mailbox` returns for the
  string carries the validated ExchangeGuid.
- *Assumption, lower confidence:* RecipientIdParameter-typed identities resolve it too -
  `Add-/Remove-RecipientPermission -Identity`, `Set-Place -Identity`, `Get-Place`, and
  `Add-DistributionGroupMember -Member`. Documented as "GUID" only (learn.microsoft.com, read
  2026-10-21). If they do not, Send As grants/revokes, Room Finder metadata and room-list adds
  refuse with "couldn't be found" until bound another way (the recorded fallback: pass the
  mailbox's own directory `Guid` read back in the same session).
- *Assumption:* on-prem `Add-ADPermission` / `Remove-ADPermission` bind `-Identity` from a piped
  `Get-Mailbox` object (`ValueFromPipelineByPropertyName` is documented True; the documented
  direct forms are DN or name only). The pipeline is one statement, the shape the restricted
  remote endpoint already runs for `OnPremRecipientExists`.
- *Assumption:* `Get-ADUser -Identity <objectGUID>` - documented for the AD module.

**Dev acceptance check owed** (operator workflow, not automatable here): on dev, for one ordinary
cloud mailbox, grant and revoke FullAccess and Send As; set and remove a calendar permission; set
and disable Out of Office; apply Room Finder metadata (with and without City/State/Country) and a
room type to one room. Each should succeed and its audit row show `writtenIdentity`. A refusal
names the identity in its message.

**Page handlers** have no behavioural harness except Out of Office's. The other three are pinned by
types (no write method takes a string identity, so the typed address cannot be passed) and by
`ValidatedWriteCallSiteTests` (source-level: each write takes its own validation's mailbox, each
audit site records it). A page passing the wrong validation's mailbox is caught only by that scan.

**Who this refuses that a2c did not - operator-visible, refusing direction:**

- Mailbox Permissions, Calendar Permissions, Out of Office: a target Exchange reports no mailbox
  for (no recipient; a contact, distribution group or mail user without a mailbox GUID) is denied
  at validation with "Exchange Online reports no mailbox for '<x>'". Before, it was allowed and the
  write failed inside Exchange.
- With no protection configured at all, validation now asks Exchange once per target (for the
  mailbox to bind to) and refuses when Exchange cannot be asked. Before, it asked nothing.
- Conference Rooms: a room neither directory knows (NotFound) is denied at the gate rather than
  failing at the room read; a cloud-only room with City/State/Country refuses before Set-Place
  ("no on-premises Active Directory object was validated").
- The other direction, also operator-visible: a synced room typed by an address that is not its
  userPrincipalName used to fail the City/State/Country step ("No AD object found with
  userPrincipalName"); it now writes the room's own proven object. A room typed by an alias that is
  already on its room list is no longer re-added (and no longer fails as "already a member").

**Messages that changed:** the Calendar result message names the mailbox's primary address and
the identity written (`<GUID>:\Calendar`) instead of `<typed primary>:\Calendar`. Audit `target`
fields are unchanged; every write row gains `writtenIdentity` (Conference Rooms Finder rows also
`writtenDirectoryObject` - since aud-a2g, only those whose Active Directory write succeeded).

**Residuals, recorded so nobody reads them as covered:**

- The Conference Rooms Type path reads the room back twice (the recipient check and Step 1); the
  second read is redundant with the first and was not mutation-proven separately.
- `BindValidatedMailbox`'s "exactly one result" branch is not reached by any test (the stand-in
  returns one object); its GUID comparison is (MB1).
- The trustee (`-User`/`-Trustee`) is still the typed string, by design: it is not a protected
  target. `ValidateSelfGrantAsync` still compares typed strings.

**Observed, not admitted (reported for admission):**

- *Candidate:* Conference Rooms writes room mailboxes INTO a room-list group (`Add-DistributionGroupMember
  -Identity <list>`, and `Add-ADGroupMember -Identity <group objectGUID>` on the on-prem fallback),
  and that group is never put through the Protected Group Targets check. The list is named from the
  operator's Building field (`"<building> Conference Rooms"`, or legacy `RoomList-<building>`), so the
  operator chooses, within that naming, which existing group is written into.
  `docs/ProtectedGroupWriteTarget-Plan.md` scoped Conference Rooms out because "their write target is
  a mailbox"; this write's target is a group. Not fixed here: it reverses a recorded owner-approved
  scope (and the target check is AD-only, while most room lists are cloud groups).

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-ab74b77ad109ec5f3`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed. Shared infrastructure (`ValidatedMailbox`,
`PermissionValidator`, `ExchangeServiceBase`, `ExchangeIdentityResolver`) moves the base app version
at merge. Module behaviour changes, version bumps due at merge: Mailbox Permissions, Calendar
Permissions, Out of Office, Conference Rooms.
