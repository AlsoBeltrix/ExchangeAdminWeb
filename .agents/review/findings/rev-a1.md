# rev-a1: a user reaching a protected security group only through a distribution group is not protected (token short-circuit)

**Severity**: HIGH - fail-open in the protected-principal check. `tokenGroups` lists security groups only; a user whose
membership in protected security group S runs through distribution group D (U in D, D in S) has no S in the token.
`ProtectedGroupMembership.TokenAnswers` treats "token read, group is a global/universal security group" as a confirmed
negative and skips the anchored member-link fallback, so the check returns NotProtected and a write proceeds. The
pre-kd-88 member-chain walk caught this case.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `145f513f`): `Services/ProtectedGroupMembership.cs:402-405`, `:492-495`; `Services/ProtectedPrincipalService.cs:447`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review A, range `8557a15a..5c4c2970`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-a.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Treat token presence as a positive answer only. A token MISS on a security group is not a confirmed negative when the
group can contain distribution groups as members: run the existing anchored member-link question for that group (one
`(&(distinguishedName=<S>)(member:1.2.840.113556.1.4.1941:=<U>))` per protected group, as before kd-88) before clearing it.
Keep the near-instant requirement: the fallback runs only for groups the token did not answer, and the earlier anchored
form ran live for months. Add the nesting case to `FakeForest` (its `ReadTokenGroups` already omits distribution
intermediaries) and a test that fails on the current code.
