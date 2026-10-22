# aud-a28w: The app's first PowerShell sessions after a start could hit the start-up race the test suite now guards against

**Severity**: LOW - unreproduced; recorded because the suite's guard rests on an observation, not a proven
cause. aud-a28r/a26p added a one-time test start-up guard after one failure where two classes' first
runspaces opened at once. Nothing in the app opens a runspace first: after a start the first two
operators' sessions may open concurrently, the same shape.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28r/a26p agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `25071281`: `ExchangeAdminWeb.Tests/PowerShellStartup.cs` (the guard and what it observed; the race
did not reproduce in 12 cold test-host runs and about 60 cold processes); `Program.cs` and the hosted
services (nothing warms a runspace); `Services/ExoConnectionPool.cs`, `Services/ExchangeServiceBase.cs`.

## Approach (to be written by the implementing agent before code)

- First establish whether the production path has the same shape (which runspace factory, which initial
  session state, whether the first open is already serialised by a pool or lock). If it is already
  serialised, close the record with that proof and no code.
- Otherwise a hosted start-up warm-up opens one runspace of each default shape once, retries, never
  throws and never blocks start-up; log one line. No live AD/EXO/Graph call - a runspace open only.
- Test: the warm-up runs before the first request and swallows a failure; mutation at the call site:
  the warm-up registration removed.

## Guard proof

Pending.

## Folded in: the warm-up fixture discards the failure it is meant to diagnose (codex, settings-batch-4, 2026-10-22) - LOW

`ExchangeAdminWeb.Tests/PowerShellStartup.cs` around line 40 and `PowerShellStartupTests.cs` line 22: when all
three warm-up attempts fail (`CreateDefault`, `CreateDefault2`, `Open`, `Invoke`), the fixture swallows the
exception and leaves only `Started == false`, so the suite reports "not registered, or its start failed"
without which shape failed or why. Keep the fixture non-throwing; store the last exception and the failing
shape in static state and include them in the pin test's message. Mutation at the call site: the stored
exception dropped.
