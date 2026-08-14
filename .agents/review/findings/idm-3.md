# idm-3: The servicer override would be untestable-by-operator - nobody could grant it

**Severity**: MEDIUM - the capability would pass its tests and be unreachable in the product, the exact failure this repo has already shipped twice.
**Status**: Verified
**Branch**: - (default-branch mode)
**Commit**: `28f848c`

## Evidence

- `docs/IntuneDeviceManagement-Plan.md` AC8 requires "a member of a group holding
  `ProtectedServicer:IntuneDevices`" to succeed against a protected target.
- `Components/Pages/ModuleConfig.razor:650-657` - `ModulesWithProtectedPrincipalServicing` is a
  hardcoded `HashSet<string>` of fifteen module ids. `IntuneDevices` is not among them, and the
  plan never mentions the list.
- `Components/Pages/ModuleConfig.razor:849-853` - `ServicerAlias` is null unless the module id is
  in that set, so the alias never joins `policyAliases` and the editor never renders. There is
  no other surface that writes the key.
- `Components/Pages/ModuleConfig.razor:648` - the list's own comment: "Add a module here in the
  same commit that adds its `Evaluate` call, never before."

The plan wrote the `Evaluate` side (S3, servicer support via `ProtectedPrincipalServicing`) and
omitted the registration side, which is precisely the ordering that comment exists to prevent -
in the opposite direction from the one it was written to catch.

## Predicted observable failure

Every servicer test passes, because tests seed `section_access` directly. In the product no
operator can grant `ProtectedServicer:IntuneDevices`, because the Module Config page for
`IntuneDevices` does not render the editor for it. A protected principal's device is therefore
undeletable by anyone, with no way to authorise the exception and nothing on screen explaining
why - and AC8 would have been marked satisfied by a green suite.

## What

Same class as `ppsvc-1` (2026-08-06) and `pgwt-1`: a capability registered, consumed and
unit-tested, with nothing able to write the key it reads. `.agents/state.md` already carries the
rule this earns - *a capability is not implemented until the person meant to use it can reach
it* - and the plan violated it anyway.

## Approach

Add `IntuneDevices` to `ModulesWithProtectedPrincipalServicing` in the **same slice** that adds
the module's `ProtectedPrincipalServicing` call, per the list's own instruction. That is S3, the
first slice with a write gate; S4 inherits it. A source-level tripwire asserts the module id is
in the set whenever a gate in `IntuneDevices.razor` calls the servicing helper, so the two
cannot drift apart later.

Also folded in, because the same reasoning reaches it and `pgwt-1` is the precedent for checking
the stored representation rather than the fixture: AC8 is restated to require the grant be made
**through the Module Config page**, not seeded into the store by a test. A test that seeds the
row proves the gate reads it; only the page proves an operator can create it.

## Files changed

- `docs/IntuneDeviceManagement-Plan.md` - S3 gains the list registration and the tripwire; AC8
  restated to run through the page; manual check 10 restated to make the grant on the page
  rather than assume it exists.

## Guard proof

Plan-stage finding; no code written. The criterion is AC8 as restated plus the S3 tripwire:
removing `IntuneDevices` from the set must fail a test by name, not merely make a manual step
awkward.

## Coder dispute (if any)

None. Verified directly against `Components/Pages/ModuleConfig.razor`.

## Known gaps

`.agents/state.md` records that nine servicer-capable modules have a gate and no
`ProtectedServicer:` row granted anywhere, deliberately - a grant is a configuration act, not
part of shipping the module. This finding is about the editor being **renderable**, not about a
group being granted. `IntuneDevices` shipping with no grant is correct and expected; shipping
with no way to make one is not.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade `fallback`; openreview over `b868e5c..6aef9e3`, verdict `acceptable_with_changes`,
`capability_ok: true`, both SHAs echoed)

Harness: codex-cli 0.147.0, `codex exec`, read-only sandbox. UTC 2026-08-14.

Reviewer's material change: "Add ModuleConfig.razor's protected-servicer opt-in for
IntuneDevices in the same slice that adds the servicer Evaluate call, with tests mirroring the
existing servicer admin UI tripwires." Adopted as written; the AC8-through-the-page restatement
is mine, from the `pgwt-1` precedent.
