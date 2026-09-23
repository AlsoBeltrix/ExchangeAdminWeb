# cpr-7: The module named a sidebar icon class that does not exist

**Severity**: LOW - cosmetic. The nav item renders without an icon.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`Modules/ModuleCatalog.cs` as of `8a411c1` set `IconCss = "bi bi-key-fill-nav-menu"`, taken from
the plan's descriptor block. `Components/Layout/NavMenu.razor` renders `module.IconCss` straight
through, and neither `Components/Layout/NavMenu.razor.css` nor `wwwroot/app.css` defines
`.bi-key-fill-nav-menu`.

## Predicted observable failure

Once the module is enabled and granted, its sidebar entry appears with no icon.

## Approach

Use `bi bi-person-fill-nav-menu`, which exists and is what `MfaReset` uses - the closest sibling
module, same category, same shape of operation.

**And a guard, because the interesting part is that nothing caught it.** A descriptor can name any
class it likes; the nav renders the string and says nothing. `ModuleIconClassTests` now asserts
every non-config-only module's `-nav-menu` class is defined in `wwwroot/app.css`.

That guard immediately found a second instance: `ExchangeOnline` names `.bi-cloud-fill-nav-menu`,
also undefined. It is **exempted rather than fixed** - config-only modules are excluded from the
sidebar by the module contract, so their icon never renders. Fixing its CSS would have been a
drive-by on an unrelated file to satisfy a test that should not have been asking about it.

## Files changed

- `Modules/ModuleCatalog.cs` - the icon class.
- `ExchangeAdminWeb.Tests/CloudPasswordResetWritePathTests.cs` - `ModuleIconClassTests`.

## Guard proof

Mutation: restore `bi bi-key-fill-nav-menu` and
`Every_descriptor_icon_class_is_defined_in_the_host_css` fails, naming the module and the class.

## Coder dispute

None.

## Known gaps

The guard checks `wwwroot/app.css` only, not the scoped `NavMenu.razor.css`. Both define the same
classes today and app.css is the fuller list, so a class defined ONLY in the scoped file would be
reported as missing. That would be a false positive, which is the safe direction.

## Reviewer comments

Round 1, Change review over `8a411c1^..8a411c1`:
Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-23.
Capability proof passed. Verdict **unsound**, one MEDIUM (`cpr-6`) and this LOW.

## Closeout

Fix and this record land with the write-path slice.
