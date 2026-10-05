# edl-2: A failed Notes stamp is reported inside a green LockdownMove row

**Severity**: MEDIUM - the plan requires a stamp failure to be visible in the step
table, the notification and the audit; all three currently show success
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

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

<pending>

## Files changed

<pending>

## Guard proof

<pending>

## Coder dispute

None. Confirmed, and it is a plan-conformance defect rather than a judgment call.

## Known gaps

The stamp stays excluded from overall success, which the plan states deliberately.
Only its visibility is fixed.

## Reviewer comments

Change review of `1bbbb73..1313fb4`, finding 2 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard

## Closeout

<pending>
