# aud-a3c: Membership through a universal group of another domain is invisible unless the target's DC happens to be a global catalog

**Severity**: HIGH - a principal protected through a universal group reads as unprotected, with
no error, wherever the target's domain controller does not carry the global catalog.
**Status**: Closed - codex group review (authz-batch, 2026-10-21) raised no defect in this change
**Branch**: `worktree-agent-acf74809ad7ee3f7d`
**Commit**: `71e7ca9` (merged)

## Evidence

Found by the implementer while designing `aud-a3b`'s fix; not raised by any review. Pre-existing
in the check `aud-a3` routed, and carried unchanged by `aud-a3b` on purpose so each finding has
its own commit.

`ProtectedGroupMembership.Evaluate` (and before it `CheckTransitiveGroupMembership`) answers
universal groups of another domain with an in-chain query on the TARGET's domain:

```csharp
directory.AnyIsInChainOf(seedDns, group.DistinguishedName, targetServer)
// (&(|(distinguishedName=<target>)(distinguishedName=<primary group>))
//   (memberOf:1.2.840.113556.1.4.1941:=<universal group>))
```

A universal group's `member` links are held by its own domain's DCs and by every global
catalog. A domain controller of the target's domain holds them only if it is itself a global
catalog. Take a user in domain A, in A's global group `Staff`, which is a member of a universal
group in domain B:

- the `Staff` -> user link is held by A's DCs;
- the universal group -> `Staff` link is held by B's DCs and the global catalogs.

If the DC answering for A is not a global catalog, the chain breaks at `Staff`, the query returns
no rows, and the target reads as unprotected. The answer depends on which DC answered, which is
an environment fact the code never checks - `.agents/repo-guidance.md` Architectural Invariant 7
forbids exactly that.

The same break hides a domain-local or global-scope ancestry that passes through such a group:
a domain-local group in A that contains B's universal group, which contains `Staff`, is not
reachable from A's data alone either, so `aud-a3b`'s own-domain query for that domain-local
group cannot see it.

## Predicted observable failure

A VIP is protected by membership of a universal `Executives` group kept in the forest root. On a
host whose domain controllers are not global catalogs, every module that consults the protected
group list treats the VIP as unprotected; the audit shows ordinary successful changes.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs ... EVERYTHING ... any more you find", `.agents/decisions.md`
2026-10-21).

**Mechanism.** Read universal-group membership from where AD guarantees it is complete: the
global catalog, which holds every universal group's `member` links for the whole forest.

1. New seam member `UniversalGroupsContainingAny(memberDns)`: every universal group in the forest
   whose member chain, as the global catalog holds it, reaches any of the DNs. The runspace
   implementation is one batched in-chain query against the GC discovered from the host's own
   forest, filtered to universal groups.
2. The evaluator asks it once per check, from the target, its primary group and every ancestor
   the target's domain reported, and adds the results to the ancestor set. Universal groups only
   nest inside universal and domain-local groups, so after this step every universal ancestor is
   known; domain-local ancestors are then found by `aud-a3b`'s own-domain query, whose seed set now
   includes them.
3. The per-group `AnyIsInChainOf` query is removed: everything it could see, the GC query sees,
   and it could see less depending on the DC. A GC that cannot be read makes the check incomplete,
   which refuses.

**Files.** `Services/ProtectedGroupMembership.cs`, `Services/PowerShellProtectedGroupDirectory.cs`,
`ExchangeAdminWeb.Tests/ProtectedGroupMembershipTests.cs`.

**Tests.** The fake forest gains a global catalog that holds universal groups' links for every
domain and nothing else. A universal group of another domain containing the target's global
group, and a domain-local group of the target's domain reached only through such a universal
group, must each protect; a target outside them must not; a failed GC read must refuse.

**Mutation, at the call site in the evaluator.** Replace the GC read with an empty result: both
protection tests fail.

## Guard proof

`ExchangeAdminWeb.Tests/ProtectedGroupMembershipTests.cs`. The fake forest's global catalog
holds every universal group's links in every domain and no other group's; each domain's DC still
holds only its own groups' links.

| Test | What it pins |
| --- | --- |
| `UniversalGroupOfAnotherDomain_ProtectsThroughAGlobalGroup` | the finding: a universal group of B containing A's `Staff` protects a member of `Staff`, and the read went to the forest |
| `UniversalGroupOfAnotherDomain_DoesNotProtectSomeoneOutsideIt` | the opposite direction |
| `DomainLocalGroup_ReachedOnlyThroughAUniversalGroupOfAnotherDomain_Protects` | the second half: a domain-local group of A reached only through B's universal group |
| `UniversalGroupsNestedInUniversalGroups_AreFollowed` | universal-in-universal nesting across domains |
| `AFailedGlobalCatalogRead_FailsClosed` | an unreadable GC refuses, never answers "not a member" |
| `UniversalContainsAnyFilter_IsLimitedToUniversalGroups` | the filter's shape |
| `AForestLookup_GoesToTheGlobalCatalog_NeverTheLocalDomain`, `EveryRead_CarriesTheServerItWasGiven` (new row) | code-view tripwires: the universal read uses the GC and its own filter |

`AUniversalGroupOfAnotherDomain_StillGetsThePreviousQuery` (added by `aud-a3b` to pin that the
old query was kept) and `AnyInChainOfFilter_KeepsThePreviousQueriesDirection` are removed with
the query and its filter.

Mutation, at the call site in `Services/ProtectedGroupMembership.cs`, run this session against
the committed source:

| Mutation | md5 before / restored | Mutated md5 | Named tests | Result |
| --- | --- | --- | --- | --- |
| replace the GC read with an empty result | `09c22be88b50da93fd480d58328793bf` | `8e706db50191ce9bde79ae94fe308d01` | `UniversalGroupOfAnotherDomain_ProtectsThroughAGlobalGroup`, `DomainLocalGroup_ReachedOnlyThroughAUniversalGroupOfAnotherDomain_Protects` | both FAIL |

File touched and rebuilt green after restore. Both tests also fail against `aud-a3b`'s version
of the evaluator, whose kept per-group query asked the target's domain: the fake's DCs are not
global catalogs, which is the case the finding is about.

## Known gaps

**Not verified against a real forest; probing AD is forbidden without a direct owner go.** That
the global catalog holds every universal group's `member` attribute is Microsoft's documented
replication rule, not an observation of this forest. That an in-chain query on the GC port
traverses those links is likewise from documentation; the picker's GC search
(`ADDirectorySearchService.ResolveGlobalCatalog`) is the only GC use in this app measured live,
and it does not use the in-chain rule.

**Every protected-group check now needs a reachable global catalog.** When none can be
discovered or read, the check refuses. Before this, a check with no bare-name or SID entries did
not touch the GC. That is the fail-closed direction and it is deliberate - universal membership
lives nowhere else - but it is a new way for an outage to refuse operations.

**What the GC query cannot see, by AD's own scope rules, and why that is not a gap:** a global
group of another domain cannot contain the target; a domain-local group can only be reached
from inside its own domain, which `aud-a3b`'s own-domain query asks. Membership granted through a
foreignSecurityPrincipal for a well-known SID is out of scope here as it is in `aud-a3b`.

Replication latency still applies, as it does to every directory read: a membership change made
moments ago on another DC may not yet be on the GC that answers.

## Reviewer comments

`Reviewer: implementer` (Opus 5.5 implementation agent on `worktree-agent-acf74809ad7ee3f7d`),
found while designing `aud-a3b`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed. Shared infrastructure (`ProtectedPrincipalService`),
so the base app version moves at merge; no module version.
