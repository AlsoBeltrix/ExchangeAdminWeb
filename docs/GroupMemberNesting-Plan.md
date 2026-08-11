# Nested Group Membership - Self-Service Refusal, Admin Support

Status: Draft, pending owner ruling on OQ1 (affected-user notification when the removed
member is a group). Owner rulings D1-D5 below are given; every slice except S4's
notification behaviour may proceed behind them. Covers two modules
(`SelfServiceGroups`, `GroupManagement`) and one shared service
(`ProtectedPrincipalService`).

Reviewed as a plan by openreview `codex`
(`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh, grade fallback) over
`618235e..074bfdb`: verdict `acceptable_with_changes`, three findings, all admitted and
all folded in - `gmn-1` (HIGH, `8fb5118`), `gmn-2` (HIGH, `c414619`), `gmn-3` (MEDIUM,
`c7897d1`). Records in `.agents/review/findings/gmn-*.md`. **The revisions are load-bearing
and are not optional polish:** as first written this plan would have shipped group writes
that skip the protection gate, a cycle guard that refuses legitimate adds while allowing
real cycles, and a picker that can write a different group from the one chosen. Implement
from the current text, not from the pre-review shape.

## Problem

Nested-group membership is unsupported in both AD group modules, in different ways and
for different reasons.

### Self-Service Groups (`SelfServiceGroups`, module 1.3.0)

A group owner who types a group name into the member box gets
`'<name>' did not match exactly one user.` - a message that reads as a typo, not as a
scope limit. The module is user-only by construction in four places:

1. `Components/Pages/SelfServiceGroups.razor:201` - the typeahead is
   `ObjectKind="User" ReturnValueKind="UPN"`, so groups never appear as suggestions and
   a group has no UPN to return.
2. `Services/SelfServiceGroups/AdOwnershipFilter.cs:97` -
   `BuildUserByIdentityFilter` emits `(&(objectCategory=person)(objectClass=user)...)`;
   `SelfServiceGroupService.cs:648` runs it through `Get-ADUser`. A group matches zero
   rows, `ResolveUserMember` returns null, and `ChangeMemberAsync` fails at
   `SelfServiceGroupService.cs:390`.
3. `SelfServiceGroupService.IsMemberOfGroup` (`:679-701`) queries `Get-ADUser`, so both
   the idempotency pre-check and the post-write read-back are blind to a group member.
4. `Services/SelfServiceGroups/GroupMemberClassifier.cs:38` - `IsRemovable` is true only
   for `objectClass=user`, so a nested group is listed (`Kind = "Group"`) with no Remove
   button.

Nested groups therefore accumulate and cannot be removed by the owner who owns them.

### AD Group Management (`GroupManagement`, module 2.2.0)

This page is admin-audienced (`Components/Pages/GroupManagement.razor:3`,
`[Authorize(Policy = "GroupManagement")]`, main permission `FailClosed: true`) and is
also user-only, but without any deliberate constraint behind it:

1. `GroupManagementService.GetMembersAsync` (`:222-240`) resolves every member with
   `Get-ADUser -Identity <sam>` under `-ErrorAction SilentlyContinue`. A nested group
   silently falls to the `?? m.Properties["Name"]` fallback, renders with an EMPTY
   Email, and is labelled `RecipientType = "ADUser"` - the member list misreports what
   the member is.
2. `GroupManagement.razor:120` calls `RemoveMember(member.Email)`. For a nested group
   that argument is the empty string, so the Remove button is a no-op that surfaces as
   `User '' not found in AD.`
3. `AddMemberAsync` (`:277-287`) and `RemoveMemberAsync` (`:328-338`) resolve the member
   with `Get-ADUser -Filter "UserPrincipalName -eq '...' -or EmailAddress -eq '...'"`.
   Besides being user-only, this interpolates caller input into a PowerShell filter
   string guarded only by `'` doubling, where the rest of the repo uses bound
   `-LDAPFilter` with RFC 4515 escaping (`AdOwnershipFilter.EscapeLdapFilterValue`).
4. Neither write reads membership back after the operation, so an add or remove that did
   not take effect is reported as success (Known Failure Class #2). The self-service
   module already does this correctly; the admin module does not.

### Shared: the protected-principal check is blind to group targets

`ProtectedPrincipalService.CheckTransitiveGroupMembership` (`:690-774`) decides whether a
target sits inside a configured protected group. Both of its directory calls are
`Get-ADUser`:

- `:721-724` - the DN fallback when the target carries only a sAMAccountName.
- `:747-750` - the in-chain membership test
  `(&(distinguishedName=<target>)(memberOf:1.2.840.113556.1.4.1941:=<group>))`.

Hand either a group DN and AD returns zero rows with no error, which the method records
as "no match" (`:761`) rather than as a failed check. The result is a silent ALLOW: a
group nested under `Domain Admins` reads as unprotected, and the audit record says the
target was clean.

The other three rules are unaffected - `CheckDirectUserMatches` (`:572`),
`CheckPatternMatches` (`:610`) and `CheckOuMatches` (`:628`) are string comparisons over
`ResolvedDirectoryPrincipal` fields and work for any object class.

Nothing is exposed today because neither module can target a group at all. Part B makes
group targets reachable, so this must close in the same change.

## Owner decisions

- **D1 (2026-08-11): Self-Service Groups never adds a group.** Adding a nested group
  stays an IT Support Desk request. This is a permanent scope rule, not a first-cut
  limit, and the page must say so before the user tries.
- **D2 (2026-08-11): Self-Service Groups may REMOVE a group,** behind a warning stating
  that re-adding it will require a ticket. Owner rationale: removal only takes access
  away, and the warning makes the one-way nature explicit at the point of action.
- **D3 (2026-08-11): the protected-principal blind spot is closed rather than worked
  around.** Owner: *"whatever's easier ... but if it's easier to fail closed, add a
  message and direct them to ITSD, fine."* Part B needs group-capable directory
  resolution regardless, so making the membership rule see groups is the cheaper of the
  two and is the fail-closed direction. A group refused on protection grounds gets the
  ITSD message.
- **D4 (2026-08-11): AD Group Management supports adding AND removing group members.**
  It is admin-audienced; nesting is a legitimate admin operation there.
- **D5 (2026-08-11): the servicer override applies to GroupManagement.** No code is
  required - `GroupManagementService.cs:16` sets `ServicerModuleId = "GroupManagement"`,
  `:84` calls `ProtectedPrincipalServicing.NoteFor`, and
  `ModuleConfig.razor:655` already lists the module in
  `ModulesWithProtectedPrincipalServicing`, so the admin page already renders the
  `ProtectedServicer:GroupManagement` editor. What is missing is a granted group, which
  is a configuration action on the Module Config page, not a code change. This plan
  verifies the path end to end and records the config step; it does not perform it.

## Non-goals

- No change to who may reach either page. Section access, policy aliases and
  `FailClosed` settings are untouched.
- No group-nesting capability in `M365GroupManagement` (Graph-backed, separate service,
  separate semantics).
- No bulk or CSV nesting in either module.
- No change to the self-service ownership model: eligibility still keys on the caller's
  SID and the group DACL (`SelfServiceGroupService.CallerCanManageMembers`).
- No new AD credential. Both modules keep their own Delinea secret
  (`ModuleCredentialService.GetCredentialsAsync("SelfServiceGroups"|"GroupManagement", ...)`).
- No paging UI for large member lists.

## Design

### S1. Group-capable protection check (`Services/ProtectedPrincipalService.cs`)

Change both directory calls in `CheckTransitiveGroupMembership` from `Get-ADUser` to
`Get-ADObject`, which is class-agnostic and accepts the same `-LDAPFilter`.

Two consequences that must be handled, not just accepted:

- **The sAMAccountName DN fallback (`:718-728`) widens.** Today `Get-ADUser` bounds the
  match to users and the code takes `.FirstOrDefault()`. With `Get-ADObject` a
  sAMAccountName could match objects of more than one class. Require EXACTLY ONE result:
  zero or more than one sets `expansionHadErrors = true` and returns, so an
  unresolvable target fails the check closed rather than resolving to an arbitrary
  object. This is a behaviour change for existing callers in the protective direction -
  a sAMAccountName that names a group previously skipped the group rules entirely.
- **`Get-ADObject` returns a smaller default property set than `Get-ADUser`.** Only
  `DistinguishedName` is read here (`:727`), which `Get-ADObject` always returns, so no
  `-Properties` addition is needed. Do not add one speculatively.

The `1.2.840.113556.1.4.1941` (LDAP_MATCHING_RULE_IN_CHAIN) filter at `:748` already
evaluates nested membership for any object class; only the cmdlet in front of it is
wrong.

Constructing a `ResolvedDirectoryPrincipal` for a group: `UserPrincipalName` is a
non-nullable `string` (`:13`) and a group has none. Pass `string.Empty`, never the
group's name - `MatchesIdentity` (`:581`) skips null-or-empty candidates, so an empty UPN
is inert, whereas putting a group name in a field named UserPrincipalName invites a
false match against a protected USER entry that happens to share the name. Identity for a
group flows through `SamAccountName`, `DistinguishedName` and `ObjectGuid`.

### S2. Group-capable membership read (`SelfServiceGroupService.IsMemberOfGroup`)

Change `Get-ADUser` (`:687`) to `Get-ADObject`. The filter
`(&(distinguishedName=<member>)(memberOf=<group>))` is already class-agnostic and both
values are escaped (`:681-682`). Behaviour for user members is unchanged; group members
become visible to both the idempotency pre-check and the post-write read-back.

### S3. Self-Service Groups: refuse group adds explicitly (D1)

Three changes, all in the module's own surface:

1. **Static copy** in the add panel (`SelfServiceGroups.razor:190-191`): state that only
   users can be added here and that nesting a group requires an IT Support Desk ticket.
   Replaces the current "Add or remove a user by their username, email, or user principal
   name." sentence, which describes the limit without naming it.
2. **A specific refusal, not a generic miss.** `ChangeMemberAsync` currently returns
   `'{memberIdentity}' did not match exactly one user.` for every non-user input
   (`SelfServiceGroupService.cs:390`). When `operation == Add` and the user lookup found
   nothing, run one additional class-agnostic probe
   (`Get-ADObject -LDAPFilter (&(objectCategory=group)(|(name=..)(sAMAccountName=..)(mail=..)))`,
   escaped) and, on a hit, return a message naming the group and directing the caller to
   the IT Support Desk. On no hit, the existing not-found message stands. The probe runs
   ONLY on the add-not-found path, so the happy path costs nothing.
3. The typeahead stays `ObjectKind="User"` (D1): a group must not be offered as a
   suggestion for a control that cannot accept one.

### S4. Self-Service Groups: allow group removal (D2)

**Single-executor refactor first.** `ChangeMemberAsync` (`:350-530`) currently interleaves
identity resolution with the check-write-reconcile sequence. Extract the part after
resolution into a private `ApplyMembershipChangeAsync(callerSid, groupObjectGuid,
ResolvedMember member, MembershipOperation operation, ProtectionGate protection)`, and
give the service two public entry points that both feed it:

- `ChangeMemberAsync(...)` - unchanged signature and behaviour, typed identity, USER-only,
  serves the Add box and typed Remove. Every existing test must pass unmodified; an edit
  to one signals a behaviour change and is a stop.
- `RemoveListedMemberAsync(callerSid, groupObjectGuid, memberObjectGuid, actingUser)` -
  new, keyed on the member's IMMUTABLE objectGUID, resolves via
  `Get-ADObject -Identity <guid>`, accepts `objectClass` of `user` OR `group`, and is the
  only path that can remove a group.

Keying list-driven removal on objectGUID rather than a display identity is a correctness
fix beyond nesting: `RemoveMember` today passes `member.Identity`
(`SelfServiceGroups.razor:384`), a sAMAccountName/UPN string that is re-resolved by name
and can drift or collide between the list render and the write.

Supporting changes:

- `GroupMemberClassifier.IsRemovable` (`:38`): return true for `user` and `group`;
  `computer` and everything else stay false. Its doc comment describing user-only scope
  must be rewritten, not left contradicting the code.
- `GroupMember` already carries `ObjectGuid` (`GroupMember.cs:17`), populated at
  `SelfServiceGroupService.cs:287`. No model change.
- Protection: `RemoveListedMemberAsync` runs the SAME `CheckMemberProtectedAsync` gate
  (`:554`) on the resolved group principal. With S1 in place a protected nested group is
  refused; the refusal message directs to the IT Support Desk (D3).

**The warning (D2).** A confirmation step before a group removal only, stating that
re-adding the group will require an IT Support Desk ticket. Implement as an inline
confirm beneath the acting row - the pattern the Migration page adopted after the
top-of-table confirm proved unreachable at scale
(`.agents/state.md`, MigrationBatchSelection slice 3) - not a JS `confirm()`. A user
member's Remove keeps its current no-confirm behaviour; adding one there is out of scope
and would change a shipped workflow the owner did not ask about.

### S5. AD Group Management: group add and remove (D4)

- **Member listing.** `GetMembersAsync` (`:192-243`): drop the per-member
  `Get-ADUser -Identity <sam>` round-trip and read the class from the
  `Get-ADGroupMember` output already in hand, resolving details with
  `Get-ADObject -Identity <objectGUID> -Properties mail,displayName`. Add `MemberKind`
  and `ObjectGuid` to `GroupMemberInfo`; render Kind as a column. A group member must
  never again be labelled `ADUser`.
- **Writes.** `AddMemberAsync` / `RemoveMemberAsync` resolve the member class-agnostically
  and by objectGUID where the caller has one. Replace the interpolated
  `-Filter "... -eq '...'"` strings (`:278`, `:329`) with bound `-LDAPFilter` values
  escaped through `AdOwnershipFilter.EscapeLdapFilterValue`. Keep the existing
  exactly-one-match refusal.
- **The protection gate must be re-pointed, or S1 never fires here (gmn-1).**
  `CheckProtectedAsync` (`GroupManagementService.cs:56-95`) gates on
  `ResolveWithExchangeFallbackAsync` (`:63`), whose AD path
  (`ProtectedPrincipalService.ResolveViaActiveDirectory`, `:419-464`) is a `Get-ADUser`
  over `(|(userPrincipalName=..)(mail=..)(sAMAccountName=..))`. A group matches nothing,
  returns null (`:444`), status `NotFound`, and the `if (resolved != null)` block
  (`:72-92`) is skipped straight to allow. **S1 fixes a method the group target never
  reaches.**
  Restructure so this module follows the shape `SelfServiceGroupService` already uses
  (`:378-399`): resolve the member ONCE to an AD object, build a
  `ResolvedDirectoryPrincipal` for a user OR a group, and call
  `ProtectedPrincipalService.CheckAsync` on that resolved principal. The single
  resolution then feeds both the protection check and the write, so the object that
  clears the gate is provably the object written.
  Keep the Exchange fallback for USER members - it closes a real alias bypass
  (`GroupManagementService.cs:43-47`) and `GroupManagementServiceTests`
  `AddMemberAsync_ResolvesThroughTheExchangeFallback_NotAdAlone` pins it. Groups bypass
  the fallback (an on-prem group has no Exchange identity to canonicalise); a group that
  fails to resolve is REFUSED, never allowed through as not-found.
- **Read-back.** Both writes gain the post-write membership reconciliation the
  self-service module already has (`SelfServiceGroupService.cs:498-514`): confirm the
  end state by reading membership back, and report an unconfirmed write as a failure. Do
  not decide success from the absence of an exception.
- **Remove button.** `GroupManagement.razor:120` passes the member's objectGUID, not
  `member.Email`.
- **Add control.** Replace `RecipientAutocomplete` (`:89`) with
  `ADIdentityAutocomplete ObjectKind="Any"`. This changes the existing user-add control on
  an admin page: the suggestion source moves from Exchange recipients to AD objects, which
  is correct for a cmdlet chain that writes to on-prem AD and currently rejects any
  cloud-only recipient the picker offers.
  **The selection must carry the object's DN, not its sAMAccountName (gmn-3).** Group
  search is deliberately forest-wide (`Services/ADDirectorySearchService.cs:485-494`: a
  local-domain-only query made WINROOT groups unreachable; a global-catalog query returns
  both domains, measured 18 ANALOG + 7 WINROOT for one term), so the dropdown can offer
  two same-named groups from different domains. `ReturnValueKind="SAM"` returns only
  `SamAccountName` (`ADIdentityAutocomplete.razor:173`) and would hand the service a
  string that distinguishes neither.
  Bind `OnResultSelected` (`:96`, raised at `:164-165`) and hold the whole
  `ADSearchResult` in page state - `DistinguishedName`, `ObjectType` and `DnsDomain` -
  passing the DN to the service as the write target. `ReturnValueKind="DN"` (`:174`) is
  the simpler alternative but puts a DN in the visible textbox; prefer
  `OnResultSelected` with a display value in the box and the DN held beside it.
  **A typed value that was never selected from the dropdown has no DN.** Clear the held
  selection on every keystroke (`ValueChanged` fires on typed input, `OnResultSelected`
  does not), and route typed input through the service's exact class-aware resolver with
  its exactly-one-match refusal. A stale DN from a previous selection surviving a retype
  would write to whatever was picked before.
- **Nesting guards. These live in `GroupManagementService.AddMemberAsync`, immediately
  before `Add-ADGroupMember` - NOT in the page (gmn-2).** `GroupManagementService.cs:36-38`
  records that this module already shipped a page-only protection check which was bypassed
  by identity format and by any non-page caller; a guard in the Razor file repeats that
  defect exactly ("UI hiding is not security", Constitution). Let TARGET be the group
  being edited and CANDIDATE the group being added to it:
  - **Self-nest:** refuse when `CANDIDATE.DistinguishedName` equals
    `TARGET.DistinguishedName` (ordinal-ignore-case on the resolved DNs, never on typed
    names).
  - **Cycle:** refuse when TARGET is already a member, directly or transitively, of
    CANDIDATE - because adding CANDIDATE under TARGET would then close a loop. The
    subject of the query is TARGET and the group searched is CANDIDATE:
    `Get-ADGroup -LDAPFilter (&(distinguishedName=<TARGET>)(memberOf:1.2.840.113556.1.4.1941:=<CANDIDATE>))`.
    **Do not invert these.** The mirror query
    `(&(distinguishedName=<CANDIDATE>)(memberOf:...:=<TARGET>))` answers a DIFFERENT
    question - whether CANDIDATE is already inside TARGET - which is the benign
    already-a-member case and must be treated as an idempotent no-op, never as a cycle.
    AD does not reliably refuse every cycle across group scopes, and a cycle is not
    repairable from this page.
  - **Fail closed:** an error or unreadable result from either query refuses the add. An
    unanswerable cycle question is not a "no".
  - surface AD's own scope refusals verbatim (a Global group cannot contain a group from
    another domain; Domain Local and Universal have their own rules). Do not pre-empt
    them with a local rule that will drift from AD's.
- **Servicer (D5).** No code. Confirm on the Module Config page that
  `ProtectedServicer:GroupManagement` renders and saves, and that a granted group can
  override a protected refusal here.

## Slices

Each is one commit, in order. S1 and S2 land before anything that can target a group.

1. **S1** - `ProtectedPrincipalService` group-capable membership check + exactly-one
   resolution. Tests only; no module behaviour change yet.
2. **S2** - `SelfServiceGroupService.IsMemberOfGroup` to `Get-ADObject`.
3. **S3** - Self-Service refusal copy + group-specific add refusal message.
4. **S4** - Self-Service single-executor refactor, `RemoveListedMemberAsync`, classifier
   change, inline removal warning.
5. **S5a** - `GroupManagement` member listing: kind, objectGUID, no per-member
   `Get-ADUser`.
6. **S5b** - `GroupManagement` writes: LDAP-escaped class-agnostic resolution,
   resolve-once-then-`CheckAsync` protection gate (gmn-1), self-nest and cycle guards in
   the SERVICE (gmn-2), read-back reconciliation, objectGUID-keyed remove.
7. **S5c** - `GroupManagement` page: picker swap, Kind column. No authorization or
   nesting logic lands here; the page only surfaces what the service decided.
8. **S6** - Versions, README, `.agents/state.md`.

## Acceptance criteria

- AC1: In Self-Service Groups, the add panel states that only users may be added and
  that a nested group requires an IT Support Desk ticket, before any attempt is made.
- AC2: Typing an existing group's name and clicking Add returns a message naming it as a
  group and directing to the IT Support Desk - not "did not match exactly one user".
- AC3: A non-existent identity still returns the existing not-found message.
- AC4: A nested group in the member list has a Remove button; a computer or other class
  does not.
- AC5: Clicking it shows a warning that re-adding requires a ticket, and requires a
  second action to proceed.
- AC6: A confirmed group removal completes, is audited, and the row disappears after
  the list refresh.
- AC7: A group that is protected, or nested inside a protected group, is REFUSED on
  removal with the ITSD message - not silently allowed.
- AC8: Existing self-service USER add/remove behaviour is byte-for-byte unchanged, proven
  by the existing suites passing unmodified.
- AC9: In AD Group Management, a nested group appears in the member list with kind
  "Group" and a working Remove button.
- AC10: An admin can add a group as a member; the write is read back and confirmed.
- AC11: Adding a group to itself, or creating a cycle, is refused with a named reason.
- AC11b: Adding a group that is ALREADY a member succeeds as an idempotent no-op - it is
  not misreported as a cycle (gmn-2). Both directions are asserted, because a
  single-direction test passes against the inverted filter.
- AC11c: The self-nest and cycle guards refuse when called directly on
  `GroupManagementService`, with no page involved.
- AC12: An AD scope refusal is surfaced with AD's own message, not a generic failure.
- AC12b: A group selected from the dropdown is the group written, including when two
  domains hold a group of the same name (gmn-3). Retyping after a selection discards the
  held DN rather than writing the previously picked object.
- AC13: A protected group is refused in AD Group Management unless the operator holds
  `ProtectedServicer:GroupManagement`, in which case the write proceeds and the audit
  event's `extra` names the authorising group.
- AC14: A GROUP member reaches `ProtectedPrincipalService.CheckAsync` in AD Group
  Management - it is never dropped as an unresolved identity before the gate (gmn-1).
  A group that cannot be resolved is refused, not allowed.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`; ASCII check over changed `.cs`
- New unit tests: `GroupMemberClassifier` group removability; the add-path group probe
  message selection; the exactly-one resolution rule in S1; the cycle/self-nest guard as
  a pure predicate; a GROUP member reaching `CheckAsync` in `GroupManagementService`
  (gmn-1) - a scripted `ProtectedPrincipalService` asserting the gate was consulted, since
  a test that only checks the write was refused passes for the wrong reason when the
  target was silently dropped.
- Non-vacuity per slice: revert the fix, confirm the new test fails, restore, confirm
  green. Verify the revert actually landed on disk before trusting either verdict, and
  touch the file after any timestamp-preserving restore (`.agents/state.md`, the
  `Copy-Item` trap).
- **No test in this repo renders a page** (no bUnit harness). AC1, AC2, AC4, AC5, AC9 and
  AC12 are page behaviour and are provable only by source-level tripwires plus manual
  checks. Every review finding in the protected-principal stream lived in a page. State
  plainly which manual checks were not run.
- Manual checks, dev: AC2 with a real group name; AC5/AC6 on a real nested group; AC7
  against a group nested under a configured protected group; AC10 and AC11 on the admin
  page; AC13 with a member of the servicer group; AC12b against a WINROOT group whose
  name also exists in ANALOG - the cross-domain case is unreachable from any test, since
  the forest search needs a live global catalog.

## Versioning

- Base app `2.8.1` -> `2.9.0`: `ProtectedPrincipalService` is shared by all 15
  servicer-capable modules and its protection semantics change (Constitution,
  Deployment And Versioning).
- `SelfServiceGroups` `1.3.0` -> `1.4.0`.
- `GroupManagement` `2.2.0` -> `2.3.0`.

Both module rules fire independently of the base rule.

## Open questions

- **OQ1 (owner ruling required before S4 ships).** When a GROUP is removed from a
  security group, the affected-user notification
  (`Email.SendGroupMembershipUserNotificationAsync`, gated by
  `MembershipChangeResult.NotifyAffectedUser`) has no correct recipient. A mail-enabled
  group's address would mail every member; suppressing it means the people who lost
  access are not told. This plan assumes SUPPRESS - the admin notification still fires
  and the audit record is written - because mailing an entire group about an
  administrative nesting change is the larger harm. Not yet ruled.
- OQ2: whether the S5 picker swap on the admin page should also apply to the group
  SEARCH box (`GroupManagement.razor:35`), which is a plain input. Out of scope here;
  raise separately if the admin page work continues.
