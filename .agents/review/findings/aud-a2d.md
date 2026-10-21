# aud-a2d: The Exchange write re-resolves the operator's string instead of writing the mailbox the check validated

**Severity**: MEDIUM - after aud-a2b and aud-a2c the check names the right object, but every write
hands Exchange the typed string again, so check and write can still name different objects.
**Status**: Plan written; implementation in progress
**Branch**: `worktree-agent-ab74b77ad109ec5f3`
**Commit**: `<pending>`
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
