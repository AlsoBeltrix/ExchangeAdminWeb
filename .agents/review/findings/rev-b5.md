# rev-b5: two consecutive failed revalidations do not end the session when the interval is short

**Severity**: MEDIUM - revocation window longer than documented. The sweep-c-15 fallback is an AGE limit
(`Interval + 2 x LookupTimeout`), not a count. With the supported 30-second interval and the 60-second timeout, a
successful read at t=0 is reused at t=30, 60, 90 and 120 on four consecutive failures; the README says "two failed checks
in a row ... end the session".
**Status**: Merged 78c03e49; PARTLY closed by codex review E 2026-11-09: the circuit path is closed, the page-request path bypasses the counter (rev-e1)
**Branch**: fix/rev-abc
**Commit**: e157a7cb
**Lines** (master `145f513f`): `Authorization/CircuitMembershipRevalidator.cs:137`, `:232-236`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Track consecutive failures per session, reset on a fresh successful read, end the session on the second consecutive
failure, and keep the age limit as the outer bound. `TwoFailedChecksInARow_EndTheSession` passes only because its harness
uses a five-minute interval and a ten-second timeout; add the 30-second-interval case.

## As built (fix/rev-abc `e157a7cb`)

Verified real: the fallback is bounded only by age (`FallbackWindow` = interval + 2 x timeout = 150 s at 30 s / 60 s), so one
answer stood in for failed checks at t=30, 60, 90 and 120.
- `MembershipRevalidatingAuthenticationStateProvider` (one per circuit, so per session) counts its consecutive checks
  confirmed from a recorded answer (`MembershipRevalidationResult.FromRecordedAnswer`), and at the second
  (`FailedReadsThatEndTheSession = 2`) revokes with `LookupFailed`, naming the count. A check whose fresh read answered resets
  it. The age limit stays the outer bound; a recorded answer that refuses still refuses at once.
- Page requests (`RevalidatePageRequestAsync`) have no session to count in and are unchanged.
- README "Session Revalidation" wording made exact; the revalidator's remarks say where the count lives.

## Guard proof

| Test | Fails on the old code / mutation (threshold 99) |
| --- | --- |
| `PageRequestRevalidationTests.TwoFailedChecksInARow_EndTheSession_WithAThirtySecondIntervalAndASixtySecondTimeout` | yes |
| `PageRequestRevalidationTests.AFreshReadBetweenTwoFailures_StartsTheCountAgain` | passes on both (guards the reset) |
| `PageRequestRevalidationTests.TwoFailedChecksInARow_EndTheSession` (5 min / 10 s) | unchanged, passes |
