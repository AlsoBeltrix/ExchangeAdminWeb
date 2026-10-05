# edl-2: A failed Notes stamp is reported inside a green LockdownMove row

**Severity**: MEDIUM - the plan requires a stamp failure to be visible in the step
table, the notification and the audit; all three currently show success
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: dbd19c2

## Evidence

`Services/EmergencyDisableService.cs` `ExecuteMoveToLockdownOu` folds the stamp
result into the move's own step:

```csharp
var stampDetail = StampPreviousOu(ps, target, credential, previousParent);
return (new DisableStepResult("LockdownMove", "OK", $"Moved to {lockdownOuDn}. {stampDetail}"), LockdownOutcome.Moved);
```

`StampPreviousOu` returns failure prose for a missing ObjectGUID, a failed re-read, an
over-length append or a failed `Set-ADUser`, and every one of those is returned with
status `OK`.

`Services/AuditService.cs:220` writes `["error"] = success ? null : errorDetail`, so on
a successful disable the stamp failure reaches no durable field at all; the audit extra
carries only `lockdownRequested` and `lockdownOutcome`, and the outcome is `Moved`.

Triggering condition: `Move-ADObject` succeeds and the subsequent `info` write does
not - the service account lacks write access to `info`, or the append would exceed
1024 characters.

Predicted observable failure: the operator sees a green `LockdownMove / OK` row, the
security notification reports a successful lockdown, and the audit records
`result=Success, lockdownOutcome=Moved` with no stamp failure anywhere. The account is
moved but carries no breadcrumb, and nothing durable says the breadcrumb is missing.

This contradicts the plan directly (`docs/EmergencyDisableLockdownOU-Plan.md`, Overall
success accounting): "its failure shows as a red row in the step table, in the
notification and in the audit, and is not silent."

## Approach

The stamp gets its own outcome (`LockdownStampOutcome`) and its own `LockdownStamp`
step row, so a failed breadcrumb is a red row beside the green move rather than a
sentence inside it. `ExecuteMoveToLockdownOu` now returns a list of steps plus both
outcomes; `StampPreviousOu` returns its outcome instead of prose alone.

The outcome also rides the audit extra as `lockdownStampOutcome` - necessary because
`AuditService.cs:220` writes `error` as null on a successful event, so nothing else in
the record could carry it - and `LockdownSummary` appends an explicit sentence when the
stamp failed, since that line is what the security team reads.

The stamp still does not fail the operation. The plan states that deliberately: the
previous OU is held authoritatively in the snapshot and the audit. Only its visibility
changed.

## Files changed

- `Services/EmergencyDisableService.cs`
- `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs` - five cases

## Guard proof

Full Release suite 3626 passed / 0 failed / 3 skipped. Format, ASCII and
`git diff --check` clean.

Three probes, all bite:

1. Removing the stamp warning from `LockdownSummary` fails
   `LockdownSummary_MovedButStampFailed_SaysTheBreadcrumbIsMissing` (1 failed / 5 passed).
2. Deleting `extra["lockdownStampOutcome"]` fails
   `StampOutcome_ReachesTheAuditExtra_NotOnlyTheStepTable` (1 failed / 0 passed).
3. Folding the stamp detail back into the `LockdownMove` step - the exact original
   defect - fails `MoveStep_DoesNotCarryTheStampResultInItsOwnDetail` (1 failed / 0 passed).

Probes 2 and 3 are source-level because `ExecuteMoveToLockdownOu` needs a live
directory; stated plainly rather than presented as behavioural coverage.

## Coder dispute

None. Confirmed, and it is a plan-conformance defect rather than a judgment call.

## Known gaps

The stamp stays excluded from overall success, which the plan states deliberately.
Only its visibility is fixed.

**Two of the three guards are source-level and the reviewer judged them weak, which
is accepted rather than argued.** Its words: the audit guard "could be satisfied by a
dead/comment occurrence of `extra["lockdownStampOutcome"]`, or by assigning a
constant/wrong value", and the move guard "could be bypassed with equivalent
concatenation, target-typed `new`, a helper, or a dead `LockdownStamp` constructor".
Both bite against the exact regression probed and neither is behavioural proof that a
failed stamp produces a red row and a durable audit value at runtime. Reaching
`ExecuteMoveToLockdownOu` needs a live directory, so that proof is manual acceptance
step 3 of the plan, not something this suite can close.

## Reviewer comments

Change review of `1bbbb73..1313fb4`, finding 2 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard

## Closeout

<pending>

### Verdict round, `549c95d..dbd19c2`, 2026-10-05

**SOUND / accepted.**
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Harness: codex-cli 0.159.0. No escalation triggers matched (T1 no, severity MEDIUM so
no T2).
Raw output: `.agents/review/edl-2.result.json`; prompt and stream log alongside.

It traced all three surfaces the finding names - step table through `RowClassFor` to
`table-danger`, notification through `LockdownSummary` and the widened step filter,
audit through `MergeExtra` running after `error` is set, which is why the key survives
a successful event. It reproduced all three probes with counts matching the
coder-side run (1/5, 1/0, 1/0) and 61 passed on the unmutated head.

Adjacent checks it ran and cleared: every no-move return path leaves
`NotAttempted`; no path emits a `LockdownStamp` row without a successful
`Move-ADObject`, and the successful path always emits one; PowerShell command and
error streams are cleared between the OU lookup, the move, the stamp read and the
stamp write; overall success still ignores the stamp outcome, as the plan specifies.

Two environment notes it reported rather than hid: `git worktree add` was blocked by
its sandbox (`.git/worktrees` read-only), so it extracted an archive of the pinned
head under `%TEMP%` instead - still an isolated tree at the right commit - and its
policy then blocked deleting that temp copy. The primary repo stayed clean.
