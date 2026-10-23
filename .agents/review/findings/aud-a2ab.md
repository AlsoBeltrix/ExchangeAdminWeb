# aud-a2ab: a room list the row creates is checked against Protected Group Targets only after Set-Place and New-DistributionGroup have run

**Severity**: HIGH - a protected-target rule is discovered only after the app has created the list and changed the room's metadata. `Services/ConferenceRoomService.cs` about 859 (`PreflightRoomListAsync` returns success for a missing list because "there is nothing to check") and about 2106 (the created list is checked after `New-DistributionGroup`); `ExchangeAdminWeb.Tests/ConferenceRoomListWriteTargetTests.cs` about 406 pins the partial outcome. Trigger: the Building has no room list and a protected-principal row names the list that will be created (for example by its primary SMTP address). Predicted failure: `Set-Place` runs, `New-DistributionGroup` runs, then the add is refused - a partial row, contrary to the aud-a2e record's "whole row refused before any write" (Known Failure Classes 1 and 3).
**Status**: Open - admitted 2026-10-23 evening from codex cr-batch-7 on aud-a2e; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head ebd66b91 (merged 3e43b35b); 2026-10-23 ~20:0xZ; capability proof: AGENTS.md read and git show --stat ebd66b91 both succeeded

## Evidence

Codex cr-batch-7 finding 1 at `3e43b35b`.

## Approach (to be written by the implementing agent before code)

- Make the to-be-created list's identity deterministic (the canonical name and the address it will receive) and check that intended target before ANY row write; if the intended identity cannot be determined before creation, fail closed (refuse the row, "Nothing was written."). Keep the post-creation re-check before the add. Change the test at about 406 to assert refusal before `Set-Place`; mutation: the pre-create check removed (the old partial outcome returns).

## Guard proof

Pending.