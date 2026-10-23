# aud-a2z: the synced room list's re-read checks objectGUID and DN but not that mail still carries the list address

**Severity**: LOW - correctness only. `Services/ConferenceRoomService.cs` about 2770 (after aud-a2e): the group re-read before the on-prem add confirms objectGUID and DN but not that `mail` still holds the room-list address, so with a lagging global catalog a room can be added to a group that no longer carries the list.
**Status**: Open - admitted 2026-10-23 evening from the aud-a2e agent's hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (aud-a2e agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `3e43b35b`. Trigger: the list's address is moved off the group between resolution and add on a lagging GC. Predicted failure: membership written to a group that is no longer the room list; the audit row names the list the operator saw.

## Approach (to be written by the implementing agent before code)

- The re-read also compares `mail` (case-insensitive) with the resolved list address and refuses on a mismatch with "Nothing was written."; test with a stand-in that answers a changed mail; mutation: the comparison removed.

## Guard proof

Pending.