# cons-2: a busy-wait test asserts an elapsed time under five seconds after a one-second hold

**Severity**: LOW - flaky test, the aud-a67 (7) class but outside the sub-item's named scope.
`ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs` `ALockReleasedWithinTheWait_LetsTheWriteThrough` asserts
`elapsed < BusyTimeout` (5 s) after holding the lock 1 s; on a loaded host the write can legitimately take longer.
**Status**: Open - admitted 2026-10-26 from the aud-a64/a67 agent's hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (aud-a64/a67 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `d0fc4c70`. Trigger: CPU or disk saturation. Predicted failure: "the write waited N s for a lock held 1 s" on a run
with no defect.

## Approach

- Keep the structural assertion (the write succeeded, no 30 s retry loop) and loosen or remove the wall-clock ceiling, as
  aud-a67 (7) did for its two; test-only; proof by the injected-delay method.

## Guard proof

Pending.