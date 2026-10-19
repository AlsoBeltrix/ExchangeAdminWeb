# aud-a6b: Disconnect-ExchangeOnline may tear down every borrower's connection, not one

**Severity**: HIGH if confirmed - it would mean the connection pool's central premise is
unsound, and that cleaning up one retired connection can break unrelated operators
mid-operation, after earlier mutations have already committed.
**Status**: CONFIRMED and FIXED 2026-10-19 (option 2, owner-chosen); review and live acceptance pending
**Branch**: -
**Commit**: -

## Evidence

Raised by codex-commercial (gpt-6-astra, max) while reviewing `1dcc1a1`, unprompted - the
review question was about the A6 fix, and this came back as a larger problem underneath it.

The reviewer states they inspected the **installed** ExchangeOnlineManagement modules, versions
**3.9.2 and 3.10.1**, reading assembly metadata and IL rather than documentation, and found:

- `Disconnect-ExchangeOnline -Confirm:false` with no connection selector routes to
  `ConnectionContextFactory.RemoveAllConnectionContexts`
  (cited at `ExchangeOnlineManagement.psm1:945` in the 3.9.2 netCore module).
- `connectionContextMap` is **static**, and that method clears the tokens and then the map.
  It is not a per-runspace collection.

`Services/ExoConnectionPool.DestroyRunspace` calls exactly that form.

If the reading is right, the consequence is not subtle: a retired connection returning and
being destroyed removes the connection context of **every other borrower in the process**,
including ones mid-operation. Their next cmdlet fails, possibly after they have already
written changes.

**This is not something the A6 work caused, and moving destruction inside `_connectedLock`
would not help** - the damaged connections are ones already borrowed and in use, not ones
sitting in the pool.

## Why this is filed rather than fixed

1. **I have not verified it.** The claim rests on someone else's IL inspection of a module on
   this machine. The repo's own rule is that control-flow claims are settled by reading the
   file; I have not read it, and neither of us has reproduced the effect against a tenant.
2. **If true, the repair is a design change, not a fix.** Either every destroy names its
   connection (`Disconnect-ExchangeOnline -ConnectionId`, which Microsoft documents for REST
   connections) or the pool stops holding concurrent connections. Both are well outside
   `docs/RepoAudit-Remediation-Plan.md`.
3. It needs an owner decision about scope before any code moves.

## Proposed next step

Verify first, in this order, stopping at the first answer:

1. Read the installed module at the cited path and confirm or refute the call chain. Cheap,
   local, no tenant.
2. If confirmed, check whether `PooledRunspace` can carry a `ConnectionId` and whether
   `Disconnect-ExchangeOnline -ConnectionId` exists in the installed version.
3. Report to the owner with both answers before proposing a change.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, reviewing `1dcc1a1`. Their own
caveat, recorded: the finding is based on static implementation inspection, with no live
tenant reproduction.



## Verification, 2026-10-19 - CONFIRMED

Done locally against the installed module. No tenant, no AD, and no code from the module was
executed: the field check is a metadata-only read through `PEReader` /
`MetadataReader`, not `Assembly.LoadFrom`.

Installed: **ExchangeOnlineManagement 3.9.2** only (`C:\Program Files\WindowsPowerShell\Modules`).
3.10.1 is not on this machine, so that half of the reviewer's claim remains unchecked here.

**1. The default disconnect really does remove everything.**
`netCore/ExchangeOnlineManagement.psm1:945`, inside `Disconnect-ExchangeOnline`'s
`DefaultParameterSet` branch:

```powershell
[Microsoft.Exchange.Management.ExoPowershellSnapin.ConnectionContextFactory]::RemoveAllConnectionContexts()
```

`Services/ExoConnectionPool.DestroyRunspace` calls exactly that form -
`Disconnect-ExchangeOnline -Confirm:$false`, no selector.

**2. The map is static.** In
`netCore/Microsoft.Exchange.Management.ExoPowershellGalleryModule.dll`:

```
TYPE: Microsoft.Exchange.Management.ExoPowershellSnapin.ConnectionContextFactory
  FIELD connectionContextMap                Static=True
```

A static field lives in the assembly's load context, which is the **process**, not the
runspace. Every pooled runspace in this app shares one map.

**So the reviewer was right.** Destroying one retired runspace removes the connection context
of every other borrower in the process, including ones mid-operation. The pool's premise -
several concurrent EXO connections, independently disposable - does not hold as built.

**3. A targeted disconnect exists, but wiring it up is not trivial.**

`Disconnect-ExchangeOnline` has a `ConnectionId` parameter set (`psm1:810`) routing to
`GetModulesToRemoveByConnectionId` / `RemoveConnectionContextUsingConnectionId` instead of the
blanket removal. That is the shape a fix would use.

