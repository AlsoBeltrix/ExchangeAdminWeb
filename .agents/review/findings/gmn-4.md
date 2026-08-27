# gmn-4: Group-removal confirmation survives a change of managed group

**Severity**: MEDIUM - one click can remove a nested group from a DIFFERENT group than the one
the warning was opened for, erasing D2's required second action.
**Status**: Verified
**Branch**: `-` (default-branch mode)
**Commit**: `6452ce9`

Reviewer-raised (generation pass over `695e73f..3f2ab21`, S3+S4 of
`docs/GroupMemberNesting-Plan.md`).

## Evidence
`Components/Pages/SelfServiceGroups.razor` - the confirm row rendered on
`pendingGroupRemoval?.ObjectGuid == member.ObjectGuid` (member GUID alone), while
`pendingGroupRemoval` was set in `BeginGroupRemoval` and never cleared by group selection,
`BackToGroups`, or `LoadMembers`.

## Predicted observable failure
Open the warning for nested group G under group A, go back, manage group B that also contains
G. B immediately shows the stale confirmation; clicking "Remove group" is the FIRST action on B
yet removes G from B - violating D2 (warn + second action) on that group.

## What
The pending-confirmation state was keyed only by the member and had no lifecycle tied to the
selected group or the member list it was rendered against.

## Approach
Two changes, both in the page: `LoadMembers()` now clears `pendingGroupRemoval` (it runs on
every group selection and after every change, so a confirm never survives a reload or switch),
and `ConfirmGroupRemoval` revalidates that the pending row IS the clicked row before acting - a
stale or mismatched state performs no removal.

## Files changed
- `Components/Pages/SelfServiceGroups.razor` - clear in `LoadMembers`, guard in
  `ConfirmGroupRemoval`
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` -
  `PendingGroupRemoval_IsClearedOnReload_AndRevalidatedOnConfirm`

## Guard proof
`GroupMemberNestingProtectionTests::PendingGroupRemoval_IsClearedOnReload_AndRevalidatedOnConfirm`
- reverting the two page changes makes it FAIL; restoring makes it PASS (no bUnit harness
exists, so the proof is the bounded source tripwire plus the manual check in the plan's
section: AC5 on a real nested group).

## Coder dispute (if any)
None - admitted as written.

## Known gaps
Page behaviour is provable only by tripwire + manual check in this repo (no bUnit); the manual
AC5 check on dev still rides the next deploy.

## Reviewer comments
Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only)
codex-cli 0.147.0, reviewed 3f2ab2191399e07de02e3c71cdb1724423df4e07, base
695e73f651b32e71037e2706e913be01aff6e755, capability_ok true, verdict findings (this is one of
two), 2026-08-27 UTC.

Verification: Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only),
codex-cli 0.147.0. Round 1 (read-only sandbox) could not create the isolated proof tree and
honestly returned guard_confirmed false - recorded as a transport failure, not a verdict.
Round 2 (workspace-write, the playbook's one retry with the worktree capability the
self-permissioning contract grants): verdict ACCEPTED, guard_confirmed true, capability_ok
true, reviewed 6452ce9a673b9a61ae9d53e4696d31fc8e1438e3 base
d2b2a6e53c478dda143b6c97a9ef0029c930e844, no comments, 2026-08-27 UTC. Working tree verified
untouched after the run.
