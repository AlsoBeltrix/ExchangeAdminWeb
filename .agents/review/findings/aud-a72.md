# aud-a72: A dynamic distribution group is classified as a group but has no membership read, so every grant to one is refused as incomplete

**Severity**: MEDIUM - fail-closed, but a legitimate grantee type is unusable under self-grant prevention.
`ExchangeRecipientKinds` (aud-a60) names `DynamicDistributionGroup` as a group; `ExchangeGroupMembers` has
no dynamic-group expansion path, so the membership read throws and `PermissionValidator` refuses the grant
as incomplete. Exchange Online exposes a dynamic group's current membership through
`Get-DynamicDistributionGroupMember` (and the filter preview), so the operator's membership can be
compared without evaluating the filter in the app.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a69 agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-7, 2026-10-23, candidate 2)

## Evidence

At `105a129f`: `Services/IIdentityResolver.cs` around 51 (`ExchangeRecipientKinds`);
`Services/ExchangeGroupMembers.cs` around 120 (the kinds it expands; the throw for any other kind);
`Services/PermissionValidator.cs` around 906 (the incomplete refusal).

## Approach (to be written by the implementing agent before code)

- `ExchangeGroupMembers.Read` expands a `DynamicDistributionGroup` through
  `Get-DynamicDistributionGroupMember -Identity <address> -ResultSize Unlimited` under the same
  pooled-read path and bound; its members compare like a distribution group's (nested groups within it
  follow the same rules). A read that fails or returns nothing at all is still incomplete (refuse); a
  membership that answers "not a member" allows. Nothing evaluates the filter in the app and nothing names
  the environment.
- Tests through `ExchangeGroupMembersTests` and `PermissionValidatorSelfGrantTests` with a dynamic-group
  stub (operator a member; not a member; the read fails). Mutation at the call site: the dynamic kind back
  to the throw.

## Guard proof

Pending.
