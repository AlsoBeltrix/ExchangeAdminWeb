# Self-Service Groups — Member Listing + AD Member Picker

Status: Approved (owner, 2026-07-28). Follow-up enhancement to the shipped GM-3
module (`docs/SelfServiceGroupManagement-Plan.md`, Implemented). Two additive
changes; no change to the credential-isolation write contract.

## Problem

The shipped Self-Service Groups page has two usability gaps (owner-identified
2026-07-28), both feature omissions, not defects:

1. **No member visibility.** The "Manage members" panel
   (`Components/Pages/SelfServiceGroups.razor:167-207`) has no member list. The
   only workflow is blind add/remove by typed identity — a manager cannot see who
   is currently in the group, and to remove someone must already know and type
   their exact identity. The first cut deliberately shipped without a listing
   method (`SelfServiceGroupManagement-Plan.md` §6.5, "only mutation in first cut").

2. **The member identity box is a raw `<input>`.** Line 184 is a plain text field
   requiring an exact typed identity, unlike the rest of the app, which uses the
   shared AD typeahead controls (`ADIdentityAutocomplete`, used in
   `AdminSettings.razor` and `ModuleConfig.razor`).

## Decisions (owner-ruled)

- **Member-picker identity source = Option A (owner, 2026-07-28).** Reuse the
  existing `Components/Shared/ADIdentityAutocomplete` as-is. Its live suggestion
  search runs under the app-pool ambient identity
  (`Services/ADDirectorySearchService.cs:8`), NOT this module's Delinea credential.
  This is accepted: the picker only assists typing a name; the actual write still
  routes through `SelfServiceGroupService.ChangeMemberAsync`, which re-resolves the
  member USER-ONLY under the module credential, runs the protected-principal check,
  re-checks eligibility, and read-back-reconciles before/after the write. A
  suggestion from the weaker read path can never cause a bad write. Building a
  second, module-credentialed typeahead (Option B) was declined as not worth the
  extra code/test surface for a read-only suggestion list. Recorded in
  `.agents/decisions.md` 2026-07-28.

## Non-goals

- No change to the write contract in `ChangeMemberAsync` — its credential
  isolation, user-only resolution, protected-principal gate, per-group serialization,
  and post-write read-back are unchanged. Remove-from-list reuses it verbatim.
- Membership listing/removal stays USER-scoped for the removable action, matching
  the first-cut user-only write constraint (`SelfServiceGroupManagement-Plan.md`
  §6.5, codex F7). Non-user members (nested groups, computers, service principals)
  are shown read-only so the manager sees full membership but cannot remove them
  through this path.
- The group-search box (`SelfServiceGroups.razor:127`) and the loaded-groups
  filter (line 71) are NOT changed in this plan. Only the member identity box gets
  the picker. (Group search resolves group names, a separate control choice; out of
  scope here to keep the change bounded.)
- No paging UI for very large groups in this cut; see Open Questions.

## Acceptance criteria

- AC-M1: Selecting a group ("Manage members") loads and displays its current
  members — display name, identity (sAMAccountName/UPN), and member kind
  (User / Group / other). The list loads on selection, not on page open.
- AC-M2: Each USER member has a Remove button that runs the existing
  `ChangeMemberAsync(Remove)` path (all its re-checks intact) and, on success,
  refreshes the displayed list. Non-user members are shown without a Remove button.
