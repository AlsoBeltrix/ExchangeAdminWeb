# aud-a6: a borrowed EXO connection can target the previous organization

**Severity**: HIGH - an administrative command runs against the wrong Exchange Online tenant.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 6 of the 2026-10-16 repository audit, verified against the code before any change.

The pool retires runspaces by moving a generation counter: a borrow reads
`_configGeneration`, and a pooled runspace whose stamped generation differs is destroyed
instead of handed out. `DrainIfConnectionConfigChanged` did the two halves of that transition
as two separate steps:

```csharp
lock (_connectedLock)
{
    if (_connectedUnder is null || _connectedUnder.Value == current)
    {
        _connectedUnder = current;
        return false;          // "nothing changed - carry on"
    }
    previous = _connectedUnder.Value;
    _connectedUnder = current; // the new identity is published HERE
}
// ... lock released, two log/trace calls ...
DrainPoolCore();               // the generation moves HERE
```

and the borrow path reads them in that same order (`Services/ExoConnectionPool.cs:177-179`):

```csharp
DrainIfConnectionConfigChanged(current);
var currentGen = Interlocked.Read(ref _configGeneration);
```

So for a second borrower arriving between the lock release and the increment:

1. `_connectedUnder` already equals the new config, so the check returns **false** - this
   borrower is told nothing changed and does not drain.
2. It then reads the **old** generation, because the increment has not happened yet.
3. `_available` still holds the previous organization's runspaces, because the destruction has
   not happened either.
4. Generation matches, idle timeout passes, and the runspace is handed out.

Every borrower in that window gets a connection to the **previous** organization while
believing it holds the current one.

The window is not theoretical: between the two halves sit an `ILogger.LogInformation` call and
an `OperationTraceService.Step` call, the second of which writes to the JSONL log on disk.

**`InProcessDrainPool` was already correct** and is the reason this was not obvious. It calls
`DrainPoolCore()` (generation first) and only then publishes `_connectedUnder`. A borrower in
*that* window reads the stale identity, concludes a drain is needed and drains again -
wasteful, not wrong. The shared-config path had the two in the opposite, unsafe order.

## Predicted observable failure

The Exchange Online connection is repointed at a different tenant on the other instance. Two
operators act at the same moment. One triggers the drain; the other, landing in the window,
runs their command - a mailbox permission grant, a room change, an auto-reply - against the
organization that was just configured away from. Nothing reports an error: the command
succeeds, in the wrong tenant, and the audit row names the new organization.

## Approach

Make publishing the identity and retiring the generation one transition, under the one lock:

```csharp
previous = _connectedUnder.Value;
_connectedUnder = current;
RetireGeneration();            // inside _connectedLock
```

Destroying the runspaces stays **outside** the lock. That half is slow - it runs
`Disconnect-ExchangeOnline` per runspace - and it does not need to be atomic, because once the
generation has moved, every pooled runspace already fails the borrow test and will be destroyed
by whoever picks it up. Holding the connection lock across that work would serialise borrows
behind a network round trip for no safety gain.

`DrainPoolCore` is now the two named halves in sequence, so the in-process path is unchanged
and the two orderings are visible side by side rather than implied.

## Files changed

- `Services/ExoConnectionPool.cs`
- `ExchangeAdminWeb.Tests/ExoConnectionPoolSharedConfigTests.cs`

## Guard proof

`ABorrowArrivingDuringTheDrain_NeverGetsTheOldOrganizationsRunspace` drives the exact
interleaving rather than asserting on source text. The pool logs from inside the old window, so
the test injects a `CallbackLogger` that parks the draining borrow on that log call, lets a
second borrow run to completion against a pool whose runspaces are all from the previous
organization, and only then releases it.

It asserts the second borrower did **not** receive the pooled runspace, that the runspace it
did receive carries the post-drain generation, and that a fresh connect was made under the new
organization.

| Mutation | Result |
| --- | --- |
| restore the original ordering exactly - drop `RetireGeneration()` from the lock and call `DrainPoolCore()` after it | FAIL, 1 of 20: `Assert.NotSame() Failure: Values are the same instance` |

That failure is the defect itself, reproduced. "The same instance" is literal: the second
borrower was handed the very runspace object that had connected to contoso. Exactly one test
failed, so the guard is specific to this defect rather than to the reordering.

File restored byte-identical (md5 `e115a181981834422d057ca718c8c488`, `md5sum -c` OK), touched
so MSBuild could not skip the rebuild, and rebuilt before re-running.

The nineteen existing shared-config and retry tests pass unchanged, which is the check that the
reordering did not alter single-threaded behaviour.

