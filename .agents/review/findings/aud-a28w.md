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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `d3f242a3`, the fixture's diagnostics folded in at
`e9886df7`. One commit `fix(startup): PowerShell is started once, in the background, as the app starts, and the test
start-up keeps the failure it swallowed (aud-a28w)`, after aud-a26s on this branch. Behaviour: app start-up (base app
version); no module version moves; the supervisor owns the bump. The folded-in part is test infrastructure and ships
nothing.

**Investigation (the record's first step): the production first open is not serialised, so code follows.** Read at
`e9886df7`.
- Shape. Every session the app opens is `InitialSessionState.CreateDefault()`, then `RunspaceFactory.CreateRunspace(iss)`,
  then `Open()`: 44 `CreateDefault` sites in 18 files - among them `ExoConnectionPool.cs` 668-671 (`CreateConnected`),
  `ExchangeServiceBase.cs` 469, 578 and 671, `ADAttributeEditorService.cs` 958-960 with its open at 1369 - and three
  `PowerShell.Create()` given no runspace, which open one of their own when invoked (`AccountLockoutRemediationService.cs` 575
  and 840, `TrueLastLogonService.cs` 132). That one measured as `CreateDefault`'s: a throwaway probe in the test host (deleted
  before any commit) found the lazily opened runspace's session state holding 593 commands, `CreateDefault()` 593 and
  `CreateDefault2()` 234. No production code uses `CreateDefault2`; the suite does, which is why its fixture opens both.
- Serialisation. None across services, and none to one inside most of them. Each throttle is its own service's:
  `SemaphoreSlim(2, 2)` statics at `ADAttributeEditorService.cs` 132, `EmergencyDisableService.cs` 129, `ExchangeServiceBase.cs`
  18 (`_onPremThrottle`), `GroupManagementService.cs` 18, `LicensingUpdatesService.cs` 53, `ProtectedPrincipalService.cs` 192 and
  `SelfServiceGroupService.cs` 42; per-instance `SemaphoreSlim(1, 1)` at `ADDirectorySearchService.cs` 14 and
  `TrueLastLogonService.cs` 36; the EXO pool's five slots (`ExoConnectionPool.cs` 140). Nothing opens a runspace at start:
  Program.cs's startup pass (371-484) touches no PowerShell, and `AddHostedService` appears nowhere in Program.cs or Services.
  So two operators acting at once after a start - in two modules, or in one whose throttle admits two - open their first
  sessions concurrently: the shape the suite's guard closes.

**Not a hosted service.** The record's approach says "hosted"; the app's standing posture says otherwise. The 2026-06-17
decision removed the app's only `AddHostedService`, and the 2026-07-02 bulk-job entry (item 1) "narrows - does not overturn -
the 2026-06-17 no-background-worker posture" by making `BulkJobService` a one-shot startup call rather than an
`IHostedService`; Program.cs 426-433 says so where it calls it. The warm-up takes that shape: a one-shot call in the startup
pass, after `builder.Build()` (365) and before `app.Run()` (513), which starts the work on the thread pool and returns at once.
It runs once, touches nothing outside the process, writes nothing and has no schedule or actor. And because it is called
before `app.Run()`, "before the first request" is a fact of the source order rather than of the host's start-up order.

1. **The warm-up.** New `Services/PowerShellWarmUp.cs`, static. `StartInBackground(ILogger)` starts the warm-up on the thread
   pool and returns its task at once - it never blocks start-up. The warm-up opens one `CreateDefault` runspace - the one shape
   the app uses - and disposes it: no module import, no command, no connection. Up to three attempts; never throws; one log
   line - Information on success (how long it took, which attempt), Warning with the last exception and the shape when every
   attempt failed (sessions then start as they always have). The attempt loop is `Start(shapes, attempts, open)`, returning
   `PowerShellStartOutcome(Started, Attempts, FailedShape, LastFailure)`: internal, and shared with the test fixture (part 3)
   so the two cannot drift.
2. **The call.** Program.cs, in the startup pass, directly after the log-root check (375-379) so it overlaps the passes below
   it: `PowerShellWarmUp.StartInBackground(...)` with a logger from the container; its task is not awaited.
3. **The folded-in fixture diagnostics.** `ExchangeAdminWeb.Tests/PowerShellStartup.cs` keeps its two shapes and its open (a
   command run in each), through `PowerShellWarmUp.Start`, non-throwing as before, and keeps the outcome - the last exception
   and the shape that failed - in static state (`Outcome`; `Started` reads it). `PowerShellStartupTests`' pin asserts with
   `NotStartedMessage(outcome)`: "not registered" when there is no outcome, otherwise the attempts, the shape that failed and
   the exception.

Tests, new `ExchangeAdminWeb.Tests/PowerShellWarmUpTests.cs`: Program.cs calls the warm-up after the host is built and before
it runs (source order); the production warm-up opens a real `CreateDefault` runspace in the test host and reports one
information line; `StartInBackground` returns while the warm-up's open is still held; an open that always throws is tried
three times, never thrown, and logs one warning naming the shape, with the exception; one that fails once is retried and logs
one information line; `Start` keeps the last exception and the shape that failed; the pin's message names both. Mutations at
the call site: (1) the warm-up call removed from Program.cs; extra (E1) the warm-up run inline, blocking start-up; (E2) an
attempt's failure thrown; folded (F1) the stored exception dropped in `Start`; (F2) dropped from the pin's message.

Known gap, carried from aud-a26p: the race was never reproduced and its cause is not established. If it is a process's first
start, the warm-up closes the window once it completes - a session opened before then, in roughly the first second or two
after a start (an estimate from the test host's cold opens, not a production measurement), is not covered; if it is a
runspace's own parallel bind, a warm-up narrows nothing.

## Guard proof

Pending.

## Folded in: the warm-up fixture discards the failure it is meant to diagnose (codex, settings-batch-4, 2026-10-22) - LOW

`ExchangeAdminWeb.Tests/PowerShellStartup.cs` around line 40 and `PowerShellStartupTests.cs` line 22: when all
three warm-up attempts fail (`CreateDefault`, `CreateDefault2`, `Open`, `Invoke`), the fixture swallows the
exception and leaves only `Started == false`, so the suite reports "not registered, or its start failed"
without which shape failed or why. Keep the fixture non-throwing; store the last exception and the failing
shape in static state and include them in the pin test's message. Mutation at the call site: the stored
exception dropped.
