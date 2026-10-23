# aud-a29i: a refused page request's audit row does not say which page was refused

**Severity**: LOW - audit completeness. `Middleware/PageRequestRevalidationMiddleware.cs` about 139 and `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` about 311: the row carries operator, IP, `SessionRevalidation`, `trigger: PageRequest`, reason and removed SIDs, but no route or path, so several refused page requests from one operator cannot be told apart by target during an incident review.
**Status**: Open - admitted 2026-10-23 evening from codex auth-batch-1 on aud-a29g; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier=standard pair (grade fallback, owner 2026-08-03) [escalated: T1 Authorization/* paths]; codex-cli 0.163.0; base eb017102 head 2678735e (merged a5d12cc8); 2026-10-23T19:33-19:5xZ; capability proof: AGENTS.md read and git show --stat 2678735e both succeeded

## Evidence

Codex auth-batch-1 finding 1 at `a5d12cc8`.

## Approach (to be written by the implementing agent before code)

- Pass a sanitised request path (`PathBase + Path`, no query string, bounded length) into the page-request audit extras; keep the existing fields. Test pins the field; mutation: the field dropped.

## Guard proof

Pending.