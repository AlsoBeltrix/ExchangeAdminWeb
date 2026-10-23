# Defect review request: installer junction-aware check, pool-move jobs warning, deterministic 8.3 test, orphaned-database refusal, docs pointers (cdt-47, cdt-48, cdt-49, cdt-50, cdt-51)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `fb2b288f` or later.
- The change is the range merged at `fb2b288f`: commits `fd7ddf18` (cdt-47), `cc5f8c82` (cdt-48), `e999ddda` (cdt-49),
  `ff36a296` (cdt-50), `7f1890f3` (cdt-51), on top of `42c2690b`. Review `git diff 42c2690b 7f1890f3 -- . ':!.agents'` and
  read the COMBINED `deploy.ps1` (the cdt-51 orphaned-database refusal and the widened verified-backup gate; the cdt-49
  pre-re-point jobs warning and the stop's own-pool check), `tools/Install-ExchangeAdminWeb.ps1` (the cdt-15 check now using
  the module's folder rule; its local normaliser removed; the module import at about 792), `tools/AppPoolControl.psm1`,
  `tools/JobStateWarning.psm1` as called, `tests/ps/DeployInvariants.Tests.ps1`, `tests/ps/AppPoolControl.Tests.ps1` (the 8.3
  test with fsutil and the Program Files fallback), `tests/ps/InstallerConfigBackup.Tests.ps1`,
  `tests/ps/TestSuiteIntegrity.Tests.ps1` (new: the suite refuses any skip path), `docs/Containerization-Feasibility.md`.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no IIS.
- Records: `.agents/review/findings/cdt-47.md` .. `cdt-51.md`; parents `cdt-36.md`, `cdt-42.md`..`cdt-46.md`. ALREADY admitted
  and out of scope - do not re-raise: `cdt-52` (the installer's refusal does not name a non-existent spelling), `cdt-53`
  (app-code pointers in the containerization doc), `cdt-54` (other applications sharing the pool are stopped and their jobs
  unread), the absence of a native -PlanOnly in deploy.ps1, and the unverified live shape of `Get-WebApplication` for a
  missing application. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariants 1 (the installer is
  standalone and never coupled to deploy.ps1), 3, 4, 5, 6 and Known Failure Classes 1, 2; `docs/ProjectConstitution.md`
  Deployment section.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 7f1890f3`. Report whether both succeeded.

## Mandate

Hunt for observable defects. cdt-51: the orphaned-database rule - exactly which old-folder paths are probed (the default
`config\exchangeadmin.db` only, or also a `ConfigStore:Path` that an absent appsettings cannot name), whether a legitimate
fresh install over an empty folder is now refused (an empty `config\` folder, a zero-byte db, a db left by a failed first
run), whether the widened verified-backup gate can now back up a database the run is not about to touch and present that
as the pre-deploy backup; cdt-49: the jobs warning for the instance being taken down - read from which path, with which
build's `db active-jobs` (the one about to be deployed, per repo guidance; is that right for the OLD instance's schema), and
what happens when the old instance's jobs database is absent or locked; the stop's own-pool check - can it stop the wrong
pool in a `-AllowRepoint` pool move; cdt-48: Invariant 1 - the installer imports `tools\AppPoolControl.psm1`, confirm that
import pre-dates this change and that a standalone copy of the installer (shipped without `tools\`) still fails with a
clear message rather than a missing-command error; cdt-50: `fsutil file setshortname` needs elevation and an NTFS volume
with 8.3 enabled - what the test does when it is not elevated (CI runners are) or when fsutil fails, and whether the
Program Files fallback can itself be a long-name-only path; the suite-integrity guard - does it reject `-Skip:$false` or
only literal skips, and can it reject a legitimate future skip with a reason; Invariants 5 and 6 across every changed
script; and tests that pass for the wrong reason (a stubbed `Get-WebApplication` shape production does not return; the
junction fixture left behind on failure).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.