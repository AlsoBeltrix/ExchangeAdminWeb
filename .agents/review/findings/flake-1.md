# flake-1: a canceled caller waiting on the token lock still sends its request

**Severity**: MEDIUM - **downgraded from HIGH by review; see the correction at the end.** A
cancelled caller takes a lock and a cached token it should not have, and proceeds to build and
dispatch a request. In production the HTTP handler then rejects the cancelled token, so the
request does not reach the service - which is what makes this MEDIUM rather than HIGH.
**Status**: Fixed in three clients; reviewed; one latent fourth path carried.
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

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - corrections

**The fix itself passed.** The reviewer verified the `SemaphoreSlim` diagnosis against the
.NET 10.0.12 implementation rather than accepting it - cancellation makes
`WaitUntilCountOrTimeoutAsync` yield, and it returns success if the waiter has already
completed, throwing only if it removes the waiter itself, so `Release` can win that race even
when `Cancel()` was called first. They confirmed all three new checks sit inside the `try`
with the wait outside it, so lock ownership is preserved on the new throw path, and found no
introduced regression and no remaining argument-binding mistake.

Three corrections to what this record claimed, all accepted.

### Severity was overstated - HIGH down to MEDIUM

This record said a cancelled operation "reaches the API". It does not, in production. The test
fixture's handler ignores its cancellation token and returns canned responses, so what the
evidence shows is an unwanted **handler invocation**. Production resolves the standard socket
handler through `IHttpClientFactory` (`Program.cs:132`, `:142`), and `SocketsHttpHandler`
rejects an already-cancelled token before sending.

So the defect is real - a cancelled caller takes a lock and a cached token it has no right to,
and builds a request - but the request does not go out. Downgraded accordingly. **The original
HIGH was my inference from a stub, not an observation of the system.**

### "Mutation proof" was overstated

The run counts are observations of failures and passes. They do not demonstrate closure, and
they do not even give a reliable estimate of how much rarer the failure became, because
nothing in the test **forces** the semaphore to grant the cancelled waiter its slot - a
passing run can simply miss the repaired branch. The word "proof" was wrong for that evidence.
Real proof needs the grant to be deterministic, which the current harness cannot arrange.

The reviewer also notes the Graph tests now pass a cancellation token but never cancel
anything, so Graph's repair has no behavioural coverage at all, and the waiter test never
starts a third caller to show the new throw released the lock.

### The "second layer of checks" was not as described

Accurate: Defender has it (`DefenderApiClient.cs:183`). Delinea has it only after the first
secret-fields acquisition (`:79`); its retry and credential paths do not (`:90`, `:152`,
`:163`). Graph has none - its GET goes from token to request directly.

**Deliberately not "fixed" by adding the missing ones.** The same fact that downgraded the
severity makes that second layer redundant in production: every one of those paths ends in
`SendAsync(request, cancellationToken)`, and the real handler rejects a cancelled token there.
Adding four more `ThrowIfCancellationRequested` calls would buy nothing and would read as
though it did. The record is corrected instead.

### Carried, not fixed: a fourth path in the EXO pool

`ExoConnectionPool.cs:163` waits on its slot with the caller's token and can then return a
cached authenticated runspace at `:194` without re-checking, so the same interleaving could
hand a cancelled borrower a usable connection. Pre-existing, and currently **latent**: the
reviewer found no product caller that supplies a cancellation token on that path
(`ExoConnectionPool.cs:264`, `RecipientAutocomplete.razor:113`).

Carried here rather than fixed, because the approved scope was the token clients and this is
neither a token cache nor currently reachable. It belongs with `aud-a6b`, the other open pool
question.
