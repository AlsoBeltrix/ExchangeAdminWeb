# aud-a29k: two PageRequestRevalidation tests hold a directory read open under a 10 s real-time harness timeout and can fail under load

**Severity**: LOW - flaky test. `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` about 404 (`AnAnswerFromAReadThatStartedEarlier_DoesNotReplaceALaterOne`) and 437 (its sibling) hold a read open while other steps run; the harness lookup timeout (about 697) is 10 s of wall time, so a stall under load turns the early check into `LookupFailed`. Seen once in about 76 runs on a saturated host; no race found in the code.
**Status**: Open - admitted 2026-10-23 evening from the aud-a29g agent's second hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (aud-a29g agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `d58b5d7c`. Trigger: CPU saturation during the test. Predicted failure: a false red on CI or a loaded dev box.

## Approach

- Give both tests a lookup timeout far above any stall (or drive the clock deterministically); no behaviour change; the test's own re-run under an injected stall proves it.

## Guard proof

Pending.