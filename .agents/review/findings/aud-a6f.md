# aud-a6f: A borrow that passes the post-slot stop check can still connect after Dispose

**Severity**: MEDIUM - not an authorization defect, but a shutdown can still open a fresh Exchange
Online session from a pool that has stopped and drained; if the caller is abandoned during the
shutdown the session is stranded. Narrower window than aud-a6e, same harm.
**Status**: Fixed on master `436a44a1` (patch applied by the supervisor); closed on guard proof; codex review pending; codex misc-batch-1 clean (2026-10-23)
**Branch**: `worktree-agent-aec54e09518f47fc1` - built and gated on a `git archive` export of master
`96d9dc4f` with the `aud-a7d` patch applied, and delivered as a patch for the supervisor to apply
**Commit**: `436a44a1`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Shared infrastructure: base-app change,
no module version moves. All in `Services/ExoConnectionPool.cs`, `BorrowAsync`; the a6c/a6d/a6e
flag mechanism is kept and nothing replaces it. Written before the code.

1. **Re-check immediately before scheduling the connect.** `ObjectDisposedException.ThrowIf` on
   `_stopped` as the statement before `Task.Run(() => _connect(...))`. It catches a stop that lands
   anywhere after the line-226 check - most widely at the only await in between, the destroy of a
   stale or idle-expired runspace the borrow took from the pool, which runs
   `Disconnect-ExchangeOnline` and can take seconds.
2. **Check again after the connect completes.** If the pool stopped while `Connect-ExchangeOnline`
   ran, destroy the new runspace (through `Task.Run`, without the token - cleanup this borrow owes,
   the aud-a6d rule), trace `ExoConnectionDiscardedPoolStopped` as `Return` does, and throw
   `ObjectDisposedException` instead of handing it out.
3. **Both refusals are inside the existing `try`,** so its `catch` traces the failed borrow,
   releases the slot exactly once and rethrows - the aud-a6e shape; no second release path.
4. **Not built: registering connects in flight under `_poolLock`** so that `Dispose` waits for or
   destroys them. The two checks bound the window to the statements between the post-connect read
   and the `return` - the same instant any borrow completing beside `Dispose` occupies, which
   `Return`'s refusal already covers. Making `Dispose` wait on a multi-second connect would add a
   shutdown stall for no further guarantee about callers abandoned mid-shutdown.
5. **Tests** in `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs`, with the aud-a6d
   `HeldContinuations` context (gaining a `PumpOne` that runs exactly one held continuation):
   - the stop lands between the post-slot check and the connect - the sixth borrow is pumped once,
     past the check and into the expired runspace's destroy, then the pool stops: it refuses and
     connects nothing;
   - the stop lands while the connect runs - the fifth borrow's connect is held inside the seam
     until the pool has stopped: the new connection is disconnected, nothing is handed out, the
     borrow throws;
   - in both, a borrow queued behind it before the stop is refused rather than starved, which it
     can only be if the refused borrow gave its slot back.
   Mutations at the call site, as the Approach lists: drop the pre-connect re-check; drop the
   post-connect check; skip the destroy on the post-connect path.

## Guard proof

Built as the Plan. Run against the unfixed code first: both tests failed as predicted - the sixth
borrow connected after the stop (`Expected: 5`, `Actual: 6`), and the fifth handed out the session
it opened while the pool stopped (`Assert.IsType() Failure: Value is null`).

| Test (`ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs`) | Pins |
| --- | --- |
| `AStopBetweenThePostSlotCheckAndTheConnect_RefusesWithoutConnecting` | five borrowed; the sixth is granted a slot and pumped once, past the post-slot check and into the destroy of the idle-expired runspace it took; the pool stops while it is held there; it throws `ObjectDisposedException` with the connect count still 5 and only that runspace disconnected; the seventh, queued before the stop, is refused, not starved |
| `AStopWhileTheConnectRuns_DisconnectsTheNewConnectionInsteadOfHandingItOut` | four borrowed; the fifth's connect is held inside the seam while the pool stops; the fifth throws `ObjectDisposedException` and the session it opened (`conn-5`) is the one disconnected; nothing is pooled; the sixth, queued before the stop, is refused, not starved |

