# rev-a1: a user reaching a protected security group only through a distribution group is not protected (token short-circuit)

**Severity**: HIGH - fail-open in the protected-principal check. `tokenGroups` lists security groups only; a user whose
membership in protected security group S runs through distribution group D (U in D, D in S) has no S in the token.
`ProtectedGroupMembership.TokenAnswers` treats "token read, group is a global/universal security group" as a confirmed
negative and skips the anchored member-link fallback, so the check returns NotProtected and a write proceeds. The
pre-kd-88 member-chain walk caught this case.
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-abc
**Commit**: 536e0e3b
**Lines** (master `145f513f`): `Services/ProtectedGroupMembership.cs:402-405`, `:492-495`; `Services/ProtectedPrincipalService.cs:447`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review A, range `8557a15a..5c4c2970`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-a.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Treat token presence as a positive answer only. A token MISS on a security group is not a confirmed negative when the
group can contain distribution groups as members: run the existing anchored member-link question for that group (one
`(&(distinguishedName=<S>)(member:1.2.840.113556.1.4.1941:=<U>))` per protected group, as before kd-88) before clearing it.
Keep the near-instant requirement: the fallback runs only for groups the token did not answer, and the earlier anchored
form ran live for months. Add the nesting case to `FakeForest` (its `ReadTokenGroups` already omits distribution
intermediaries) and a test that fails on the current code.

## As built (fix/rev-abc `536e0e3b`)

Verified real: `TokenAnswers` cleared every security group the token did not carry; `tokenGroups` is expanded through
security groups only (FakeForest models it), so U in D (distribution) in S (security) had no S and the check said
NotProtected.

Departure from the fix shape. The reviewer (and the brief) proposed one anchored in-chain question to S's domain per group
the token missed. Analysis showed that is incomplete in a multi-domain forest, so it was not built:
- U (domain A) in global distribution group D of A, in universal security group S of B: B's DC holds S's links but not D's
  (only A's DCs hold a global group's links); A's DC holds D's but S's only if it is a GC. No single anchored question
  follows both.
- U in universal distribution group D of B, in domain-local security group S of A: A's DC does not hold D's links (only B
  and the catalogs do).
Invariant 7 forbids resting on ADI's forest shape. The existing link-read path (kd-88's ancestry: `ReadMemberOf` level by
level in the target's domain and at the GC, then the anchored domain-local question with every ancestor DN) is complete for
both. So `TokenAnswers` became `CannotContain`: a token miss goes to that path unless the group is a global group of another
domain (which cannot hold the account). Token presence stays a positive answer.

Cost (measured in the stand-in, estimated live): the ancestry runs once per check, whatever the number of protected groups.
For an account in its primary group only: +3 directory reads (the primary group's read, one level in two places). Each
further nesting level adds 2 reads (+1 per extra 50 groups per level). The timing tests moved 4 -> 7, 7 -> 10, 6 -> 9. At
tens of milliseconds per warm read that is well under a second for ordinary accounts; an account in several hundred groups
nested five deep is roughly 20-40 reads. Not measured live; the owner's timing line ("Protected-principal check for <t> took
<n> ms (<r> directory reads ...)") gives the figure on the deploy.

## Guard proof

| Test | Fails on the old code |
| --- | --- |
| `ProtectedGroupMembershipTests.ASecurityGroupReachedThroughADistributionGroup_Protects_ThoughTheTokenDoesNotCarryIt` (U in D in S, one domain) | yes |
| `ProtectedGroupMembershipTests.AUniversalSecurityGroupOfAnotherDomain_ReachedThroughADistributionGroup_Protects` | yes |
| `ProtectedGroupMembershipTests.ADomainLocalSecurityGroup_ReachedThroughAnotherDomainsUniversalDistributionGroup_Protects` | yes |
| `ProtectedGroupMembershipTests.ASecurityGroupTheTokenDoesNotCarry_AndNoLinkReaches_IsNoMember` | yes (asserts the links were read) |
| `ProtectedGroupMembershipTests.AGlobalGroupOfAnotherDomain_CannotHoldTheAccount_AndNoLinkIsRead` | passes on both (guards the shortcut) |
| `AnAccountOutsideEveryProtectedSecurityGroup_IsConfirmedFromItsLinks_OnceForAllTheGroups` (renamed), three `ProtectionCheckTimingTests` counts | yes (counts) |

Proof: `Services/ProtectedGroupMembership.cs` stashed, build, 8 failed; restored, all pass.
