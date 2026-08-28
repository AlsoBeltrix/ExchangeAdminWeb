# pgwt-8: AdminSettings module version not bumped for the new target list

**Severity**: LOW - deployment/support checks cannot distinguish the changed module.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: (filled in after commit)

## Evidence

S4 added observable behaviour to the Admin Settings page (the Protected Group Targets
list) but the catalog's AdminSettings module version was left unchanged. Constitution:
module-scoped behaviour changes bump that module's version, independently of the base
bump.

## Predicted observable failure

Module Config shows the old AdminSettings version on a build whose Admin Settings gained a
new protection-control workflow.

## Approach

Bump the AdminSettings catalog version with a comment, same motion as the other bumps.

## Files changed

- `Modules/ModuleCatalog.cs`

## Guard proof

Version literals are not testable non-vacuously; the range reviewer verified the other
bumps and this record closes the miss. `git diff` shows the single-line bump.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner standing dispatch),
generation pass over `8700531..5336072`, verdict `findings` (7), capability_ok true.
