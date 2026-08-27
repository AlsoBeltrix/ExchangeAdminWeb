# gmn-5: S4 tripwires do not enforce the safety properties they claim

**Severity**: MEDIUM - CI stays green after a refactor that lets a protected group removal
reach the write, or turns group removal into a one-click action.
**Status**: Verified
**Branch**: `-` (default-branch mode)
**Commit**: `d2b2a6e`

Reviewer-raised (generation pass over `695e73f..3f2ab21`, S3+S4 of
`docs/GroupMemberNesting-Plan.md`).

## Evidence
`ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` - the S4 tripwires searched the
ENTIRE service file for call strings and counted protection-gate call sites, but never asserted
that a denial RETURNS before the executor; the page tripwire asserted method names and warning
text existed anywhere. Deleting the denial return in `RemoveListedMemberAsync`, or wiring the
group button straight to `ConfirmGroupRemoval`, left every test green.

## Predicted observable failure
A refactor deletes `if (protection.Denial is not null) return ...` (protected group removals
reach the write) or bypasses `BeginGroupRemoval` (one-click group removal): the suite passes.

## What
Comment-satisfiable / order-blind source assertions - the exact guard-test weakness the repo's
blr-3/blr-4 lesson names, reproduced in new tests.

## Approach
Both tripwires rebuilt as bounded, ordered assertions:
`RemoveListedMemberAsync_ResolvesGatesAndDenies_InOrder_BeforeTheSharedExecutor` bounds the
method body and asserts resolve -> gate -> denial-check -> denial-RETURN -> executor in index
order; `Page_RemovesByGuid_AndGroupRowsOnlyEnterThePendingState` bounds the Group branch of the
row markup (BeginGroupRemoval only - no confirm, no direct removal), and pins that
`ConfirmGroupRemoval(member)` occurs exactly once, inside the pending-state block.

## Files changed
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` - the two tests above replace
  their loose predecessors

## Guard proof
Mutations from the finding itself: delete the denial return -> the order test FAILS; route the
group button to `ConfirmGroupRemoval` directly -> the page test FAILS. Both reversed, suite
green.

## Coder dispute (if any)
None - admitted as written.

## Known gaps
Source tripwires remain weaker than an executing harness; the compensating controls are the
pure-rule unit tests and the plan's manual checks.

## Reviewer comments
Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only)
codex-cli 0.147.0, reviewed 3f2ab2191399e07de02e3c71cdb1724423df4e07, base
695e73f651b32e71037e2706e913be01aff6e755, capability_ok true, verdict findings (this is two of
two), 2026-08-27 UTC.

Verification: Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only),
codex-cli 0.147.0. Round 1 (read-only sandbox) returned verdict "invalid" solely because the
isolated guard proof could not run ("verification must fail closed"; static inspection found
no adjacent regression) - recorded as a transport failure. Round 2 (workspace-write, the
playbook's one retry): verdict ACCEPTED, guard_confirmed true, capability_ok true, reviewed
d2b2a6e53c478dda143b6c97a9ef0029c930e844 base 76e0eb7eaf3a5a4458281752215c11ef54b63142, no
comments, 2026-08-27 UTC. Working tree verified untouched after the run.
