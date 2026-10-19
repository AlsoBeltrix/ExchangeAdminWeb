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
