# Defect review request: aud-a29c, a29d, a29e, a29f - revalidation follow-ups

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`.
- The change is `git diff 9fab2e4f 9916b0e4` (commits `59e973e`, `80eacbf`, `fe0a9ab`, `9916b0e`),
  merged into master after `3562cf0`. Base `9fab2e4f` already contains aud-a29b and its first
  review's verdict (`.agents/review/a29b.result.txt`), which these commits answer.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a29c.md`, `aud-a29d.md`, `aud-a29e.md`, `aud-a29f.md`,
  and the amended `aud-a29b.md`. The a29c record quotes the ASP.NET Core circuit pipeline order
  it relies on. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Architectural Invariants"
  (7) and "Known Failure Classes" (3); `docs/ProjectConstitution.md` authorization and audit.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 9916b0e4`. Report whether
both succeeded.

## Mandate

HIGH-severity authorization code. Hunt for observable defects: in a29c, any inbound path that
bypasses the pending hold (hub methods, JS interop, render acks, location changes, stream
chunks, reconnect, resume), a deadlock or an unbounded wait, a hold released in a way that
runs work on a revoked circuit, the off-switch interaction; in a29d, a grant removal that is
not seen (setting/SID pairing, the AllowedGroups fallback, captured-set growth and
non-shrinkage, reconnect tokens), an unreadable store read as empty, false revocations that
would end legitimate sessions on routine configuration edits; in a29e, a static group that
still authorizes without being validated, or a legitimate domain configuration refused at
startup; in a29f, a servicer value that still authorizes by name, or a legitimate servicer SID
refused; plus the implementer's veto list in each record (say which choices you would change
and why) and tests that pass for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
