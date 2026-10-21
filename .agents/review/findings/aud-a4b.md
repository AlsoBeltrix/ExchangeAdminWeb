# aud-a4b: The self-service DACL gate is not a faithful Windows access check

**Severity**: HIGH - unauthorized writes remain possible, and some valid grants are now
refused.
**Status**: Finding 7 merged to master (`d74e66f`); codex group review (authz-batch, 2026-10-21) raised no defect in it. Findings 1-6: Verified, fixed directly on
`master` earlier.
**Branch**: findings 1-6 -- (direct-to-main); finding 7 `worktree-agent-acf74809ad7ee3f7d`
**Commit**: findings 1-6 `<pending>` as recorded before; finding 7 `d74e66f` (merged)

## Evidence

Raised by codex-commercial / `gpt-6-astra` / max reviewing `a5b6216` (`aud-a4`). Verdict:
*"Changes required... It does not faithfully implement Windows access checks."*

`aud-a4` closed the narrow case it set out to close - a Deny whose trustee SID appears in the
caller's claims is now honoured. The gate is still not an access check. Five of the seven are
pre-existing and survived the extraction; **two are mine**, and one of those is a correctness
regression rather than an omission.

### Mine

**1. Unconditional Deny precedence refuses grants Windows allows.** `GroupMembershipAce.cs:117`.
I wrote that Deny wins regardless of ACE order, and that a canonical DACL lists denies first.
**Both are wrong.** Windows evaluates a DACL in order and stops once the requested right is
satisfied, and canonical ordering puts *explicit* ACEs before *inherited* ones - so an explicit
Allow for member-write followed by an inherited group Deny grants access. This evaluator always
refuses it. `GroupDeny_BeatsAnIndividualAllow_EvenWhenTheAllowComesFirst` pins that incorrect
behaviour.

The direction is conservative, so it blocks legitimate self-service rather than permitting a
write - but it is still wrong, and the test makes it look deliberate.

**2. The null-principal refusal guards one entry point, not the write.**
`SelfServiceGroupService.cs:535`, `:709`. `RemoteListedMemberAsync` can be called with
`actingUser: null`, pass protection for an unprotected member, and reach the shared writer with
an empty token set - so an individual Allow again survives group Denies. My comment saying the
principal "is non-null by the time execution reaches here" is false for that path. The current
UI callers all pass `authState.User`, so this is an incomplete service-boundary guard rather
than a demonstrated page bypass.

### Pre-existing, carried through the extraction

**3. Inheritance-only ACEs become effective grants.** `SelfServiceGroupService.cs:1348`,
`GroupMembershipAce.cs:68`. `GetAccessRules(true, true, ...)` returns rules whose propagation
flags matter, and the projection keeps neither `PropagationFlags` nor any applicability flag.
An Allow carrying `INHERIT_ONLY_ACE` is not effective on the object itself; Windows refuses,
this evaluator returns true, and the privileged write proceeds.

**4. A Membership property-set Deny is ignored.** `GroupMembershipAce.cs:58`.
`ConveysMemberWrite` accepts only the `member` attribute GUID or `Guid.Empty`, so a Deny
scoped to the Membership property set `bc0ac240-79a9-11d0-9020-00c04fc2d4cf` is skipped and a
following Allow authorises the write. Windows applies that Deny to `member`.

**5. Composite masks are treated as discarding attribute scope.** `GroupMembershipAce.cs:56`.
An object-specific Allow whose mask is `0x00020028` against an unrelated attribute GUID passes
immediately as `GenericWrite`. Expanded rights do not discard the ACE's `ObjectType` - the
write-property permission stays scoped to that attribute. The comment claiming these masks
always write all properties is wrong, **and `GroupMembershipAceTests.cs:26` explicitly enforces
the wrong assumption**, so the existing test suite defends the defect.

**6. Deny-only token SIDs are dropped.** `GroupMembershipChecker.cs:31`. `WindowsIdentity`
emits deny-only memberships as `DenyOnlySid` / `DenyOnlyPrimaryGroupSid`, and
`GroupClaimTypes` accepts neither. A Deny carried by such a group plus an individual Allow
still grants. **These need distinct handling** - adding them to the shared trustee set would
wrongly let their *Allow* ACEs grant access.

**7. The web token is not necessarily the resource domain's token.**
`SelfServiceGroupService.cs:539`. Where the target group's domain differs from the web
server's, an applicable domain-local membership can be absent from the web principal, so a
Deny it carries is skipped while an individual Allow succeeds. `aud-a4` acknowledged this as a
residual; it is recorded here as a live permissive gap.

