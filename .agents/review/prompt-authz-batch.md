# Defect review request: protected-principal and self-service gate batch (aud-a3b, a3c, a2b, a4b finding 7, a1b)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`.
- The change is the branch `1633ad0..f0e9844e` (commits `ef0664e`, `71e7ca9`, `1ad872f`,
  `f0e9844`, `d74e66f`, `0ad4ee3`), merged into master at `git log --merges -1 --format=%h
  -- Services/ProtectedGroupMembership.cs`. Review `git diff 1633ad0 f0e9844e`. Master has
  since merged other branches; `Services/SelfServiceGroups/SelfServiceGroupService.cs` on
  master combines this branch's change with psl-5 (`bc91960`) - check the combined file on
  master for that one.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Finding records: `.agents/review/findings/aud-a3b.md`, `aud-a3c.md`, `aud-a2b.md`,
  `aud-a4b.md` (finding 7 and its Resolution), `aud-a1b.md`; parents `aud-a3.md`,
  `aud-a2.md`, `aud-a4.md`, `aud-a1.md`. Each record has a Plan section with the
  implementer's design and a list of new refusal behaviours. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Architectural Invariants" (7: environment neutrality,
  including in reasoning) and "Known Failure Classes" (3: fail-closed authorization and its
  two scoped exceptions - Self-Service Groups keeps its member check and skips Protected
  Group Targets; Comms-10k consults neither); `docs/ProjectConstitution.md` Protected
  Principals and the never-do list.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat f0e9844e`. Report whether
both succeeded.

## Mandate

HIGH-severity authorization code. Hunt for observable defects: any remaining path by
which a protected principal reads as unprotected or a protected mailbox is reachable
(membership evaluation across domains, primary group, domain-local and universal scope,
global-catalog reads, SID binding, objectGUID agreement between Exchange and AD, the legacy
excluded-users list, the alias branch, IsDirSynced handling); the write path still taking
the operator's typed string after an objectGUID-bound check (the record admits this - judge
whether a check-then-write by different identifiers is exploitable in practice); a new
refusal that would block legitimate everyday operations (the record lists several new
"needs a reachable global catalog / Exchange" behaviours - judge whether any is wrong rather
than merely strict); the self-service DACL resource-domain step (can it grant where it must
only deny; does it respect the scoped exception); the a1b save-time validation (can a bad
grant still be saved; can an outage block removing a grant); anything environment-specific
in code or in the safety argument; a test that passes for the wrong reason. Note that
another agent is concurrently fixing aud-a29c/d/e in `Authorization/Circuit*` - do not
review those files.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
