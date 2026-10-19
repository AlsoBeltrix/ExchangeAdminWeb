# aud-a3b: The protected-group membership check has three same-forest fail-open holes

**Severity**: HIGH - each one lets a genuinely protected principal read as unprotected, with no
error and no sign anything went wrong.
**Status**: Open - not approved for implementation
**Branch**: -
**Commit**: -

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

## Known gaps

**None of this is verified against a real forest.** The analysis is from source plus Microsoft's
documentation on replication, `IN_CHAIN` and primary-group semantics, which is also how the
reviewer reached it. **Probing Active Directory is forbidden without a direct owner go**, so
confirming hole 1 in particular needs the owner's directory team or an explicit authorisation.

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `d8702f0`. Findings 1, 3 and 4 of 5.
Raw output: `.agents/review/out/aud-a3.txt` (machine-local, gitignored).

Finding 2 of that review - the DN parser mis-routing an escaped backslash - was a regression
introduced by `aud-a3` and is fixed in its repair commit, not carried here. Finding 5 - the
tripwires passing against commented-out code - is fixed in the same commit.

## Closeout

Pending owner approval. Recorded under the 2026-10-19 ruling that everything needing
remediation gets planned and fixed.