## A correction to `aud-a4`'s own record

`aud-a4`'s Known gaps said primary-group membership may be missing from claims. **That is
wrong**: .NET emits it and `GroupMembershipChecker` already includes `PrimaryGroupSid`. I
conflated it with the domain-local case in finding 7 and with `aud-a3b`, where primary-group
invisibility is real because that check reads `memberOf`. Corrected in `aud-a4.md`.

## Predicted observable failure

Two directions, both live:

- A contractor with an inheritance-only Allow, or an object-specific `GenericWrite` on an
  unrelated attribute, edits a sensitive group's membership. Windows would refuse.
- An operator with a legitimate explicit Allow and an inherited group Deny is told they may no
  longer manage a group they can in fact manage.

## Approach - not designed

The gate needs faithful inputs before it can be a faithful check:

- carry `PropagationFlags` / `IsInherited` into `Entry` and honour inheritance-only;
- expand property sets, or at minimum treat the Membership set as covering `member`;
- stop treating composite masks as unscoped when the ACE carries an `ObjectType`;
- extract deny-only SIDs separately and apply them only to Deny ACEs;
- evaluate in canonical order - explicit before inherited, Deny before Allow *within* each
  group - rather than Deny-before-everything.

**The existing tests that enforce the wrong assumptions must be rewritten as part of this, not
worked around.** Reviewing the fix will also need counterexamples for each case above; the six
tests `aud-a4` added would all still pass if the service discarded group SIDs before calling
the evaluator, or if the evaluator stopped honouring individual Denies.

## Resolution

**Six of the seven are fixed. Finding 7 is carried.**

| # | What was done |
|---|---|
| 1 | Canonical evaluation: explicit before inherited, Deny before Allow *within* each level, first decisive match wins. An explicit Allow now defeats an inherited Deny. The ACEs are re-sorted rather than trusted in stored order, because `GetAccessRules` guarantees none and a non-canonical DACL must still be read canonically |
| 2 | `RemoveListedMemberAsync` refuses a null principal, like `ChangeMemberAsync` |
| 3 | `Entry` carries `IsInherited` and `InheritOnly`; the projection reads `IsInherited` and `PropagationFlags & 2`, and an inherit-only ACE applies to nothing on this object |
| 4 | `ConveysMemberWrite` accepts the `Membership` property set GUID as covering `member` |
| 5 | An object-scoped ACE keeps its scope whatever the mask, so `GenericWrite`/`GenericAll` against an unrelated attribute no longer conveys member-write. **The two tests that enforced the wrong assumption are rewritten, not worked around** |
| 6 | `GroupMembershipChecker.ExtractDenyOnlyGroupClaims` is new and deliberately separate; deny-only SIDs are passed as their own set and can only make a Deny apply, never satisfy an Allow |
| 7 | Fixed later, on `worktree-agent-acf74809ad7ee3f7d` - see "Plan for finding 7" and "Guard proof for finding 7" below |

A projection fault now defaults to "applies, not inherited" - the shape that gets evaluated
rather than skipped - so a missing flag cannot silently drop a Deny.

Eleven tests cover this, including both directions of inheritance-only and both halves of the
deny-only rule. Mutations run this session: removing the `InheritOnly` skip fails two; putting
Deny ahead of everything, which is what `aud-a4` did, fails `AnExplicitAllow_DefeatsAnInheritedDeny`.

## Known gaps

**Finding 7 stood when this was written** (it is fixed below): the web server's token is not
necessarily the resource domain's token. Where the target group lives in a different domain, an
applicable domain-local membership can be absent from the Negotiate principal, so a Deny it
carries is still invisible while an individual Allow succeeds. Closing it needs an
authoritative membership read against the resource domain rather than a claims read - the same
requirement as `aud-a3b`, and it should be built with it.

## Plan for finding 7 (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21), which
approves the carried finding.

**Mechanism.** When - and only when - the caller's token cannot decide the answer, read the
caller's memberships from the directory, with `aud-a3b`'s evaluator, against the group's OWN
domain, and let what is found make Denies apply.

