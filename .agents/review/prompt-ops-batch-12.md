# Defect review request: deploy.ps1 refuses to move an existing IIS application to another pool or folder unless -AllowRepoint (cdt-36)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `72430407` or later.
- The change is the branch merged at `72430407`: one commit `493c270d` forked from `eb017102`. Review
  `git diff eb017102 493c270d -- . ':!.agents'` and read the COMBINED `deploy.ps1` (the new step at about lines 288-305,
  `Assert-ApplicationTargetUnchanged` at about 191-238, the `-AllowRepoint` switch, the -Force / -ConfirmFreshInstall branch
  at about 815-882), `tools/AppPoolControl.psm1` (`ConvertTo-ComparablePhysicalPath` now exported; `Assert-AppPoolServesPath`),
  `tests/ps/DeployInvariants.Tests.ps1` (the three new cdt-36 tests), and the README deploy paragraph.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network, no IIS.
- Records: `.agents/review/findings/cdt-36.md`; reference shape `cdt-15.md` (the installer's equivalent check, commit `480d290`);
  `cdt-28.md` (serving-pool check). ALREADY admitted and out of scope - do not re-raise: `cdt-38` (the upgrade path's pool
  step and ACL grants run before its binding check), `cdt-39` (a stale line pointer in docs/ConfigDbOperations.md), and the
  known absence of a native -PlanOnly in deploy.ps1 (Architectural Invariant 4 gap, recorded in .agents/state.md Ops gaps).
  Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Architectural Invariants" 1, 4, 5, 6 and "Known Failure Classes" (1, 2);
  `docs/ProjectConstitution.md` Deployment section.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 493c270d`. Report whether both succeeded.

## Mandate

Hunt for observable defects. The step's position: is there any IIS, pool, ACL, filesystem or appsettings change on ANY path
(-Force, -ConfirmFreshInstall, upgrade) that can run before the new read-only check; the comparison itself: path normalisation
(trailing slash, case, 8.3 names, UNC, junctions, forward slashes, `%SystemDrive%` expansion), pool-name case, an application
whose `physicalPath` is an environment-variable string as IIS stores it; the read failing: does "IIS cannot be read" refuse
on EVERY error, and does a genuinely absent application (true fresh install) proceed - what does `Get-WebApplication
-ErrorAction Stop` return or throw for a missing app on the real WebAdministration module, and does the code handle both
shapes; `-AllowRepoint` semantics: does it warn and proceed, does it still take the verified backup, does it leave the OLD pool
and folder documented anywhere; `$LASTEXITCODE` / native-exe conversion for anything the step shells out to (Invariant 5);
pure ASCII and Windows PowerShell 5.1 parse safety (Invariant 6) of both changed scripts; the exported module function:
callers of `ConvertTo-ComparablePhysicalPath` and whether its export changes `Export-ModuleMember` lists so another public
function is now missing; the tests: do the stubs stub the real cmdlet names the step calls (a stub for a cmdlet the code no
longer calls passes vacuously), does the "position in the script" test pin a line number or a structural order; and anything
in the README paragraph that overstates what the check does.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.