# aud-a3: The protected-group check asks the wrong domain

**Severity**: HIGH - a foreign-domain object is reported as not protected, so Group Management
will act on a principal the protection configuration covers.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 3 of the 2026-10-16 repository audit, verified against the code before any change.

`Services/ProtectedPrincipalService.CheckTransitiveGroupMembership` built the in-chain
membership query with no directory scope:

```csharp
ps.AddCommand("Get-ADObject")
  .AddParameter("LDAPFilter", $"(&(distinguishedName={targetFilter})(memberOf:1.2.840.113556.1.4.1941:={groupFilter}))")
  .AddParameter("Credential", credential)
  .AddParameter("ErrorAction", "Stop");
```

No `-Server`, no `-SearchBase`, so `Get-ADObject` binds to the host's own domain. A target in
another domain of the forest does not exist in that partition, so the query returns zero rows.
Zero rows is indistinguishable here from "this object is not in the protected group", and the
caller reads it as the negative.

**The failure is silent and it is the safe-looking direction.** There is no error, no warning,
and the check returns a clean result. The target is then treated as unprotected.

## Predicted observable failure

A protected principal whose object lives in a second domain of the forest - for example a
WINROOT object on an ANALOG-hosted app, the exact pairing recorded in findings `gmn-8` and
`fsr-1` - passes the protected-group check and can be modified through Group Management by a
non-servicer.

## Approach

Route the query to the target's own domain, derived from its DN:

```csharp
var targetServer = GroupManagementService.ServerFromDn(targetDn);
```

`ServerFromDn` already exists, is pure, is unit-tested, and splits on unescaped commas only so
an escaped comma inside a CN cannot smuggle a fake DC segment. **This is the third instance of
one class** - `gmn-8` fixed the picker's resolution, `fsr-1` fixed the member read, and this is
the protection check. Reusing the helper rather than re-deriving the rule is the point; a
second implementation would be a second thing to drift.

**Where this deliberately differs from Group Management.** `ServerFromDn` returns null for a DN
with no DC components, and the Group Management convention is then to omit `-Server` and keep
the local binding. That is right for a listing and wrong here. This is a protection check, and
an unanswerable question is not a negative answer, so a null fails the whole check closed
**before** the protected-group loop - one unresolvable target refuses everything rather than
being re-asked per group.

Fail-closed plumbing already existed and is reused unchanged: the caller turns
`expansionHadErrors` with no matches into *"Group membership check was incomplete... Cannot
confirm target is not protected."*

Environment neutrality holds: the domain is discovered from the object's own DN at runtime.
Nothing is named, defaulted or configured.

## Files changed

- `Services/ProtectedPrincipalService.cs`
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs`

## Guard proof

No test in this repo can execute a runspace, so this follows the file's established idiom -
source tripwires over a sliced `CheckTransitiveGroupMembership` body, which is how every
existing rule in that method is pinned.

| Test | What it pins |
| --- | --- |
| `TransitiveCheck_AsksTheTargetsOwnDomain_NotTheHosts` | the domain is derived from the target DN, and `-Server` sits on the in-chain call specifically - the slice is bounded between the in-chain filter and its `Invoke` so a `-Server` elsewhere cannot satisfy it |
| `TransitiveCheck_FailsClosed_WhenTheOwningDomainCannotBeDetermined` | the null guard returns the fail-closed tuple, and does so BEFORE the protected-group loop |

Mutations, both run this session:

| Mutation | Result |
| --- | --- |
| delete `.AddParameter("Server", targetServer)` | FAIL (`AsksTheTargetsOwnDomain`) |
| delete the `targetServer is null` fail-closed guard | FAIL (`FailsClosed`) |

Service restored byte-identical after both (md5 verified) and touched.

## Coder dispute

None.

## Known gaps

**Routing to the target's domain is better than the host's; it is not proof of cross-forest
completeness.** `LDAP_MATCHING_RULE_IN_CHAIN` is evaluated by the DC answering the query, and
its treatment of memberships that cross a domain boundary depends on group scope - a universal
group's membership is in the global catalog, a domain-local group's is not. This slice fixes
"asked the wrong directory". It does not establish that one DC can see every nesting path in a
multi-domain forest. **Nothing here has run against a real forest, and probing AD is forbidden
without a direct owner go.**

`ResolveProtectedGroupDn` is unchanged and may itself bind locally for a protected group that
lives in another domain. That path already sets `expansionHadErrors` and so fails closed, which
is why it is not in this slice - but it means a foreign protected group currently refuses
everything rather than resolving. Worth its own slice; recorded here rather than widened into
this one.

Source tripwires prove the call shape, not the behaviour. They would not catch a `ServerFromDn`
that returned the wrong domain - that rule is pinned by its own pure tests in this same file.

## Reviewer comments

Source: the 2026-10-16 audit, finding 3. Verified by the implementer before changing anything.
Independent review of the fix pending.

## Closeout

Fixed directly on `master`. Not pushed.
