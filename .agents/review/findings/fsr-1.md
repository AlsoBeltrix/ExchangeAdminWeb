# fsr-1: Forest-search results cannot be opened outside the credential's home domain

**Severity**: HIGH - the 2.5.0 forest search surfaces foreign-domain rows the whole rest of
the module then mishandles: a unique foreign name is refused as not-found, and a name that
ALSO exists locally (Domain Admins) silently resolves to the LOCAL group, so the protection
check and any write act on the wrong object.
**Status**: Verified (closed on the coder-side guard proof per the 2026-08-31 ruling:
verification rounds are CRITICAL-only, owner-approved; this is HIGH)
**Branch**: `-` (default-branch mode)
**Commit**: `f6a4eb1`

## Evidence
`Services/GroupManagementService.cs` - `SearchGroupsAsync` returns global-catalog rows from
every domain, but `ResolveGroupForWrite` LDAP-filters sam/name/mail with no `-Server`
(local domain only), and `GetMembersAsync` reads the group by DN with no `-Server` on the
group read (the primary-group read directly below it DOES route via `ServerFromDn`). The
2.6.0 `CheckTargetProtectionAsync` inherits `ResolveGroupForWrite`'s local-only resolution.
Raised by codex over `ac6face..3505e67`; the same gap was independently recorded in
`.agents/state.md` the same day as pre-existing ("survey is not authorization").

## Predicted observable failure
Selecting a WINROOT row from search: unique name -> "AD group not found" denial and a dead
panel (protection check fails closed), for servicers too. Same-named-in-both-domains row ->
the local group's snapshot feeds the gate and the write target: protection is evaluated
against, and a write would land on, a DIFFERENT group than the operator picked.

## What
Group resolution is home-domain-bound while search became forest-wide, so foreign-domain
results are either unusable or - worse - quietly swapped for their local namesakes.

## Approach
Route resolution by the picked DN. `ResolveGroupForWrite`: a DN-shaped identity resolves by
`-Identity <dn>` with `-Server` from the DN's owning domain (exact match, no name
ambiguity); non-DN identities keep the existing candidate loop. `GetMembersAsync`: the
group read routes via `ServerFromDn(resolvedDn)`, mirroring the adjacent primary-group
read. `CheckTargetProtectionAsync` inherits the fix through `ResolveGroupForWrite`.

## Files changed
- `Services/GroupManagementService.cs` - DN-routed resolution in both paths
- `ExchangeAdminWeb.Tests/GroupManagementTargetGateTests.cs` or a listing suite - tripwires

## Guard proof
`ExchangeAdminWeb.Tests/GroupSearchForestScopeTests.cs::ResolveGroupForWrite_TakesTheDnFastPath_ExactOrNothing`
and `::MemberRead_And_Writes_RouteByTheGroupsOwningDomain` - probed 2026-08-31: DN
fast-path neutered, 1/1 FAIL; restored, PASS; full suite 1841/0/3 at commit.

## Coder dispute (if any)
None.

## Known gaps
The fix routes resolution, the member read, the cycle probe, and both write cmdlets by the
group DN's owning domain. `IsDirectMemberOf` (the idempotency pre-check and the post-write
read-back) stays un-routed: its memberOf-backlink pair query has no single correct server
for a cross-domain pair (the member's partition lacks the foreign group's backlink), so a
cross-domain write can still report "could not be confirmed" after succeeding - fail-safe,
never a false success. Live cross-domain add/remove stays a manual dev check.

## Reviewer comments
`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (inline, session-only; owner
dispatch "codereview codex gpt-5.6-sol xhigh"), generation pass over
`ac6face9c1025d9b6064102f3f0c43f8390618ef..3505e6707ccdecf41146913845f1275709ed1532`,
verdict `findings` (2), `capability_ok: true`, 2026-08-31. Transport notes: dispatched via
`codex.cmd exec` with the wrapper's env replicated (CODEX_HOME=.codex-commercial,
OPENAI_*/PORTKEY_* stripped) because the .ps1 wrapper's parameter binder eats `-o`/`exec`;
prompt piped via stdin because the cmd shim drops multi-line argument text (a blind
"clean" from that fault was discarded on the unechoed SHA pins).

**Verification round 2026-08-31: TRANSPORT-FAILED.** Dispatch codex-commercial /
gpt-5.6-sol / xhigh (T2 -> frontier; fallback accepted per the owner standing dispatch,
lst-1 precedent), `-s workspace-write`, pins 3505e67..f6a4eb1. Every native exec was
rejected (`helper_unknown_error: setup refresh had errors` - the fault recorded in
`.agents/machines.md` 2026-08-28, still live). The reviewer returned an honest
`invalid` with `capability_ok: false` and an empty reviewed_sha - correctly fail-closed,
not a review. A pre-dispatch smoke "passed" only because it executed through the serena
MCP server, which does not exercise the native exec sandbox; noted so the next probe is
not fooled. Per the playbook's terminal-denial rule: recorded, transport unsupported,
routed to the owner. Options: owner-run interactive verification (the 2026-08-28
`manual-verify` pattern - prompt staged at `.agents/review/manual-verify-fsr1.md`),
accept the coder-side guard proof, or retry after a codex upgrade.
