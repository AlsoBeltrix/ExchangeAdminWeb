# rev-e1: page-request revalidation bypasses the consecutive-fallback counter

**Severity**: MEDIUM - the rev-b5 rule ("two consecutive failed lookups end the session") is enforced only on the
circuit path. A page request or reload while the directory is not answering renders from the recorded answer through
`RevalidatePageRequestAsync` without counting the fallback, and a fresh circuit starts its counter at zero, so stale
access can be admitted until the recorded answer ages out (interval + 2 min) instead of ending at the second failure.
**Status**: Admitted 2026-11-09 from codex review E (`.agents/review/rev-e.result.txt`); awaiting a slice
**Reviewer**: codex (Portkey) / configured route / xhigh
**Range reviewed**: 3f2bcb3b..78c03e49 (merge rev-abc)

## Evidence (master f877090d)

- `Authorization/CircuitMembershipRevalidator.cs:168-169`: `RevalidatePageRequestAsync` calls `CheckAsync(...,
  acceptRecordedAnswer: true, ...)`.
- `Authorization/CircuitMembershipRevalidator.cs:239-244`: the fallback verdict is valid and marked
  `FromRecordedAnswer = true`.
- `Middleware/PageRequestRevalidationMiddleware.cs:89-92`: `if (verdict.IsValid) { await _next(context); return; }`
  - `FromRecordedAnswer` is not counted.
- `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs:199-205`: the only counter is
  `_consecutiveFailedReads`, per circuit provider.

## Failure scenario

Operator has one recorded good answer; AD stops answering. First circuit check falls back (counter 1). The operator
reloads: the page request falls back again from the same recorded answer and admits, counting nothing; the new
circuit's counter is 0. Repeated reloads keep admitting until the recorded answer ages out.

## Fix shape (reviewer's proposal; verify first)

Make the page-request fallback charge the same per-session failure budget (a session-keyed counter the circuit
provider and the middleware both read), or refuse a page request whose only valid answer is `FromRecordedAnswer`
when no session counter can be charged. Add a page-request / reload case to the 30-second-interval, 60-second-timeout
test (`PageRequestRevalidationTests` today exercises only `provider.ValidateAsync`).

## Review E closure summary

Twelve of thirteen rev-abc findings CLOSED; rev-b5 PARTLY (this record is the remainder). No other new observable
defect in the range.
