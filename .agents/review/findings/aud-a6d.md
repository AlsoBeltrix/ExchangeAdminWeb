# aud-a6d: a cancelled borrow can strand the stale connection it took out of the pool

**Severity**: LOW - latent. No product caller passes a cancellation token to `BorrowAsync` today
(`RunWithRetryAsync` and `RecipientAutocomplete` both call it without one), so the shipped app
cannot reach it; but the method is public, takes a token, and is one caller away from live.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a6325a13692998ce6`
**Commit**: `<pending>`
**Reviewer**: implementer (found while fixing `aud-a6c`; same class)

## Evidence

`Services/ExoConnectionPool.cs`, `BorrowAsync`. After the slot wait, the borrow takes a pooled
runspace with `_available.TryTake`; one that is stale or idle-expired is destroyed with

```csharp
var stale = pooled;
await Task.Run(() => DestroyRunspace(stale), ct);
```

`Task.Run(action, token)` with a token that is already cancelled returns a cancelled task and
never runs the action. The runspace has left the pool by then, so it is never destroyed, never
returned and never disconnected: the await throws `OperationCanceledException` into the catch,
which releases the slot and rethrows. That is `aud-a6c`'s class exactly - a connection removed
from the pool must be disconnected - on the borrow path instead of teardown.

The window is real once a token is supplied: the borrow's continuation after the slot wait is
queued, not inline, so a cancellation that lands between the slot being granted and that line
running reaches `Task.Run` already set.

## Predicted observable failure

A caller that cancels a borrow while it is queued for a slot - a closed browser tab, a timed-out
request - takes an idle connection out of the pool and drops it. Its session stays open and its
context stays in the module's process-wide map, with nothing left that can name it.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Base-app change; no module version.

1. Destroy without the token: `await Task.Run(() => DestroyRunspace(stale));`. The destroy is
   cleanup of something this borrow already owns; cancellation belongs to the borrow, which still
   honours it at the connect's `Task.Run(..., ct)` on the next line and throws there.
2. Test the window itself, not the source text: a `SynchronizationContext` that holds every
   continuation until the test pumps. Hold all five slots, start a sixth borrow under that context,
   return one runspace (it is pooled, and the sixth's continuation is posted but not run), age it
   past the idle timeout, cancel, pump. The sixth must still throw `OperationCanceledException`,
   the aged runspace must have been disconnected, and nothing may be connected.

## Guard proof

Built as the Plan. Test in `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs`:
`ABorrowCancelledAfterItsSlotWait_StillDisconnectsTheExpiredConnectionItTook`, using a
`HeldContinuations` context (the Comms-10k `C7YieldContext` shape) to hold the sixth borrow's
continuation between its slot being granted and its next line. The stub connect would fail the
test if anything were connected after the fifth.

Run against the unfixed code first: it failed exactly as predicted - the borrow threw as
cancelled, and the disconnect log was empty (`Expected: ["conn-1"]`, `Actual: []`).

| Mutation (call site, `Services/ExoConnectionPool.cs`) | md5 | Failed |
| --- | --- | --- |
| pre-comment fixed state | `CCCCB4A933EDEC8C6E7B6CAD433755C9` | - |
| M1 the stale destroy takes the borrow's token again | `9E9325BA0CBB07DFC33CF1FAF4B8FF5F` | `ABorrowCancelledAfterItsSlotWait_StillDisconnectsTheExpiredConnectionItTook` |

M1's md5 equals `git show 01528479:Services/ExoConnectionPool.cs`: the mutation is the exact
pre-fix file. Restored (md5 matched the pre-comment state), touched, rebuilt; comment written after.

Gate at the committed state: Release build 0 errors / 61 warnings (unchanged); `dotnet test
ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` 4508 passed / 3 known AD skips /
0 failed (4507 before, +1), no hang; `dotnet format --verify-no-changes` clean;
`git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` clean after staging.

## Files changed

- `Services/ExoConnectionPool.cs` (one token removed, comment)
- `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs` (the test and its context)

## Known gaps

- `BorrowAsync` can still return a runspace taken from the pool without re-checking the caller's
  token (the `flake-1` reviewer's path, recorded in `aud-a6b`). That returns a working connection
  to a caller that has stopped wanting it - it is not stranded, the caller still returns it - so it
  is not this class and is left alone.
- Latent as stated: no product caller supplies a token today.

## Reviewer comments

Raised by the implementer while fixing `aud-a6c`. No independent review yet.

## Closeout

Fixed on `worktree-agent-a6325a13692998ce6`, awaiting merge and the base version bump. Not
pushed. Independent review pending.
