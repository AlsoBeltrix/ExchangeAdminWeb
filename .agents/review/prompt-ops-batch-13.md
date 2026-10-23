# Defect review request: deploy.ps1 upgrade ordering, -AllowRepoint backup source, write ordering, junction-aware folder comparison, docs pointer (cdt-42, cdt-43, cdt-44, cdt-45, cdt-46)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `0c9917d5` or later.
- The change is the branch range merged at `0c9917d5`: commits `2bb80879` (cdt-42), `de48aff4` (cdt-43), `59e2c31e` (cdt-45),
  `349253f7` (cdt-44), `42c2690b` (cdt-46), on top of the already-reviewed `493c270d` (cdt-36). Review
  `git diff 493c270d 42c2690b -- . ':!.agents'` and read the COMBINED `deploy.ps1` (the cdt-36 check and where the upgrade
  path's pool step, ACL grants, backup folder creation and pre-force appsettings copy now sit relative to it; the
  -AllowRepoint path's backup source and config carry-forward), `tools/AppPoolControl.psm1` (`ConvertTo-ComparablePhysicalPath`,
  the new `Test-SamePhysicalFolder` with its compiled C# final-path helper, `Assert-AppPoolServesPath`), `tools/SqliteConfigBackup.psm1`
  as called, `tests/ps/DeployInvariants.Tests.ps1`, `tests/ps/AppPoolControl.Tests.ps1`, `docs/ConfigDbOperations.md`, `README.md`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network, no IIS.
- Records: `.agents/review/findings/cdt-42.md`, `cdt-43.md`, `cdt-44.md`, `cdt-45.md`, `cdt-46.md`, parent `cdt-36.md`; the
  ops-batch-12 result that raised 44-46 is `.agents/review/ops-batch-12.result.txt`. ALREADY admitted and out of scope - do
  not re-raise: `cdt-47` (stale line pointers in docs/Containerization-Feasibility.md), `cdt-48` (the installer's cdt-15
  comparison stays literal), `cdt-49` (active-jobs warning reads the old instance in a pool move), `cdt-50` (the 8.3 test can
  skip), the known absence of a native -PlanOnly in deploy.ps1, and the unverified live shape of
  `Get-WebApplication -ErrorAction Stop` for a missing application. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  Architectural Invariants 1, 3, 4, 5, 6 and Known Failure Classes 1, 2; `docs/ProjectConstitution.md` Deployment section.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 42c2690b`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Ordering (cdt-42, cdt-45): enumerate every statement on each of the three paths (upgrade,
-Force, -ConfirmFreshInstall) that writes to IIS, a pool, an ACL, the filesystem or appsettings, and name any that still
precedes the check; the consent prompt stays before the check - does a refusal after consent leave any consent-side
state. cdt-44: the -AllowRepoint path derives the OLD folder from the existing application's physicalPath - is that the
resolved or the literal spelling, does the backup target the old folder's `appsettings.json` `ConfigStore:Path` (the shared
database outside both publish folders, Architectural Invariant 3) or the old folder's per-instance file, what happens when
the old folder has no appsettings (fresh re-point over a half-removed instance), and is the carried `ConfigStore:Path`
validated by the same rule the app applies (cdt-17/cdt-30/cdt-37 - relative, drive-relative, UNC, forward slashes); the
"move whose old folder keeps its own config database is refused" rule - can it refuse a legitimate move and is the message
actionable. cdt-46: the compiled C# helper under Windows PowerShell 5.1 and PowerShell 7 (Add-Type cost, failure when the
compiler is unavailable on a locked-down host - does it fail closed or crash the deploy), behaviour when one path does not
exist yet (fresh install), on UNC paths, on a path under a junction several levels up, on a reparse point that is not a
junction (OneDrive, symlink to a file); `Assert-AppPoolServesPath` now matching "literal OR resolved" - can it now match
MORE nodes than before and refuse a legitimate deploy, or match a node outside the tree. Invariant 5: every native call
checks `$LASTEXITCODE`. Invariant 6: both scripts and the module parse under 5.1 and are pure ASCII (the inline C# too).
Tests: stubs name the real cmdlets, the junction fixture is removed on failure, no test depends on the host's drive layout
beyond the admitted cdt-50 case.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.