1. A pure step, `GroupMembershipAce.DenyTrusteesTheTokenCannotDecide`, lists the Deny trustees
   that apply to this object, convey member-write and are absent from the caller's token -
   restricted to SIDs whose membership the directory records (domain account SIDs and BUILTIN
   aliases; Everyone, Authenticated Users and the other logon SIDs are the logon's own and the
   token already reports them). It returns nothing when the claims already refuse, or when the
   answer would still be "grant" with every one of those Denies applied - so the directory is
   only read when it could change the outcome.
2. `ResourceDomainDenies.ApplicableToCaller` resolves each such trustee to its group - a BUILTIN
   alias in the target group's own domain, where that alias lives under the same SID as in every
   other domain; an account SID forest-wide - and asks `ProtectedGroupMembership.Evaluate` whether
   the caller (found by SID across the forest) is a member. That evaluator already asks a
   domain-local group in its own domain, reads universal ancestry from the global catalog and
   counts the primary group. A trustee that is not a group cannot contain the caller.
3. Applicable trustees join the DENY-ONLY set and the DACL is evaluated again. They can make a
   Deny apply; they never satisfy an Allow - the conservative direction, and the one the finding
   is about. A legitimate Allow through another domain's domain-local group therefore stays
   unhonoured, as it is today.
4. Any read that fails, any trustee that resolves ambiguously, a caller that cannot be found, an
   incomplete evaluation or a group DN naming no domain makes `CallerCanManageMembers` return
   false: the group is excluded, or the write refused.

Self-Service Groups' scoped exception is untouched: this is the module's own DACL gate (whether
the caller may write the group in AD), not a Protected Group Target check, and the member
protection check is unchanged.

**Files.** `Services/SelfServiceGroups/GroupMembershipAce.cs`,
`Services/SelfServiceGroups/ResourceDomainDenies.cs` (new), `Services/SelfServiceGroups/SelfServiceGroupService.cs`,
`Services/ProtectedGroupMembership.cs` and `Services/PowerShellProtectedGroupDirectory.cs`
(a forest-wide object-by-SID read; `Result.MatchedEntries`), tests in
`ExchangeAdminWeb.Tests/GroupMembershipAceTests.cs` and a new `ResourceDomainDeniesTests.cs`, with
the fake forest moved to a shared test file.

**Mutation, at the call site.** In the composed decision, skip the resource-domain step and
evaluate the claims alone: the finding-7 test fails. Drop the "would it change the answer"
shortcut's worst-case evaluation: the test pinning that no read happens fails.

## Guard proof for finding 7

`ExchangeAdminWeb.Tests/ResourceDomainDeniesTests.cs`, driven against the shared
`ExchangeAdminWeb.Tests/FakeForest.cs` (moved out of `ProtectedGroupMembershipTests` unchanged,
plus a forest-wide object-by-SID read). The caller is in domain A; the group whose DACL is
evaluated, and the domain-local group carrying the Deny, are in domain B; the caller's token is
A's groups only.

| Test | What it pins |
| --- | --- |
| `ADenyCarriedByTheGroupsOwnDomainLocalGroup_RefusesTheCaller` | finding 7 itself - and it asserts first that the token alone GRANTS, so the refusal is the fix's |
| `TheSameDeny_DoesNotRefuseSomeoneOutsideTheGroup` | the opposite direction, and that the directory really was asked |
| `ADenyTheTokenAlreadyCarries_IsDecidedWithoutTheDirectory`, `AnUnknownInheritedDenyBelowAnExplicitAllow_DoesNotNeedTheDirectory` | no read when the token decides, or when no Deny could change the answer |
| `ADenyForAnotherAccount_DoesNotRefuse` | a non-group trustee cannot contain the caller |
| `ABuiltinAlias_IsResolvedInTheGroupsOwnDomain` | a BUILTIN alias is the group's own domain's copy, not ambiguous and not the caller's domain's |
| `AReadThatFails_Refuses` (5 reads), `ACallerWhoCannotBeFound_Refuses` | fail closed on every read |
| `AMembershipFoundInTheDirectory_CanOnlyDeny_EvenWhereItsAllowIsDecidedFirst` | what is found joins the deny-only set: an explicit Allow for the same group, which canonical order reaches first, does not grant |
| `AnAllowCarriedByTheGroupsOwnDomainLocalGroup_IsStillNotHonoured` | the conservative half, recorded below as a gap |
| `TheServiceGate_RoutesThroughTheResourceDomainDecision` | code-view tripwire: `CallerCanManageMembers` returns the composed decision, not the token-only one |
| `GroupMembershipAceTests`: `AnUnknownDenyThatWouldRefuse_IsListed`, `NothingIsListed_*` (3), `InheritOnlyAndNonMemberWriteDenies_AreNotListed`, `LogonSids_AreTheTokensToReport_NotTheDirectorys` (3), `IsDirectoryRecordedSid_*` (6) | the pure step that decides when the directory is needed |

