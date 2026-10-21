# aud-a3b: The protected-group membership check has three same-forest fail-open holes

**Severity**: HIGH - each one lets a genuinely protected principal read as unprotected, with no
error and no sign anything went wrong.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-acf74809ad7ee3f7d`
**Commit**: `ef0664e` (merged)

## Evidence

Raised by codex-commercial / `gpt-6-astra` / max while reviewing `d8702f0` (`aud-a3`). All three
are **pre-existing**: `aud-a3` fixed which directory is asked, not what the question can see.
They are filed together because they share one root - `CheckTransitiveGroupMembership` treats
"the query returned nothing" as "not a member", and there are several ways to return nothing
while the answer is yes.

### 1. Domain-local nesting across a domain boundary

`Services/ProtectedPrincipalService.cs:929`. A user in domain A belongs to a global group in A,
which is nested in a protected **domain-local** group in B. The query now runs against A, where
the user lives - but A's DC does not hold B's domain-local membership links.
`LDAP_MATCHING_RULE_IN_CHAIN` traverses the links the answering DC has, so it returns zero.

**This is same-forest, not cross-forest.** `aud-a3`'s record calls the limit "cross-forest
completeness", which understates it, and that wording is corrected there. Choosing a global
catalog does not fix the domain-local case - domain-local membership is not replicated to the
GC.

### 2. Primary-group membership is invisible

`Services/ProtectedPrincipalService.cs:929`. `memberOf` does not include a user's primary
group; that relationship lives in `primaryGroupID`, which this method never reads. If the
protected group IS the user's primary group - or protection is reachable only through it - even
a correctly routed, same-domain query returns zero.

Protecting `Domain Users` therefore protects almost nobody, since it is the default primary
group and is usually absent from `memberOf`.

### 3. The protected-group resolver discards the domain half

`Services/ProtectedPrincipalService.cs:982`. A stored entry such as `B\Privileged` has its
domain stripped and is resolved against the host domain A. A same-named group in A is accepted
in its place, so a target protected only through B's actual group passes. A configured full DN
returns at `:985` without any lookup at all.

**This also falsifies a claim in `aud-a3`'s record**, which said a foreign protected group
"refuses everything rather than resolving". It does not - it silently resolves the wrong group,
or passes a DN through unchecked, and then reaches the incomplete membership query above.

## Predicted observable failure

A VIP is protected by nesting their account's global group inside a domain-local `Protected
VIPs` group in another domain of the forest. Group Management reports them as unprotected and a
non-servicer modifies their group memberships. The audit record shows an ordinary successful
change.

## Approach - not designed

These need an authoritative membership evaluation rather than one filtered LDAP query. Options
to weigh, none chosen:

- Evaluate membership from the target's `tokenGroups` (the DC computes the transitive set,
  including domain-local and primary group) instead of an `IN_CHAIN` filter.
- Resolve each protected group to its SID once and compare SIDs against that set, which also
  removes the name-collision in hole 3.
- Keep the current query as a fast path and fail closed when it cannot establish an answer.

Whatever is chosen, the resolver must stop discarding the domain half of `DOMAIN\Group`, and
`aud-a1`'s lesson applies: identity comparisons belong on SIDs, not names.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21).

**Mechanism.** The membership question moves out of the runspace into a pure evaluator,
`ProtectedGroupMembership.Evaluate`, over a narrow directory seam (`IProtectedGroupDirectory`).
The runspace implementation (`PowerShellProtectedGroupDirectory`) only executes reads, each
routed to the domain that holds the data. Every read that fails, returns nothing where an
object must exist, or returns more than one object makes the check incomplete, which the
caller already turns into a refusal.

1. **Resolve each entry to exactly one group, bound by objectSid (hole 3).** A DN is read at
   that DN in its own domain - it must exist and be a group, no pass-through. `DOMAIN\Name` is
   looked up only in the domain the prefix names (NetBIOS name mapped through the forest's
   `CN=Partitions` crossRefs, the mapping `SectionAccessGroupDirectory` already uses). A bare
   name or a SID is looked up forest-wide through the global catalog. Zero or several matches,
   or an unknown domain, refuse.
2. **Read the target where it lives, and derive its primary group (hole 2).** objectSid,
   objectClass and primaryGroupID are read on the target's own domain. A SID-bearing non-group
   object without a readable primaryGroupID refuses. The primary group's SID is the target's
   account-domain SID plus that RID, resolved by SID in the target's domain. Seeds = target +
   primary group.
3. **Ancestors in the target's domain.** One in-chain query there returns every group whose
   member chain, as that domain holds it, reaches a seed. A protected group that is a seed or
   an ancestor (compared by SID) matches.
4. **A domain-local group is always asked in its own domain (hole 1).** For every resolved
   domain-local group not already matched, one query routed by the group's own DN asks whether
   its same-domain nesting has any seed or ancestor as a member. Domain-local groups can only
   nest inside domain-local groups of their own domain, so that domain holds every link of the
   outer chain, and the inner end is always a seed or an ancestor.
5. The pre-existing per-group in-chain query on the target's domain is kept, with the primary
   group added, for universal groups outside the target's domain, so no coverage the old check
   had is lost. Whether that coverage is complete is a separate defect, filed as `aud-a3c`.

**Files.** `Services/ProtectedGroupMembership.cs` (new: entry parser, records, seam, filter
builders, evaluator), `Services/PowerShellProtectedGroupDirectory.cs` (new: the runspace reads),
`Services/ProtectedPrincipalService.cs` (`CheckTransitiveGroupMembership` delegates;
`ResolveProtectedGroupDn` removed), `ExchangeAdminWeb.Tests/ProtectedGroupMembershipTests.cs`
(new), `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` (the old body tripwires
re-pointed at the new shape).

**Tests.** A fake forest models which domain controller holds which links (a domain's DC holds
its own groups' member links and nothing else), so every hole is reproduced with the data
genuinely absent from the place the old check asked. One test per hole plus the fail-closed
edges (unknown domain, ambiguous name, missing DN, unreadable primary group, owning domain
undeterminable), plus pure tests for the parser, the primary-group SID and the filter builders.

**Mutation, at the call sites in the evaluator.** Drop the domain-local own-domain query (hole
1 test fails); drop the primary-group seed (hole 2 tests fail); route a `DOMAIN\Name` entry to
the target's domain instead of the named one (hole 3 test fails); accept a DN entry without
reading it (missing-DN test fails).

## Guard proof

The tests drive `ProtectedGroupMembership.Evaluate` against a fake forest in which each
domain's DC holds its own groups' member links and nothing else - no global-catalog replicas -
so every hole is reproduced with the data genuinely absent from the place the old check asked.
`ExchangeAdminWeb.Tests/ProtectedGroupMembershipTests.cs`:

| Test | What it pins |
| --- | --- |
| `DomainLocalGroupInAnotherDomain_ProtectsThroughANestedGlobalGroup` | hole 1 itself, and that the question goes to the GROUP's domain |
| `DomainLocalGroupInAnotherDomain_DoesNotProtectSomeoneOutsideIt` | the opposite direction, so hole 1 cannot pass by matching everything |
| `DomainLocalGroupInAnotherDomain_ProtectsADirectMember`, `DomainLocalGroup_NestedInsideAnotherOfItsOwnDomain_IsFollowed` | the other two shapes of the outer chain |
| `PrimaryGroup_IsAMembership` | hole 2 itself: the primary group has no member link and still protects |
| `PrimaryGroup_NestedInAProtectedGroupOfTheSameDomain_Protects`, `PrimaryGroup_NestedInADomainLocalGroupOfAnotherDomain_Protects` | the primary group is a seed for every later query, including hole 1's |
| `AnAccountWhosePrimaryGroupCannotBeRead_FailsClosed`, `APrimaryGroupThatDoesNotResolve_FailsClosed` | an unreadable primary group is an incomplete read, never "no primary group" |
| `DomainQualifiedEntry_ResolvesInTheNamedDomain_NotAnother` | hole 3 itself: `B\Privileged` is B's group, not A's namesake |
| `DomainQualifiedEntry_ForTheOtherDomain_SelectsThatDomainsGroup` | the control: the domain half selects in both directions |
| `DomainQualifiedEntry_NamingNoDomainOfTheForest_FailsClosed`, `BareName_SharedByTwoDomains_FailsClosed` | an unknown domain or an ambiguous name refuses rather than resolving somewhere |
| `DnEntry_ThatNoLongerExists_FailsClosed`, `DnEntry_IsReadInItsOwnDomain` | hole 3's second half: a DN is read, in its own domain, never passed through |
| `BareName_UniqueInTheForest_IsResolvedForestWide`, `SidEntry_IsResolvedForestWide` | entries without a domain are resolved across the forest, not in an assumed one |
| `AGroupTarget_ThatIsTheProtectedGroup_Matches`, `AGroupTarget_DoesNotNeedAPrimaryGroup` | the nesting plan's self-match survives, by SID now |
| `ATargetWhoseOwningDomainCannotBeDetermined_FailsClosed`, `TheTargetIsReadInItsOwnDomain` | `aud-a3`'s owning-domain rule, moved into the evaluator |
| `AUniversalGroupOfAnotherDomain_StillGetsThePreviousQuery` | no coverage the old check had is dropped |
| `ADirectoryReadThatFails_FailsClosed` (3 cases), `AFailedDomainLocalQuery_FailsClosed`, `AMatchElsewhere_StillProtects_WhenAnotherEntryCannotBeAnswered`, `NoEntries_IsAnsweredWithoutTheDirectory` | fail-closed on every read, and a real match is not cancelled by an unanswerable entry |
| `Parse_ClassifiesEachShape` (8), `PrimaryGroupSid_*`, `RequiresPrimaryGroup_*`, `ParseScope_*`, filter and batch tests | the pure rules the evaluator and the runspace reads stand on |
| `EveryRead_CarriesTheServerItWasGiven` (5), `AForestLookup_GoesToTheGlobalCatalog_NeverTheLocalDomain`, `TheTargetRead_IsClassAgnostic`, `AReadThatReportedAnError_IsAFailedRead`, `TheServiceDelegatesTheDecision_AndPassesIncompleteThrough` | code-view tripwires over the runspace reads, which need a DC |

`GroupMemberNestingProtectionTests`: the three `IsProtectedGroupItself` tests and the
body tripwires for the old per-group query are removed with the code they pinned; the
self-match and the owning-domain rule are now pinned by behaviour above.
`TransitiveCheck_UsesClassAgnosticCmdlet_ForTheFallbackLookup` replaces the two-call count.

Mutations, each at the call site in `Services/ProtectedGroupMembership.cs`, each run this
session against the committed source; source md5 `d146948ae56384f8d077335402cc71bb` before and
after every one, file touched and rebuilt green after restore. (A first round passed the same
way against a version `dotnet format` then rejected for case-block brace indentation; the
round was re-run after the reformat so the hashes below are the committed file's.)

| Mutation | Mutated md5 | Named test | Result |
| --- | --- | --- | --- |
| delete the domain-local own-domain query (the branch just `continue`s) | `e02170f4dae6b04e5b3ada63e6fe7adf` | `DomainLocalGroupInAnotherDomain_ProtectsThroughANestedGlobalGroup` | FAIL |
| drop the primary-group seed | `6779e21c920798e0dc918f08b998ed81` | `PrimaryGroup_IsAMembership` | FAIL |
| discard the domain half (resolve `DOMAIN\Name` as a bare name) | `610581b3180604b7182a7aefb07f8cc7` | `DomainQualifiedEntry_ResolvesInTheNamedDomain_NotAnother` | FAIL |
| pass a configured DN through without reading it | `f8211884ecb0f49e1f0d95284317f124` | `DnEntry_ThatNoLongerExists_FailsClosed` | FAIL |

## Known gaps

**None of this is verified against a real forest, and probing AD is forbidden without a direct
owner go.** The tests prove the evaluator's logic against a modelled forest; the model's rules -
a domain holds its own groups' links, domain-local groups nest only inside domain-local groups
of their own domain, global groups contain only their own domain's members, `primaryGroupID`
is never a `member` link - are Microsoft's documented group-scope semantics, not observations of
this forest. The runspace reads are pinned by source tripwires only.

**Operator-visible behaviour changes, all in the refusing direction:**

- An entry whose `DOMAIN\` prefix names no domain of the forest - including `BUILTIN\X`, which
  used to resolve against whatever domain the query bound to - now makes every group check
  refuse until the row is re-picked, so it stores the group's DN.
- A bare-name entry that two domains share now refuses instead of resolving to one of them.
- A configured DN that no longer exists now refuses instead of silently protecting nobody.
- Every group check now also reads the target object, its primary group, and - for entries
  without a domain - the forest's global catalog. Any of those failing refuses. Each check costs
  roughly three more directory reads than before, plus one per domain-local protected group;
  none of it has been measured.

**Left as they were, recorded so nobody reads them as covered:**

- Membership reached through a universal group of another domain is still read only as far as
  the target's domain can see, exactly as before. That is a further fail-open path the record did
  not list, filed as `aud-a3c` and fixed in its own commit.
- A protected group whose membership is granted through a well-known SID's
  foreignSecurityPrincipal (for example Authenticated Users nested in it) is not treated as
  containing everyone. Neither check ever did; it is a configuration no protection list should
  hold, but nothing refuses it.
- `MatchesDnToProtectedGroup` - which compares only the name half of `DOMAIN\Group`, hole 3's
  pattern - is dead code with tests and no caller; it was not touched.
- `ADDirectorySearchService.NormalizeIdentity` still strips the domain half when the admin page
  validates or stale-sweeps a `DOMAIN\Name` entry. That is the admin display, not enforcement,
  but it can report such a row as found when only a same-named group elsewhere exists.

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `d8702f0`. Findings 1, 3 and 4 of 5.
Raw output: `.agents/review/out/aud-a3.txt` (machine-local, gitignored).

Finding 2 of that review - the DN parser mis-routing an escaped backslash - was a regression
introduced by `aud-a3` and is fixed in its repair commit, not carried here. Finding 5 - the
tripwires passing against commented-out code - is fixed in the same commit.

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-acf74809ad7ee3f7d`. Independent review of the fix pending.

## Closeout

Fixed on branch, not merged, not pushed. Shared infrastructure (`ProtectedPrincipalService`),
so the base app version moves at merge; no module version.
