# sweep-d-20: a Comms-10k authorization re-check between batches could leave the list half-replaced (sweep item 20)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 20: `RequireAuthorization()` ran as
`beforeDispatch` of the clear and of every add batch (`Comms10kService.cs:496-499`, `:512-513`), so a revocation
between the clear and an add stopped the run with the list empty or partial.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-d` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-d`, from master `6ea8963b`)
**Commit**: `cbb10c4e`, and `ed328432` (a test assertion style fix, xUnit2013) | **Reviewer**: implementer (candidate)

## Evidence

- At `6ea8963b` the deleted test `C2AfterAnyClearAttemptRevocationStopsWritesAndReportsReadback` pinned exactly that:
  revoke at the clear or between batches, and the run ended `PartlyApplied` with 0 or 2,000 members.

## Approach (as built)

- `PerformReplace` consults `authorizationDenial()` exactly once: after the target re-read, the current-membership read
  and the approved primary-group check, immediately before the first write. A denial there is `Refused` - nothing
  written. No write carries a gate (`beforeDispatch` only marks dispatch). A revocation after that point applies to the
  next run. The processor's stage-level checks (resolve row, replace row) are unchanged.
- **What a mid-run stop now leaves, and why that is the safe state.** No authorization refusal happens mid-run any more.
  A run that stops on a write failure, under sweep-d-19's add-first order, leaves either every member the list held
  before (an add failed: nothing removed) or every member the file names (a remove failed: some extras remain). Neither
  removes a recipient the operator did not intend to remove; the old shape could leave the list empty.
- Unchanged: the file must resolve completely before Replace (C3), and matching is exact UPN or mail only.
- README Comms-10k: one sentence on the single check.

## Where the code goes beside the fix shape

- None. Note: the decision to proceed is now made once, up to the time an add batch is sent - a run of 53 s worst case
  (sweep-d-19) completes on an authorization taken before its first write.

## Guard proof

| Mutation | Failing tests |
|---|---|
| M5 re-check before every add (the old shape) | `Comms10kRefusalTests.C2ARevocationAfterTheFirstWriteDoesNotLeaveAHalfReplacedList` (theory), `C2TheServiceConsultsAuthorizationExactlyOnce_AfterPreparation_BeforeTheFirstWrite`; `Comms10kReplaceSequenceTests.AuthorizationIsConsultedOnce_BeforeTheSequence_AndNeverInsideAWrite` |
| M6 no check at all | 11 failures: `C2ADenialAtTheSingleCheckRefusesWithTheListUntouched`, `C2RevocationDuringAwaitedCredentialsRefusesBeforeActualDispatch`, `C2TheWriteGateFollowsAllPreparation` (theory; then named `C2TheFinalClearGateFollowsAllPreparation`), `C2TheServiceConsultsAuthorizationExactlyOnce...`, `AuthorizationIsConsultedOnce...` |

## Gates

Run once for the branch at its tip; see the sweep-d hand-back.
