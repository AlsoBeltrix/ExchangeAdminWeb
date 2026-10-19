# flake-1: CanceledTokenLockWaiterCannotSendALaterRequest fails intermittently under load

**Severity**: MEDIUM as a test defect - it makes the gate unreliable, and an unreliable gate is
how a real failure gets waved through. **Possibly HIGH as a product defect; that is not
settled** - see below.
**Status**: Open - identified and reproduced, NOT diagnosed
**Branch**: -
**Commit**: -

## Evidence

`ExchangeAdminWeb.Tests/DefenderEnrichmentCancellationTests.cs:164`,
`CanceledTokenLockWaiterCannotSendALaterRequest(vault: true)` and `(vault: false)`.

```
Assert.IsAssignableFrom() Failure: Value is null
Expected: typeof(System.OperationCanceledException)
Actual:   null
```

**This is the flake that was hunted and not found on 2026-10-19.** A full run that day reported
one failure whose name a `tail -3` destroyed; three consecutive full re-runs at that tree state
were green, and it was recorded as unidentified. It surfaced again during the aud-a5 repair
gate - both theory cases in one run, then one case in the very next run, then neither.

Reproduction, such as it is: it fails only in a **full parallel suite run**
(`xUnit.MaxParallelThreads` default). Run the test alone and it passes every time. It is
load-dependent, not state-dependent.

## Why it is not diagnosed here

The test starts the second call and then cancels:

```csharp
var second = Invoke(true);
cancellation.Cancel();
release.TrySetResult();
```

Nothing synchronises "the second call has reached the token lock and is waiting" with the
`Cancel()`. The comment above the release claims the sequence is "independent of scheduler
speed"; the failure says otherwise. Under load the second call can still be before the
cancellable region when `Cancel()` fires.

**Whether that makes it a test defect or a product defect is exactly what has not been
established.** `error` being null means the second call *completed*. If it completed because it
genuinely finished its work before cancellation was observed, the product is correct and the
test's sequencing is wrong. If it completed because a canceled waiter went on to send a request
anyway, that is the product defect the test was written to catch - and it would mean the test
is failing honestly and intermittently rather than flaking.

Settling that needs the other two assertions' values at the moment of failure
(`requests.Count` and whether `/second` was sent), which the first assertion short-circuits.
That is a small change to the test and the right first step.

## Proposed next step

1. Reorder the assertions, or capture all three, so a failure says whether the later request
   was actually sent. That one run distinguishes test defect from product defect.
2. Only then decide what to fix.

It is in the Defender work stream, not the repository-audit plan, and it is not mine - noting
that for scope, not as a reason to leave it.

## Impact on current work

The aud-a5 repair's final gate ran with this failing: 4309 passed, 3 known AD skips, 1 failed.
The failure is in a file that change does not touch, it predates the change, and the affected
tests pass in isolation. That is the basis for treating it as unrelated - stated so it can be
checked rather than taken on trust.

## Closeout

Not started.
