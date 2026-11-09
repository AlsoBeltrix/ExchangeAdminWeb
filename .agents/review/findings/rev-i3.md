# rev-i3: the deploy plan says REFUSES twice for the app-folder verdict

**Severity**: LOW - wording only. `deploy.ps1` `Write-PlanRefusal` prefixes "a real run REFUSES here:", and the rev-f3
`AclRefuses` path passed it `Resolve-ConfigDbTool`'s AclSummary, which already reads "App folder permissions: a real
run REFUSES to run the db tool from ..." (that text carries its own refusal wording because promote,
Move-ConfigDbToShared and the installer print it with plain `Write-Plan`).
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/rev-i` (worktree `D:\source\ExchangeAdminWeb-wt\rev-i`, from master `79af56e7`)
**Commit**: `417d0889` (`fix(rev-i3): the deploy plan says REFUSES once for the app-folder verdict`) | **Implementer**: Claude (supervised agent)
**Reviewer**: owner (live plan run 2026-11-09)
**Range reviewed**: live `deploy-pipeline.ps1 -Dev -PlanOnly` output after rev-f1/rev-g1 (master 79af56e7)

## Verified real (master 79af56e7)

`deploy.ps1:77` `Write-Host "  !  PLAN: a real run REFUSES here: $m"`; `:532`
`if ($planTool.AclRefuses) { Write-PlanRefusal $planTool.AclSummary }`; `tools/SqliteConfigBackup.psm1:122,126` build
the AclSummary with "a real run REFUSES to run the db tool from". The rev-f3 test in
`tests/ps/DeployInvariants.Tests.ps1` pinned the doubled text ("REFUSES here: App folder permissions: a real run
REFUSES ..."). The other `Write-PlanRefusal` callers pass bare reasons and are not doubled.

## As built

- `deploy.ps1`: `Write-PlanRefusal` gains `-Verdict`; with it the line is `  !  PLAN: <verdict>` (no prefix), counted
  the same. Only the AclRefuses call uses it. AclSummary is unchanged, so the other scripts' plan lines are unchanged.
- Tests: the rev-f3 plan-run test now expects `!  PLAN: App folder permissions: a real run REFUSES to run the db tool
  from X` and not `REFUSES here: App folder permissions`; it still expects "1 refusal" in the plan summary.
- PSScriptAnalyzer: one more `PSAvoidUsingWriteHost` warning in deploy.ps1 (45 -> 46, the file's existing pattern);
  no errors.

## Guard proof (`tests/ps/DeployInvariants.Tests.ps1`)

| Mutation | Failing tests |
| --- | --- |
| Call site back to `Write-PlanRefusal $planTool.AclSummary` | 1: "rev-f3: a db tool that cannot be located, and a folder verdict that refuses, are each a counted refusal" |
| `-Verdict` still prints the prefix | 1: the same test |
| `-Verdict` refusals not counted | 1: the same test ("1 refusal") |
| Restored | file 156 of 156 |