The obstacle: **`Connect-ExchangeOnline` generates the id itself and does not accept one**
(`psm1:413`, "Generate a ConnectionId to use in all logs and to send in all server calls"). So
the pool cannot assign an id per runspace; it has to read back the one that was generated.

`Get-ConnectionInformation` is exported by the module and returns active connections with
their `ConnectionId`. But **it reads the same process-wide static map**, so called inside a
runspace it returns every connection in the process, not that runspace's. Picking the right
row is the actual design problem - a before/after diff around the connect, or "newest", and
both are racy when two borrowers connect concurrently. The pool would likely have to
serialise connection establishment to attribute ids reliably.

## Recommendation to the owner

Confirmed, and the repair is a connection-pool design change, not a patch. Options, cheapest
first:

1. **Serialise EXO work.** One connection, a real lock around borrow. Removes the defect
   outright and is by far the smallest change. Costs the concurrency the pool exists for.
2. **Attribute ConnectionIds and disconnect by id.** Keeps concurrency. Needs connection
   establishment serialised anyway to attribute ids safely, plus a new field on
   `PooledRunspace` and a changed destroy path.
3. **Stop disconnecting entirely and just dispose the runspace.** Leaks the server-side
   session until it times out, and leaves the static map holding dead contexts. Not
   recommended, recorded because it is the "do nothing" option and it is not free.

Nothing is reproduced against a live tenant in any of this; it is a static reading of the
shipped module plus the code that calls it.

## Closeout

Verified, not fixed. Awaiting an owner decision on which option, per the plan's
`W1F-A6B` entry, which already said to stop here.

## Fixed, 2026-10-19 - option 2, on the owner's answer

The owner was asked how often several people use the Exchange features at once and answered
"regularly, it's a team" - the option whose text said multiple connections were worth keeping
and that it would be prioritised. So: keep concurrency, disconnect by id.

That answer also changes the read on severity. **If several operators really are in there at
once, they have been interrupting each other already**, and it would have looked like
intermittent Exchange flakiness rather than anything attributable.

**The change.** `PooledRunspace` carries the `ConnectionId` its session was given.
`DestroyRunspace` sends `Disconnect-ExchangeOnline -ConnectionId <id>` instead of the
unselected form that clears the static map.

Attributing the id is the awkward part, because `Connect-ExchangeOnline` generates it and does
not accept one, and `Get-ConnectionInformation` reads the same process-wide map. So the pool
reads the id list before and after the connect and takes the one that appeared - and
**connection establishment is serialised under `_connectLock`** to make that comparison
meaningful. Two concurrent connects would make it ambiguous.

Borrowing stays concurrent. Only establishing a NEW connection queues, and the pool exists to
reuse connections, so that is not the common path. **That is the cost of this option and it is
deliberate.**

**When the id cannot be established, the runspace is disposed WITHOUT disconnecting.** There is
no safe alternative selector - the only other form is the one that hits everybody. Leaking a
server-side session until it times out is recoverable; breaking every operator is not. Both the
unknown-id case and the attribution failure log a warning, because a leaked session is still
worth knowing about.

## Guard proof

Split by what can honestly be tested.

**Behaviour** - `NewConnectionId` is a pure function and is tested as one: the id that appeared
is returned; a first connection is identified; and **nothing is returned** for zero new ids,
two new ids, an unchanged list, or a case-only difference. That last set is the important half,
because a wrong answer there means disconnecting a connection someone else is using.

**Source scans, and labelled as such** - the disconnect call itself needs a real module, a real
tenant and a real second connection to observe the damage. Three scans instead: every
`Disconnect-ExchangeOnline` in the pool names a `ConnectionId` before its `Invoke`; the
unknown-id guard precedes the disconnect; and `Connect-ExchangeOnline` runs inside
`_connectLock`.

These read `RazorSyntax.CommentsBlanked`, not `ProgressScan.CodeView`. CodeView blanks string
literals, and the cmdlet and parameter names under test *are* literals - it would have blanked
the very thing being checked. Comments are still stripped, which matters because the comments
in this file name both cmdlets repeatedly and would satisfy every scan on their own.

| Mutation | Result |
| --- | --- |
| restore the unselected `Disconnect-ExchangeOnline` | FAIL - `TheDisconnectAlwaysNamesTheConnectionItIsClosing` |
| replace `lock (_connectLock)` with `if (true)` | FAIL - `ConnectionEstablishmentIsSerialised` |

Source restored byte-identical (md5 verified), touched, rebuilt.

## Known gaps - and they are large

- **Nothing here has run against a real tenant.** The central claim - that the targeted
  disconnect closes one session and leaves the others alive - is read from Microsoft's module,
  not observed. This is the finding most in need of a real-world check before it is trusted.
- The before/after id diff assumes `Get-ConnectionInformation` lists connections made by other
  runspaces in this process. That follows from the map being static, which was verified, but
  the cmdlet's own behaviour was not.
