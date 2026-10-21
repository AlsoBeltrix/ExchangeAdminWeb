# aud-a6c: the EXO pool's teardown does not reliably retire the connections it knows about

**Severity**: MEDIUM - connections survive the pool that owned them, and a failed disconnect
is neither reported nor retried. Pre-existing; not introduced by the A6B work.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a6325a13692998ce6`
**Commit**: `0152847` (merged)

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing `154ed2f`, as finding 3, and
explicitly checked against the parent commit: these lifecycle methods are unchanged by that
work. Filed separately so the A6B record does not absorb a defect it did not cause.

Three distinct gaps in `Services/ExoConnectionPool.cs`:

1. **`Dispose` drains only what is idle at that instant.** It stops the cleanup timer and
   empties `_available`, but does not retire the generation and does not stop further borrows
   or returns. A runspace that was borrowed, or still connecting, can be returned afterwards,
   pass the generation check and be added to a pool whose cleanup timer no longer runs - so it
   is never reaped.
2. **`CleanupIdle` can lose a race with `Dispose`.** It takes entries out of `_available` and
   puts the live ones back; a callback already running when `Dispose` drains will put its
   entries back afterwards, into the same stopped pool.
3. **A failed disconnect is silent.** `DestroyRunspace` swallows exceptions and ignores the
   error stream before disposing, so a `Disconnect-ExchangeOnline` that fails looks identical
   to one that succeeded. With A6B this now matters more, not less: the id is the only handle
   on that session, and it is discarded along with the runspace.

## Predicted observable failure

The app recycles its app pool. Connections borrowed at that moment return into a disposed
pool, are never reaped, and their contexts stay in the module's static map for the remaining
life of the process. Separately, a disconnect that fails during a tenant blip leaves a session
the app believes it closed, and nothing anywhere records otherwise.

## Approach - NOT BUILT

- Give `Dispose` a flag that makes `Return` destroy rather than pool, and have it retire the
  generation so anything still in flight comes back stale by construction. That reuses the
  mechanism `aud-a6` already built rather than inventing a second one.
- Make `CleanupIdle` check that flag before putting anything back.
- Have `DestroyRunspace` inspect the error stream and log a failed disconnect with the
  `ConnectionId` it could not close. Do not retry in place - the runspace is going away - but
  make the leak visible.

Worth doing together with `aud-a6b`'s remaining gap (an untracked connection has no later
recovery path), because both are about connections the pool can no longer name.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). The pool is shared infrastructure:
base-app change, no module version moves. All in `Services/ExoConnectionPool.cs`.

1. **One stop transition, one admission gate.** A `_poolLock` guards a `_stopped` flag and
   every ADD to `_available`. `Dispose` sets `_stopped` under the lock (idempotent), disposes
   the timer, then drains and destroys. Every add goes through `TryPool(runspace)`: under the
   lock, refuse when stopped, else add. `Return` and the idle sweep's put-back are the only
   adds; both call `TryPool` and destroy on refusal. Why it holds: a successful add precedes the
   stop in lock order and `Dispose` drains after the stop, so the drain sees every add; anything
   offered later is refused and destroyed by whoever offered it. Takes stay lock-free, so
   borrowing is unchanged.
2. **Not the generation retire the Approach suggested.** It cannot hold by itself: a borrow
   that read the generation AFTER a retire-on-dispose stamps its connection with the new value
   and passes `Return`'s generation test, landing in the stopped pool. The flag, checked at the
   add under the lock, is what holds; a second mechanism beside it would be decoration.
   `Return`'s existing generation check is unchanged.
3. **A stopped pool refuses borrows** (`ObjectDisposedException`) so shutdown opens no new
   sessions. Best effort by design - a borrow already past the check comes back through
   `Return` and is destroyed there, which is the load-bearing half.
4. **The sweep owns what it takes.** Partition the snapshot into idle-expired and keep (idle
   criterion unchanged), log the retiring count, put the keepers back through `TryPool`
   FIRST - so they are out of the pool for microseconds, not for a disconnect round trip - and
   destroy the refused keepers with the expired ones. `CleanupIdle` becomes `internal` as the
   test seam.
5. **A failed disconnect is recorded, never swallowed.** `DestroyRunspace` clears the streams
   before the disconnect (an error left by the borrower's own failed cmdlet must not be misread
   as the disconnect's), then treats a throw OR an error record as failure: increment
   `_failedDisconnects`, log a Warning naming the `ConnectionId` with the exception, write
   trace step `ExoConnectionDisconnectFailed` (Failed). No retry in place - the runspace is going
   away. It still disposes PowerShell and runspace in `finally` and never throws, so a loop over
   several connections continues past a failure (Known Failure Class 1), and each loop reports
   how many failed rather than a blanket count (Class 2).
6. **Unchanged:** `IsConnectionError`, `IsRetriablePrecheckError`, `RunWithRetryAsync`,
   `RunWithRetryCoreAsync` (psl-4 is changing the last one concurrently), and the A6B rule that a
   connection with no known id is never sent the unselected disconnect.
7. **Tests**, as a partial of `ExoConnectionPoolSharedConfigTests` over real open runspaces
   carrying a stub `Disconnect-ExchangeOnline`: dispose with three connections disconnects all
   three when one throws or writes an error; the failure is logged with its id and counted; the
   sweep disconnects what it retires and keeps the rest; a sweep holding a keeper while `Dispose`
   runs does not put it back; a return after stop disconnects instead of pooling; a borrow after
   stop is refused without connecting.

**Out of scope, deliberately:** `aud-a6b`'s untracked-connection gap. A connection whose id was
never attributed still cannot be disconnected without the selector that hits every operator,
and the alternative (cleanup through the runspace's own module) cannot be verified here without
the real module and a tenant. The warning that names the leak stays.

## Guard proof

Built as the Plan. Tests in `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs` (a partial of
`ExoConnectionPoolSharedConfigTests`, which became `partial` for it), over real open runspaces whose
stub `Disconnect-ExchangeOnline` records the id it was asked to close and can throw or write an
error record. Behaviour throughout; no source scans.

| Test | Pins |
| --- | --- |
| `DisposeDisconnectsEveryPooledConnection_EvenWhenOneDisconnectFails` (throw, error) | three pooled; the middle disconnect fails; all three are sent, none left pooled, one failure counted |
| `AFailedDisconnectIsLoggedWithTheConnectionItCouldNotClose` (throw, error) | one Warning naming `conn-bad`, exception attached; counter 1; trace step `ExoConnectionDisconnectFailed` / Failed carrying the id |
| `AnErrorLeftByTheBorrowersOwnCmdlet_IsNotMistakenForAFailedDisconnect` | a borrower's own error record in the stream at Discard is not counted or warned |
| `TheIdleSweepDisconnectsWhatItRetires_AndKeepsTheRest` | the idle one is disconnected; the fresh one stays and is the next borrow |
| `ASweepHoldingAConnectionWhenThePoolStops_DisconnectsItInsteadOfPuttingItBack` | the interleaving itself: sweep held on its retiring log call (asserted to hold both runspaces), `Dispose` runs, sweep resumes; nothing pooled, both disconnected; the hold is asserted, not assumed |
| `AConnectionReturnedAfterThePoolStops_IsDisconnectedNotPooled` | the predicted failure: borrowed through `Dispose`, returned after it; disconnected, not pooled |
| `ABorrowAfterThePoolStops_IsRefusedWithoutConnecting` | `ObjectDisposedException`, connect seam never called |

Mutations at the call site, applied to `Services/ExoConnectionPool.cs` at its pre-comment state
(md5 `4386BF8A83DDDBF5F2863ABE7B88D32B`) by a scripted driver: apply, md5, rebuild, run the named
tests, restore, touch, md5 re-checked equal to the original every time, then a final rebuild.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 `Return` adds to `_available` directly instead of `TryPool` | `BDCC9DF502731B1B9B64C40B8AA3F094` | `AConnectionReturnedAfterThePoolStops_IsDisconnectedNotPooled` |
| M2 sweep put-back adds directly instead of `TryPool` | `611B9F71FBBED8EC95EFD1F670A55D82` | `ASweepHoldingAConnectionWhenThePoolStops_...` |
| M3 `DestroyRunspace` rethrows the disconnect's exception | `C68E7A072BE12B4C50B1DF59A3AA99D0` | `DisposeDisconnectsEveryPooledConnection_...(throw)` |
| M4 `RecordFailedDisconnect` call removed | `15066A071E6D45198E98433B650D8B08` | both `AFailedDisconnectIsLogged...` cases; both `DisposeDisconnects...` cases |
| M5 error-stream check after the disconnect removed | `333F07BB31B9320125F2C43548F1F71C` | the two `(error)` cases |
| M6 `Streams.ClearStreams()` before the disconnect removed | `1D61C2DA48D7C3424F88F54E897682B0` | `AnErrorLeftByTheBorrowersOwnCmdlet_...` |
| M7 borrow refusal removed | `68FD4F579E6941EC550D3CB12977C447` | `ABorrowAfterThePoolStops_IsRefusedWithoutConnecting` |
| M8 `Dispose` never sets `_stopped` | `45A5EF3F7055BE5CE5B8CC7CB8FD54B1` | return-after-stop, sweep-holding, borrow-after-stop |
| M9 sweep skips destroying what it retires | `51309AB4F175C1341061031E6CDE046E` | `TheIdleSweepDisconnects...`, `ASweepHolding...` |

Comments were written after the round.

Gate at the committed state: Release build 0 errors / 61 warnings (baseline 61); `dotnet test
ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` 4499 passed / 3 known AD skips /
0 failed (baseline 4490, +9 for these tests), no hang; `dotnet format --verify-no-changes` clean;
`git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` clean after staging.

## Files changed

- `Services/ExoConnectionPool.cs`
- `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs` (new)
- `ExchangeAdminWeb.Tests/ExoConnectionPoolSharedConfigTests.cs` (`partial` only)

## Known gaps

- `aud-a6b`'s untracked-connection gap, as stated in the Plan.
- The lock argument in `TryPool` is reasoned, not stress-tested: the tests drive the one sweep
  interleaving the log call reaches and the sequential return-after-stop, not every ordering of
  `Return` against `Dispose` on two threads.
- Nothing here has run against a real tenant; "a failed disconnect leaves a session open" is still
  read from the module, and the stub stands in for both failure shapes.
- Base-app change: the base version bump is the supervisor's (`ExchangeAdminWeb.csproj` is out of
  this branch's hands). No module version moves.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 3 of the `154ed2f` review,
labelled pre-existing by the reviewer after comparing the parent.

## Closeout

Fixed on `worktree-agent-a6325a13692998ce6`, awaiting merge and the base version bump. Not
pushed. Independent review pending.