Gate at the committed state: Release build 0 errors / 42 warnings; `dotnet test` on the
solution 4281 passed / 3 known AD skips / 0 failed (4280 before this slice, +1 for the new
test); `dotnet format --verify-no-changes` clean; `git diff --check` clean.

## Coder dispute

None.

## Known gaps

The test proves the one interleaving the log call can reach. It is not a general concurrency
proof of the pool - there is no stress or interleaving-exhaustive test here, and other orderings
between `BorrowAsync`, `Return` and `DrainPool` are unexercised.

Nothing here has run against a real tenant, so "the command would have executed against the
previous organization" remains an argument from the connect parameters, not an observation.

## Reviewer comments

Source: the 2026-10-16 audit, finding 6. Verified by the implementer before changing anything.
Independent review of the fix pending.

## Closeout

Fixed directly on `master`. Not pushed.

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - CHANGES REQUIRED

The reviewer agreed the original window is closed - "the second cannot pass the identity
comparison before the first retires the generation, because both happen under
`_connectedLock`" - and then showed the fix was **incomplete**, because the borrow path still
read the config and the generation as two separate operations.

Their schedule, verified against the code before accepting it:

| Step | State |
| --- | --- |
| Borrower A reads organization A, completes the config comparison, pauses before reading the generation | marker A, generation 0 |
| Config changes to B. Borrower B publishes B, retires to generation 1, drains, borrows a B connection | marker B, generation 1 |
| A resumes, reads generation **1**, and connects using **A** | A's runspace stamped 1 |
| A returns it. `Return` compares 1 to 1 and pools it | a contoso runspace in a fabrikam pool |
| Any later B borrower matches marker B and generation 1 | **it is handed A's connection** |

That is persistent contamination of the pool, not one operation finishing late, and it was
not newly introduced by the first fix - it survived it.

**Repair.** `DrainIfConnectionConfigChanged` now returns `(drained, generation)`, read under the
same lock that compares the config, and `BorrowAsync` uses that value instead of reading the
counter again. The invariant this buys: a runspace stamped generation G was connected under the
config that was current at generation G, so a borrower holding the pair (config C, generation G)
can only ever accept runspaces built for C.

`DrainPool`, the in-process path, had the mirror-image gap - it retired first and published
afterwards, so a borrow holding an older snapshot could pick up the new generation and stamp a
previous-organization connection with it. Both halves are now inside the one lock there too, and
`DrainPoolCore` is gone: each caller states its own order explicitly.

New guards:

| Test | What it pins |
| --- | --- |
| `AConnectionMadeForOneOrganization_IsNeverPooledForAnother` | the end-to-end outcome: a switch during connect leaves the contoso runspace unpoolable and the fabrikam borrower unaffected |
| `BorrowAsync_TakesTheGenerationFromTheConfigComparison_AndNeverRereadsIt` | the pairing itself |
| `DrainIfConnectionConfigChanged_FirstCallRecordsWithoutDraining` (extended) | the returned generation, not just the drained flag |

**The second of those is a source scan, and that is a real limit, not a shortcut.** The defect
needs a borrow paused between comparing the config and reading the generation; once those are a
single call there is no seam in that gap to pause at, so nothing executable can tell "uses the
generation it was handed" from "re-reads it".

Mutation: restoring the separate `Interlocked.Read` in `BorrowAsync` fails that guard and
nothing else - which is exactly the honest result, and the reason the limit is stated rather
than implied. Source restored byte-identical (md5 `e3e7212acb24349e748cdb2296a5a2cc`), touched,
rebuilt.

The reviewer also found the first test's blocking wait could expire silently after 30 seconds,
letting the buggy ordering escape the intended schedule and pass. The wait's result is now
asserted.

Gate: Release 0 errors; 4301 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. Base app version 2.27.8 -> 2.27.9.

## Still open after this repair

**`Disconnect-ExchangeOnline` is not per-runspace.** The reviewer inspected the installed
ExchangeOnlineManagement 3.9.2 and 3.10.1 modules - metadata and IL, not documentation - and
reports that the default disconnect path calls `ConnectionContextFactory.RemoveAllConnectionContexts`
against a **static** `connectionContextMap`. If that is right, `DestroyRunspace` tearing down one
retired connection also tears down every other borrower's live connection, and the pool's whole
premise of N concurrent EXO connections in one process is unsound.

This predates every A6 change and is well outside the audit plan's scope. Recorded as `aud-a6b`
for the owner, not fixed here. It is unverified by me and unverified against a live tenant.
