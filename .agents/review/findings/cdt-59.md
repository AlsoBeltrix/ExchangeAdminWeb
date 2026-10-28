# cdt-59: deploy.ps1's pool-sharing report throws, and refuses the deploy, when another application sits on a drive the host lacks; the installer reads no bulk jobs

**Severity**: MEDIUM for (1) - a refusal where a warning was meant. (1) `deploy.ps1`'s cdt-54 report (`Write-AppPoolSharingReport`
or its equivalent) joins each other application's folder with `Join-Path`, which throws `DriveNotFoundException` for a drive
the host does not have; the throw refuses the deploy before the stop. The cdt-58 reports in promotion and cutover guard this
with an on-disk check; deploy.ps1 does not. Confirmed by the cdt-58 agent under Windows PowerShell 5.1 and PowerShell 7.
(2) LOW: `tools/Install-ExchangeAdminWeb.ps1` reads no bulk jobs at all before stopping the pool - neither its own instance's
nor the other applications' (cdt-57's known gap, carried into cdt-58). (3) Trivial: `tools/JobStateWarning.psm1`'s header lists
`deploy-pipeline.ps1` as a caller, which it is not, and omits the cutover.
**Status**: Open - admitted 2026-10-27 from the cdt-58 agent's hand-back; inside the mandate
**Branch**: -
**Commit**: -
**Reviewer**: implementer (candidate)

## Evidence

At `857accaf`: the cdt-58 record's "Found and not fixed".

## Approach

- (1) the same on-disk guard the cdt-58 reports use: a folder not on disk is named in the warning and not read for jobs;
  Pester: another application on a nonexistent drive - the deploy warns and proceeds to the stop; mutation: the guard removed
  (the test sees the throw).
- (2) the installer imports `JobStateWarning.psm1` the way it imports `AppPoolControl.psm1` (cdt-48 precedent; Invariant 1 -
  no coupling to deploy.ps1) and warns about its own instance's active jobs and the other applications' before the stop,
  never refusing; `-PlanOnly` prints the plan line and reads nothing; Pester per path; mutation: the warning call removed.
- (3) the header comment lists the real callers.
- Gates: PSScriptAnalyzer (0 errors), Pester, ASCII lint, `git diff --check`; parse under Windows PowerShell 5.1.

## Guard proof

Pending.