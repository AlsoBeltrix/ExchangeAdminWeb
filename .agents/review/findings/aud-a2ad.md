# aud-a2ad: remove every protected-principal check the weekend added to Conference Rooms, and restore room-list creation

**Severity**: HIGH-class by provenance - this row reverses weekend work under an owner ruling and must leave the module's
pre-weekend behaviour intact while keeping the weekend's genuine fixes.
**Owner ruling** (2026-10-26, `.agents/decisions.md`, the "later rulings" entry, item 2): "protected principals or groups
are, ONCE AGAIN, OUT OF SCOPE FOR THIS MODULE. DO NOT BRING ME ANYTHING ABOUT PROTECTED PRINCIPALS RELATED ISSUES.
REMOVE ALL OF THAT ILLEGALLY ADDED BULLSHIT AND LIMIT TO FIXING FUCKING ISSUES." And on room-list creation:
"functionality CANNOT change. this is a top tier module and the stakeholders fought hard to get it where it was."
There is NO scoped exception and NO check before the add (the earlier shape of this record is superseded).
**Status**: Verified on branch; awaiting merge - UNREVIEWED until the single final codex-commercial review (approved by
owner ruling; the step 2 slice of the close-out plan in `.agents/state.md`)
**Branch**: `fix/a2ad` (worktree `D:\source\ExchangeAdminWeb-wt\a2ad`, from master at `20e6b507`)
**Commit**: `c22cecc5` (code, tests, docs, catalog); `86fdbde9` (this record); aud-a2z reinstated, with this record's
update, in the commit after `86fdbde9`
**Reviewer**: owner ruling; the one codex-commercial review at the end of the stream

## What goes (added since `eb017102`, the pre-weekend master)

- **aud-a2e** (`ebd66b91`): the Protected Group Targets check on the room list a Room Finder row writes into -
  `CheckRoomListTarget`, `RoomListTargetRefusal`, the missing-list and synced-list "cannot be checked" refusals, the
  `ProtectedPrincipalService` / `ProtectedPrincipalServicerService` constructor parameters and fields on
  `ConferenceRoomService`, the `actingUser` threading added for the servicer grant on the list, the list-as-write-target
  identity resolution (`ResolvedDirectoryPrincipal` for the list, its Entra object id read), the bulk processor and
  `IConferenceRoomBulkOperations` changes that carry it, the page changes in `Components/Pages/ConferenceRooms.razor`
  that call it, and the tests that pin it (`ConferenceRoomListWriteTargetTests`, `ConferenceRoomRoomListPreflightTests`,
  the a2e parts of `ConferenceRoomBulkProcessorTests`, `ConferenceRoomDirectoryAuditTests`,
  `ConferenceRoomWriteIdentityTests`, `ExchangeWriteHarness`). The doc edits a2e made to
  `docs/ProtectedGroupWriteTarget-Plan.md` and `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` go back to their
  `eb017102` text.
- **aud-a2ab** (`c38defa0`): the refusal of a Building whose room list does not exist. Creation comes back: the module
  creates the building's room list when Exchange affirmatively reports none, exactly as at `eb017102`, then adds the room.
  kd-68 (the refusal's audit fields) is superseded by this restore - close it so.
- **kd-66** (`5798e45e`): the preview goes back to marking a missing room list "new" (it will be created); the
  `ConferenceRoomMissingListPreviewTests` assertion that it says "refused" is reversed to assert "new", or removed.

## What stays (genuine fixes, by name)

- **aud-a2ac** (`fefb3e6a`): only Exchange's affirmative "no such object" means a building has no room list; an
  ambiguous name or a failed lookup refuses the row before any write. Creation proceeds ONLY after a confirmed absence.
- **aud-a2aa** (`26e8cd9b`): the neutral ambiguity message.
- **aud-a2z** (`170b182f`): the synced list's on-prem group, re-read in its own domain, must still carry the list's
  address. Moved here from "What goes" on 2026-10-27 by the coordinator's correction, under the owner's mandate (find and
  fix bugs; remove no intended functionality). It is a correctness check, not a protection check: a global catalog behind
  an address move answers the group the address has left, its objectGUID and DN still check out, and without it the room
  is written into a group that is no longer the list's (aud-a2z's own record: "LOW - correctness only"). It was first
  removed here in `c22cecc5` because this brief had it feeding a2e's identity; it is reinstated on the pre-weekend
  resolver in the commit after `86fdbde9`.
- **kd-52, kd-27, kd-5, kd-32** job-control audit and re-authorization; **kd-29, aud-a2y** refusal rows and
  "nothing was written" wording; **kd-28** room type by name; **kd-7** blank-ticket refusal on the four writes.
