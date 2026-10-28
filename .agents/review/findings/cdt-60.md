# cdt-60: the cutover's pool-sharing report hides the nested IIS applications it stops; -PlanOnly still queries the live jobs database

**Severity**: LOW. (1) `tools/Move-ConfigDbToShared.ps1` about 239 allows nested applications under the instance's folder
(`-NestedNodes Allow`), but the report at about 265 uses `Get-AppPoolOtherApplication`, which excludes descendants
(`tools/AppPoolControl.psm1` about 628), so the cutover prints "stopping it stops nothing else" and stops a nested
application with active jobs unnamed. The cdt-58 record already noted the nested gap. (2) Pre-existing: `tools/JobStateWarning.psm1`
about 151 calls `Get-ActiveBulkJobs` (which runs the app's `db active-jobs` tool, about 88) BEFORE it branches on `-PlanOnly`,
so a plan-only cutover or promotion opens the real jobs databases (read-only, but a real read - Invariant 4).
**Status**: Open - admitted 2026-10-27 from the final codex-commercial review (findings 4 and 5); inside the mandate
**Branch**: -
**Commit**: -
**Reviewer**: codex-commercial / gpt-6-astra / max / read-only; session 01a18016-55c8-7262-bdc3-d3d86c927988; 2026-10-27

## Evidence

`.agents/review/final-review-2026-10-27.result.md` findings 4 and 5, at `b9c8f19b`.

## Approach

- (1) the cutover excludes only the instance itself from the report, keeping nested applications and their job warnings
  (a parameter on the helper, or a cutover-local filter). Pester: a nested application with jobs is named and its jobs
  read; mutation: the exclusion restored.
- (2) branch on `-PlanOnly` before `Get-ActiveBulkJobs` and print the intended check. Pester: plan mode launches no tool
  (the tool path stand-in records no call); mutation: the branch moved back.

## Guard proof

Pending.