Mutations, each run this session; every file restored byte-identical, touched and rebuilt green:

| Mutation | File md5 (before = restored) | Mutated md5 | Named test | Result |
| --- | --- | --- | --- | --- |
| composed decision skips the resource-domain step (call site) | `ResourceDomainDenies.cs` `085006acbe3c420ebb251f495e00b5e4` | `fff0064dd0dcac65d5e78e291df4cdc4` | `ADenyCarriedByTheGroupsOwnDomainLocalGroup_RefusesTheCaller` | FAIL |
| `CallerCanManageMembers` returns the token-only evaluation (service call site) | `SelfServiceGroupService.cs` `08149cfae87002a218baf5a365b62631` | `bddb33e0075a61d40f56d04d91190235` | `TheServiceGate_RoutesThroughTheResourceDomainDecision` | FAIL |
| found memberships join the GRANTING set | `ResourceDomainDenies.cs` `085006acbe3c420ebb251f495e00b5e4` | `bda7c04625ebf1fe13fd2311a859ac2d` | `AMembershipFoundInTheDirectory_CanOnlyDeny_EvenWhereItsAllowIsDecidedFirst` | FAIL |
| drop the worst-case shortcut (helper; efficiency, not a security guard) | `GroupMembershipAce.cs` `72ee0957f6075a5fc4d6a09e42b106ec` | `44a8d53ac63abdd0555eb396520e7587` | `NothingIsListed_WhenNoUnknownDenyCouldChangeTheAnswer` | FAIL |

**Finding 7's remaining gaps:**

- Whether finding 7 was reachable before this fix is itself unverified. Every self-service group
  read (`GetOwnedGroupsAsync`, `SearchManageableGroupAsync`, `ResolveGroupByGuid`, and the DACL read
  in `CallerCanManageMembers`) omits `-Server`, so it binds to the AD module's default domain, and a
  group elsewhere fails that read and is excluded. If the default is the web server's own domain -
  the domain whose domain-local groups the token carries - the gap was latent, and would have
  opened the moment those reads were routed to the group's own domain the way every other module's
  reads have been. The fix does not depend on which: it settles the Denies against the group's own
  domain, taken from its DN.
- A grant that exists only through a domain-local group of another domain is still not honoured.
  The directory is read only to make Denies apply; widening grants on that read was not needed to
  close the finding.
- A Deny naming a well-known logon SID (Everyone, Authenticated Users, Network, ...) is decided by
  the token alone, on the reasoning that those memberships belong to the logon session and the
  web logon is a network logon, as an ADUC write would be. A Deny naming a principal no longer in
  the forest is treated as applying to nobody.
- Self-service now reads the directory - the caller's object, its groups, the global catalog and
  the group's own domain - only when a member-write Deny names a group the token does not carry
  and could change the answer. When it does and any of that fails, the group is excluded or the
  write refused. The extra reads have not been measured.

All seven were source-level and specification-based when raised, and the fixes are verified
the same way. **Nothing here has run against a real directory, and probing AD is forbidden
without a direct owner go.** In particular the `PropagationFlags & 2` test for
`INHERIT_ONLY_ACE` and the Membership property-set GUID are taken from Microsoft's
documentation, not observed on this forest.

The evaluator is pure and fully tested; the projection from `PSObject` into `Entry` still is
not, because it needs a domain controller.

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `a5b6216` against `2bf988a`. All
seven findings. It confirmed all three read callers supply `callerGroupSids` and that the
ClickGate count is correct. Raw output: `.agents/review/out/aud-a4.txt` (machine-local,
gitignored).

Finding 7 implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-acf74809ad7ee3f7d`. Independent review of that fix pending.

## Closeout

Findings 1-6 fixed on `master` earlier. Finding 7 fixed on branch, not merged, not pushed; it
changes Self-Service Groups' own DACL gate (module version) and adds a read to the shared
protected-group directory seam (`IProtectedGroupDirectory.FindObjectDnsBySid`).

The earlier note stands as history: **`aud-a4` was net-positive but incomplete** - it closed a
real bypass and introduced a conservative over-refusal, which finding 1's fix removed.
