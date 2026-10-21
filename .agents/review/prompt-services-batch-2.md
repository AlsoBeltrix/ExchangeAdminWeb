# Defect review request: services and promotion batch (psl-2/3/4/5, aud-a38b/c/d, aud-a6c/a6d, aud-a7b/a7c)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `98ac93b`.
- Three merged branches, each forked from `1633ad0` or `ca6575a` and merged into master:
  - PowerShell lifecycle: commits `c557fa3` (psl-4), `fd5d867` (psl-2), `6b3953e` (psl-3), `bc91960` (psl-5).
  - Promotion script: `25e7187` (aud-a38b), `bafac35` (aud-a38c), `7f469d1` (aud-a38d).
  - EXO pool and Out of Office: `0152847` (aud-a6c), `419ad2a` (aud-a7b), `3e5684b` (aud-a6d), `b8dd5d7` (aud-a7c).
  `git show <sha>` each; `Services/ExoConnectionPool.cs` and `Services/SelfServiceGroups/SelfServiceGroupService.cs` on master combine two of these branches (and `6afacaf` adapted one test to that) - review the combined files on master for those two.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Finding records under `.agents/review/findings/` with the same ids; each has a Plan section,
  guard proof and known gaps. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Architectural
  Invariants" (4, 5, 6 for the PowerShell script) and "Known Failure Classes" (1, 2, 3);
  `docs/PowerShellCommandLifecycle-Plan.md` for the psl stream's invariants.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 98ac93b`. Report whether
both succeeded.

## Mandate

Hunt for observable defects: in the psl changes, a diagnostic consumed after it was cleared or
cleared before it was consumed, a retry rule changed, an exception identity lost, a success
reported as failure or the reverse; in the pool, a connection stranded or returned to a stopped
pool, a disconnect failure that stops the drain, a deadlock between `_poolLock` and the existing
locks, a borrow path that bypasses the stop, interaction with psl-4's `ConnectionFault` path; in
Out of Office, any post-capture read of a bound field (page or service), an audit or
notification that names something other than what was written, the changed service signature's
other callers; in promote, a path to the mirror without a verified 'Stopped', the serving-pool
check (false refusals on legitimate IIS layouts, false passes), the IIS: drive check, dry-run
side effects, 5.1 compatibility; and tests that pass for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
