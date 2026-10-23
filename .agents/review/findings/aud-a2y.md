# aud-a2y: Conference Rooms and credential wording leftovers - the Type path's room read that throws is not logged, "Check the DelineaSecretId configuration" at seven sites in other modules, and two refusals outside the service lack "Nothing was written."

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1)
`ConferenceRoomService.SetRoomTypeAsync`: when the Type path's room read throws, the service neither
catches nor logs it (aud-a2w part 2's gap on the Type path). (2) "Check the DelineaSecretId configuration"
is shown whatever the actual reason at six `SelfServiceGroupService` sites and one `GroupManagementService`
site - the aud-a2x defect in other modules; they can now call `ModuleCredentialService.LookupCredentialsAsync`.
(3) Refusals outside the service do not end "Nothing was written.": `ConferenceRoomBulkProcessor` "Invalid
type" and `ConferenceRooms.razor` "Invalid room type" (line-keyed page).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-8/a2w/a2x agent, 2026-10-23, candidates 1, 7 and 8 of its hand-back)

## Evidence

At `9293b77a`: `Services/ConferenceRoomService.cs` 931; `Services/SelfServiceGroups/SelfServiceGroupService.cs`
88, 165, 242, 468, 738, 791; `Services/GroupManagementService.cs` 107; `Services/Jobs/ConferenceRoomBulkProcessor.cs`
205; `Components/Pages/ConferenceRooms.razor` 1400 (line-keyed by `ClickGateRegistry`, 1758 lines, edit in
place only).

## Approach (to be written by the implementing agent before code)

- The Type path's room read gets the aud-a2w Warning-and-rethrow.
- The seven credential refusals choose their clause from `LookupCredentialsAsync`'s failure (the aud-a2x
  shape).
- The two refusals end " Nothing was written." (the page edit in place).
- Mutations at the call site: the catch removed; one site's single text again; one suffix dropped.

## Guard proof

Pending.
