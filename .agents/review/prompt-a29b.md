# Defect review request: aud-a29b - circuit revalidation of group membership

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`.
- The change is the three commits `cb3be91`, `702f66f`, `0d30442`, merged at `b60a507`.
  Review `git diff 1633ad0 0d30442` (the branch as a whole; `1633ad0` is its fork point).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Finding record: `.agents/review/findings/aud-a29b.md` - the plan, the implementer's
  recorded design choices and veto list, the known gaps and the guard proof. Parent:
  `aud-a29.md`. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Architectural
  Invariants" (7: environment neutrality, including in reasoning) and "Known Failure
  Classes" (3: fail-closed authorization); `docs/ProjectConstitution.md` authorization,
  audit and never-do sections.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat b60a507`. Report whether
both succeeded.

## Mandate

This is a HIGH-severity authorization change in a Blazor Server app authenticated with
Windows Negotiate. Hunt for observable defects: a path by which a revoked operator keeps
acting (a race between the periodic check and an in-flight action, a reconnect, a second
tab, prerendering, the gate not covering a dispatch route); a path by which a legitimate
operator is wrongly revoked (SID comparison errors, group scope such as domain-local groups
from another domain, nested or universal groups, the account's own domain discovered wrongly,
a lookup that returns a partial answer and is read as a removal); the directory read
through the PowerShell seam (what happens when the command returns nothing, an error record
beside a row, a throw, a cancellation; connection leaks; concurrency cap); the off-switch
(a way to be off while believing it is on; the startup audit line); configuration parsing
(TimeSpan and bool edge cases); the audit line contents versus the Constitution's audit
rules; anything environment-specific in code or in the safety argument; a test that passes
for the wrong reason. Also judge the implementer's recorded design choices (the record's
veto list, items 1 to 8) and say which, if any, you would change and why.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
