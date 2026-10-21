# Defect review request: deploy.ps1 fix batch (cdt-5 .. cdt-10) and the C7 test-context fix (cdt-7)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`.
- Base: `cd961c3`. Head: `27f8167`. Review `git diff cd961c3..27f8167`; `git log --oneline
  cd961c3..27f8167` lists the commits (fix commits, two merges, record commits).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Finding records: `.agents/review/findings/cdt-5.md` through `cdt-10.md`. Each states the
  bug, the approach, the assumptions the implementer took under the owner's go, and a guard
  proof table. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Architectural
  Invariants" (2: one shared config DB; 3: deploys never overwrite runtime config, backups by
  verified online backup, jobs/usage DBs are never backed up; 4: plan mode; 5: error model;
  6: ASCII and Windows PowerShell 5.1) and "Known Failure Classes" (1: side-effect ordering vs
  try/catch/finally).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 27f8167`. Report whether
both succeeded.

## Mandate

Hunt for observable defects in `deploy.ps1` as it now stands and in the two test files:
a path through fresh install, upgrade or `-Force` that mirrors or stops a pool without a
verified backup; a backup that can be torn or point at the wrong database; a failure path
that leaves or deletes the wrong thing (trace the one outer try/finally and the inner ones -
what happens when a `finally` body itself throws); a regenerated `appsettings.json` that
loses or alters a key it should keep; a 5.1 incompatibility; an exclusion list that
misses a per-instance file or excludes a file that must be backed up; an AST guard that
pins the wrong thing; and, for `cdt-7`, whether the new `ResumeAsync` test context can
itself hang or mask a real failure. Judge the implementers' recorded assumptions (preserve
`ConfigStore:Path` exactly as read; `-Force` stops on malformed appsettings; backup gate
`$Force -and (Test-Path $configPath)`; subdirectories of `config\` not in the snapshot).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
