# aud-a2e: Conference Rooms writes to a room-list group that is never checked against Protected Group Targets

**Severity**: MEDIUM - an authorization gap (Known Failure Class 3). Conference Rooms adds a room mailbox to a room-list distribution group chosen through the Building field (`ConferenceRoomService.AddToRoomListAsync`, on-prem fallback included); that group write is never routed through `ProtectedPrincipalService.CheckWriteTarget`. `docs/ProtectedGroupWriteTarget-Plan.md` scoped the module out as "its write target is a mailbox", which is true of `Set-Place` but not of the room-list membership write. A room list configured as a Protected Group Target can therefore be written by any Conference Rooms operator.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded by the supervisor at the 2026-10-23 hand-over)

## Evidence

At `4498da49`: `Services/ConferenceRoomService.cs` 332-442 (`ResolveRoomListAsync` / `FindRoomList` / `ResolveRoomListTargetAsync` - the list is resolved with name, GUID and primary SMTP), 560-565 (preflight), 663-665 and 710+ (`AddToRoomListAsync`, the cloud add and the on-prem fallback). No call to `CheckWriteTarget` (`Services/ProtectedPrincipalService.cs` 312) or `ProtectedPrincipalServicing.ForWriteTarget` (`Services/ProtectedPrincipalServicing.cs` 89) appears in the file. Compare `Services/GroupManagementService.cs` and `Services/PermissionValidator.cs` 512 (`BindWriteTargetAsync`), which do route their group writes.

Trigger: a room list (canonical or legacy name, cloud or synced) is listed under Protected Group Targets; an operator adds a room with that Building. Predicted failure: the membership write succeeds with no refusal and no protected-target audit evidence.

## Approach (to be written by the implementing agent before code)

- Resolve the room-list group to a directory principal by immutable identifier (GUID / DN for synced lists; the cloud list's identity for cloud-only ones, with the Constitution's structural limit stated) and route it through the existing write-target check before the preflight reports the add as possible and again before the write (the aud-a2b/a2d shape: the checked object is provably the written one).
- Fail closed: no credential or a failed read refuses the room-list add (the room's own `Set-Place` may still proceed only if the plan for the module says the two writes are separable; otherwise refuse the whole row and say so - "Nothing was written.").
- The servicer exemption (`ForWriteTarget`) applies as elsewhere.
- Mutation at the call site: the check removed; the check consulted after the write.

## Guard proof

Pending.