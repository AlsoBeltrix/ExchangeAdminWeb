# flake-1: a canceled caller waiting on the token lock still sends its request

**Severity**: HIGH - a cancelled operation reaches the API anyway. Here the intermittently
failing cases are GETs, but the defect is in the shared token path every Defender call uses,
including the POSTs.
**Status**: Open - DIAGNOSED, not fixed. Product defect, not a flaky test.
**Branch**: -
**Commit**: `e8c9041` carries the diagnostic test change only.

## What it was mistaken for

This was recorded on 2026-10-19 as an unidentified flaky test. A full run reported one failure
whose name a `tail -3` destroyed; three consecutive re-runs were green. It then reappeared
during the `aud-a5` repair gate.

**It is not a flake.** `ExchangeAdminWeb.Tests/DefenderEnrichmentCancellationTests.cs`,
`CanceledTokenLockWaiterCannotSendALaterRequest`, was failing honestly and intermittently, and
the test was written to catch exactly this. The reason it read as a flake is that its
assertions ran cancellation-first, so every failure said only:

```
Assert.IsAssignableFrom() Failure: Value is null
Expected: typeof(System.OperationCanceledException)
Actual:   null
```

which says nothing about what the code did. Reordering them worst-first - and attaching the
request list - produced the real answer on the first failing run:

```
A canceled waiter sent its request anyway.
requests=[/tenant/oauth2/v2.0/token, /first, /second]; error=null
requests=[/oauth2/token, /api/v1/secrets/1, /api/v1/secrets/2]; error=null
```

`/second` and `secrets/2` are the canceled caller's requests. They went out, and the call
returned success rather than throwing. Four full suite runs: failed in 3 of 4, both theory
cases in one of them, neither in another. Always passes run alone.

## Mechanism

`Services/DefenderApiClient.cs:277`:

```csharp
await _tokenLock.WaitAsync(cancellationToken);
try
{
    if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
        return _accessToken;
```

`SemaphoreSlim.WaitAsync(token)` does **not** guarantee cancellation wins when the slot becomes
available and cancellation is requested at the same moment - it can complete the wait
successfully instead. The test arranges precisely that: it cancels, then releases the first
caller's token request, freeing the lock.

The waiter therefore acquires the lock, finds the token the first caller just cached, returns
it without looking at `cancellationToken` again, and `SendAsync` goes on to issue the request.
`SendAsync` checks cancellation only at its entry (`:170`), before the lock wait.

So: **nothing re-checks cancellation between acquiring the token lock and issuing the
request.**

`Services/GraphTokenClient.cs:248` is worse and should be fixed with it:
`GetAccessTokenAsync()` there takes **no CancellationToken at all** - neither
`_tokenLock.WaitAsync()` nor the token `PostAsync` is cancellable - and `GetWithStatusAsync`
has no token parameter either. That client simply cannot be cancelled.

## Proposed fix - NOT BUILT

`cancellationToken.ThrowIfCancellationRequested()` immediately after the lock is acquired in
`DefenderApiClient.GetAccessTokenAsync`, inside the `try` so the lock is still released, and
again in `SendAsync` after `GetAccessTokenAsync` returns. That makes the outcome deterministic
regardless of which way the semaphore race falls.

Whether `GraphTokenClient` should be given cancellation support at the same time is a larger
question - it changes a public signature used by several modules - and belongs to its own
slice.

## Why it is not fixed here

It is in the Defender work stream, not `docs/RepoAudit-Remediation-Plan.md`, and it was found
by the audit wave's gate rather than by that plan. It needs an owner go.

## Guard

The reordered assertions are the guard and they already bite - they are what produced the
diagnosis. A fix must make all four of four runs pass; three of four is the current baseline.

The test's `requests` list was also a plain `List<string>` written from two concurrent handler
invocations, which is not safe and could have corrupted the count independently. Now a
`ConcurrentQueue`. That was a second, separate defect in the same test.

## Closeout

Diagnosed. Fix not started.

## Fixed, 2026-10-19 - and it was in three clients, not one

Owner go given to fix both `DefenderApiClient` and `GraphTokenClient`. Verifying that fix
found a third.

| Client | What it needed |
| --- | --- |
| `Services/DefenderApiClient.cs` | re-check after the lock, and after the token call in `SendAsync` |
| `Services/GraphTokenClient.cs` | the same, **plus cancellation support at all** - nine public methods and the token path took no `CancellationToken` whatsoever |
| `Services/DelineaService.cs` | the same shape again, in the vault token path |

`GraphTokenClient`'s methods each gained `CancellationToken cancellationToken = default`, so
every existing caller compiles unchanged, and the token now reaches the lock wait, the token
POST, every `SendAsync` and every body read.

**The third one is the part worth remembering.** After fixing the first two I ran the suite
four times, got four clean runs, and said so. The fifth run failed on `vault: true` - the
Delinea path, which neither fix touched. Four clean runs was not proof; it was the sample
being too small for a race whose odds the first two fixes had already lowered. The claim was
wrong when it was made, not merely unlucky.

Evidence after all three:

| State | Full suite runs | Failures |
| --- | --- | --- |
| Before any fix | 4 | 3 |
| After Defender + Graph only | 5 | 1 (vault) |
| After all three | 5 | 0 |

The mutation proof is that first row rather than a reverted line: the pre-fix tree, measured,
fails this test most of the time, and the post-fix tree has not failed it once.

Two collateral repairs the change forced, both recorded rather than folded in silently:

- Widening `GraphTokenClient`'s signatures made the xUnit analyzer flag eighteen existing call
  sites in `GraphTokenClientTests`, which fails the `dotnet format` gate CI enforces. Each now
  passes the test's own token. Three of them needed a **named** argument - positional put the
  token in the `object? body` parameter, which compiled fine and would have silently passed a
  `CancellationToken` as a request body.
- `ModuleCatalogTests.Catalog_HasDefenderEndpointDevicesModule` pins the Defender module
  version and was updated with the bump.

Versions: base 2.27.9 -> 2.27.10 (GraphTokenClient and DelineaService are app-wide);
DefenderEndpointDevices 1.6.16 -> 1.6.17.

## Closeout

Fixed directly on `master`. Not pushed at time of writing. Independent review not yet run on
this fix.
