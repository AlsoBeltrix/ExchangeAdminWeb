# gmn-7: Changing groups during an add can write the held picker object to the wrong group

**Severity**: HIGH - a write lands in a different AD group than the operator initiated it
for, with a blank member label in the audit record.
**Status**: Verified
**Branch**: `-` (default-branch mode)
**Commit**: `b8379dc`

Reviewer-raised (generation pass over `3f2ab21..45b95e9`).

## Evidence
`Components/Pages/GroupManagement.razor` - AddMember awaits ticket validation, then reads
the MUTABLE `selectedGroup`, `newMember`, and `newMemberSelection`; group Manage buttons
stay clickable during `isLoading`, and `SelectGroup` clears `newMember` but not
`newMemberSelection`. RemoveMember has the same shape (reads `selectedGroup` after its
awaits). The S5c test checked only keystroke clearing.

## Predicted observable failure
Operator starts adding a picked member to group A; clicks Manage on group B while ticket
validation is pending; the resumed handler writes A's held member DN into group B, audit
label blank. A remove started from A's list can likewise execute against B.

## What
In-flight handlers consumed page state that group navigation mutates mid-await.

## Approach
Both handlers snapshot everything they act on - the selected group, the visible identity,
and the held picker selection - BEFORE their first await, and use only the snapshots
through validation, the service call, audit, notify, and refresh. `SelectGroup`
additionally clears the held selection (with `newMember`), so a group switch can never
inherit a stale DN. The write therefore always targets the group the operation was
initiated for.

## Files changed
- `Components/Pages/GroupManagement.razor` - snapshots in AddMember and RemoveMember;
  SelectGroup clears `newMemberSelection`
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` - snapshot-order and
  SelectGroup-clear tripwires

## Guard proof
Reverting the snapshots (reading page state after the awaits again) makes the new
tripwire FAIL; restoring makes it PASS. Page behaviour has no executing harness in this
repo; the compensating manual check rides the plan's deploy checklist.

## Coder dispute (if any)
None - admitted as written. Disabling group navigation while a mutation is pending was
considered and not taken: snapshots remove the correctness hazard, and freezing navigation
is a UX decision the owner did not ask for.

## Known gaps
The audit member label for a picker-selected add is addressed separately in gmn-9.

## Reviewer comments
Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only)
codex-cli 0.147.0, reviewed 45b95e901189addc4e60df403f019362b8089619, base
3f2ab2191399e07de02e3c71cdb1724423df4e07, capability_ok true, verdict findings (2 of 4),
2026-08-27 UTC.

Verification: Reviewer: codex / gpt-5.6-sol / xhigh (inline, session-only), routed per T2 as
gmn-6, workspace-write sandbox per machines.md. Verdict ACCEPTED, guard_confirmed true,
capability_ok true, reviewed b8379dc2337b8d5d48683926855dc3b2f1d0e7d5 base
0b4b72ef53ad08db302294d48b8f1a343df7727e, no comments, 2026-08-27 UTC. Working tree verified
untouched after the run.
