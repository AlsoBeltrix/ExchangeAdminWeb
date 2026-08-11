# pgwt-1: An M365 group cannot be marked protected at all, so its new gates would guard nothing

**Severity**: HIGH — AC5 would pass on fixtures while being unreachable in production; the plan's own non-goal forbids the fix.
**Status**: Verified, then MOOTED BY SCOPE (owner, 2026-08-11: "we're not touching the cloud groups module")
**Branch**: —
**Commit**: `7c5f8a6` (plan revision), scope narrowed in the commit that followed

> **Read this first.** The M365 half of the plan this finding was written against no longer
> exists: the owner ruled `M365GroupManagement` out of scope, and it was scope the agent had
> added unasked in the first place. The defect described below is therefore no longer
> reachable through any planned work. **The record is kept, not deleted, for two reasons.**
> The gap it documents is real and still live in the product - an M365 group cannot be marked
> protected, so any future M365 protection work starts from here. And the criterion it earned
> survived the scope cut as AC7: the new on-prem target list also needs an admin input, and
> "a capability nobody can configure" is not a cloud-specific failure.

## Evidence

`docs/ProtectedGroupWriteTarget-Plan.md` requires M365 target gates (T2, AC4, AC5) while
its non-goals forbid changing "what the protected-principals config stores or how it is
edited". Those two cannot both hold.

Both admin inputs are AD-only:

- Protected Users — `Components/Pages/AdminSettings.razor:144`,
  `ADIdentityAutocomplete ObjectKind="User" ReturnValueKind="UPN"`.
- Protected Groups — `:172`, `ObjectKind="Group" ReturnValueKind="DN"`, captioned
  "DN, DOMAIN\GroupName, or CN - transitive membership" (`:150`).

**Stronger than the review stated: a typed value cannot get in either.** `AddPpUser` /
`AddPpGroup` (`:552-553`) route through `AddValidatedAsync` (`:639-670`), which calls
`ADSearch.ValidateExists(raw, objectKind)` (`:660`) and refuses anything that does not
resolve, failing closed on an exception (`:664-667`). A cloud-only M365 group resolves as
neither an AD user nor an AD group, so it is **refused by the UI**, not merely
undiscoverable.

`MatchesIdentity` (`Services/ProtectedPrincipalService.cs:581-598`) does compare
`PrimarySmtpAddress` and `EntraObjectId`, so the engine could match a cloud group — but
nothing can put those values in the store.

## Predicted observable failure

The M365 gates ship, every unit test passes against hand-built fixtures, and no operator
can mark a single M365 group protected. AC5 ("renaming or deleting a protected M365 group
is refused") is unfalsifiable in production: the precondition cannot be created. This is
the `ProtectedPrincipalBreakGlass` failure repeating — registered, consumed, unit-tested,
and unreachable by the person meant to use it.

## What

The plan asserted coverage for a class of object the configuration surface cannot express,
and then ruled the configuration surface out of scope in the same document.

## Approach

The non-goal is withdrawn and the plan now carries an explicit target-identity model: a
separate Protected Targets list, stored beside the existing four rule kinds and never
reinterpreting them, whose picker accepts on-prem groups AND Entra groups. This resolves
pgwt-1 and OQ1 together — see the plan's Design section, where the reasoning for choosing
a new list over reinterpreting `Groups` is recorded.

## Files changed

- `docs/ProtectedGroupWriteTarget-Plan.md` — non-goal withdrawn; target identity model,
  storage, and admin surface specified; OQ1 resolved into the design

## Guard proof

Not applicable: plan document. The implementation must test through the stored
representation an operator actually produces, not a hand-built `ResolvedDirectoryPrincipal`
— a fixture that bypasses the store is exactly what would have hidden this.

## Coder dispute (if any)

None. Verified against the admin page and the validator before admitting, and found to be
worse than reported.

## Known gaps

Whether Entra group search is available to that picker is unverified;
`ADDirectorySearchService` is AD-only. The plan names it as the first implementation
question.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback — frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `2eedaa9094ac58bb8ff30d1eab98a1fbf39a7826..503c1a8dcaf3a599c8f62d3423cbaa62ed638812`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-11T20:22Z.
