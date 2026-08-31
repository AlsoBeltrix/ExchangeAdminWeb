# fsr-1: Forest-search results cannot be opened outside the credential's home domain

**Severity**: HIGH - the 2.5.0 forest search surfaces foreign-domain rows the whole rest of
the module then mishandles: a unique foreign name is refused as not-found, and a name that
ALSO exists locally (Domain Admins) silently resolves to the LOCAL group, so the protection
check and any write act on the wrong object.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: (pending)

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
(pending)

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
