# Defect review request: installer and deploy.ps1 hardening batch (cdt-11 .. cdt-27)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `13bb8b1` or later.
- The change is the branch merged at `c4dab11`: seventeen fix commits from `5784583` (cdt-11) to
  `e04ae9e` (cdt-27), forked from `0e18583`. `git log --oneline 0e18583..e04ae9e` lists them.
  Review `git diff 0e18583 e04ae9e`. Master had meanwhile merged `cdt-8/9/10` (-Force path) and
  `aud-a38c` (AppPoolControl.psm1 `Assert-AppPoolServesPath`) on the same files; git merged them
  without conflict, so review the COMBINED `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1` and
  `tools/SqliteConfigBackup.psm1` on master as they stand, not only the branch diff.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/cdt-11.md` through `cdt-27.md` (each has a Plan, Evidence,
  assumptions taken under the owner's go, and guard proof). Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Architectural Invariants" (1: the installer is standalone and
  must never couple to deploy.ps1; 2: one shared config DB; 3: deploys never overwrite runtime
  config, verified online backups, jobs/usage DBs never backed up; 4: plan mode; 5: error
  model; 6: ASCII and Windows PowerShell 5.1) and "Known Failure Classes" (1: side-effect
  ordering vs try/catch/finally; 4: stale references).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat c4dab11`. Report whether
both succeeded.

## Mandate

Hunt for observable defects in the installer and deploy.ps1 as they now stand: any path that
mirrors, stops or restarts without the guards the records claim (verified backup first, binding
check, serving pool, 'Stopped' before mirror, restart only after a complete mirror, primary
exception preserved); the installer's new ConfigStore plan (`Resolve-ConfigStorePlan`,
`Assert-ConfigStorePlanDatabase`, key preservation versus `-ConfigStorePath`, the cdt-20
refusal) - can a shared-DB instance still be repointed, can a legitimate first install or
regenerate be refused; the staging try/finally shapes on both scripts (what happens when a
finally body throws; plan mode leaving or removing staging); the `Set-AclChecked` empty-name
refusal and the identity plan; the new `ConfigStore:Path` validation rule versus what the app's
`ConfigStorePath.Resolve` accepts (does the PowerShell rule refuse something the app accepts,
or vice versa); the manifest's gitDirty/gitCommit handling; 5.1 incompatibilities; AST guards
that pin the wrong thing or depend on exact text that a harmless edit would move; and the
interaction of this branch's deploy.ps1 edits with the -Force path edits from cdt-8/9/10 that
git merged mechanically.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
