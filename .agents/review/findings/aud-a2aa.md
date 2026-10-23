# aud-a2aa: every Ambiguous protection result is reported as "matches multiple AD users", which is often wrong

**Severity**: LOW - diagnostic text. `Services/ConferenceRoomProtectionGate.cs` about 149: since aud-a2b/a2c an `Ambiguous` result can come from groups, lists or mixed object kinds, but the operator text always says "matches multiple AD users".
**Status**: Open - admitted 2026-10-23 evening from the aud-a2e agent's hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (aud-a2e agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `3e43b35b`. Trigger: an address or name that resolves to two directory objects that are not both users. Predicted failure: the refusal names the wrong object kind, sending the operator to look at user accounts.

## Approach (to be written by the implementing agent before code)

- Carry the object kinds (or a neutral "directory objects") into the message; test pins the wording for a group/group and a user/group ambiguity; mutation: the fixed text restored.

## Guard proof

Pending.