# lst-3: Admin listing accepts output from an errored membership read

**Severity**: MEDIUM - an errored read can present as an empty or truncated member list
(Known Failure Class #2), on the admin module only.
**Status**: In progress (fix landed; independent verification NOT DISPATCHED - blocked by the
workspace-write transport fault recorded on lst-1, per the playbook's terminal-denial rule)
**Branch**: `-` (default-branch mode)
**Commit**: `0667099`

## Evidence

`Services/GroupManagementService.cs` GetMembersAsync (post-3b766ca) takes the first
Get-ADGroup output and checks only for null - it never consults `ps.HadErrors`. The
self-service twin explicitly rejects `ps.HadErrors`
(`Services/SelfServiceGroups/SelfServiceGroupService.cs` GetGroupMembersAsync). `MemberDnsOf`
treats an absent `member` property as an empty group, so an object emitted alongside a
non-terminating retrieval error reads as "no members". Additionally, earlier
`ResolveAdGroupIdentity` candidates run with SilentlyContinue and can leave entries in the
shared error stream.

## Predicted observable failure

Get-ADGroup emits the group object while reporting an error retrieving the linked property (or
a later range): the admin page presents zero or a subset as the complete membership instead of
a read error.

## What

The fail-closed check that the self-service path carries was not mirrored on the admin path -
the two twins disagree about what an errored read means.

## Approach

Mirror the self-service rule: clear the error stream before the membership read, and treat
`ps.HadErrors || null` as a read failure setting `GroupMemberList.Error` ("The group's
membership could not be read.") before any member is projected - never as an empty group.

## Files changed

- `Services/GroupManagementService.cs` - pre-read stream clear + HadErrors rejection
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` /
  `GroupMemberListingTests.cs` - tripwire pinning the rejection

## Guard proof

- `GroupMemberListingTests::AdminListing_RejectsAnErroredMembershipRead` - tripwire: the
  HadErrors rejection precedes member projection. Reverting the fix makes it FAIL; restoring
  makes it PASS.

## Coder dispute (if any)

None.

## Known gaps

`-ErrorAction Stop` makes most failures throw before this check; the guard covers the
non-terminating remainder, which no unit harness can execute against a live directory - the
tripwire pins the wiring, matching the repo's established pattern.

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner-named pair, dispatch
2026-08-28; codex-cli 0.150.1 via Headroom proxy; wrapper exit-code -1 quirk noted)

Generation pass over `fbf37ac..3b766ca`, verdict `findings` (3), `capability_ok: true`.
