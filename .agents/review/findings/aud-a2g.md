# aud-a2g: Room Finder audits name a written directory object when no Active Directory write ran

**Severity**: MEDIUM - the audit row overstates what was written, which weakens the
protected-principal and audit trace aud-a2d added it for.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a0c2a0143143dd6e1`
**Commit**: `<pending>`
**Reviewer**: codex / gpt-5.5-dzs / xhigh over 3562cf08..7ae55b9a, 2026-10-21

## Evidence

Raised by the codex review of aud-a2d (verdict `.agents/review/a2d.result.txt` in the main
checkout, candidate 2). Line numbers at the aud-a2f commit (the page and the processor are as at
`86300ae6`).

The key's own definition - `Services/ValidatedMailbox.cs:25-26`:

```
25    /// <summary>The <c>extra</c> key for the Active Directory object written, when one was.</summary>
26    public const string DirectoryAuditKey = "writtenDirectoryObject";
```

and how it is decided - by a flag the caller passes, not by anything that was written (`:100-101`):

```
100        if (includeDirectoryObject && mailbox.DirectoryObjectGuid is not null)
101            extra[DirectoryAuditKey] = mailbox.DirectoryObjectGuid;
```

Every Room Finder caller passes it unconditionally. The page - `Components/Pages/ConferenceRooms.razor:1722`:

```
1722        try { Audit.LogConferenceRoomAction(... extra: ValidatedMailbox.WithWrittenIdentity(ServicedExtra(servicedAuditDetail), room, includeDirectoryObject: true)); }
```

the bulk processor - `Services/Jobs/ConferenceRoomBulkProcessor.cs:140`, `:152` (the exception
path) and `:219`:

```
140                        room: room, directoryWritten: isFinder);
152                    Audit(job, actionAudit, target, success: false, ticket, ex.Message, room: room, directoryWritten: isFinder);
219                extra: ValidatedMailbox.WithWrittenIdentity(null, room, includeDirectoryObject: directoryWritten));
```

But a Room Finder row writes Active Directory only in two places, and often in neither -
`Services/ConferenceRoomService.cs`:

```
1469        if (attrs.Count == 0)
1470            return null; // nothing synced to write for this row
1500                ps.AddCommand("Set-ADUser")
1638                if (alreadyMember)
1639                    return null;
1642                ps.AddCommand("Add-ADGroupMember")
```

No City/State/Country means no `Set-ADUser`; a cloud room list means no `Add-ADGroupMember`; a
room already on its on-prem list means none either; a refused or failed AD step writes nothing. And
`RoomOperationResult` (`Models/ConferenceRoomModels.cs:176-190`) carries nothing about AD writes,
so no caller could have known.

## Predicted observable failure

A Room Finder apply of Building/Capacity/Floor only, for a synced room on a cloud list: the writes
are `Set-Place`, the timezone pair and `Add-DistributionGroupMember -Member <GUID>` - all Exchange.
The audit row reads `writtenDirectoryObject: <objectGUID>`, so anyone tracing what the app changed
in Active Directory finds a change that never happened. The same row is written when City is given
and `Set-ADUser` fails, and when the row is refused or throws before any write (bulk exception path).

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21);
codex's better approach, verbatim: "have the service result report whether an AD write actually
succeeded and which object it wrote; add `writtenDirectoryObject` only for successful `Set-ADUser`
or `Add-ADGroupMember` writes."

**Rule.** The audit names a written directory object only when the service reports one written, and
names the object the service says it wrote.

- `RoomOperationResult.WrittenDirectoryObject` (string?): the objectGUID of the AD object an AD write
  in the operation changed, null when none did. Set by the service only.
- `SetSyncedAttributesViaAdAsync` returns the object after `Set-ADUser` succeeds; nothing for the
  no-attributes no-op or any failure. `AddToRoomListViaAdAsync` returns it after `Add-ADGroupMember`
  succeeds; nothing for already-a-member or any failure. `SetRoomMetadataAndListAsync` records it the
  moment the write succeeds, so a later step failing (a partial row) still names the object that WAS
  written.
- `ValidatedMailbox.WithWrittenIdentity(extra, mailbox, string? writtenDirectoryObject = null)`: the
  caller's `bool includeDirectoryObject` is replaced by the object reported written; the key is added
  only for a non-null value. Exchange-only callers (Mailbox, Calendar, Out of Office, Room Type) pass
  nothing, as now.
- Page `AuditFinderAction` and the processor's `Audit` take the reported object and pass it on; the
  page's and the processor's exception paths pass none (nothing reported). `writtenIdentity` is
  unchanged: it records the identity the writes were bound to, as its own doc says.
- Page line count unchanged (1758): edits in place on the `@code` lines, so no `ClickGateRegistry`
  entry moves. If the count did change, re-anchor by `.agents/playbooks/clickgate-reanchor.md`.

**Tests.** Behavioural on both audit surfaces, not source scans: the page (a real `ConferenceRooms`
instance driven through reflection - the `OutOfOfficeRequestCaptureTests` idiom - over the real
service on `ExchangeWriteHarness`, a real protection gate and the real audit, read back) and the
bulk processor (real processor, service, gate and audit). For each surface:

- Finder with no City/State/Country on a cloud list: the row has `writtenIdentity` and no
  `writtenDirectoryObject`;
- City written: `writtenDirectoryObject` is the proven objectGUID;
- `Set-ADUser` fails: no `writtenDirectoryObject` (row partial);
- City written, then the timezone step fails: present (the object was written; row partial);
- on-prem room-list add: present; already a member on-prem: absent (nothing added).

**Mutations, at the call sites:** the processor and the page each restored to always-include; the
service reporting the object for the no-op, for a failed `Set-ADUser`, for already-a-member; the
fallback's object not carried to the row; the Set-ADUser success not recorded until the row
succeeds; `WithWrittenIdentity` dropping the reported object. A named test fails for each.

**Files.** `Models/ConferenceRoomModels.cs`, `Services/ValidatedMailbox.cs`,
`Services/ConferenceRoomService.cs`, `Services/Jobs/ConferenceRoomBulkProcessor.cs`,
`Components/Pages/ConferenceRooms.razor`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` (the
stand-in `Get-ADGroupMember` reports configurable members),
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs` (new), this record, and a correction
pointer in `aud-a2d.md`.

## Approach (as implemented)

- `RoomOperationResult.WrittenDirectoryObject` (string?), set only inside
  `ConferenceRoomService.SetRoomMetadataAndListAsync`.
- `SetSyncedAttributesViaAdAsync` and `AddToRoomListViaAdAsync` return `(error, written)`. `written`
  is the objectGUID only on the line after the write cmdlet returned without errors
  (`return (null, objectGuid)` after `Set-ADUser`, `return (null, roomGuid)` after
  `Add-ADGroupMember`); every other return - the no-attributes no-op, already-a-member, every
  refusal, error and catch - is `(<message>, null)` or `(null, null)`. The `Task.Run` lambdas are typed
  `Task.Run<(string? error, string? written)>` so each tuple literal is checked against that shape.
- The row takes Set-ADUser's object unconditionally on the line after the call, before the error
  check - one place decides "written", and it is the write itself. The on-prem add's object rides the
  list result (`AddToRoomListAsync` sets it on its success branch) and joins the row with `??=` (both
  writes, when both run, name the same proven object).
- `ValidatedMailbox.WithWrittenIdentity(extra, mailbox, string? writtenDirectoryObject = null)`: the
  `bool includeDirectoryObject` - a claim about the kind of operation - is gone; the key is added
  only for an object passed in. The Mailbox, Calendar, Out of Office and Room Type callers passed
  nothing before and pass nothing now (two-argument calls, unchanged).
- Page: `AuditFinderAction` gains `string? writtenDirectoryObject = null` and passes it on; the
  `onAllowed` call passes `r.WrittenDirectoryObject`; the denial, ticket and exception calls pass
  nothing (they never had a room either). Three lines edited in place - 1758 lines before and after,
  so no `ClickGateRegistry` entry moved and no re-anchor was needed; the ClickGate suite was run.
- Processor: `Audit(..., string? writtenDirectoryObject = null)` replaces `bool directoryWritten`;
  the row passes `r.WrittenDirectoryObject`; the exception path passes nothing.

## Guard proof

All in `ConferenceRoomDirectoryAuditTests`; each a theory over `page` (a real `ConferenceRooms`
instance, its injected services set by reflection, `SetupSingleRoom` invoked) and `bulk` (the real
`ConferenceRoomBulkProcessor.ProcessRowAsync`), both over the real service on the harness, the real
gate (resolver scripted to the synced room, unprotected) and the real audit read back from JSONL.

| Test | What it pins |
| --- | --- |
| `NoActiveDirectoryWrite_TheAuditNamesNoDirectoryObject` | the codex trigger - synced room, no City/State/Country, cloud list: success, `writtenIdentity` = the room's GUID, no `Set-ADUser`, no `Add-ADGroupMember`, and the row has NO `writtenDirectoryObject` key |
| `CityWritten_TheAuditNamesTheObjectWritten` | `Set-ADUser` on the proven objectGUID, and the row names exactly that object |
| `ASetADUserThatFails_IsNotRecordedAsWritten` | `Set-ADUser` attempted and failed: row failed, key absent |
| `AWriteThatSucceeded_IsRecordedEvenWhenALaterStepFails` | `Set-ADUser` succeeded, the timezone failed: row failed (partial), the object still named |
| `TheOnPremRoomListAdd_IsRecordedAsWritten` | synced list, cloud add refused, `Add-ADGroupMember` of the proven object: the row names it |
| `ARoomAlreadyOnItsOnPremList_IsNotRecordedAsWritten` | the on-prem idempotency check finds the room: no `Add-ADGroupMember`, key absent |
| `AnApplyThatThrows_IsNotRecordedAsWritten` | the read-back answers another mailbox and throws before any write: row failed, nothing written, key absent (page: the catch has no room; bulk: the catch has the room and still no key) |

**Revert the fix.** The five production files replaced by HEAD's (the aud-a2f commit:
`ConferenceRoomModels.cs` `db166829709959e4807eeb734522dc7f`, `ValidatedMailbox.cs`
`e26c00a21bbdb52048f478a26a8f25c3`, `ConferenceRoomService.cs` `052721e3b1f38a2d046369566fd97546`,
`ConferenceRoomBulkProcessor.cs` `12d4e2e71c36546d7cb6d0bb374b7b69`, `ConferenceRooms.razor`
`de5ae5ce52362f4ea943fa2494993a99`), tests and harness as fixed: 7 of 14 FAIL - no-AD-write (page,
bulk), Set-ADUser fails (page, bulk), already a member (page, bulk), apply throws (bulk) - and the 7
where the old always-include happened to be true pass. All five restored byte-identical
(`48bd807f0dbad463f41cb50ba82a19a5`, `4805b0cb3c3c9cbc1e2ed101c137aef9`,
`c807ad832294ac3f3ae782413163b315`, `e941a96990d42c3bb6f82a4b39e16989`,
`9d581a1e960c7442981b92e18f9920d2`), touched; rebuild and 14/14 green.

**Mutations** (`scratchpad/a2fg/round_a2g.py` in the session scratchpad, the aud-a2f runner; each
edit applied only if its old text occurs exactly once and the md5 changes; rebuilt; the named test
run; restored from the saved bytes and touched; md5 re-checked). md5 before = restored for every
row, and each is the file as committed - no edit followed the round. Test names are in
`ConferenceRoomDirectoryAuditTests`.

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- | --- |
| G1 | the shared helper back to always-include (the proven object, whatever was written) | `ValidatedMailbox.cs` `4805b0cb3c3c9cbc1e2ed101c137aef9` | `5ee563a509075ffe126ce33cd77b5c2b` | `NoActiveDirectoryWrite_TheAuditNamesNoDirectoryObject` | FAIL (page, bulk) |
| G2 | the shared helper drops the reported object | same | `7ca88626d74dd4513395931c19db09d3` | `CityWritten_TheAuditNamesTheObjectWritten` | FAIL (page, bulk) |
| G3 | bulk row audit: always-include restored at the call site | `ConferenceRoomBulkProcessor.cs` `e941a96990d42c3bb6f82a4b39e16989` | `aa355125c8a8a7b5462389a7a80a76ad` | `NoActiveDirectoryWrite_TheAuditNamesNoDirectoryObject` | FAIL (bulk) |
| G4 | bulk exception path: always-include restored | same | `60d5e79a54046abac3d8ea67760f41de` | `AnApplyThatThrows_IsNotRecordedAsWritten` | FAIL (bulk) |
| G5 | bulk `Audit` helper: always-include restored inside the helper | same | `c2cb451471504f9865299f17d34b5a16` | `NoActiveDirectoryWrite_TheAuditNamesNoDirectoryObject` | FAIL (bulk) |
| G6 | page Finder audit: always-include restored at the call site | `ConferenceRooms.razor` `9d581a1e960c7442981b92e18f9920d2` | `a78269c99b47fd4443282046520ec845` | same | FAIL (page) |
| G7 | page `AuditFinderAction`: always-include restored inside the helper | same | `e61a8e228cd2caaa9bde1c8ff5783477` | same | FAIL (page) |
| G8 | service: the nothing-to-write no-op reported as written | `ConferenceRoomService.cs` `c807ad832294ac3f3ae782413163b315` | `953a31d59be23721ad6d70e5f5886e4c` | same | FAIL (page, bulk) |
| G9 | service: a failed Set-ADUser reported as written (both failure returns) | same | `25071028b91b5ad253489e2561f299f4` | `ASetADUserThatFails_IsNotRecordedAsWritten` | FAIL (page, bulk) |
| G10 | service: a successful Set-ADUser not reported | same | `a912c414ba5c576052b0b38c3722f66c` | `CityWritten_TheAuditNamesTheObjectWritten` | FAIL (page, bulk) |
| G11 | service: the Set-ADUser object recorded only once the whole row succeeds | same | `0b629ec6c543cfb190d4970963ebcdee` | `AWriteThatSucceeded_IsRecordedEvenWhenALaterStepFails` | FAIL (page, bulk) |
| G12 | service: already-a-member reported as written | same | `58620a666f0e3e1dc6c1c4df7dec3f61` | `ARoomAlreadyOnItsOnPremList_IsNotRecordedAsWritten` | FAIL (page, bulk) |
| G13 | service: a successful Add-ADGroupMember not reported | same | `43cbb4ca123051e24a707e1b827ad2df` | `TheOnPremRoomListAdd_IsRecordedAsWritten` | FAIL (page, bulk) |
| G14 | service: the on-prem add's list result does not carry its object | same | `ae7bcf444fac42c87a5557f5ee8b538d` | same | FAIL (page, bulk) |
| G15 | service: the on-prem add's object not carried to the row | same | `e9f843aa4469653fc03b527e5bfaec78` | same | FAIL (page, bulk) |

15 mutations, 15 named-test failures, every restored md5 equal to "before"; final rebuild and the
named tests green (14/14).

## Known gaps

- *Reasoning, not a test:* the exception paths pass no object, so an exception escaping the service
  AFTER a successful AD write would understate the row. No such path is known: since aud-a2f every
  read that can throw runs before the first write; after `Set-ADUser` the timezone step and the
  cloud add run inside `RunAsync`, which never re-throws; the AD helpers catch everything; and the
  credential lookup returns null on failure (`DelineaService.GetCredentialsBySecretIdAsync` catches).
- *Live-only, the aud-a2d class:* "written" means the cmdlet returned without an error. The
  stand-ins prove the wiring, not that a real `Set-ADUser -Replace` or `Add-ADGroupMember` that
  reports no error changed the directory. A cmdlet that changed the directory and then reported an
  error would be recorded as not written.
- **Observed, not admitted:** `writtenIdentity` is still recorded on every row with a bound room -
  refused (aud-a2f's preflight), failed and partial rows included - in all four aud-a2d modules. Its
  own contract says it is "what the write was bound to", so this is not the a2g defect; but its name
  reads as "written", and a refused row now carries it. Reported for a decision, not changed here
  (cross-module, and a documented aud-a2d design).
- Downstream readers: `writtenDirectoryObject` appeared with aud-a2d, merged today, so nothing can
  yet depend on it being on every Finder row.

**Page:** 1758 lines before and after; no `ClickGateRegistry` entry moved; ClickGate suite run.

**Dev acceptance owed:** Room Finder on dev with Building/Capacity only (audit row: no
`writtenDirectoryObject`) and with City (audit row: the room's objectGUID).

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a0c2a0143143dd6e1`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed. Conference Rooms behaviour change (module version bump due
at merge); `Services/ValidatedMailbox.cs` is shared infrastructure, so the base app version bump is
due too (Constitution, Deployment And Versioning) - no behaviour change for the other modules, whose
calls are unchanged.
