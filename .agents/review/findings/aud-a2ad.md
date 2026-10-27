# aud-a2ad: remove every protected-principal check the weekend added to Conference Rooms, and restore room-list creation

**Severity**: HIGH-class by provenance - this row reverses weekend work under an owner ruling and must leave the module's
pre-weekend behaviour intact while keeping the weekend's genuine fixes.
**Owner ruling** (2026-10-26, `.agents/decisions.md`, the "later rulings" entry, item 2): "protected principals or groups
are, ONCE AGAIN, OUT OF SCOPE FOR THIS MODULE. DO NOT BRING ME ANYTHING ABOUT PROTECTED PRINCIPALS RELATED ISSUES.
REMOVE ALL OF THAT ILLEGALLY ADDED BULLSHIT AND LIMIT TO FIXING FUCKING ISSUES." And on room-list creation:
"functionality CANNOT change. this is a top tier module and the stakeholders fought hard to get it where it was."
There is NO scoped exception and NO check before the add (the earlier shape of this record is superseded).
**Status**: Open - approved by owner ruling; the step 2 slice of the close-out plan in `.agents/state.md`
**Branch**: `fix/a2ad` (worktree `D:\source\ExchangeAdminWeb-wt\a2ad`, from master)
**Commit**: -
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
- **aud-a2z** (`170b182f`): the synced list's on-prem group must carry the list's address - this exists only to feed the
  a2e check's identity; it goes with it (`ConferenceRoomListGroupAddressTests` with it).
- **aud-a2ab** (`c38defa0`): the refusal of a Building whose room list does not exist. Creation comes back: the module
  creates the building's room list when Exchange affirmatively reports none, exactly as at `eb017102`, then adds the room.
  kd-68 (the refusal's audit fields) is superseded by this restore - close it so.
- **kd-66** (`5798e45e`): the preview goes back to marking a missing room list "new" (it will be created); the
  `ConferenceRoomMissingListPreviewTests` assertion that it says "refused" is reversed to assert "new", or removed.

## What stays (genuine fixes, by name)

- **aud-a2ac** (`fefb3e6a`): only Exchange's affirmative "no such object" means a building has no room list; an
  ambiguous name or a failed lookup refuses the row before any write. Creation proceeds ONLY after a confirmed absence.
- **aud-a2aa** (`26e8cd9b`): the neutral ambiguity message.
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

## Guard proof

Pending.