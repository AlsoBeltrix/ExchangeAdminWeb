# aud-a6f: A borrow that passes the post-slot stop check can still connect after Dispose

**Severity**: MEDIUM - not an authorization defect, but a shutdown can still open a fresh Exchange
Online session from a pool that has stopped and drained; if the caller is abandoned during the
shutdown the session is stranded. Narrower window than aud-a6e, same harm.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (storage-batch-1, `prompt-storage-batch-1.md`, 2026-10-22)

## Evidence

At `720bb2a7`, `Services/ExoConnectionPool.cs`, `BorrowAsync` after the slot wait:

```
226            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped), this);
...
272            return await Task.Run(() => _connect(current, currentGen, ct), ct);
```

and `Dispose`:

```
888        public void Dispose()
889        {
890            lock (_poolLock)
891            {
892                if (_stopped) return;
893                _stopped = true;
894            }
...
897            var (destroyed, failed) = DestroyAllPooled();
```

A borrow that acquires a slot and passes the line-226 check, then loses the race to `Dispose`
(which sets `_stopped`, drains `_available` and returns), continues through the rest of the try
block, finds nothing pooled, and reaches `_connect`. The connect completes after the stop and
the fresh session is handed to the caller. `Return` refuses to pool it (`TryPool` honours
`_stopped`), so a caller that returns it destroys it; a caller abandoned mid-shutdown does not.

## Approach (to be written by the implementing agent before code)

- Make entry into `_connect` part of the stop protocol: take the stop decision and the
  "connect in flight" registration under `_poolLock`, so `Dispose` either sees the in-flight
  connect and waits for or destroys its result, or the borrow sees the stop and refuses. The
  minimum that holds: re-check `_stopped` immediately before scheduling `_connect`, and after the
  connect completes check again; if stopped, disconnect and destroy the new runspace and throw
  `ObjectDisposedException` instead of handing it out. The slot is released on both refusals
  (the existing catch does that).
- Keep aud-a6c/a6d/a6e behaviour: the flag is the mechanism; no generation-bump substitute.
- Tests through the existing `ExoConnectionPoolTeardownTests` harness and the aud-a6d hold
  context: stop landing between the post-slot check and the connect refuses and connects nothing;
  stop landing while the connect runs destroys the result and throws, nothing handed out; the
  slot is released in both cases (a follow-on borrow after a restart-free pool is refused, not
  starved). Mutations at the call site: drop the pre-connect re-check; drop the post-connect
  check; skip the destroy on the post-connect path.

## Guard proof

Pending.
