# mt-detail-slice8: MessageTrace module version bump 1.1.1 -> 1.2.0 (slice 8)

**Severity**: n/a — slice-landing review of a module version bump
**Status**: Verified — accepted round 1; codex CLI (gpt-5.5-dzs/xhigh/std)
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `e7ce73c` (slice 8), base `a6ae3d0`

## Evidence
`Modules/ModuleCatalog.cs` — the `MessageTrace` descriptor `Version` changes
`"1.1.1"` -> `"1.2.0"`. One line. No base app version (`ExchangeAdminWeb.csproj`
`<VersionPrefix>`/`AssemblyVersion`/`FileVersion`) change.

## Predicted observable failure
Wrong-layer version bump: bumping the base app version for a module-scoped change
(or failing to bump the module version for a shipped behavior change) violates the
Constitution "Deployment And Versioning" split rule. Changing the module count or
descriptor shape would break `ModuleCatalogTests`.

## What
Eighth slice of the Message Analysis detail work stream (plan task 8). The
per-message delivery-detail feature (slices 1-7) is a module-scoped behavior
change to MessageTrace, so its module version bumps; the base app version does not
(the feature reuses the existing bulk-job runner, audit log root, admin-email
config, and EXO/on-prem credentials — no shared-infrastructure change).

## Approach
Single-line `Version` bump in the MessageTrace catalog entry. Module count
unchanged (no new module).

## Files changed
- `Modules/ModuleCatalog.cs` — MessageTrace `Version` 1.1.1 -> 1.2.0 (only line).

## Guard proof
Mechanical version bump; guarded by `ModuleCatalogTests` (24/24 pass;
`Catalog_HasExpectedModuleCount` still 23, unaffected by a version change). Build 0
errors, ASCII/format/diff-check clean.

## Coder dispute (if any)
None.

## Known gaps
None for this slice. Slice 9 is the verification + manual-validation note.

## Reviewer comments

### Round 1 — accepted (codex CLI, gpt-5.5-dzs/xhigh/std), commit e7ce73c, base a6ae3d0
Verdict `accepted`, `guard_confirmed:true`, `capability_ok:true`, SHAs match dispatch.

Confirmed: correct-layer bump — only `Modules/ModuleCatalog.cs` MessageTrace
`Version` changed `1.1.1` -> `1.2.0`; `ExchangeAdminWeb.csproj` is absent from the
diff (base app VersionPrefix/AssemblyVersion/FileVersion untouched), matching the
Constitution "Deployment And Versioning" module-scoped rule. No collateral
descriptor/id/count/permission change; count guard
(`Catalog_HasExpectedModuleCount` = 23) still holds, ModuleCatalogTests 24/24.
Capability build EXIT=0. No findings.
