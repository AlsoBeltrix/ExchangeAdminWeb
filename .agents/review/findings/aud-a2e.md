# aud-a2e: Conference Rooms writes to a room-list group that is never checked against Protected Group Targets

**Severity**: MEDIUM - an authorization gap (Known Failure Class 3). Conference Rooms adds a room mailbox to a room-list distribution group chosen through the Building field (`ConferenceRoomService.AddToRoomListAsync`, on-prem fallback included); that group write is never routed through `ProtectedPrincipalService.CheckWriteTarget`. `docs/ProtectedGroupWriteTarget-Plan.md` scoped the module out as "its write target is a mailbox", which is true of `Set-Place` but not of the room-list membership write. A room list configured as a Protected Group Target can therefore be written by any Conference Rooms operator.
**Status**: Verified on branch; awaiting merge and codex review - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: `fix/a2e` (worktree `D:\source\ExchangeAdminWeb-wt\a2e`, forked from master at `eb017102`)
**Commit**: the one commit on `fix/a2e` that adds this record (its SHA is in the implementer's hand-back)
**Reviewer**: implementer (recorded by the supervisor at the 2026-10-23 hand-over)

## Evidence

At `4498da49`: `Services/ConferenceRoomService.cs` 332-442 (`ResolveRoomListAsync` / `FindRoomList` / `ResolveRoomListTargetAsync` - the list is resolved with name, GUID and primary SMTP), 560-565 (preflight), 663-665 and 710+ (`AddToRoomListAsync`, the cloud add and the on-prem fallback). No call to `CheckWriteTarget` (`Services/ProtectedPrincipalService.cs` 312) or `ProtectedPrincipalServicing.ForWriteTarget` (`Services/ProtectedPrincipalServicing.cs` 89) appears in the file. Compare `Services/GroupManagementService.cs` and `Services/PermissionValidator.cs` 512 (`BindWriteTargetAsync`), which do route their group writes.

Trigger: a room list (canonical or legacy name, cloud or synced) is listed under Protected Group Targets; an operator adds a room with that Building. Predicted failure: the membership write succeeds with no refusal and no protected-target audit evidence.

## Approach

Written before the code, against `eb017102`. The supervisor's brief for it: resolve the room-list group by immutable
identifier and route it through the existing write-target check before the preflight reports the add as possible and
again before the write (the aud-a2b/a2d shape: the checked object is provably the written one); fail closed; the servicer
exemption applies as elsewhere; mutate at the call site.

**What is written, and where nothing checks it.** A Room Finder row with a Building adds the room to that building's room
list, by one of two writes:

- the cloud add, `Services/ConferenceRoomService.cs:768-771` - `Add-DistributionGroupMember -Identity <listGuid> -Member
  <room GUID>` - into the list `FindRoomList` read (`:381-400`, its `Guid` since aud-a2m) or the one `New-DistributionGroup`
  returned for a list the row creates (`:734-740`);
- for a list synchronized from on-premises, which Exchange Online refuses the cloud add for, the on-prem fallback
  `:2166-2171` - `Set-ADGroup -Identity <groupGuid> -Add @{ member = <room DN> }` - into the group `ResolveAdGroupGuid`
  (`:2458-2495`) found in the forest by the list's primary address (aud-a2k) and re-read in its own domain
  (`ReadAdGroupInItsDomain`, `:2504-2527`).

Neither the preflight that proves the add can complete (`PreflightRoomListAsync`, `:1925-2050`, called at `:563-571`) nor
the add (`AddToRoomListAsync`, `:710-865`, called at `:663-685`; `AddToRoomListViaAdAsync`, `:2068-2266`) calls
`ProtectedPrincipalService.CheckWriteTarget` (`Services/ProtectedPrincipalService.cs:312`) or
`ProtectedPrincipalServicing.ForWriteTarget` (`Services/ProtectedPrincipalServicing.cs:89`), and the service holds neither
dependency (constructor, `:15-30`). Group Management is the precedent for a group write target: it resolves the group once
into a full snapshot and gates that snapshot before the write that names it (`Services/GroupManagementService.cs:642-661`,
`:813-832`). `docs/ProtectedGroupWriteTarget-Plan.md:184-186` left Conference Rooms out because "their write target is a
mailbox" - true of `Set-Place`, not of this write.

**The rule.** The room list is checked with the shared gate `ForWriteTarget` (module id `ConferenceRooms`, the room
gate's, so one `ProtectedServicer:ConferenceRooms` grant services both), on the object the write names, built from the
read that gave the write its identifier:

| List | Object checked | Bound to the write by |
| --- | --- | --- |
| synchronized from on-premises | the on-prem group `ResolveAdGroupGuid` resolved, as its own domain answered for it: objectGUID, DN, sAMAccountName, name; its mail is the list's address, which the forest search matched it by; and the list's Entra object id | `Set-ADGroup -Identity` is that snapshot's objectGUID, routed by its DN |
| held by Exchange Online | the list as `Get-DistributionGroup` reported it: its primary address and Entra object id (`ExternalDirectoryObjectId`, newly read from the same object); no DN, objectGUID or sAMAccountName | the cloud add's `-Identity` is the `Guid` of that same object (aud-a2m) |
| created by the row | the list as `New-DistributionGroup` returned it, likewise | `-Identity` is the `Guid` that creation returned |

The synced group's snapshot is the full one pgwt-2 requires. One read back without a sAMAccountName is refused, so a
pattern rule is never skipped in silence. A cloud list has no on-premises object, so only protected user rows can match it
- the Constitution's structural limit for cloud-only principals, stated in Known gaps.

**When it runs.**

1. Before anything is written, in the preflight, before it reports the add as possible: an existing cloud list always
   (the check is directory-free); an existing synced list right after its group is resolved - which the preflight does
   only when Exchange does not already show the room on the list. A room already on its synced list writes nothing to it,
   and aud-a2f made such a re-run need no Active Directory; that stays. A list the row creates does not exist yet. A
   refusal - protected and not serviced, a check that failed, no protected-principal service - refuses the whole row with
   nothing written; the message names the list and ends "Nothing was written."
2. Again immediately before each write into the list, on the exact object that write names: the cloud add after its
   membership read and before `Add-DistributionGroupMember`; the on-prem add after its membership read and before
   `Set-ADGroup`, on the snapshot it has just resolved. A synced list's cloud add is checked against the group the preflight
   cleared, carried on the `RoomListTarget`; with none (the room was on the list when the row was checked, so no group was
   looked up) that cloud add is refused, never made unchecked. A refusal here comes after Set-Place: the row is the partial
   the module already reports, and the message does not claim nothing was written.

**Fail closed.** The decision is `ForWriteTarget`'s: a failed or errored check refuses with its reason; a protected list
refuses unless the acting user holds the module's servicer grant. The service takes `ProtectedPrincipalService` and
`ProtectedPrincipalServicerService` as optional constructor dependencies - the container supplies both; without them every
list write is refused, never unchecked.

**The servicer exemption.** `SetRoomMetadataAndListAsync` and `IConferenceRoomBulkOperations` take the acting user. The page
passes the current operator, read fresh as for the room gate; the bulk processor passes null explicitly - no servicer
bypass off-circuit, the room gate's rule. A serviced note comes back on the result (`RoomListServicedNote`), and the page
records it beside the room gate's note in the audit `extra` (`protectedPrincipalServiced`, joined by
`GroupManagementService.CombineNotes`), on success and failure alike (pgwt-7).

**Owner call named in the brief - may Set-Place proceed when the room-list add is refused?** Taken fail-closed: no. A list
the check refuses refuses the whole row before Set-Place (Known gaps).

**Files to change.** `Services/ConferenceRoomService.cs`, `Services/Jobs/IConferenceRoomBulkOperations.cs`,
`Services/Jobs/ConferenceRoomBulkProcessor.cs`, `Models/ConferenceRoomModels.cs`, `Components/Pages/ConferenceRooms.razor`
(two lines edited in place; the line count, 1758, is kept, so no ClickGate or Progress entry moves), `Modules/ModuleCatalog.cs`
(Conference Rooms 2.6.8 -> 2.7.0); a one-line correction pointer at each plan's stale claim
(`docs/ProtectedGroupWriteTarget-Plan.md`, `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md`). Tests:
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` (a group re-read reports sAMAccountName and name, a list its
ExternalDirectoryObjectId, a created list an address; real protection services over the harness's store), the room-service
fixtures of `ConferenceRoomRoomListPreflightTests.cs`, `ConferenceRoomWriteIdentityTests.cs`,
`ConferenceRoomDirectoryAuditTests.cs` and `ConferenceRoomBulkProcessorTests.cs` (signatures and the protection services,
assertions unchanged), a new `ConferenceRoomListWriteTargetTests.cs`; this record.

**Mutations planned, at the call sites.** Each check removed (preflight cloud, preflight synced, cloud add, on-prem add,
created list); each add's check moved after its write; the synced cloud add made without a cleared group; a missing
protection service read as an allow; the Entra object id and the sAMAccountName dropped from the snapshots; the page
recording the room gate's note only.

## Files changed

Snapshot before repair: `eb017102`. Implemented as the Approach describes; nothing in it changed in the building.

- `Services/ConferenceRoomService.cs` - `CheckRoomListTarget` (the one call to `ForWriteTarget`; no protection services
  refuses), `CloudListTarget` (a cloud list's identifiers), `RoomListTargetRefusal` / `RoomListNotCheckedRefusal`; the
  optional `ProtectedPrincipalService` / `ProtectedPrincipalServicerService` constructor dependencies; `RoomListTarget`
  gains `ExternalDirectoryObjectId` (read by `FindRoomList` from the same `Get-DistributionGroup` object) and
  `DirectoryGroup` (the synced group the preflight cleared); `SetRoomMetadataAndListAsync` and `AddToRoomListAsync` take
  the acting user; `PreflightRoomListAsync` checks a cloud list always and a synced list's group right after resolving it,
  and returns the list as checked and any servicer note; `AddToRoomListAsync` checks the object its cloud add names after
  the membership read and before `Add-DistributionGroupMember` (a created list as the creation returned it; a synced list
  by its cleared group, none refusing); `AddToRoomListViaAdAsync` takes the list and the user and checks the group it
  resolved after the membership read and before `Set-ADGroup`, its note on every later outcome; `ResolveAdGroupGuid` /
  `ReadAdGroupInItsDomain` return the group as a full snapshot - objectGUID and DN as its own domain answered, the
  sAMAccountName and name of the default property set (a read without a sAMAccountName refuses), the list's address as
  its mail, the list's Entra object id - and the membership read and the write take its objectGUID and DN.
- `Services/Jobs/IConferenceRoomBulkOperations.cs` - the Finder operation takes the acting user.
- `Services/Jobs/ConferenceRoomBulkProcessor.cs` - passes `actingUser: null`, explicitly.
- `Models/ConferenceRoomModels.cs` - `RoomOperationResult.RoomListServicedNote`.
- `Components/Pages/ConferenceRooms.razor` - two lines edited in place: the Finder write passes `await CurrentUserAsync()`
  (1073), and its audit records `GroupManagementService.CombineNotes(serviced, r.RoomListServicedNote)` (1079). 1758
  lines before and after, so no `ClickGateRegistry` or `ProgressRegistry` entry moves.
- `Modules/ModuleCatalog.cs` - Conference Rooms 2.6.8 -> 2.7.0. The base app version is not bumped here.
- `docs/ProtectedGroupWriteTarget-Plan.md`, `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` - a correction pointer at
  each stale "the write target is a mailbox" claim.
- `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` - a group re-read answers `SamAccountName` (`DirectoryGroupSamAccountNames`,
  else its CN) and `Name`; a list answers `ExternalDirectoryObjectId` (`ListExternalIds`); a created list an address
  (`CreatedListAddress`); `ListMembersClearedAfterSetPlace` stages the room taken off its list in between;
  `DirectoryGroupAsChecked`; real `ProtectedPrincipals` and `Servicers` over the harness's own store.
- `ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`, `ConferenceRoomWriteIdentityTests.cs`,
  `ConferenceRoomDirectoryAuditTests.cs` - their room-service fixtures take the harness's protection services (nothing
  protected), the calls pass `actingUser: null`, and a synced list handed straight to the add carries the cleared group
  the preflight would have handed it. No assertion changed; one test comment updated (below).
- `ExchangeAdminWeb.Tests/ConferenceRoomBulkProcessorTests.cs` - the fake takes the acting user and records it; one new pin.
- `ExchangeAdminWeb.Tests/ConferenceRoomListWriteTargetTests.cs` - new, the guards below.

## Guard proof

| Test (`ConferenceRoomListWriteTargetTests` unless named) | What it pins |
| --- | --- |
| `ASyncedRoomListWhoseGroupIsAProtectedGroupTarget_IsRefusedBeforeAnyWrite` | the finding: a synced list whose on-prem group is a Protected Group Target (stored as the admin page's validator stores it) refuses the row before Set-Place - nothing written, "Nothing was written." once, the refusal logged once |
| `AProtectedGroupTarget_IsMatchedByTheGroupsObjectGuid_NotItsName_AfterARenameOrMove` | by immutable identifier: a row saved under an old DN still matches by objectGUID |
| `ASyncedRoomListGroupMatchingAProtectedPattern_IsRefusedBeforeAnyWrite`, `ASyncedRoomListGroupInsideAProtectedOu_IsRefusedBeforeAnyWrite` | AC9 / pgwt-2 for this module: the pattern and OU rules reach the group, which a DN-only snapshot would not |
| `ASyncedRoomListGroupReadBackWithoutASamAccountName_IsRefusedBeforeAnyWrite` | missing lookup data fails closed |
| `AGroupListedOnlyToProtectItsMembers_IsStillWritten` | AC8: the Groups (members) list and a target row for another group refuse nothing here, and no note arises |
| `ACloudRoomListProtectedByItsAddress_IsRefusedBeforeAnyWrite`, `ACloudRoomListProtectedByItsEntraObjectId_IsRefusedBeforeAnyWrite` | a cloud list, by the identifiers Exchange reported for it |
| `ACloudRoomListNoRuleNames_IsWritten` | the control: rules naming other objects leave the cloud add as it was |
| `WithoutTheProtectedPrincipalServices_ARoomListIsRefusedBeforeAnyWrite`, `AProtectionStoreThatCannotBeRead_RefusesBeforeAnyWrite` | fail closed: no service, or rules that cannot be read, refuse with the reason |
| `TheCloudAdd_ChecksTheListImmediatelyBeforeItWrites` | the add's own check (a rule saved after the preflight): no `Add-DistributionGroupMember`, refused in the add's step, logged once, not claiming nothing was written |
| `TheOnPremAdd_ChecksTheGroupItResolves_ImmediatelyBeforeItWrites` | the on-prem write is checked on the group it resolves and names, not the one the preflight cleared: no `Set-ADGroup` |
| `ASyncedListsCloudAdd_WithNoGroupCheckedBeforeIt_IsNotMade`, `ARoomTakenOffItsSyncedListAfterTheRowWasChecked_IsNotAddedUnchecked` | a synced list's cloud add with no checked group is refused, directly and through the row's race - the partial reports it, the list is not written |
| `AListTheRowCreates_IsCheckedBeforeTheRoomIsAddedToIt` | a list the row creates is checked as the creation returned it, before the room is added |
| `AnAuthorisedServicer_MayAddARoomToAProtectedList_AndTheResultCarriesTheNote`, `SomeoneOutsideTheServicerGroup_IsRefusedBeforeAnyWrite` | the servicer exemption, and its note naming the rule and the authorising group; anyone else refused |
| `ThePage_PassesTheOperator_AndRecordsTheListsServicerNoteInTheAudit` | the page, driven for real: the operator reaches the list's check and the audit row's `protectedPrincipalServiced` carries the list's note |
| `ABulkRow_NeverServicesAProtectedList`; `ConferenceRoomBulkProcessorTests.AFinderRow_HandsTheRoomListCheckNoOperator` | off-circuit rows never service: refused and audited with no note, and the processor hands no operator |

**Existing tests changed** (fixtures only, as aud-a2c and aud-a2d did): the three room-service fixtures pass the harness's
protection services; every direct call passes `actingUser: null`; `TheSyncedList()` in the preflight tests, and the list in
`ConferenceRoomWriteIdentityTests.TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce`, carry the cleared group
the preflight hands the add. That test's comment says what changed: through the row its refusal is no longer reachable
even by the race (the cloud add is refused first), so it stays the add's own guard, called directly. No assertion changed.

**Mutations**, each at its call site, each applied by a script that refuses unless every old text occurs exactly once and
the md5 changes; rebuilt; the named test run; the file restored from the saved bytes with a fresh mtime; rebuilt; the
named test run again. The md5s are of the files as committed.

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named test | Mutated / restored |
| --- | --- | --- | --- | --- | --- |
| M1 | the preflight's check of a cloud list replaced by an allow | `ConferenceRoomService.cs` `3ab67aa6b221ae3c912a8427adf6a422` | `ee8587f33a4c3d7e652ecc75ef11541f` | `ACloudRoomListProtectedByItsAddress_IsRefusedBeforeAnyWrite` | FAIL / PASS |
| M2 | the preflight's check of a synced list's group replaced by an allow | same | `fb32d04f7a2321607c14b16d02d46f7c` | `ASyncedRoomListWhoseGroupIsAProtectedGroupTarget_IsRefusedBeforeAnyWrite` | FAIL / PASS |
| M3 | the cloud add's check replaced by an allow | same | `a0f8d8498a3defc9aa84297bc9df59e3` | `TheCloudAdd_ChecksTheListImmediatelyBeforeItWrites` | FAIL / PASS |
| M4 | the cloud add's check consulted after `Add-DistributionGroupMember` | same | `ac1854585df9d84b7936bf261a48c120` | same | FAIL / PASS |
| M5 | a synced list's cloud add with no checked group falls back to the list as Exchange reported it | same | `3bb37c36d040e4ee346c28a5295fb049` | `ASyncedListsCloudAdd_WithNoGroupCheckedBeforeIt_IsNotMade`, `ARoomTakenOffItsSyncedListAfterTheRowWasChecked_IsNotAddedUnchecked` | FAIL (both) / PASS (both) |
| M6 | the check of a list the row created skipped | same | `7d5d797a294d6f06af1022d6d45ef027` | `AListTheRowCreates_IsCheckedBeforeTheRoomIsAddedToIt` | FAIL / PASS |
| M7 | the on-prem add's check replaced by an allow | same | `d2c46a189d95d3231d5a3a299915f673` | `TheOnPremAdd_ChecksTheGroupItResolves_ImmediatelyBeforeItWrites` | FAIL / PASS |
| M8 | the on-prem add's check consulted after `Set-ADGroup` | same | `88d23a85c14c69ee3214d822680f5d82` | same | FAIL / PASS |
| M9 | no protected-principal service read as an allow | same | `b6e68b4c5701ca92f6f47e183865bd9b` | `WithoutTheProtectedPrincipalServices_ARoomListIsRefusedBeforeAnyWrite` | FAIL / PASS |
| M10 | the check reads only `IsProtected`, so a check that failed reads as not protected | same | `78a318002d083386fc8515fa64e8eb4b` | `AProtectionStoreThatCannotBeRead_RefusesBeforeAnyWrite` | FAIL / PASS |
| M11 | the operator not handed to the servicer grant | same | `55bfcb747d67c50ebf4c116dfffba312` | `AnAuthorisedServicer_MayAddARoomToAProtectedList_AndTheResultCarriesTheNote` | FAIL / PASS |
| M12 | the list's Entra object id not read | same | `611200296d4ca05ec0ea98db6eebcc4d` | `ACloudRoomListProtectedByItsEntraObjectId_IsRefusedBeforeAnyWrite` | FAIL / PASS |
| M13 | the group snapshot without its sAMAccountName | same | `bc812447c34bf240ac457d2c46aaf39f` | `ASyncedRoomListGroupMatchingAProtectedPattern_IsRefusedBeforeAnyWrite` | FAIL / PASS |
| M14 | the group snapshot without its objectGUID | same | `953d2eae56a08f5bd5db4c0d6715f281` | `AProtectedGroupTarget_IsMatchedByTheGroupsObjectGuid_NotItsName_AfterARenameOrMove` | FAIL / PASS |
| M15 | a group read back without a sAMAccountName not refused | same | `0cc3ba37b3f0a30e006a841f5e1186cc` | `ASyncedRoomListGroupReadBackWithoutASamAccountName_IsRefusedBeforeAnyWrite` | FAIL / PASS |
| M16 | the page records the room gate's note only | `ConferenceRooms.razor` `0ff410bff9f6dc1c0f27672d0595b2c2` | `c4221198bb9664936a05b2aae20600a6` | `ThePage_PassesTheOperator_AndRecordsTheListsServicerNoteInTheAudit` | FAIL / PASS |
| M17 | the page hands the list's check no operator | same | `214ffb0e1122e3ef4758ab59b1448cd0` | same | FAIL / PASS |

Seventeen mutations, eighteen named-test failures, every restored md5 equal to "before", every restored rebuild and
named-test run green. Not mutation-proven separately: the OU, AC8 and control tests (coverage of the same check), and
`AFinderRow_HandsTheRoomListCheckNoOperator`, a pin - the processor has no operator it could pass instead.

## Coder dispute

None. One correction to the Evidence: `PermissionValidator.BindWriteTargetAsync` (`:512`) binds the MAILBOX a permission
write lands on, not a group; Group Management is this finding's group-write precedent, and BindWriteTargetAsync the
precedent for binding the checked object to the write.

## Known gaps

**Source-level and fake-driven, not live.** Probing Active Directory or Exchange is forbidden without a direct owner go.
The stand-ins answer as the cmdlets document; whether this tenant's room lists carry what the check reads was not seen:

- *Assumption:* `Get-ADGroup -Identity <DN> -Server <domain>` answers `SamAccountName` and `Name` in its default property
  set (documented). If not, every synced-list row refuses ("without a sAMAccountName") - loudly, on the first dev check.
- *Assumption:* `Get-DistributionGroup` and `New-DistributionGroup` return `ExternalDirectoryObjectId` and
  `PrimarySmtpAddress` (documented). Absent, a cloud list is matched by what remains; nothing is refused for it.

**Owner call, taken fail-closed: Set-Place does not proceed when the room list is refused.** A list the check refuses
refuses the whole row before anything is written. If the owner would rather have the room's metadata applied and only the
list membership refused, that is a reversal: the preflight would skip the list and the add would refuse it, and the row
would come back partial.

**The structural limit for cloud lists.** A cloud-only room list has no on-premises object: Protected Group Targets
(objectGUID|DN), OU and pattern rules cannot match it, only a protected user row naming its address or Entra object id
(Constitution, Protected Principals). And the admin page's user-row input refuses an entry Active Directory does not hold
(`ProtectedPrincipalEntryValidator.Decide`, "cloud-only objects cannot be protected"), so a cloud list cannot be protected
through the page today; the check honours a stored row (a legacy import, a hand repair). Room lists the module creates are
cloud lists (owner, 2026-06-18).

**A synced list is bound to its group by address.** Exchange exposes no immutable attribute tying a synced list to its
on-prem group (aud-a2k), so the group is the one the forest search finds carrying the list's primary address. The on-prem
write is checked on the exact object it names by objectGUID; the cloud add to a synced list - which Exchange refuses for
a synchronized group, the write landing on-prem - is checked against the group the preflight cleared, by that binding.

**Who this refuses that was written before - operator-visible, refusing direction:**

- A row whose room list - its synced group, or a cloud list by address or Entra id - is protected: refused before
  anything is written, unless the operator holds `ProtectedServicer:ConferenceRooms` on the page; a bulk row always.
- A protected cloud list refuses even a row whose room is already on it: the preflight does not read a cloud list's
  membership (aud-a2h's read counts stand), and refusing is the safe direction. A protected synced list does not refuse a
  row Exchange shows already on it - nothing is written to the list, and aud-a2f made such a re-run need no Active
  Directory. In neither case is a write made without a check of the object it names.
- A protected synced group refuses the row even when Exchange shows the room off the list but the group already holds it
  (sync lag): the preflight checks the group right after resolving it.
- The race - the room taken off its synced list between the preflight and the add - now ends partial with the "not
  checked" refusal instead of completing through the on-prem fallback; a re-run resolves and checks the group.
- A synced group read back without a sAMAccountName refuses (real groups always carry one).
- A list the row creates is checked only once it exists, so a refusal there is partial - reachable only by a rule naming
  the new list's address beforehand.

**Dev acceptance check owed** (operator workflow, not automatable here): on dev, add a room list's on-prem group under
Protected Group Targets and apply a Room Finder row with that Building - refused, nothing written; repeat as a member of
the Conference Rooms servicer group - applied, the audit row's `protectedPrincipalServiced` naming the list's rule; and an
ordinary row on an unprotected list, unchanged.

**Not covered here:** the admin notification does not mention a servicer override, as it does not for the room gate's.

**Observed, not admitted (reported for admission, not fixed here - one finding per commit):**

- *Candidate, LOW:* `Services/ConferenceRoomService.cs:2770` (`ReadAdGroupInItsDomain`). The re-read in the group's own
  domain proves the objectGUID and DN the global catalog returned, not that the group still carries the list's address in
  `mail`. A catalog lagging an address move binds the list to the group that no longer carries it, and the on-prem add
  writes that group. Correctness, not authorization: the group written is the group checked.
- *Candidate, doc/UI drift:* `docs/ProjectConstitution.md:94` prescribes a protected user row for a cloud-only principal;
  `Services/ProtectedPrincipalEntryValidator.cs:90` refuses every entry Active Directory does not hold ("cloud-only objects
  cannot be protected"), so that row cannot be made through the admin page. Which one is right is the owner's.
- *Candidate, LOW (diagnostic):* `Services/ConferenceRoomProtectionGate.cs:149` tells every `Ambiguous` as "matches
  multiple AD users"; since aud-a2b/aud-a2c it also means Exchange and AD name different objects, or two objects hold the
  mailbox GUID.
- *Candidate, flaky test:* `ExchangeAdminWeb.Tests/SettingsStoreBusyTests.cs:128` bounds a save's wall-clock wait at
  9 s. It failed under host load here (17.6 s in a full parallel run, 10.3 s in a class run) and passed on reruns - 3/3
  with this change, 2/2 at `eb017102`. Untouched by this change; predicted to fail intermittently on a loaded host.

## Reviewer comments

Implemented under the owner's 2026-10-21 go, released by the 2026-10-23 evening ruling, by an Opus 5.5 implementation
agent on `fix/a2e`. Independent review pending (codex).

## Closeout

Fixed on branch, not merged, not pushed. Module-scoped: Conference Rooms 2.6.8 -> 2.7.0. No shared infrastructure
changed - `ProtectedPrincipalService`, `ProtectedPrincipalServicing` and `GroupManagementService.CombineNotes` are consumed
as they are - so this change alone does not move the base app version.

Gates at the branch tip, this session: `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` 0 errors, 19
warnings, none in a file this change touches; `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` 6714
passed, 0 failed, 0 skipped; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean;
`git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed. An earlier full Release run failed only
`SettingsStoreBusyTests`' wall-clock bound under host load (Known gaps), green on reruns.