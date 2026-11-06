# rev-b5: two consecutive failed revalidations do not end the session when the interval is short

**Severity**: MEDIUM - revocation window longer than documented. The sweep-c-15 fallback is an AGE limit
(`Interval + 2 x LookupTimeout`), not a count. With the supported 30-second interval and the 60-second timeout, a
successful read at t=0 is reused at t=30, 60, 90 and 120 on four consecutive failures; the README says "two failed checks
in a row ... end the session".
**Status**: Admitted; awaiting fix on branch
**Lines** (master `145f513f`): `Authorization/CircuitMembershipRevalidator.cs:137`, `:232-236`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Track consecutive failures per session, reset on a fresh successful read, end the session on the second consecutive
failure, and keep the age limit as the outer bound. `TwoFailedChecksInARow_EndTheSession` passes only because its harness
uses a five-minute interval and a ten-second timeout; add the 30-second-interval case.
