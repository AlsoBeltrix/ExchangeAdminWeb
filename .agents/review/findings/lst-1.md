# lst-1: A degraded admin row's Remove can resolve and act on a DIFFERENT member

**Severity**: HIGH - the immutable-identity contract inverts: a row created BECAUSE its identity
could not be resolved falls back to resolving its display name, which can match a same-named
local object and remove the wrong member.
**Status**: In progress (fix landed; independent verification BLOCKED - transport, see
Reviewer comments)
**Branch**: `-` (default-branch mode)
**Commit**: `f239437`

## Evidence

The 3b766ca listing fix creates degraded rows with `ObjectGuid = ""`
(`Services/GroupManagementService.cs` GetMembersAsync, unresolvable-member branch). The admin
page renders Remove for every row (`Components/Pages/GroupManagement.razor:127-129`) and passes
`listed.ObjectGuid` (the empty string) plus `listed.DistinguishedName` to `RemoveMemberAsync`
(`:386`), with `member` = the display name when Email is empty (`:353`). In
`ResolveMemberForWrite`, `var immutableKey = memberObjectGuid ?? memberDn;` - `??` passes the
NON-NULL empty string through - so `IsNullOrWhiteSpace(immutableKey)` routes to the
typed-identity branch, which discards the DN hint and resolves the DISPLAY NAME through the
local-domain LDAP filter.

Before 3b766ca this was unreachable: an unresolvable cross-domain member faulted the whole
listing, so no empty-GUID row ever rendered. The fix created the reachable path.

## Predicted observable failure

Clicking Remove on an unresolved foreign `CN=Ops` row resolves "Ops" locally. If a same-named
local object is also a member, the WRONG member is removed; if not, the idempotency pre-check
reports a successful no-op while the clicked foreign member remains - both outcomes silently
wrong, on the write path of an authorization-adjacent module.

## What

The degraded row was designed to be inert ("no immutable id so nothing can act on it" - the fix
commit's own comment) but the page still offers Remove and the service falls back from the
absent immutable identity to the mutable display name. The inertness claim was never enforced
at either end.

## Approach

Fail closed at the service and remove the affordance at the page. `ResolveMemberForWrite` now
coalesces on non-BLANK (`FirstNonBlank(memberObjectGuid, memberDn)`), so a blank GUID with a DN
hint resolves by the DN routed to its owning domain - the exact resolution that failed at list
time; if it still fails, the removal is refused with the reload message, never resolved by
name. The list-driven remove path additionally refuses outright when the row carries a blank
GUID (`RemoveMemberAsync` blank-listed-identity guard). The page disables Remove for rows with
an empty `ObjectGuid`, titled to say why.

## Files changed

- `Services/GroupManagementService.cs` - non-blank coalesce + blank-listed-GUID refusal
- `Components/Pages/GroupManagement.razor` - Remove disabled on empty-GUID rows
- `ExchangeAdminWeb.Tests/GroupMemberListingTests.cs` - pure coalesce tests + tripwires

## Guard proof

- `GroupMemberListingTests::FirstNonBlank_*` - pure rule: blank GUID never wins over a DN.
- `GroupMemberListingTests::AdminRemove_RefusesABlankListedGuid_AndPageDisablesTheButton` -
  tripwires pinning the service guard and the page's disabled affordance. Reverting the fix
  makes these FAIL; restoring makes them PASS.

## Coder dispute (if any)

None. Verified against the page and the resolver before admitting.

## Known gaps

The self-service module is already safe (blank memberObjectGuid is refused at entry; degraded
rows classify "Other" and are not removable) - asserted by existing tests, unchanged here.

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner-named pair, dispatch
2026-08-28; codex-cli 0.150.1 via Headroom proxy - environment notes: wrapper exit-code -1
quirk on a completed turn, recorded not invalidating)

Generation pass over `fbf37ac..3b766ca`, verdict `findings` (3), `capability_ok: true`.

**Verification rounds - transport unsupported (2026-08-28):** two workspace-write dispatches
(`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard`, pins base `cd70f69` head
`f239437`) both returned `invalid` with `capability_ok: false` / `guard_confirmed: false`: the
sandbox rejected every process launch (round 2 also file reads) with
`helper_unknown_error: setup refresh had errors`. Not a dispute about the fix - the reviewer
never inspected the diff or ran the guard. Per the playbook's terminal-denial rule the
transport is recorded unsupported and no further variants were tried. Read-only dispatches on
the same harness worked the same day (the generation pass above), so the fault is specific to
the workspace-write sandbox on codex-cli 0.150.1. Coder-side guard proof stands (fix reverted:
compile failure of the guarding tests; restored: 1775/0/3 green). **Owner decides:** retry
codex-commercial later, name another verifier (grok holds a verified cache entry -
an offer, not a dispatch), or accept the coder-side proof.