`HeldContinuations` gained `PumpOne`; `PumpUntil` is unchanged, so the `aud-a6d` and `aud-a6e`
tests run as before. Both new tests passed five consecutive runs with a full suite running beside
them.

Mutations at the call site, `Services/ExoConnectionPool.cs` at its pre-comment fixed state (md5
`4bf5ff080172956d393a0e72f3204f46`). Each applied by exact-text replacement asserted to occur
once, md5 confirmed, the `ExoConnectionPool` tests rebuilt and run, the file restored from a saved
copy and touched, and the md5 re-confirmed equal to the pre-comment state.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| F1: the pre-connect re-check dropped | `b5a93f7643010a18e81be28882180c41` | `AStopBetweenThePostSlotCheckAndTheConnect_...`: `Expected: 5`, `Actual: 6` - it connected; only the post-connect check kept the session from the caller, which is why the test counts connects rather than exceptions |
| F2: the post-connect check dropped | `dd684887b0e347c7612232db6e0c2ab5` | `AStopWhileTheConnectRuns_...`: the borrow returned the new session (`Value is null` where `ObjectDisposedException` was expected) |
| F3: the destroy skipped on the post-connect path | `26c91d25fba382139a553a3fc99fc573` | `AStopWhileTheConnectRuns_...`: `Expected: ["conn-5"]`, `Actual: []` - refused, but the session it opened was left open |

Each mutation fails only its own test. Comments were written after the round; md5s are of LF
bytes. Final: `Services/ExoConnectionPool.cs` `961683f4bcc31e44d593baaa02c8cfd2`, test file
`9d968703e34b43e42eff392baa55d701`.

## Gates

On the export of master `96d9dc4f` with `aud-a7d` and this change applied (the same export with
`aud-a7d` alone: Release 0 errors / 61 warnings; 5057 passed, 3 skipped, 0 failed):

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings (61 before).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5059 passed,
  3 skipped (the known AD skips), 0 failed (5057 before, +2 for these tests); no hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check` and `tools/Test-AsciiOnly.ps1` need a repository, which the export is not.
  Their rules were applied to the patch and its files instead: no trailing whitespace, space
  before tab or blank line at end of file in what it adds, and no byte above 0x7F in either
  changed `.cs` file. Owed in the repository at apply.

## Files changed

- `Services/ExoConnectionPool.cs` - the pre-connect re-check; the post-connect check, its destroy
  and refusal; comments (the two checks, the entry check, `Dispose` remarks).
- `ExchangeAdminWeb.Tests/ExoConnectionPoolTeardownTests.cs` - the two tests;
  `HeldContinuations.PumpOne`.

## Known gaps

- The irreducible instant stays, as Plan point 4 records: a borrow whose post-connect read just
  precedes the stop returns its session, exactly as a borrow that completed beside `Dispose`
  does; `Return` destroys it, and an abandoned caller strands it.
- The pooled path has the same instant: a borrow taking a pooled runspace in the statements
  between the post-slot check and the take, as `Dispose` drains, hands out that existing session.
  No new session is opened; `Return` handles it as above.
- `Dispose` does not wake a borrow waiting for a slot (as `aud-a6e` records); it is refused when
  it resumes.
- Nothing here has run against a real tenant.
- Base-app change: the base version bump is the supervisor's (`ExchangeAdminWeb.csproj` is out of
  this branch's hands). No module version moves.

## Reviewer comments

Raised by `codex / gpt-5.5-dzs / xhigh / standard`, storage-batch-1, 2026-10-22, reviewing
`aud-a6e`. The record's "minimum that holds" is what was built; the registration design it names
first is recorded as not built, with the reason (Plan point 4).

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent. Independent
review of the fix pending.

## Closeout

Fixed on branch and delivered as a patch; not merged, not pushed. Base version bump due at merge.
