# aud-a6c: the EXO pool's teardown does not reliably retire the connections it knows about

**Severity**: MEDIUM - connections survive the pool that owned them, and a failed disconnect
is neither reported nor retried. Pre-existing; not introduced by the A6B work.
**Status**: Open - not started
**Branch**: -
**Commit**: -

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

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 3 of the `154ed2f` review,
labelled pre-existing by the reviewer after comparing the parent.

## Closeout

Not started.
