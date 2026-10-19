# aud-a4b: The self-service DACL gate is not a faithful Windows access check

**Severity**: HIGH - unauthorized writes remain possible, and some valid grants are now
refused.
**Status**: Open - not approved for implementation
**Branch**: -
**Commit**: -

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

## Known gaps

All seven are source-level and specification-based. The reviewer said so, and I have not
reproduced them either - **nothing here has run against a real directory, and probing AD is
forbidden without a direct owner go.**

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `a5b6216` against `2bf988a`. All
seven findings. It confirmed all three read callers supply `callerGroupSids` and that the
ClickGate count is correct. Raw output: `.agents/review/out/aud-a4.txt` (machine-local,
gitignored).

## Closeout

Pending owner approval. **`aud-a4` is net-positive but incomplete**: it closes a real bypass
and introduces a conservative over-refusal. Whether to leave it in place while this is built,
or revisit it, is the owner's call.
