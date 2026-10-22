# aud-a2f: A building row whose room list needs the on-prem fallback is refused only after Set-Place has written

**Severity**: MEDIUM - not a wrong-principal write, but a Conference Rooms partial mutation on a path
the aud-a2d record says fails closed before any write.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a0c2a0143143dd6e1`
**Commit**: `<pending>`
**Reviewer**: codex / gpt-5.5-dzs / xhigh over 3562cf08..7ae55b9a, 2026-10-21

## Evidence

Raised by the codex review of aud-a2d (verdict `.agents/review/a2d.result.txt` in the main
checkout, candidate 1). At `86300ae6`, `Services/ConferenceRoomService.cs`:

The only pre-write check of the Active Directory steps covers City/State/Country:

```
418        // Preflight the AD step BEFORE any mutation. Set-Place (Step 1) commits EXO-side
423        var adPreflightError = await PreflightSyncedAttributesViaAdAsync(room, roomEmail, city, state, countryOrRegion);
```

then the writes commit - Set-Place, City/State/Country, the timezone pair:

```
437        var placeResult = await RunAsync((ps, tracker) =>
440            var placeCmd = ps.AddCommand("Set-Place")
467        var adError = await SetSyncedAttributesViaAdAsync(room, roomEmail, city, state, countryOrRegion);
485                ps.AddCommand("Set-MailboxRegionalConfiguration")
491                ps.AddCommand("Set-MailboxCalendarConfiguration")
```

and only then is the room list even looked up:

```
513        if (!string.IsNullOrWhiteSpace(building))
515            var listResult = await AddToRoomListAsync(room, roomInfo.Email, roomEmail, building, city);
551        var (roomListName, exists, isLegacy, _) = await ResolveRoomListAsync(building, city);
```

A list synchronized from on-premises refuses the cloud add, which is classified as the on-prem
fallback (`:258-259`, `:633-635`), and the fallback's prerequisites are first tested there:

```
1466        if (room.DirectoryObjectGuid is not { } roomGuid)
1467            return NoDirectoryObject(roomEmail, "the room could not be added to the on-prem room list");
1469        var creds = await GetModuleCredentialsAsync($"Add-ADGroupMember on-prem room list for {roomEmail}");
1491                var roomError = ConfirmDirectoryObject(ps, roomGuid, roomEmail, credential);
1495                var (groupError, groupGuid) = ResolveAdGroupGuid(ps, roomListName, credential);
```

so the row comes back partial with everything before it committed:

```
527                result.Partial = true;
528                result.Message = $"Partial: room metadata was set, but adding the room to its room list failed: {listResult.Message} Re-run this row after fixing the cause.";
```

The same ordering has a second symptom. `ResolveRoomListAsync` runs through `RunPooledQueryAsync`,
which lets a terminating read failure propagate (`ExchangeServiceBase.cs:111-133`). At `:551` it
runs after Set-Place, City/State/Country and the timezone have committed, so a failed list read
escapes `SetRoomMetadataAndListAsync` as an exception and the page and the bulk processor record a
plain failure (`ConferenceRooms.razor:1084-1088`, `ConferenceRoomBulkProcessor.cs:149-153`) - not
a partial - for a room that was written.

The aud-a2d record claims the opposite (`aud-a2d.md`, Conference Rooms approach): "A cloud-only
room has no proven object: a row with City/State/Country, or one needing the on-prem list
fallback, is refused before any write and before a credential is fetched." The City/State/Country
half is true (`:1360-1361`); the list half was not.

## Predicted observable failure

Room Finder for a cloud-only room (the gate proved no on-premises object) with Building "San Jose",
whose list "San Jose Conference Rooms" is synchronized from on-premises. Set-Place writes
Building/Capacity/Floor/devices, the timezone and working hours are set, the cloud add is refused
as on-prem-mastered, the fallback refuses for want of an AD object, and the row reports PARTIAL.
Nothing the operator can do on this page completes it: a re-run rewrites the metadata and fails the
same way every time. A synced room whose AD credential is missing, whose AD object cannot be read,
or whose list has no (or two) on-prem groups is left half-configured the same way.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21);
codex's better approach, verbatim: "add a room-list preflight before `Set-Place` for building rows.
If the existing list requires on-prem fallback, validate `DirectoryObjectGuid`, credentials, group,
and member object up front; if that cannot be proven, refuse before Exchange metadata writes."

**Rule.** Whether the room can be added to its room list is settled before any write. A row that
cannot complete its list step writes nothing.

- **Resolve the list once, before any write.** For a row with a Building, the list is looked up
  (canonical name, then legacy - the order `ResolveRoomListAsync` has always used) before Set-Place,
  and Exchange's `IsDirSynced` is read from the `Get-DistributionGroup` result. The add uses that
  resolution; nothing looks the list up again after the writes. `ResolveRoomListAsync` keeps its
  signature and behaviour for the CSV preview and shares the lookup.
- **Preflight, after the City/State/Country preflight and before Set-Place:**
  - no such list: it is created cloud-side and the add is the cloud add - pass;
  - an existing list Exchange reports as not synchronized: the cloud add - pass;
  - an existing list Exchange reports as synchronized from on-premises: the fallback is what adds the
    room, so prove it without writing - a proven `DirectoryObjectGuid` (refused before any credential
    is fetched when there is none), the module AD credential, the room's AD object read by that
    objectGUID (`ConfirmDirectoryObject`), and the list's on-prem group resolving to exactly one
    object (`ResolveAdGroupGuid`);
  - an existing list whose sync state Exchange does not report: refuse - the preflight cannot prove
    the row completes (fail closed, Architectural Invariant 7: verify, do not assume).
  - A refusal is `Success = false`, `Partial = false`, one failed "Room list preflight" step, and
    nothing written - the shape of the existing City/State/Country preflight refusal (Known Failure
    Class 2: the per-row outcome is unchanged in kind).
- **The fallback runs only for a list the preflight proved it for.** A cloud add refused as
  on-prem-mastered on a list Exchange reported as NOT synchronized is surfaced rather than added
  on-prem: nothing proved that add could complete, and an on-prem group found by that name would not
  be the list Exchange holds.
- **Writes unchanged, in the same order:** Set-Place, Set-ADUser (City/State/Country), the timezone
  pair, New-DistributionGroup (new list), Add-DistributionGroupMember, Add-ADGroupMember (fallback).

**Assumption (live-only, fails closed):** Exchange Online's `Get-DistributionGroup` reports
`IsDirSynced` on a room list, True exactly for a list synchronized from on-premises - the condition
under which it refuses `Add-DistributionGroupMember` with the on-prem-mastered message. If it reports
nothing, every building row on an existing list is refused before writing, with a message naming the
cause; it cannot write partially.

**Tests** (`ExchangeWriteHarness` idiom; the stand-in `Get-DistributionGroup` reports `IsDirSynced`
per list name - False by default, True, or absent - and the stand-in `Get-ADGroup` returns a
configurable set of groups):

- the codex scenario: cloud-only room, Building, synced list - refused with no write at all (no
  Set-Place, no timezone) and no credential requested;
- a synced list whose fallback cannot be proven (no credential; room object unreadable; no on-prem
  group; two on-prem groups) - refused with no write;
- a list that does not report its sync state - refused with no write;
- a provable synced-list row writes exactly Set-Place, Set-ADUser, the timezone pair,
  Add-DistributionGroupMember, Add-ADGroupMember, in that order;
- a cloud-only room on a cloud list is still written (the preflight does not over-refuse);
- a cloud list refusing the add as on-prem-mastered is not added on-prem;
- the list is read once, and before Set-Place.

**Mutations, at the call sites:** the preflight moved below Set-Place; the no-AD-object refusal
dropped; each of the credential, room-object and group checks dropped; an unreported sync state read
as cloud; the list re-resolved after the writes; the fallback gate dropped; every existing list
treated as synced; `IsDirSynced` not read. A named test fails for each.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs` (new),
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` (the fallback test's list is now one
Exchange reports as synchronized), this record, a correction pointer in `aud-a2d.md`, and an
amendment pointer in `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` (its "try cloud, fall back"
approach now has a precondition). No page change.

## Approach (as implemented)

- `FindRoomList` (static, one pooled session): `Get-DistributionGroup` for the canonical name, then
  the legacy name, exactly as before, and `ReadIsDirSynced` of the list found - a bool, a wrapped
  bool or a "True"/"False" string; anything else, including an absent property, is null.
  `ResolveRoomListAsync` (the CSV preview) keeps its signature and call order - stray city list,
  canonical, legacy - and now delegates the last two. `RoomListTarget` carries name, exists, legacy
  and the sync state; `TakesOnPremFallback` is "exists and Exchange said synchronized".
- `SetRoomMetadataAndListAsync`: after the City/State/Country preflight and before Set-Place, a row
  with a Building resolves its list (`ResolveRoomListTargetAsync`, read-only, retry-safe) and runs
  `PreflightRoomListAsync`. A refusal returns `Success = false`, `Partial = false`, the message, and
  one failed "Room list preflight" step. Step 4 hands the add that same `RoomListTarget`;
  `AddToRoomListAsync` no longer resolves anything (its dead "Could not determine room list name"
  branch went with the lookup - the name was never null).
- `PreflightRoomListAsync`: no list, or a cloud list - pass. Sync state unreported - refuse. A
  synced list - refuse without an AD object (before any credential is requested), without the
  credential, when `ConfirmDirectoryObject` cannot read exactly the proven object, or when
  `ResolveAdGroupGuid` does not find exactly one group. These are the helpers the add itself calls,
  so the preflight and the add read the same objects the same way. Its own messages end "Nothing was
  written."
- The `OnPremFallback` branch adds on-prem only when `list.TakesOnPremFallback`; otherwise it
  surfaces Exchange's refusal with "Exchange reported room list '<name>' as not synchronized from
  on-premises, so the room was not added on-prem instead." (still partial - the earlier writes did
  commit; nothing on-prem is looked up or written).
- A stale comment on `ResolveAdGroupGuid` named `ResolveAdObjectGuid`, removed by aud-a2d; it now
  names `ConfirmDirectoryObject` and says the preflight shares it.

## Guard proof

| Test | What it pins |
| --- | --- |
| `ConferenceRoomRoomListPreflightTests.ACloudOnlyRoom_ForARoomListSyncedFromOnPremises_IsRefusedBeforeAnyWrite` | the codex scenario (the list also refuses the cloud add, as a synced list does): refused, not partial, the message names the list, NO write recorded - no Set-Place, no timezone pair - and no credential requested |
| `ASyncedRoomList_WhoseOnPremAddCannotBeProven_IsRefusedBeforeAnyWrite` (no credential; room object unreadable; no on-prem group; two on-prem groups) | each missing prerequisite of the on-prem add refuses the row with no write |
| `ARoomListThatDoesNotSayWhetherItIsSynced_IsRefusedBeforeAnyWrite` | an absent `IsDirSynced` refuses with no write |
| `AProvableRow_ForASyncedRoomList_WritesInTheSameOrderAsBefore` | writes are exactly Set-Place, Set-ADUser, Set-MailboxRegionalConfiguration, Set-MailboxCalendarConfiguration, Add-DistributionGroupMember, Add-ADGroupMember - passes on the unfixed file too, which is the point: the order did not change |
| `ACloudOnlyRoom_ForACloudRoomList_IsStillWritten` | no over-refusal: a cloud-only room on a cloud list writes Set-Place, the timezone pair and the cloud add; no credential |
| `ACloudRoomList_RefusingTheAddAsOnPremMastered_IsNotAddedOnPrem` | the fallback serves only a list the preflight proved: partial, no `Get-ADGroup`, no `Add-ADGroupMember` |
| `TheRoomList_IsReadOnce_BeforeAnythingIsWritten` | exactly one `Get-DistributionGroup`, before Set-Place (City given, so the old apply path would also have read the stray city list) |
| `ConferenceRoomWriteIdentityTests.TheOnPremRoomListFallback_AddsTheProvenDirectoryObject` | existing; fixture only - its list now reports `IsDirSynced` True, the only kind the fallback serves; assertion unchanged |

**Revert the fix.** `Services/ConferenceRoomService.cs` replaced whole by HEAD's
(`91926ed347c6a362349979d1a0ea0b68`, the file aud-a2d committed), the rest of the tree as fixed:
8 of the 11 new cases FAIL - the codex scenario, all four unprovable-prerequisite cases, the
unreported sync state, the cloud list falling back, the list read after the writes - and the
provable-order case, the cloud-list case and the existing fallback test pass (13 of 21 in the two
classes). Restored to `052721e3b1f38a2d046369566fd97546`, touched; rebuild and 21/21 green.

**Mutations** (`scratchpad/a2fg/round_a2f.py` in the session scratchpad; each edit applied only if
its old text occurs exactly once and the md5 changes; test project rebuilt; the named test run; the
file restored from the saved bytes and touched; md5 re-checked). File
`Services/ConferenceRoomService.cs`, md5 before = restored = `052721e3b1f38a2d046369566fd97546` for
every row - the file as committed; no edit followed the round. Test names are in
`ConferenceRoomRoomListPreflightTests`.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| F1 | the room-list preflight moved below Set-Place | `ad6dea6cda122c710230f4d17469937c` | `ACloudOnlyRoom_ForARoomListSyncedFromOnPremises_IsRefusedBeforeAnyWrite` | FAIL (first attempt hit a CS2012 file lock from an overlapping build of mine, not a compile error; re-run alone, same mutated md5) |
| F2 | the preflight call deleted (the list still resolved) | `6898fa897e784a7136527f7f9ce6bf6d` | same | FAIL |
| F3 | synced list: the no-AD-object refusal passes instead | `4688d44a5a1c498ca09f83d226a28875` | same | FAIL |
| F4 | synced list: a missing credential passes | `d9834aa0881f02edf5a81f9dfe2830db` | `ASyncedRoomList_WhoseOnPremAddCannotBeProven_IsRefusedBeforeAnyWrite` | FAIL ("no credential") |
| F5 | synced list: the room object is not read | `0e07dccc4dd94788e28d42c60166b1ff` | same | FAIL ("room object unreadable") |
| F6 | synced list: the on-prem group is not resolved | `270ec484354c4df18f0d494c93a77d93` | same | FAIL ("no on-prem group", "two on-prem groups") |
| F7 | an unreported sync state read as a cloud list | `29edf01d1520d49f7357d4171db6c71b` | `ARoomListThatDoesNotSayWhetherItIsSynced_IsRefusedBeforeAnyWrite` | FAIL |
| F8 | the add re-resolves the list after the writes (the old shape) | `f2252f7b9bd28034deed2577116990dc` | `TheRoomList_IsReadOnce_BeforeAnythingIsWritten` | FAIL |
| F9 | the fallback gate dropped | `e583a37dd0985154718b039292339707` | `ACloudRoomList_RefusingTheAddAsOnPremMastered_IsNotAddedOnPrem` | FAIL |
| F10 | every existing list treated as synchronized | `48e299b488e92c82a819a92a5c893512` | `ACloudOnlyRoom_ForACloudRoomList_IsStillWritten` | FAIL |
| F11 | `IsDirSynced` not read for the canonical list (taken as cloud) | `88666e309a542fd77f028c245772ad16` | `ACloudOnlyRoom_ForARoomListSyncedFromOnPremises_IsRefusedBeforeAnyWrite` | FAIL |

11 mutations, 11 named-test failures, every restored md5 equal to "before"; final rebuild and the
named tests green.

## Known gaps

- *Assumption, live-only:* Exchange Online reports `IsDirSynced` on `Get-DistributionGroup`, True
  exactly for a list synchronized from on-premises. Not probed (no live calls without an owner go).
  Wrong in the absent direction, every building row on an existing list refuses before writing with
  "Exchange did not report whether room list ... is synchronized"; wrong in value, a synced list read
  as cloud is tried in the cloud, refused, and surfaced as partial with no on-prem add (the old
  partial shape, without the fallback) - neither writes anything the old code would not have.
- *Inherent two-system residual, unchanged in kind:* the add re-reads what the preflight read (room
  object, group by name) and can still fail after the earlier writes - the group renamed or deleted
  in between, the credential revoked, or the account lacking write-members rights (the plan's own
  "VERIFY IN DEV" item, which no read can prove). Partial, as before.
- A list created by someone else between the preflight ("not found") and the add makes
  `New-DistributionGroup` fail after the writes - surfaced, partial; inherent. Pre-existing and
  unchanged: the lookup runs with `-ErrorAction SilentlyContinue`, so a list name Exchange finds
  ambiguous reads as "not found" too, and its creation then fails the same way.
- The legacy-name branch of `FindRoomList` is not driven by a test: the stand-in answers every
  `Get-DistributionGroup`, so the canonical name always exists. Its order is the preview's, unchanged.
- Cost: a synced room with City/State/Country on a synced list now opens two AD runspaces and asks
  for the credential twice before writing (one per preflight). Correctness, not performance, was the
  brief.

**Operator-visible changes** (all in the refusing direction, all before any write):

- a cloud-only room with a Building whose list is synchronized from on-premises is refused before
  Set-Place - it used to be written and reported partial on every run;
- a synced room on a synced list with no AD credential, an unreadable AD object, or no (or several)
  on-prem groups for the list is refused before Set-Place - partial before - unless it is already a
  member of that list, when nothing on-prem is needed and the row is written as before (the repair
  below);
- a building row on an existing list whose sync state Exchange does not report is refused (new);
- a cloud list that refuses the add as on-prem-mastered is no longer added to an on-prem group of the
  same name; the refusal is surfaced (still partial);
- a failed room-list read now fails the row before anything is written; it used to throw after the
  writes and be recorded as a plain failure;
- the apply no longer reads the stray city-named list, whose answer it discarded (the preview still
  reads it).

**Dev acceptance owed** (operator workflow, not automatable here): on dev, Room Finder for (1) a
synced room on an existing synced list (the "San Jose Conference Rooms" shape) - succeeds via the
on-prem add; (2) a cloud-only room with a Building whose list is synced - refused, and the room's
Place metadata unchanged; (3) a room on a cloud list - the cloud add, unchanged.

**Page:** unchanged (1758 lines); no `ClickGateRegistry` entry moved.

## Repair before review: a room already on its synced list needs nothing on-prem

Found by the implementer after the fix commit, before any review: the fix introduced a regression.

**Evidence** (at `f7a364ec`, `Services/ConferenceRoomService.cs`). The preflight demands the
on-prem add's prerequisites for every synced list:

```
1545        if (!list.TakesOnPremFallback)
1546            return null;
1548        if (room.DirectoryObjectGuid is not { } roomGuid)
1551        var creds = await GetModuleCredentialsAsync($"Add-ADGroupMember preflight for {roomEmail}");
1552        if (creds is null)
1553            return $"On-prem AD credential is not configured for Conference Rooms ...
```

but the add writes nothing at all for a room already on the list:

```
646            var alreadyMember = members.Any(m =>
649            if (!alreadyMember)
661                result.Steps.Add(new RoomOperationStep { Stage = $"Already in '{roomListName}'", Success = true });
```

**Predicted observable failure.** A Room Finder re-run (Building/Capacity only) for a synced room
already on its synced list, with the AD credential unset, the AD object unreadable or the list's
on-prem group ambiguous: at `86300ae6` it succeeded - nothing on-prem was touched - and after the fix
it is refused before Set-Place.

**Plan.** In the synced-list branch of the preflight, before any AD check, ask Exchange whether the
room is already a member, with the add's own test - the primary address Exchange returned for the
validated room, never the typed string (aud-a2d) - factored into one `IsListMember` so the preflight
and the add cannot disagree. A member needs nothing on-prem: the preflight passes and the add is the
no-op it always was. Not a member: unchanged. Read-only, before any write.

**Test:** `ASyncedRoom_AlreadyOnItsSyncedRoomList_NeedsNothingOnPrem` - synced list, the room among
its members, no credential configured, typed by a different string than its primary address: the
row succeeds, writes exactly Set-Place and the timezone pair, and asks for no credential.
**Mutations:** the membership check deleted; compared with the typed string; always "a member".

**As implemented.** `IsListMember(members, roomPrimary)` is the one membership test; the add's
already-member check calls it (same comparison as before, moved, not changed). `IsOnRoomListAsync`
reads `Get-DistributionGroupMember` for the list in a pooled read-only query and asks it.
`PreflightRoomListAsync` takes `roomPrimary` (`roomInfo.Email`, the address the validated read
returned) and, for a synced list, returns null for a member before any AD check or credential
request. A failed membership read returns no members, so it falls through to the AD checks - the
stricter path.

**Proof.** Service file before (= restored) `104e8b5b60546277c57d77aed26e681a`, the file as
committed (`scratchpad/a2fg/round_repair.py`, same runner):

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| revert | the service file back to `f7a364ec` (before the repair) | `c807ad832294ac3f3ae782413163b315` | `ASyncedRoom_AlreadyOnItsSyncedRoomList_NeedsNothingOnPrem` | FAIL |
| RP1 | the membership check deleted | `fcb5e3807f3b7fd2a6f0f15471efcc53` | same | FAIL |
| RP2 | membership compared with the typed string | `7d551d2d0c5d368b09438f9e7b4d18d0` | same | FAIL |
| RP3 | every room read as already a member | `210db25f73b5eed2881aa0a8a2fc2baf` | `ACloudOnlyRoom_ForARoomListSyncedFromOnPremises_IsRefusedBeforeAnyWrite` | FAIL |
| RP4 | the add's own membership test compared with the typed string (the shared helper's other caller) | `44b1fb3adb6570e7028be37a6a2a3d51` | `ConferenceRoomWriteIdentityTests.ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain` | FAIL |

Every restored md5 equal to "before"; final rebuild and the three named tests green.

**Residual added:** membership read in the preflight and again in the add. A room taken off its
synced list in between, with Active Directory unprovable, reaches the on-prem add after the writes
and fails there - partial, the inherent two-system shape; nothing is written to a wrong object.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a0c2a0143143dd6e1`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed - in two commits, the fix and the repair above (found before
any review; history is not rewritten). Module-scoped behaviour change: the Conference Rooms module
version bump is due at merge; no shared code changed by this finding, so no base app bump from it.
