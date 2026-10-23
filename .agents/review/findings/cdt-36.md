# cdt-36: deploy.ps1 -Force re-points an existing application to whatever -PublishPath / -AppPoolName it is given

**Severity**: MEDIUM, design - `deploy.ps1` run with `-Force` over an existing deployment lands in the fresh-install branch and re-creates or re-points the IIS application to this run's `-PublishPath` and `-AppPoolName` with no cdt-15-style check that they still name the pool and folder serving the alias. A mistyped or defaulted value moves the live application (the cdt-15 / aud-a34 outage shape) while the run ends successfully.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded by the supervisor at the 2026-10-23 hand-over, from the cdt-15 agent's hand-back)

## Evidence

At `4498da49`: `deploy.ps1` 33 (`-Force`), 202 (` = (Test-Path ) -and (-not )`), 212-223 (the -Force consent and the appsettings copy), 815-882 (the -Force branch: backup, then `New-WebApplication` / re-point with `-PhysicalPath ` and the pool), 618 and 932 (`Assert-AppPoolServesPath` runs on the upgrade path and after the mirror, not before the re-point). The cdt-15 fix in `tools/Install-ExchangeAdminWeb.ps1` (`480d290`) is the reference shape: read the existing application's physicalPath and applicationPool first and refuse a silent move.

Trigger: `deploy.ps1 -Force` (or the pipeline passing it) with a pool name or publish path that differs from the serving one. Predicted failure: the alias now serves another pool or folder; the old one is left unserved; "complete".

## Approach (to be written by the implementing agent before code)

- Before the -Force branch changes IIS, read the existing application's `physicalPath` and `applicationPool` and compare them with the parameters; a difference refuses (`Write-Fail`) unless an explicit new switch names the move, and `-PlanOnly` reports the comparison.
- Pester covers: same values proceed; different pool refuses; different path refuses; the explicit switch allows; plan mode prints and changes nothing.
- Mutation: the comparison removed.

## Guard proof

Pending.