- Only 3.9.2 is installed here. A different version could route differently.
- `ExoConnectionPool.cs:163` still waits on its slot with the caller's token and can return a
  cached runspace without re-checking cancellation (the `flake-1` reviewer's fourth path). Left
  alone: it is latent, with no product caller supplying a token, and it is not this finding.

Gate: Release 0 errors; suite 4328 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. Base 2.27.11 -> 2.28.0 - a minor bump rather than a patch, because
the pool's disconnect contract changed.

## Closeout

Fixed on `master`. Independent review pending. **Manual acceptance against a real tenant with
two concurrent operators has NOT been done and is the thing that would actually confirm this.**

## Independent review of the fix, 2026-10-19 (codex-commercial / gpt-6-astra / max) - CHANGES REQUIRED

Lock ordering was found sound and the successful attribution path correctly scoped. Four
findings otherwise; three are repaired here and one is split out.

### 1. HIGH - a bare `lock` behind the slot semaphore was an availability bug - FIXED

A borrower takes one of five slots **before** establishment, and `lock` has no timeout and no
cancellation - `Task.Run(..., ct)` cannot cancel a delegate already blocked in a monitor. So
one stalled establishment could pin every slot shut with no way out. **I introduced that.**

Worse, the reviewer established by IL inspection what the critical section actually costs:
`Get-ConnectionInformation` is **not a map read**. `GetConnectionInformation.ProcessRecord`
calls `GetPingStatus` per REST context, and that calls `HttpClient.SendAsync(...).Result` -
a synchronous HTTP health check **per existing connection**, in both 3.9.2 and 3.10.1. Five
overlapping cold borrows meant up to 25 serial health checks on top of five serial connects.
My own record had called it a cheap read.

Now a `SemaphoreSlim` with a 90-second timeout and the caller's cancellation token; a borrower
that cannot get its turn fails with a busy message instead of holding a slot indefinitely.
Serial establishment stays - it is what makes the id attributable - but the queue behind it is
bounded.

### 2. MEDIUM - a failed snapshot was indistinguishable from an empty one - FIXED

`ReadConnectionIds` swallowed errors and returned an empty list, so "I could not look" and
"nothing is there" were the same answer, and a perfectly good connection silently became
unattributable - then never disconnected, leaving a **client-side context in the static map**,
not merely a server session to time out.

It now returns null on failure, checks the error stream rather than suppressing it, and
`NewConnectionId` refuses when either side is null.

**Two corrections to what this record claimed.** "The session will time out" was wrong about
the leak: server or token expiry does not remove the entry from this client's static map.
And "there is no safe alternative selector" overlooked cleanup through the runspace's own
imported module - the targeted disconnect itself ultimately works by module removal. The
fallback is still the right emergency behaviour; treating it as an acceptable steady state
was not, and that is now stated as the gap it is.

### 3. MEDIUM, pre-existing - teardown does not retire known ids - NOT FIXED

`Dispose` stops the timer and drains only what is available; a borrowed or still-connecting
runspace can return afterwards into a pool whose cleanup has stopped, and `DestroyRunspace`
swallows a failed disconnect without reporting or retrying. The reviewer checked the parent
commit and confirmed none of this is new. Split out as `aud-a6c` rather than folded in.

### 4. MEDIUM - the source scans did not test their own names - FIXED

The reviewer did not assert this, they reproduced it: all three scans still passed with a
**foreign selector value**, with the disconnect **moved outside the null guard**, and with the
connect after an **empty lock**. They also noted the record said four scans where there were
three.

Replaced with behaviour. `StubbedRunspace` opens a real runspace carrying a stub
`Disconnect-ExchangeOnline` that records the parameters it receives - PowerShell resolves a
function ahead of a cmdlet, so the pool's real call runs unmodified. The pool then borrows,
returns and drains, and the test asserts what was actually sent. The repo already used this
shape at `MigrationMailboxLoadTests.cs:207`; the reviewer pointed at it.

| Test | What it pins |
| --- | --- |
| `TheDisconnectNamesThisRunspacesOwnConnection` | the selector's **value**, not its presence |
| `ARunspaceWithNoKnownConnectionIdIsNeverDisconnected` | nothing is sent at all without an id |
| `AnUnreadableSnapshotIsNeverTreatedAsAnEmptyOne` | null != empty, three cases |
| `EstablishmentIsSerialisedBehindABoundedCancellableGate` | still a scan, and says so |

| Mutation | Result |
| --- | --- |
| send `"someone-elses-id"` as the selector | FAIL - `TheDisconnectNamesThisRunspacesOwnConnection` |
| remove the unknown-id guard so the disconnect always runs | FAIL - `ARunspaceWithNoKnownConnectionIdIsNeverDisconnected` |

Both are exactly the counterexamples the reviewer slipped past the old scans.

Gate: suite 4331 passed / 3 known AD skips / 0 failed; format and `git diff --check` clean.
