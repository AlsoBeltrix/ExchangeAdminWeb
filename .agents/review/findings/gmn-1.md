# gmn-1: GroupManagement group writes would still skip the protected-principal check

**Severity**: HIGH — the plan's own AC13 would be unmet, and a group nested under a protected group could be added or removed with no refusal and no servicer audit.
**Status**: Open
**Branch**: —
**Commit**: (filled in after commit)

## Evidence

`docs/GroupMemberNesting-Plan.md` S1 closes the blind spot only inside
`ProtectedPrincipalService.CheckTransitiveGroupMembership` (`:690-774`).

`GroupManagementService.CheckProtectedAsync` (`Services/GroupManagementService.cs:56-95`)
never reaches that method for a group. It gates on
`_protectedPrincipals.ResolveWithExchangeFallbackAsync(member)` (`:63`), whose AD path is
`ResolveViaActiveDirectory` (`Services/ProtectedPrincipalService.cs:419-464`) — a
`Get-ADUser` over `(|(userPrincipalName=..)(mail=..)(sAMAccountName=..))` (`:434`). A group
matches zero rows, the method returns null (`:444-445`), the status is `NotFound`, and
`CheckProtectedAsync` falls through the `if (resolved != null)` block (`:72-92`) to
`return new(null, null)` — allow.

`SelfServiceGroupService` is not affected: it resolves the member itself and passes the
`ResolvedDirectoryPrincipal` straight to `CheckAsync`
(`Services/SelfServiceGroups/SelfServiceGroupService.cs:397, 559`).

## Predicted observable failure

Implementing the plan as written, an admin adds a group that is nested under a configured
protected group. The write succeeds with no refusal and the audit event carries no
`ProtectedServicing` note. AC13 fails while every S1 unit test passes, because the target
never reaches the code S1 fixed.

## What

The plan fixed the deepest layer of the protection check and left the layer above it
user-only. A group target is filtered out one call before the fix takes effect, so the
gap the plan exists to close would survive in the module the plan opens to groups.

## Approach

S5 now names the re-pointing explicitly: `GroupManagementService` resolves the member once
to an AD object, builds a `ResolvedDirectoryPrincipal` for a user or a group, and calls
`CheckAsync` on it — the shape `SelfServiceGroupService:378-399` already uses, where the
single resolution feeds both the gate and the write. The Exchange fallback is retained for
USER members (it closes a documented alias bypass and is pinned by an existing test);
groups skip it and an unresolvable group is refused rather than passed through as
not-found. AC14 states the requirement and the verification section names a guard that
asserts the gate was consulted, not merely that the write failed.

## Files changed

- `docs/GroupMemberNesting-Plan.md` — S5 gains an explicit resolve-then-check contract

## Guard proof

Not applicable: this finding is against a plan document, and the fix is a plan revision.
The guard belongs to the implementation slice — S5b must carry a test asserting that a
GROUP member reaches `ProtectedPrincipalService.CheckAsync`, provable by reverting the
resolve-as-object change and watching it fail.

## Coder dispute (if any)

None. Verified against current code before admitting.

## Known gaps

Overlaps gmn-3: both are consequences of the admin module identifying members by loose
strings rather than by a resolved directory object. Fixed independently; the shared root
is named in the plan.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback — frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `618235e9e18bb957860e36a03f1a4b4c5cd42b38..074bfdb7ddffd91e5e6e80904ed71e173ff4f03d`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-11T18:24Z.
