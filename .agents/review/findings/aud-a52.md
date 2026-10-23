# aud-a52: Self-grant check - a nested group membership still passes, and every grant to a cloud-only group is refused outright

**Severity**: MEDIUM (both parts). (1) `PermissionValidator`'s group branch (aud-a43) checks the primary group
and a direct `member=<operator DN>` link only; an operator in a group that is itself a member of the granted
mail-enabled security group is allowed, receives access through nesting, and no refusal is recorded. The
aud-a43 record named nested membership a known gap; under the EVERYTHING go a known gap on an authorization
check is a finding. (2) The same branch refuses any Exchange group recipient with no matching on-premises
group as "could not be completed"; a Microsoft 365 group or any cloud-only group never has one, so every
Mailbox Permissions and Calendar Permissions grant to such a group now fails before Exchange can write - a
functional block introduced by aud-a43.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; folded into the aud-a50 agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-5, 2026-10-22, candidates 1 and 2)

## Evidence

At `132d6e97`: `Services/PermissionValidator.cs` around 783 (direct member and primary group only) and 773
(the cloud-only refusal); `Services/SelfGrantDirectory.cs` (the member-link read);
`Services/ProtectedGroupMembership.cs` `Evaluate` (the transitive shape already in the repo,
`member:1.2.840.113556.1.4.1941`).

## Approach (to be written by the implementing agent before code)

- Transitive membership for a directory group grantee: the LDAP_MATCHING_RULE_IN_CHAIN filter against the
  group's own domain (reuse the protected-group transitive shape; call it, do not copy it), plus the
  primary-group handling; an incomplete read refuses; a nested member is a self-grant.
- Cloud-only group grantee: when Exchange names a group the directory does not hold, the membership check
  asks Exchange (`Get-DistributionGroupMember` / the unified group's members through the existing EXO
  session) for the operator's membership, transitive where the cmdlet allows; a check that cannot answer
  refuses as incomplete; one that answers "not a member" allows. Nothing names the environment.
- Tests through `SelfGrantDirectoryStandIn` (a nested chain; a cloud-only group with and without the operator)
  and `ExchangeWriteHarness`; mutations at the call site: direct-only membership again; the outright
  cloud-only refusal again.

## Guard proof

Pending.
