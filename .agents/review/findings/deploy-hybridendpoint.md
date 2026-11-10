# deploy-hybridendpoint: `deploy.ps1` accepted `-HybridEndpoint` and ignored it

**Severity**: LOW - nothing depended on it. But an operator who passed `-HybridEndpoint` to a deploy was told
nothing and got nothing: the value was read nowhere. The Migration endpoint is a module setting (`HybridEndpoint` in
Module Configuration), which this parameter never reached.
**Status**: Merged to master at 27f53db7 (2026-11-10) on guard proof (LOW, no review round); master gates green (9073 xUnit, 687 Pester, 19 warnings, format/diff-check/ASCII clean)
**Branch**: `fix/p0-c-phase0-lows` (worktree `D:\source\ExchangeAdminWeb-wt\p0-c`, from master f151f527)
**Commit**: d6be8b2b (the parameter removed, Pester test) |
**Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.7 (defect #9; the same item as unwired-audit row A41)

## Evidence (master f151f527)

- `deploy.ps1:31`: `[string]$HybridEndpoint      = "hybrid1",` - the script's only mention of the name.

## Fix

The line is removed. Nothing else in the script changes.

Callers searched - every script, document and playbook (`grep -rn "HybridEndpoint"` over the repo, less `.git`,
`bin`, `obj`): **none passes it.** The remaining mentions are the Migration module's own setting
(`Modules/ModuleCatalog.cs`, `Services/MigrationService.cs`), `docs/SqliteConfigStore-Plan.md` (the legacy
`Migration:HybridEndpoint` appsettings key, a phase 1 item), `docs/ProdReadinessReview-2026-06-12.md` (history) and
the audit files. No argument had to be removed anywhere.

The script stays pure ASCII (`tools/Test-AsciiOnly.ps1` passes) and keeps its CRLF line endings.

## Tests

`tests/ps/DeployInvariants.Tests.ps1`, `deploy.ps1` block, 1 new: **`reads every parameter it accepts`** - parses
the script and fails on any declared parameter that no expression outside its own declaration reads. No existing
test pinned the parameter list, so none needed adjusting. This is a parse of the script, not a run of it: a deploy
cannot be run in a test, and the repo's deploy invariants are all checked this way.

No test deleted or changed.

## Guard proof

| Reverted | Failed | Tests that failed |
|---|---|---|
| The parameter put back (`deploy.ps1` as before the fix) | 1 | `deploy.ps1 / reads every parameter it accepts` - "Expected $null or empty ... but got 'HybridEndpoint'" |

Restored: passes; the whole Pester suite passes (687, in the hand-back).

## The two PowerShell gates

- `Invoke-ScriptAnalyzer -Path . -Recurse` (PSScriptAnalyzer 1.25.0), run on the branch before the change and after:
  452 findings before (335 Warning, 117 Information), 451 after (334 Warning, 117 Information). Compared by rule,
  severity and file, the one difference is `PSReviewUnusedParameter | Warning | deploy.ps1` going from 2 to 1 - the
  warning for this parameter. No new finding.
- `Invoke-Pester tests/ps`: 687 passed, 0 failed.

## Left alone, and why

- `deploy.ps1` still takes `-CloudTargetDomain` and `-OnPremTargetDomain`, which the 2026-06-12 readiness review
  questioned. They are read (prompted for and validated), so the new test passes them and they are not this defect.
- The one remaining `PSReviewUnusedParameter` warning in `deploy.ps1` was there before and is not a script
  parameter (the new test would name it).
- The legacy `Migration:HybridEndpoint` appsettings fallback is unwired-audit row 40, phase 1.
- Version: none. The Constitution (Deployment And Versioning) bumps the base version for shared infrastructure
  changes and a module's for module behaviour; `deploy.ps1` is not part of the shipped app, and the earlier
  `deploy.ps1`-only fixes (for example 417d0889, e45731d5) carried no version bump. Applied: no bump.
