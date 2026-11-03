# sweep-c-15: one failed or hung revalidation lookup no longer ends the session (sweep item 15)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 15: a single `LookupFailed` (a directory read
that threw, answered nothing, or took longer than the 60 s `LookupTimeout`) revoked the circuit with no retry, and a page
request got a 403. On 10-30 ADWS held one call for 2:00; four such calls (the directory's concurrency) end every new tab.
aud-a29j made it worse on purpose: a failed read *forgot* the account's recorded answer.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-c` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-c`, from master `72611fc2`)
**Commit**: see `git log master..fix/sweep-c` (`fix(sweep-c, item 15): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `72611fc2`: `Authorization/CircuitMembershipRevalidator.cs` `ReadDirectoryAsync` `Forget`s on throw / no answer;
  `CheckAsync` returns `TimedOut()` on the timeout; `MembershipRevalidatingAuthenticationStateProvider.ValidateAsync` revokes
  every invalid verdict. Page requests could be confirmed from an answer read within the interval (aud-a29g), never refused by one.

## Approach (as built)

- **Fallback.** `FallBackOrFail(state, failure)`: when the fresh read throws (not a cancellation), answers nothing, or times
  out (both `CheckAsync` timeout paths), the account's recorded answer is used if its read started within
  `FallbackWindow` = Interval + 2 x LookupTimeout, and compared exactly as a fresh one (`Compare`): it confirms, or refuses
  with its own reason. No answer that recent -> the failure stands and the session ends, as before.
- **Why that window.** The framework loop starts the next check one Interval after the last ended; a check takes at most one
  LookupTimeout; the failure is known at most one more later. So the previous check's answer is always inside, and an answer
  two checks old never is: one failed check is forgiven, two in a row end the session. "Within the interval" read literally
  (age < Interval, the page shortcut) would never hold the loop's own previous answer, so an idle tab would still end on
  one failure.
- **Only the directory's failures.** `CheckState.Reading` is set just before the read; a check that failed earlier (no
  usable SID, unreadable store, unattestable static grant) never falls back.
- **Answers kept, not forgotten.** `ReadDirectoryAsync` no longer forgets on failure (aud-a29j's rule reversed); answers
  expire at `FallbackWindow` (the sweep and `RecordedAnswerFor`). The page shortcut is unchanged (age < Interval).
- **Counted and logged.** `FallbackCount`; one Warning per fallback naming the account, the failure, confirmed/refused, the
  answer's age and the running count. The verdict carries `FromRecordedAnswer` and the failure in its Detail (audit line on
  a refusal). The 60 s timeout is unchanged.
- **Help.** README Session Revalidation: new bullet and the two fail-closed bullets reworded. No page changed.
- **Versions.** None: app-wide (base 2.36.0 undeployed).

## Where the code goes beside the sweep's fix shape

- The window is Interval + 2 x LookupTimeout, not the Interval, for the reason above; access confirmed by an answer can now
  be up to 7 min old (default settings) during a directory failure, against 5 min when the directory answers.
- aud-a29j's "a failed read forgets the answer" is reversed; its sweep (no unbounded growth) is kept, at the new window.

## Guard proof

| Revert | Failing test |
|---|---|
| M1 no fallback | `AFailedRead_FallsBackToTheRecordedAnswer_AndTheSessionIsKept`, `AHungDirectory_WithAnAnswerFromTheLastCheck_KeepsTheSession`, `TwoFailedChecksInARow_EndTheSession`, `ARecordedDenial_IsNotForgiven_WhenTheNextReadFails`, `AFailureOfAReadThatStartedEarlier_DoesNotForgetALaterAnswer` |
| M2 window = Interval | `AHungDirectory_WithAnAnswerFromTheLastCheck_KeepsTheSession`, `TwoFailedChecksInARow_EndTheSession` |
| M3 fallback confirms without comparing | `ARecordedDenial_IsNotForgiven_WhenTheNextReadFails` |
| M4 fallback not counted | `AFailedRead_FallsBackToTheRecordedAnswer_...`, `AHungDirectory_WithAnAnswerFromTheLastCheck_...` |

`AHungDirectory_WithNoAnswerRecorded_EndsTheSession`, `ARealDenial_StillEndsTheSessionAtOnce_WhateverWasRecorded` and
`AFailureBeforeTheDirectoryIsAsked_NeverFallsBack` pin behaviour that did not change (they pass before and after).

## Gates

Run once for the branch at its tip: see the sweep-c report. Not run: a live directory stall.