- **The July 2026 room-mailbox protection gate** (`Services/ConferenceRoomProtectionGate.cs`, `2a97d097`) is
  pre-weekend, decided behaviour: not touched.

## Approach (the implementing agent writes its plan here before code, then executes)

- Start from `git diff eb017102 master -- Services/ConferenceRoomService.cs Components/Pages/ConferenceRooms.razor
  Services/Jobs/ConferenceRoomBulkProcessor.cs Services/Jobs/IConferenceRoomBulkOperations.cs Models/ConferenceRoomModels.cs`
  and classify every hunk as a2e/a2z/a2ab/kd-66 (goes) or one of the keeps (stays). Record the classification here.
- Remove by hand, hunk by hunk; do not `git checkout eb017102 --` any file, because every file also carries keeps.
- Prove the restore: a test that a confirmed-missing list is created and the room added (the pre-weekend
  `ConferenceRoomWriteIdentityTests` shape), and that no protected-principal service is consulted on the list path
  (the stand-in records no call). Mutation: creation removed again - the restore test must fail.
- Re-anchor `ClickGateRegistry` / `ProgressRegistry` entries for `ConferenceRooms.razor` per
  `.agents/playbooks/clickgate-reanchor.md` if line counts move.
- Bump the Conference Rooms module version once in `Modules/ModuleCatalog.cs`; replace the a2e/a2z/a2ab/kd-66 note
  lines with one note naming this row and the ruling.
- Gates: Release build (0 errors), full `dotnet test ExchangeAdminWeb.slnx`, `dotnet format --verify-no-changes`,
  `git diff --check HEAD`, `tools/Test-AsciiOnly.ps1`. ONE compile at a time on this host.

### Hunk classification (written before the code, against `git diff eb017102 20e6b507`)

