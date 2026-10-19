# aud-a1: Module-admin grants match across domains

**Severity**: HIGH - the field that decides who can change a module's configuration and the AD
attribute allowlist accepts a member of a different domain's same-named group.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 1 of the 2026-10-16 repository audit, independently verified by the implementer before
any change.

`Services/ModuleAdminService.cs:31` (pre-fix) stripped the domain from each stored grant and
then accepted a bare-name match:

```csharp
var normalizedGroup = group.Contains('\\') ? group.Split('\\')[1] : group;

if (user.IsInRole(group)
    || user.IsInRole(normalizedGroup)
    || roleClaims.Any(r => r.Equals(group, StringComparison.OrdinalIgnoreCase))
    || roleClaims.Any(r => r.Equals(normalizedGroup, StringComparison.OrdinalIgnoreCase)))
```

A grant for `FOREIGN\Ops` is satisfied by a member of `HOME\Ops`.

**The storage side is worse than the audit stated, and the owner spotted it.**
`Components/Pages/ModuleConfig.razor:625` bound the picker as
`<ADIdentityAutocomplete ObjectKind="Group" ReturnValueKind="SAM" .../>`, while the same page's
section-access pickers at `:230` and `:330` already use `ReturnValueKind="SID"`. Underneath,
`Components/Shared/ADGroupAutocomplete.razor:137` does
`Value = result.SamAccountName ?? result.DisplayName` - it holds the resolved directory object
and keeps only a name, falling back to the **display** name. Owner, 2026-10-19: *"so even
though the GUI resolves groups on entry, it's still storing the arbitrary display name? yes,
fix that."*

**This is a store that `docs/SectionAccessSidStorage-Plan.md` already solved and simply
missed.** `section_access` stores SIDs, compares exactly, and filters unmigrated rows out.
`module_admins` was left on names. The fix is to apply the existing model, not to invent one.

## Predicted observable failure

A member of `HOME\Ops` opens a module whose admin grant names `FOREIGN\Ops` and can change its
configuration and its AD attribute allowlist. Nothing in the audit trail distinguishes this
from a legitimate grant, because the grant really did match as stored.

## Approach

Three changes, each mirroring the reviewed section-access pattern:

- `ModuleAdminService.IsModuleAdmin` now does
  `GroupMembershipChecker.UsableSidsOnly(...)` + `ExtractGroupClaims(...)` + `IsMemberOfAny(...)`.
  The name normalization and both `IsInRole` calls are gone.
- `ModuleConfig.razor:625` picker switched to `ReturnValueKind="SID"`, matching `:230`/`:330`.
- `AddModuleAdmin()` gained the same `IsUsableGroupSid` guard, and the same operator message, as
  `AddAccessGroup()`. Typed text is refused rather than stored.

Plus `GetUnusableGrants()`, which lists stored grants that are not usable SIDs - see Known gaps.

**`UsableSidsOnly` is load-bearing, not defensive.** Review finding `sid-1` measured that exact
comparison ALONE does not make an unmigrated store fail closed, because `IsInRole` resolves
names as well as SIDs and a role claim can carry a name. Filtering to SIDs is what makes a
legacy grant inert.

**Not touched:** `Security:AdminGroups` in appsettings, which must NOT be filtered this way -
no migration converts it, it ships as `DOMAIN\Name`, and filtering it locks every admin out of
the admin page (review finding `sidf-1`). Different store, different rule.

## Files changed

- `Services/ModuleAdminService.cs`
- `Components/Pages/ModuleConfig.razor`
- `ExchangeAdminWeb.Tests/ModuleAdminServiceTests.cs`

## Guard proof

**An existing test pinned the defect and had to be rewritten.**
`IsModuleAdmin_MatchesRoleClaim` asserted that a grant for `DOMAIN\Migration-Admins` IS
satisfied by a bare `Migration-Admins` role claim - the exact behaviour this finding is about.
It is replaced by `IsModuleAdmin_MatchesGrantedGroupSid`. This is the audit's own closing
observation in practice: *"several existing tests validate individual pieces whose combined
behavior is wrong."*

New tests, with two domains sharing a group name and a RID:

| Test | What it pins |
| --- | --- |
| `IsModuleAdmin_MatchesGrantedGroupSid` | a SID grant still authorizes its own group |
| `IsModuleAdmin_DoesNotAcceptAnotherDomainsSameNamedGroup` | the finding: wrong-domain SID plus bare and qualified name claims, all refused |
| `IsModuleAdmin_LegacyNameValuedGrantAuthorizesNothing` | an unmigrated grant is inert, not loosely matched |
| `GetUnusableGrants_ReportsOnlyTheRowsThatAuthorizeNothing` | the report lists only non-SID rows |

Mutations, both run this session:

| Mutation | Result |
| --- | --- |
| drop `UsableSidsOnly`, compare raw stored values | FAIL (`LegacyNameValuedGrantAuthorizesNothing`) |
| re-add the bare-name fallback **ahead of** the usable-SID filter | FAIL (same test) |

**The second mutation passed on the first attempt and that was my error, not a weak test.**
Placed after the `usableGrants.Count == 0` early return it is unreachable for a legacy grant,
so it changed nothing. Moved ahead of the filter - where a regression would actually be
written - it fails. Recorded because a mutation that cannot execute is not evidence, the same
trap as the `8b56902` probe that did not compile.

Service restored byte-identical after both (md5 verified) and touched.

A fixture error is worth noting: the first wrong-domain SID used sub-authorities above
`uint.MaxValue`, and `IsUsableGroupSid` rejected it - the test failed for the right reason and
caught my bad input.

## Coder dispute

None.

## Known gaps

**The non-resolving-grant list has NOT been produced, and the owner ruled it must reach him
before this ships.** `GetUnusableGrants()` exists and is tested, but the grants live in the
shared config database on the deployed hosts - there is no `ConfigStore:Path` in the repo and
no local `config/*.db`, so the list cannot be generated from source. **It must be produced
against the live store before deployment**, and nothing yet surfaces it in the UI. Carried as
the next slice; see `.agents/state.md`.

Until that happens the effect of this change on the running system is: **every module-admin
grant stored as a name stops authorizing.** That is the ruled behaviour, not a regression, but
it is invisible until someone cannot open their module.

The guard on `AddModuleAdmin` is source-verified only - no bUnit harness exists, so nothing
proves the message renders.

`ADGroupAutocomplete.razor:137` (the OTHER picker, used for `AdGroup` config fields at `:456`)
still returns `SamAccountName ?? DisplayName`. It is out of this slice's scope because those
values are module configuration, not authorization subjects - but it is the same shape and
should be checked before any of them becomes an authorization input.

## Reviewer comments

Source: the six-agent read-only audit of `34bac08`, 2026-10-16, finding 1. Verified against the
code by the implementer before changing anything. No independent review of the FIX has run yet.

## Closeout

Fixed directly on `master`. Full gate pending at time of writing. Not pushed.
