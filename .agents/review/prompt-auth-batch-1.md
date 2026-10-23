# Defect review request: a page request's access is confirmed before the page is prerendered (aud-a29g)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `a5d12cc8` or later.
- The change is the branch merged at `a5d12cc8`: one commit `2678735e` forked from `eb017102`. Review
  `git diff eb017102 2678735e -- . ':!.agents'` and read the COMBINED files: `Middleware/PageRequestRevalidationMiddleware.cs`
  (new), `Authorization/CircuitMembershipRevalidator.cs` (new `RevalidatePageRequestAsync` and the interval cache of the
  directory's answer), `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs`, `Program.cs` (middleware order
  relative to authentication, authorization, the page-load scope and static files), `appsettings.json.sample`, `README.md`,
  and `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` (18 cases; the end-to-end ones run a loopback host with a
  probe page prerendered like the app's pages).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a29g.md` (approach, cost table, mutation list, known gaps, veto list);
  parents `aud-a29b.md`, `aud-a29c.md`, `aud-a29d.md`, `aud-a29e.md`, `aud-a29f.md` and their commits. ALREADY admitted and
  out of scope - do not re-raise: `aud-a29h` (the off-switch warning's "until it is reloaded" wording). Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (3: fail-closed authorization) and Architectural
  Invariant 7 (no environment facts as safety arguments); `docs/ProjectConstitution.md` (authorization, audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 2678735e`. Report whether both succeeded.

## Mandate

Hunt for observable defects. The middleware's position: which requests reach it (full page loads, enhanced navigation,
`_blazor` negotiate and websocket upgrade, static files, the `db` command-line mode, health or diagnostics endpoints) and
which bypass it - name any page-rendering request that can skip it, and any non-page request it needlessly reads the
directory for; the interval cache: keyed by what (SID? name? ticket?), shared across operators or per operator, invalidated
when section-access configuration changes (aud-a29d made that observable on the circuit - does a page request see it
inside the interval?), what a stale cached "allowed" means when the off-switch is flipped at runtime, and whether a cached
"refused" persists past a re-grant; the fail-closed path: what exactly is refused when the directory read throws, times
out, or the credential is missing - a 403 page, and never a rendered page; the catch-all branch no test reaches - what
exception reaches it and what the response is; the off-switch off: reads nothing and refuses nothing - confirm; the
403 body: environment-neutral, no group names, no SIDs, no directory error text; the audit line per refused page request
(trigger `PageRequest`) - operator identity, path, reason, and whether a flood of refusals (a revoked operator's browser
retrying) can fill the audit log or block the response path; thread-safety and lifetime of the cache (singleton vs
scoped), and memory growth per distinct operator; the second read on a first page load with an empty cache (the circuit
checks never use the cache) - any race where the page read and the circuit read disagree; enhanced navigation after a
403 (the agent read `blazor.web.js` 10.0.12 but did not observe a browser) - what does the client do with a 403 to a
navigation fetch, and can the old page stay interactive on a stale circuit; the loopback-host tests: do they exercise the
real middleware pipeline order from `Program.cs` or a hand-built one that could drift from it; and any Known Failure
Class 1 ordering in the middleware (audit written before the refusal is final, or not at all on an exception).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.