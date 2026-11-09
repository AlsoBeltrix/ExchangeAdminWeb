# rev-e1: page-request revalidation bypasses the consecutive-fallback counter

**Severity**: MEDIUM - the rev-b5 rule ("two consecutive failed lookups end the session") is enforced only on the
circuit path. A page request or reload while the directory is not answering renders from the recorded answer through
`RevalidatePageRequestAsync` without counting the fallback, and a fresh circuit starts its counter at zero, so stale
access can be admitted until the recorded answer ages out (interval + 2 min) instead of ending at the second failure.
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-e1 (worktree `D:\source\ExchangeAdminWeb-wt\rev-e1`, from master 59522ca6)
**Commit**: 5b10738e
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

## As built (fix/rev-e1 `5b10738e`)

Verified real at master 59522ca6: `PageRequestRevalidationMiddleware.InvokeAsync` admits any valid verdict, the
fallback verdict is valid with `FromRecordedAnswer = true`, and the only count was the provider's per-circuit
`_consecutiveFailedReads`. Against that code the new tests `ReloadsWhileTheDirectoryIsDown_...`,
`ACircuitFailureThenAPageRequestFailure_...`, `APageRequestFailureThenACircuitFailure_...` and
`ANewCircuit_DoesNotStartTheCountAgain` fail (guard proof below).

- **Where the count lives.** The recorded-answer store already had a per-account shape
  (`CircuitMembershipRevalidator._answers`, keyed by account SID), so the count is on its `RecordedAnswer` entry - not a
  parallel store. Per account means every tab, every page request and every new circuit of the session charge and read
  the same count, and it goes with the answer when that expires (past `FallbackWindow` nothing falls back and the
  failure itself refuses, as before). The provider's counter is removed; `FailedReadsThatEndTheSession = 2` moved to the
  revalidator.
- **Charge and refuse.** `FallBackOrFail` charges the answer once per check and, at the second failure in a row, returns
  `LookupFailed` "The directory could not be read for 2 access re-checks in a row. ..." (`FromRecordedAnswer` still set)
  instead of the fallback's confirmation. The middleware refuses it (403, audit trigger `PageRequest`, reason
  `LookupFailed`); the provider revokes it with the same reason and detail as before. A recorded answer that refuses still
  refuses for its own reason.
- **Reset.** A later successful read replaces the answer and with it the count. A success whose read started no later
  than the latest charged failed read carries the count on (it does not show the directory answering after that
  failure); a failed read that started before the current answer's read is not charged (the directory answered since).
- **Concurrency.** Every change to `_answers` (recording, the sweep) and every charge run under one lock
  (`_answersSync`); reads stay lock-free. One check's two arrivals at `FallBackOrFail` (its own failure and its timeout)
  get one settled verdict (`CheckState.Outcome`), so one failed lookup is never charged twice and both return the same
  answer. Replaces the old `Settled` interlocked flag.
- **Several tabs, one stall.** A failed read that was already in flight when the previous failure was charged is the same
  outage seen at the same moment, and is not charged again; every read started after a charge is. Without this, two open
  tabs checked during one directory stall would end the session on its first failure, undoing sweep-c item 15 for any
  operator with more than one tab. The window this admits is bounded by one lookup timeout (reads in flight together)
  and by the age limit.
- **Page shortcut.** Unchanged (an answer read within the interval confirms without a read), except that it never confirms
  an account already at the refusal count: the request reads, and is refused unless the directory answers.
- **Help.** README "Session Revalidation": the fallback bullet now states the per-account count across tabs, new tabs,
  page requests and reloads, the reset and the in-flight rule; the page-shortcut sentence names the exception. The
  interval is appsettings-only; Admin Settings has no help for it. No page changed.
- **Versions.** None: shared code, base 2.36.0 not bumped again this cycle.

## Guard proof

| Revert / mutation | Failing tests |
| --- | --- |
| M0 old code (master 59522ca6 sources, new tests) | `ReloadsWhileTheDirectoryIsDown_TheSecondFailedReadRefusesThePage`, `ReloadsWithAFreshReadBetweenTwoFailures_StartTheCountAgain`, `ACircuitFailureThenAPageRequestFailure_RefusesThePage`, `APageRequestFailureThenACircuitFailure_EndsTheSession`, `ANewCircuit_DoesNotStartTheCountAgain`, `OnceTheSecondFailureRefused_AReloadIsNotConfirmedWithoutARead`, `TwoTabsHeldByOneStall_AreChargedOnce`, `AnAnswerFromAReadThatStartedBeforeAFailure_DoesNotStartTheCountAgain` |
| M1 no in-flight coalescing | `TwoTabsHeldByOneStall_AreChargedOnce` |
| M2 an older success resets (no carry-on) | `AnAnswerFromAReadThatStartedBeforeAFailure_DoesNotStartTheCountAgain` |
| M3 page shortcut unguarded | `OnceTheSecondFailureRefused_AReloadIsNotConfirmedWithoutARead` |
| M4 a success never resets | `ReloadsWithAFreshReadBetweenTwoFailures_StartTheCountAgain`, `AFreshReadBetweenTwoFailures_StartsTheCountAgain` |
| M5 a failure older than the answer is charged | `AFailureOfAReadOlderThanTheAnswer_IsNotCharged` |
| M6 the second failure never refuses | 9 tests, including rev-b5's `TwoFailedChecksInARow_EndTheSession_WithAThirtySecondIntervalAndASixtySecondTimeout` |

All ran at the 30-second interval / 60-second timeout except the slow-read tests (2-minute timeout so a held read cannot
time out on a loaded host, aud-a29k). Every existing revalidation test passes unchanged.

## Gates (branch tip `5b10738e`)

Build Release (no-incremental): 19 warnings, 0 errors. Tests: 8861 passed, 0 failed (baseline 8852 + 9 new). Format
verify: clean. `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1`: passed. Not run: a live directory stall with
several tabs and reloads.
