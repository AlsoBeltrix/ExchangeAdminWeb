# gmn-8: Forest-wide selections are resolved against the local domain only

**Severity**: MEDIUM - the cross-domain case gmn-3 was built for fails closed at
resolution: a WINROOT selection from the forest-wide picker cannot be written, and a
foreign-domain listed member cannot be removed by GUID.
**Status**: Verified
**Branch**: `-` (default-branch mode)
**Commit**: `dc503e1`

Reviewer-raised (generation pass over `3f2ab21..45b95e9`).

## Evidence
`Services/GroupManagementService.cs` - `ResolveMemberForWrite` passes the selected DN or
removal GUID to `Get-ADObject` without `-Server`; Get-ADObject defaults to the computer's
domain and the requested partition must exist on the contacted server, so a foreign-domain
DN or GUID resolves nothing there. The picker's group search is deliberately forest-wide
(`ADDirectorySearchService`, global catalog).

## Predicted observable failure
On the ANALOG-hosted app, selecting a WINROOT group offered by the picker fails in
`ResolveMemberForWrite` before any check or write; removing a listed WINROOT member by
objectGUID fails the same way. Fail-closed, so no exposure - the feature is broken, not
bypassed.

## What
Immutable-key lookups were domain-blind while the suggestion source is forest-wide.

## Approach
The DN carries the routing: `ServerFromDn` (pure, unit-tested) derives the object's domain
DNS name from its `DC=` components, and `ResolveMemberForWrite` binds `-Server` to it for
the immutable-key lookup. The member list now carries each member's DN
(`GroupMemberInfo.DistinguishedName`, read from the membership output already in hand), and
the page passes it alongside the GUID so GUID-keyed removals route to the member's own
domain; the picker path derives from the selected DN itself. Typed identities keep the
local-domain filter search (the picker is the cross-domain path). The S5a detail lookup
gets the same routing so foreign members' mail/display resolve instead of degrading.

## Files changed
- `Services/GroupManagementService.cs` - `ServerFromDn`, `-Server` on immutable-key and
  detail lookups, `RemoveMemberAsync` accepts the member DN hint
- `Components/Pages/GroupManagement.razor` - passes the listed member's DN
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` - ServerFromDn unit tests
  and wiring tripwires

## Guard proof
Reverting `ServerFromDn` wiring makes its tests FAIL; restoring makes them PASS. The live
cross-domain behaviour is unreachable from tests (needs a global catalog) - it stays on the
plan's manual checklist (AC12b).

## Coder dispute (if any)
None - admitted as written.

## Known gaps
Cross-domain writes may still hit AD group-scope rules; those surface verbatim by design
(AC12).

## Reviewer comments
Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only)
codex-cli 0.147.0, reviewed 45b95e901189addc4e60df403f019362b8089619, base
3f2ab2191399e07de02e3c71cdb1724423df4e07, capability_ok true, verdict findings (3 of 4),
2026-08-27 UTC.

Verification: Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only),
workspace-write sandbox per machines.md. Verdict ACCEPTED, guard_confirmed true,
capability_ok true, reviewed dc503e1eb7372d49dacd551d298c9ccd59eb6351 base
b8379dc2337b8d5d48683926855dc3b2f1d0e7d5, no comments, 2026-08-27 UTC. Working tree verified
untouched after the run.
