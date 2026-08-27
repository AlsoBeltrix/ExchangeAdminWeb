# gmn-6: Resolved USER principals bypass the authoritative protection gate

**Severity**: HIGH - a protected user can be written (removed or added) when supplied by
GUID/DN with a label the string pre-gate cannot resolve; the gate this stream built for
groups was conditional and skipped exactly the class the pre-gate already misses.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: the commit that adds this record

Reviewer-raised (generation pass over `3f2ab21..45b95e9`, S5a-S6 of
`docs/GroupMemberNesting-Plan.md`).

## Evidence
`Services/GroupManagementService.cs` - AddMemberAsync ran `CheckResolvedMemberAsync` only
when `resolvedMember.IsGroup`; RemoveMemberAsync only when `IsGroup || label blank`.
`Components/Pages/GroupManagement.razor` passes `DisplayName` as the label for a listed
member with empty Email (non-blank), then the GUID: the DisplayName pre-gate resolves
NotFound and passes through, GUID resolution finds the real - possibly protected - user,
and the resolved gate is skipped. The seam tests exercised only `IsGroup: true`.

## Predicted observable failure
A mail-less user protected via a Groups rule is removed by GUID: pre-gate NotFound on the
display name, resolved-user gate skipped, `Remove-ADGroupMember` executes. A direct
`AddMemberAsync` caller pairs an innocuous label with a protected user's `memberDn` and
writes the protected object.

## What
The resolve-once-then-gate contract (gmn-1) was implemented conditionally: the resolved
principal was gated for groups but trusted-by-label for users, and the label is attacker- or
accident-controlled.

## Approach
The resolved principal is ALWAYS gated: both write paths now run `CheckResolvedMemberAsync`
on whatever resolution produced - user or group - after the (kept) user-alias Exchange
pre-gate. The gate's outcome supplies the serviced note. Seam tests add the exact bypass
shape: pre-gate NotFound, resolved USER protected, refusal asserted plus the gate
consultation on the resolved principal.

## Files changed
- `Services/GroupManagementService.cs` - unconditional resolved-principal gate in
  AddMemberAsync and RemoveMemberAsync
- `ExchangeAdminWeb.Tests/GroupManagementServiceTests.cs` - resolved-USER bypass tests
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` - order tripwires updated

## Guard proof
Reverting the unconditional gate (restoring the IsGroup condition) makes the new
resolved-user seam tests FAIL; restoring makes them PASS.

## Coder dispute (if any)
None - admitted as written. The double-check for typed users (pre-gate plus resolved gate)
is accepted cost; correctness outranks one extra CheckAsync.

## Known gaps
Typed identities still pre-gate on the string (kept deliberately for the pinned alias
bypass); the resolved gate is now the authoritative one either way.

## Reviewer comments
Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only)
codex-cli 0.147.0, reviewed 45b95e901189addc4e60df403f019362b8089619, base
3f2ab2191399e07de02e3c71cdb1724423df4e07, capability_ok true, verdict findings (1 of 4),
2026-08-27 UTC. Verification round pending (T2: HIGH routes frontier; owner goal-directive
named codex/gpt-5.6-sol@xhigh for this stream's reviews - recorded as fallback accepted).
