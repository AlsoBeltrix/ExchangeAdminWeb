# lst-2: The member-attribute source swap silently omits primaryGroupID members

**Severity**: MEDIUM - a real member class disappears from a complete-looking list; rare in
admin-managed groups (primary group is almost always Domain Users) but silent when it happens.
**Status**: In progress (fix landed; independent verification NOT DISPATCHED - blocked by the
workspace-write transport fault recorded on lst-1, per the playbook's terminal-denial rule)
**Branch**: `-` (default-branch mode)
**Commit**: `550a8dc`

## Evidence

Both listings after 3b766ca enumerate only the group's linked `member` attribute
(`Services/GroupManagementService.cs` GetMembersAsync;
`Services/SelfServiceGroups/SelfServiceGroupService.cs` GetGroupMembersAsync). Membership
expressed through a principal's `primaryGroupID` is not stored in `member`, but WAS included in
the replaced `Get-ADGroupMember` output. No supplementary query or incompleteness indicator
exists.

## Predicted observable failure

A user or computer whose primaryGroupID points at the selected group is absent from both
modules' counts and tables; the operator sees an apparently complete list missing a real
member. The new tests stay green because they only exercise `member` values.

## What

The source swap changed enumeration semantics, not just transport: `member` is the LINKED
membership, `Get-ADGroupMember` was linked + primary. The fix restored availability at the cost
of a silently narrower definition of "members".

## Approach

Union the primary members back in. Primary-group membership cannot cross domains, so ONE extra
query against the group's own domain suffices: read the group's SID (returned by the same
Get-ADGroup call), derive the RID, query `(primaryGroupID=<RID>)` routed via `ServerFromDn` of
the group DN, and append rows deduplicated by DN. Primary rows are marked and NOT removable -
`Remove-ADGroupMember` cannot remove a member from its primary group, so offering Remove would
be an always-failing affordance: admin rows carry `IsPrimaryMember` (page disables Remove,
titled), self-service rows get `IsRemovable = false`. RID derivation is a pure helper
(`RidFromSid`) with unit tests.

## Files changed

- `Services/GroupManagementService.cs` - primary-member query + `RidFromSid` + `IsPrimaryMember`
- `Services/SelfServiceGroups/SelfServiceGroupService.cs` - same union, rows non-removable
- `Components/Pages/GroupManagement.razor` - Remove disabled on primary rows
- `ExchangeAdminWeb.Tests/GroupMemberListingTests.cs` - RidFromSid tests + tripwires

## Guard proof

- `GroupMemberListingTests::RidFromSid_*` - pure derivation.
- `GroupMemberListingTests::Listings_UnionPrimaryGroupMembers_*` - tripwires pinning the
  primaryGroupID query, the DN dedupe, and the non-removable marking in both services.
  Reverting the fix makes these FAIL; restoring makes them PASS.

## Coder dispute (if any)

None on substance. Scale note recorded: for AD's default primary groups (Domain Users /
Domain Computers) the RID query enumerates the domain, exactly as the replaced
Get-ADGroupMember did - parity, not a regression.

## Known gaps

Live behaviour needs the deploy-time manual check (list a group holding a primary member).

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner-named pair, dispatch
2026-08-28; codex-cli 0.150.1 via Headroom proxy; wrapper exit-code -1 quirk noted)

Generation pass over `fbf37ac..3b766ca`, verdict `findings` (3), `capability_ok: true`.