- AC-M3: A member-list read failure shows a clear error, never an empty list
  presented as "no members" (fail-closed display, Known Failure Class #2).
- AC-M4: Eligibility is re-checked (fail-closed, `CallerCanManageMembers`) before
  members are returned; a caller who cannot manage the group gets no member list.
- AC-M5: The member identity box for add is `ADIdentityAutocomplete`
  (`ObjectKind="User"`, `ReturnValueKind` chosen so the returned value is an
  identity `ChangeMemberAsync` resolves — SAM or UPN), replacing the raw `<input>`
  at line 184. Add still works by typing + picking a suggestion or typing a full
  identity.
- AC-M6: Adding via the picker still succeeds/fails through the unchanged write
  path; the picker changes only how the identity string is entered.

## Design

### Service (`Services/SelfServiceGroups/SelfServiceGroupService.cs`)

New method:

```
public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(
    string callerSid, string groupObjectGuid)
```

- Validate `callerSid` as a genuine SID (reuse `IsSecurityIdentifier`), reject
  blank `groupObjectGuid` — same guard style as the existing methods.
- Fetch this module's credential (`ModuleCredentialService.GetCredentialsAsync
  ("SelfServiceGroups", ...)`); throw `InvalidOperationException` if unavailable so
  the page surfaces an error (never an empty list — AC-M3).
- Run in a per-operation runspace via `ThrottledAdAsync` + `Task.Run`, same shape
  as `GetOwnedGroupsAsync`: `PrepareAdRunspace(ps)`, build credential, resolve the
  group by objectGUID (`ResolveGroupByGuid`) — null => throw (group gone).
- **Re-check eligibility (AC-M4):** `CallerCanManageMembers(ps, credential,
  groupDn, callerSid)` — false => throw a not-authorized `InvalidOperationException`
  (fail-closed; the page shows the error, not an empty list).
- Read members with bound `-Identity` (objectGUID or DN), `-Credential`,
  `-ErrorAction Stop`. Prefer `Get-ADGroupMember -Identity <guid>` (returns
  objectClass per member). Project each member to `GroupMember` primitives in
  PowerShell (no S.DS types cross into C#), same discipline as
  `CallerCanManageMembers`. On `ps.HadErrors` after the read, throw (fail-closed).
- Determine `IsRemovable` from the member's objectClass: `user` (and not a
  computer) => removable. Delegate the classification to a pure, unit-testable
  static so it is covered without AD (see below).

New model `Services/SelfServiceGroups/GroupMember.cs`:

```
public sealed record GroupMember
{
    public required string ObjectGuid { get; init; }
    public required string DistinguishedName { get; init; }
    public required string DisplayName { get; init; }
    public string Identity { get; init; } = "";   // sAMAccountName/UPN for the Remove call
    public string Kind { get; init; } = "";        // "User" / "Group" / "Computer" / ...
    public bool IsRemovable { get; init; }         // true only for user members (first-cut write scope)
}
```

Pure classifier (unit-testable, no AD) — e.g. a static
`GroupMember.ClassifyRemovable(string objectClass)` or a small
`GroupMemberClassifier` type mirroring `GroupMembershipAce`: returns Kind + whether
removable from the raw objectClass string. This is the non-vacuous test target.

### Page (`Components/Pages/SelfServiceGroups.razor`)

- In `SelectGroup`, after setting `selected`, load members: call
  `GroupService.GetGroupMembersAsync(callerSid, group.ObjectGuid)` (async; make
  `SelectGroup` async or kick off a load method), with an `isLoadingMembers`
  spinner and a `memberLoadError` string. Store `IReadOnlyList<GroupMember>`.
- Render a member table in the manage panel: DisplayName, Identity, Kind, and a
  Remove button only when `IsRemovable`. Empty (genuinely no members, no error) =>
  a neutral "This group has no members." On `memberLoadError` => alert-danger.
- Remove button => calls the existing `ChangeMember(MembershipOperation.Remove)`
  with that member's `Identity` (set `memberIdentity` then invoke, or refactor
  `ChangeMember` to take an explicit identity). On success, reload the member list
  so the row disappears (AC-M2).
- Replace the raw member `<input>` (line 184) with:
  `<ADIdentityAutocomplete ObjectKind="User" ReturnValueKind="UPN"
   @bind-Value="memberIdentity" Placeholder="Search for a user..."
   Disabled="isChanging" />` (confirm `ReturnValueKind` yields a value
   `ResolveUserMember`'s `BuildUserByIdentityFilter` accepts — UPN or SAM).
- After a successful add, keep the existing `memberIdentity = ""` reset and reload
  the member list so the new member appears.

### Versioning

Module-scoped behavior change: bump `SelfServiceGroups` `Version` in
`Modules/ModuleCatalog.cs` `1.1.1 -> 1.2.0` (new feature). No base app-version bump
(Constitution §Deployment And Versioning; adding behavior to an existing module is a
module bump only). Update the module comment.

## Slices (commit each; one finding/fix per commit)

1. Model + pure classifier + its unit test (`GroupMember`, classifier,
   `GroupMemberClassifierTests`). Prove non-vacuous (revert classifier, test fails).
2. `GetGroupMembersAsync` service method (eligibility re-check, fail-closed read,
   projection). Build + test green.
3. Page: member listing UI + load-on-select + error/empty states.
4. Page: per-row Remove wired to `ChangeMember(Remove)` + list refresh.
5. Page: swap member `<input>` for `ADIdentityAutocomplete`; add-refreshes-list.
6. Module version bump `1.1.1 -> 1.2.0` + comment.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`, `dotnet test
  ExchangeAdminWeb.slnx`, `dotnet format ExchangeAdminWeb.slnx
  --verify-no-changes --no-restore`, `git diff --check HEAD`. ASCII-only in all
  touched `.cs` (CI lint).
- New pure classifier has a non-vacuous unit test (slice 1).
- Live/manual (runs against PROD AD from the dev instance — no separate tenant):
  member list renders for an owned+eligible group; Remove of a user member works
  and the row disappears; a non-user member shows read-only; add via the picker
  succeeds; a group the caller cannot manage yields the error, not an empty list.
  Not covered by automated tests; state clearly if not run.

## Open questions

- Large-group paging: `Get-ADGroupMember` on a very large group returns all
  members; if this proves heavy in live validation, add a cap/paging in a follow-up.
  Not addressed in this cut.
- Nested-group expansion: members are listed direct-only (no recursive expansion),
  consistent with the direct-membership write model.