Read from `git show` of each of the four commits and of every other weekend commit on the same files. "Goes" is removed
by hand; "stays" is not touched. A cross-check was built in the session scratchpad only (3-way reverts of `170b182f`,
`c38defa0`, `ebd66b91` and `5798e45e` from HEAD's text with `git merge-file`, never in a worktree): its two conflicts were
exactly the two places a keep sits beside a go (the a2ac resolution try/catch beside the a2e preflight tuple; kd-28's
`TypesApplied` beside a2e's `ActingUsers`), and with those resolved it is pre-weekend plus the keeps. The hand edit is
compared with it before the commit.

`Services/ConferenceRoomService.cs`
- Goes, a2e: `using System.Security.Claims`; the `_protectedPrincipals`/`_servicers` fields and the two optional
  constructor parameters; the `RoomListTarget` parameters `ExternalDirectoryObjectId` and `DirectoryGroup` and their doc
  lines; the `ReadExternalDirectoryObjectId(...)` argument on FindRoomList's two returns (the lines around them are
  a2ac's and stay) and the helper itself; the whole "room list as a write target" block (`CloudListTarget`,
  `CheckRoomListTarget`, `RoomListTargetRefusal`, `RoomListNotCheckedRefusal`); `actingUser` on
  `SetRoomMetadataAndListAsync` and its doc; the preflight's tuple, `roomList = checkedList` and `RoomListServicedNote`
  in the row; `actingUser` and the serviced-note merge at the row's add; in `AddToRoomListAsync` the `actingUser`
  parameter, `listRefusal`/`servicedNote`/`createdTarget`, the check before the cloud add and the refusal handling after
  `RunAsync`; `PreflightRoomListAsync` back to `Task<string?>` with no `actingUser`, its cloud-list check, its on-prem
  group check and `list with { DirectoryGroup = group }`; `AddToRoomListViaAdAsync` back to its three-value result and
  `(room, roomEmail, roomListName, listAddress)` parameters, without its pre-write check and serviced notes (and the a2u
  comment back to its own words); `ResolveAdGroupGuid` and `ReadAdGroupInItsDomain` back to `(error, objectGuid,
  distinguishedName)`, without the sAMAccountName refusal and the `ResolvedDirectoryPrincipal` snapshot.
- Stays, aud-a2z (classified "goes" here first and removed in `c22cecc5`; reinstated after the 2026-10-27 correction -
  see What stays): `-Properties mail` on the in-domain re-read and the mail comparison after it, master's text. On the
  pre-weekend resolver the one adaptation is that `ResolveAdGroupGuid` hands `ReadAdGroupInItsDomain` the list's address
  as `listAddress` - the parameter a2e had added and the comparison reads - and the refusal returns the three-value shape.
- Goes, a2ab: `RoomListDoesNotExistRefusal` and the preflight's refusal of a list that does not exist - back to
  `if (!list.Exists) return null;`, so the add creates it.
- Stays: aud-a2ac (FindRoomList's comment and `ReadRoomList` calls, `ReadRoomList`, `RoomListNotResolvedRefusal`, the
  resolution try/catch in the row - the rule that only an affirmative "no such object" is absence and that anything
  else refuses before any write); aud-a2y (1) (SetRoomTypeAsync's room read logged and rethrown); kd-7/kd-24/kd-23
  (`TicketRefusalStep`, `WithRefusal`, `RequireTicket`).

`Models/ConferenceRoomModels.cs` - goes: a2e's `RoomListServicedNote`. Stays: kd-28's `RoomTypeNames`.

`Services/Jobs/IConferenceRoomBulkOperations.cs` - goes: all of it is a2e (the using, the remarks paragraph,
`actingUser`); the file returns to its pre-weekend text.

`Services/Jobs/ConferenceRoomBulkProcessor.cs` - goes: a2e's comment and `actingUser: null` in `ApplyFinderRowAsync`.
Stays: kd-23's ticket refusal; aud-a2y/kd-28's type parse and "Nothing was written."

`Components/Pages/ConferenceRooms.razor` - goes: kd-66's badge (back to `badge bg-info` "new") and its warning, with the
legacy warning unfolded back onto its two lines; a2e's `await CurrentUserAsync()` argument and
`GroupManagementService.CombineNotes(serviced, r.RoomListServicedNote)` (back to `serviced`). Stays: kd-5/kd-27/kd-32
(job controls), kd-7/kd-24 (`RequireTicket`), kd-52/kd-29 (`AuditRefusal`, refusal rows), kd-28, aud-a2y. Both kd-66
edits were in place (-1/+1), so the restore is too: 1758 lines before and after, and no `ClickGateRegistry` or
`ProgressRegistry` entry moves.

`Program.cs` - nothing: a2e registered nothing; its two weekend hunks are aud-a67 (10) and aud-a29g.
`Services/ConferenceRoomProtectionGate.cs` - nothing: the July gate, with aud-a2aa's message, is not touched.
`Modules/ModuleCatalog.cs` - the Conference Rooms notes for 2.7.0 (a2e), 2.14.0 (a2ab) and 2.18.0 (kd-66) are replaced by
one 2.19.1 note naming this row and the ruling; 2.19.0 -> 2.19.1. The 2.16.0 (a2z) note stays: removed in `c22cecc5`,
restored verbatim with a2z, and the 2.19.1 note says a2z's re-check stays. No other module's entry.
Docs - `docs/ProtectedGroupWriteTarget-Plan.md` and `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md`: only `ebd66b91`
touched either since `eb017102`, so both are restored whole to their `eb017102` text.

Tests
- Deleted: `ConferenceRoomListWriteTargetTests.cs` (a2e, with a2ab's three tests).
- Kept: `ConferenceRoomListGroupAddressTests.cs` (a2z) - deleted in `c22cecc5`, restored from its `20e6b507` text with
  only its four a2e touch-points taken out (the two protection services in its constructor, `actingUser` twice, and the
  cleared group on its direct-call list); every assertion is as it was.
- Reversed: `ConferenceRoomMissingListPreviewTests.cs` (kd-66) now asserts the "new" badge and no refusal warning.
- `ExchangeWriteHarness.cs` - goes, a2e: `CreatedListAddress`, `ListExternalIds`, `ListMembersClearedAfterSetPlace`,
  `DirectoryGroupSamAccountNames`, `DirectoryGroupAsChecked`, `ProtectedPrincipals`, `Servicers`, `HarnessEnvironment`,
  `HarnessDelinea`, their runspace variables and stand-in lines, `Get-StandInGroupSam`. Stays: aud-a2ac (`MissingLists` as
  ObjectNotFound, `AmbiguousLists`, `ListsAnsweringTwice`), aud-a70 (14) (`OnPremTransportFailures`), and aud-a2z
  (`DirectoryGroupMailInItsDomain`, its runspace variable and the stand-in's mail answer when asked for - removed in
  `c22cecc5`, restored as master has them).
- `ConferenceRoomWriteIdentityTests.cs` - goes: a2e's constructor arguments, `actingUser` arguments and the a2u test's
  cleared-group list; a2ab's `NoListYet` and its two direct-call tests, restored to their pre-weekend row shape
  (`AListTheRowCreates_IsNamedByTheGuidTheCreationReturned`, `AListCreatedWithoutAGuid_IsNotAddedToByItsName`). Stays:
  kd-37's cancellation token, aud-a2y (1)'s test. Added: the list path consults no protected-principal service.
- `ConferenceRoomRoomListPreflightTests.cs`, `ConferenceRoomDirectoryAuditTests.cs` - goes: a2e's constructor arguments,
  `actingUser` arguments, `TheSyncedList`'s cleared group. Stays: kd-37.
- `ConferenceRoomBulkProcessorTests.cs` - goes: a2e's `ActingUsers`, the stub's `actingUser`,
  `AFinderRow_HandsTheRoomListCheckNoOperator`. Stays: kd-28's `TypesApplied` and the aud-a2y/kd-28 tests.
- a2e's signature carried into keep files written after it - the a2e part goes, the tests stay:
  `ConferenceRoomListLookupTests.cs` (aud-a2ac: constructor arguments, `actingUser`; and
  `BothNamesConfirmedNotFound_IsABuildingWithNoRoomList`, which asserted a2ab's refusal, now asserts the list is created and
  the room added), `ConferenceRoomBulkTicketTests.cs` (kd-23: the stub's `actingUser`), `ConferenceRoomPageTests.cs`
  (aud-a2y: constructor arguments).

## Guard proof

**Cross-check.** After the hand edit, every file with a mechanical reference is byte-identical to it:
`Services/ConferenceRoomService.cs`, `Components/Pages/ConferenceRooms.razor`, `Models/ConferenceRoomModels.cs`,
`Services/Jobs/ConferenceRoomBulkProcessor.cs`, `Services/Jobs/IConferenceRoomBulkOperations.cs` (which is also its
`eb017102` text), `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, and `ConferenceRoomWriteIdentityTests.cs` (before the
new test below was added), `ConferenceRoomRoomListPreflightTests.cs`, `ConferenceRoomDirectoryAuditTests.cs`,
`ConferenceRoomBulkProcessorTests.cs`. The service has no `ClaimsPrincipal`, `ProtectedPrincipalService`,
`ProtectedPrincipalServicerService` or Protected Group Targets reference left, and nothing in the repo names a removed
member. The page is 1758 lines before and after, so the line-keyed registries pass unchanged. Since aud-a2z's
reinstatement the service and the harness are that reference plus a2z's lines, which match master's text except for the
refusal's return shape and the `listAddress` hand-over described above.

**The restore tests.**
- `ConferenceRoomWriteIdentityTests.AListTheRowCreates_IsNamedByTheGuidTheCreationReturned` and
  `AListCreatedWithoutAGuid_IsNotAddedToByItsName` - the pre-weekend row-shape tests, back to their `eb017102` text: a
  building both of whose list names Exchange reports missing (the stand-in's ObjectNotFound, aud-a2ac's one absence) has
  the list created and the room added by the Guid the creation returned; a creation that returns no Guid fails the row
  partial and adds nothing by name.
- `ConferenceRoomListLookupTests.BothNamesConfirmedNotFound_IsABuildingWithNoRoomList_SoTheListIsCreatedAndTheRoomAdded`
  - reversed from a2ab's refusal: one `New-DistributionGroup` (the canonical name, `-RoomList`), the add naming the
  created Guid and the validated room, the row a success. Its neighbours, kept, assert that an ambiguous or failed lookup
  refuses with no `New-DistributionGroup` at all - creation follows only Exchange's affirmative "no such object".
- `ConferenceRoomWriteIdentityTests.TheRoomListPath_ConsultsNoProtectedPrincipalService_WhenTheContainerHoldsOne` - new.
  The production `ConferenceRoomService` is resolved from a `ServiceCollection` that registers it as `Program.cs` does
  (`AddScoped`) and also holds a recording `ProtectedPrincipalService` (every virtual member recorded, each answering
  protected or failed) and a real `ProtectedPrincipalServicerService`, so a constructor asking for either would be handed
  them. The missing list is created, the room added, and the stand-in records no call.
- `ConferenceRoomMissingListPreviewTests` - reversed: the missing list's badge is `bg-info` "new", and no warning says
  the row will be refused.

**Mutation log.** Before: `Services/ConferenceRoomService.cs` md5 `55a02807987cbf376229e2903bc2f146`,
`Components/Pages/ConferenceRooms.razor` md5 `a93ab6309a7f161c7f75ca3c3c0c0b88`. Each mutation was made on the worktree
file, built and run (the Conference Rooms tests, 442; the page tests read the page at run time), and restored by copying
back a pre-mutation copy, re-hashed.
- M1, creation removed in a2ab's shape - `PreflightRoomListAsync`'s `if (!list.Exists) return null;` made to return a
  refusal (md5 `c46966e7c09dce84b7285ece9aacb1a8`): 4 failed, 438 passed - exactly `BothNamesConfirmedNotFound...`,
  `AListTheRowCreates...`, `AListCreatedWithoutAGuid...` and `TheRoomListPath_ConsultsNo...`. Restored:
  `55a02807987cbf376229e2903bc2f146`.
- M2, creation removed in the add - `AddToRoomListAsync`'s `New-DistributionGroup` block deleted (md5
  `0ff1c6eec12871f3856e612d530a2272`): the same 4 failed, 438 passed. Restored: `55a02807987cbf376229e2903bc2f146`.
- M3, a consult put back - an optional `ProtectedPrincipalService` constructor parameter, read once before the cloud add
  with its answer ignored (md5 `7b283efaf2aaa732a83a81af3b14235c`): 1 failed, 441 passed - `TheRoomListPath_ConsultsNo...`
  alone, "Assert.Empty() Failure: Collection was not empty". The row still succeeded; only the stand-in's record caught
  it. Restored: `55a02807987cbf376229e2903bc2f146`.
- M4, kd-66's badge put back (page md5 `a7cc4a66348a719af59e5035a2b625e8`, 1758 lines): `AMissingRoomList_IsLabelledNew`
  alone fails. Restored: `a93ab6309a7f161c7f75ca3c3c0c0b88`.
- M5, kd-66's warning put back in place - the legacy warning folded, the refusal warning on the freed line (md5
  `803c33cd1a012c2b754c2876fd4bbda9`, 1758 lines): `AMissingRoomList_IsNotPreviewedAsARefusal` alone fails. Restored:
  `a93ab6309a7f161c7f75ca3c3c0c0b88`.

The solution was rebuilt after the last restore, before the gates below.

**aud-a2z reinstated** (the commit after `86fdbde9`). Its four tests in `ConferenceRoomListGroupAddressTests` run on the
pre-weekend resolver: a group whose own domain has moved the list's address, or holds no mail, refuses the row before
any write; the on-prem add refuses such a group after Set-Place without claiming nothing was written; and an address
differing only in case is still the list's group (the control). Before: `Services/ConferenceRoomService.cs` md5
`f061a4a9d9fa445a8600ef04b345a47a`; the Conference Rooms tests now number 446.
- Z1, the comparison removed (md5 `fb2116021081c900489383044250b76f`): 3 failed, 443 passed - the three refusal tests;
  the control passes. Restored: `f061a4a9d9fa445a8600ef04b345a47a`.
- Z2, the comparison made case-sensitive (md5 `caae5f94e63a5e2be85c906883bcade0`): 1 failed, 445 passed - the control
  alone. Restored: `f061a4a9d9fa445a8600ef04b345a47a`.

**Gates for the reinstatement commit** (each run alone, after the last restore):
- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 17 warnings - the 19 below less the test project's two,
  which an incremental build with that project up to date does not report again.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 7355 passed, 0 failed, 0 skipped (7351 and a2z's
  four).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1`: passed.

**Gates** (on `fix/a2ad` before `c22cecc5` was made, each run alone - one compile at a time on this host):
- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings, every one pre-existing and none in a file this
  row touches.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 7351 passed, 0 failed, 0 skipped.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1`: passed.

Not run: a live Room Finder apply against Exchange Online and the directory; the stand-ins answer as the tests describe.

## Notes for the supervisor (facts found while doing this row; none changes what was done)

- **aud-a2z** - resolved 2026-10-27: raised here as a correctness check rather than a protection check, and reinstated
  by the coordinator's correction (see What stays).
- **The Constitution does not yet carry the third exemption.** Item 2 of the 2026-10-26/27 later-rulings entry says the
  protected-principal rule "carries Conference Rooms as its third module-scoped exemption ... by this ruling", but
  `docs/ProjectConstitution.md` (Protected Principals) still lists only Self-Service Groups and Comms-10k, so its first
  bullet still reads as covering the room-list write. Not edited here: outside this row's brief.
- **The same entry says the module "does not consult Protected Group Targets or the protected-principal list at all"**,
  while this brief keeps the July 2026 room-mailbox gate (`Services/ConferenceRoomProtectionGate.cs`), which consults the
  protected-principal list for the room. The brief's reading - the ruling removes what the weekend added - is the one
  followed.
- **The keep list is not the whole weekend on these files.** kd-23, kd-24, kd-37, aud-a70 (14), aud-a67 (10) and aud-a29g
  also touched them; none is a protected-principal change and all are kept.
- **The rows this one reverses** (aud-a2e, aud-a2ab, kd-66 in `.agents/review/index.md`) still describe their landed
  state; only this row's index line was in scope here.