# aud-a6e: a borrow queued for a slot when the pool stops still connects

**Severity**: MEDIUM - the stopped pool still admitted post-stop work: shutdown could open a fresh
Exchange Online session from a pool that had already drained and stopped reaping.
**Status**: Fixed on master `fc95609e` (patch applied by the supervisor); closed on guard proof (MEDIUM); codex storage-batch-1 clean, raised aud-a6f (narrower window)
**Branch**: `worktree-agent-aec54e09518f47fc1` - forked at `ca6575a2`, before the `aud-a6c` code it
fixes, so built and gated on a `git archive` export of master `1c6038d5` and delivered as a patch
for the supervisor to apply
**Commit**: `fc95609e`
**Reviewer**: codex / gpt-5.5-dzs / xhigh, services-batch-2, 2026-10-21

## Evidence

Raised by `codex / gpt-5.5-dzs / xhigh` reviewing the services batch at `98ac93b`, finding 1.
Verdict text: `.agents/review/services-batch-2.result.txt` in the main checkout (machine-local).
A follow-on of `aud-a6c`, whose Plan made the borrow refusal best effort and left `Return`'s
refusal as the load-bearing half.

`Services/ExoConnectionPool.cs`, `BorrowAsync`, at `1c6038d5` (unchanged since `98ac93b`):

```csharp
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped), this);   // line 196
        ...
        if (!await _slots.WaitAsync(waitTimeout, ct))                          // line 210
        ...
        _operationTrace.Step("ExoPoolSlotAcquired", backend: "ExchangeOnline");

        try
        {
            ...
            var (_, currentGen) = DrainIfConnectionConfigChanged(current);
            if (_available.TryTake(out var pooled))
            ...
            return await Task.Run(() => _connect(current, currentGen, ct), ct); // line 264
```

The stop is checked once, before the slot wait, and never after it. With all five slots held, a
sixth borrow passes line 196 and waits at line 210. `Dispose` sets `_stopped`, drains `_available`
and returns. A held runspace that comes back through `Return` is refused by `TryPool`, destroyed,
and its slot released - and the released slot goes to the queued borrow. It resumes after the
await, finds `_available` empty (the drain emptied it and `TryPool` refuses every add since), and
reaches `_connect` at line 264.

The `aud-a6c` comment on line 196 says a borrow already past it "comes back through Return". A
queued borrow's wait can outlast the stop by up to its whole wait timeout (two minutes by
default), so at shutdown under load this is the systematic case, not a narrow race; and the
session it opens is closed only if its caller later returns or discards it.

## Predicted observable failure

The app pool recycles while the pool is saturated - five operations in flight, more queued. Each
finishing operation's `Return` releases a slot to a queued borrow, which opens a new Exchange
Online session from the stopped pool. If that request is abandoned during shutdown, the session
and its context in the module's process-wide map are stranded.

Reproduced before the fix: the test below, run against the unfixed pool, failed in both orderings
with `Expected: 5`, `Actual: 6` - the sixth borrow connected.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Shared infrastructure: base-app change,
no module version moves. All in `Services/ExoConnectionPool.cs`.

1. Re-check `_stopped` as the first statement of the `try` that follows the slot wait: after the
   slot is held, before `DrainIfConnectionConfigChanged`, `_available` or `_connect`. A stopped
   pool throws `ObjectDisposedException`, as the entry check does.
2. Inside the `try` deliberately: its `catch` already traces the failed borrow, releases the slot
   exactly once and rethrows. A second release path would be a second place to get the count wrong.
3. Not built: closing the window between the re-check and the connect. A borrow past both checks
   is the case `aud-a6c` routes through `Return`'s refusal; holding `_poolLock` across a
   multi-second connect, or checking again after it, is not what this finding needs.
4. Test with the `aud-a6d` `HeldContinuations` idiom, in two orderings: the pool stops while the
   sixth borrow is still queued; and the slot is granted to it before the stop, its continuation
   held until after it. In both, no sixth connect and an `ObjectDisposedException`. A seventh queued
   borrow proves the slot is given back: nothing frees one after the single `Return`, so it can only
   finish - refused, unconnected - if the refused sixth released its slot.
5. Comments after the mutation round: the new check, plus the entry check's comment and
   `Dispose`'s remarks, so neither still says only `Return` stands behind the entry check.

## Guard proof

Built as the Plan. Test in `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs`.

| Test | Pins |
| --- | --- |
| `ABorrowQueuedWhenThePoolStops_IsRefusedWithoutConnecting` (`stopped while queued`, `stopped after the slot was granted`) | five borrowed, a sixth and a seventh queued; `Dispose` and one `Return` in the case's order; the sixth throws `ObjectDisposedException` with the connect count still 5; the seventh is refused the same way, which it can only reach if the sixth gave its slot back; every connection is disconnected and nothing is pooled |

Mutations at the call site, `Services/ExoConnectionPool.cs` at its pre-comment fixed state (md5
`cedc3f508e1450f104dc6f06f586db30`). Each applied by exact-text replacement asserted to occur
once, md5 confirmed, the named test rebuilt and run, the file restored from a saved copy and
touched (an untouched restore can keep the old mtime and leave the mutated binary in place), and
the md5 re-confirmed equal to the pre-comment state.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the re-check removed | `4bbc72ff81908598c2dc8d1e032768da` | both cases: `Expected: 5`, `Actual: 6` - the sixth connected |
| M2: the re-check moved before the `try`, so its throw skips the slot release | `1d003133d010e73365b6c66e81a30195` | both cases, at the seventh's pump: the sixth was refused correctly, and the seventh never got a slot (`TimeoutException: The held borrow neither completed nor posted a continuation`) |

M1's md5 equals the file at `1c6038d5`: the mutation is the exact pre-fix file. Comments were
written after the round; md5s are of LF-normalised bytes, and the committed file's is
`1b8f06d6c52dcaeecac5acf543775b1e`.

## Gates

On the export of `1c6038d5` with this change applied:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings (61 before).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 4993 passed,
  3 skipped (the known AD skips), 0 failed (4991 before, +2 for the two cases); no hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check` and `tools/Test-AsciiOnly.ps1` need a repository, which the export is not.
  Their rules were applied to the patch and its files instead: no trailing whitespace, space
  before tab or blank line at end of file in what it adds, and no byte above 0x7F in the changed
  `.cs` files. Owed in the repository at apply.

## Files changed

- `Services/ExoConnectionPool.cs` - the re-check; the entry check's comment; `Dispose` remarks.
- `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs` - the test.

## Known gaps

- The window between the re-check and the connect stays open, as `aud-a6c` accepted: a borrow past
  both checks when the pool stops still connects, and that session is closed only when its caller
  returns or discards it. The window is now two adjacent statements rather than the whole wait.
- `Dispose` does not wake a queued borrow. It waits for a slot or its timeout as before, and is
  refused without connecting once it resumes.
- Nothing here has run against a real tenant.
- Base-app change: the base version bump is the supervisor's (`ExchangeAdminWeb.csproj` is out of
  this branch's hands). No module version moves.

## Reviewer comments

`Reviewer: codex / gpt-5.5-dzs / xhigh, services-batch-2, 2026-10-21`, finding 1 (MEDIUM). Raw
verdict `.agents/review/services-batch-2.result.txt` in the main checkout (machine-local). The
reviewer's suggested approach is the one built.

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent. Independent
review of the fix pending.

## Closeout

Fixed on branch and delivered as a patch; not merged, not pushed. Base version bump due at merge.
