# edl-1: A declined lockdown renders as a red failure row

**Severity**: MEDIUM - a routine, authorised operator choice is presented as an error on a security screen, and S1 ships that row before any choice exists
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## Evidence

`Components/Pages/EmergencyDisable.razor:114` maps every step status that is not
`OK` or `SKIPPED` to `table-danger`:

```razor
var rowClass = step.Status == "OK" ? "table-success" : step.Status == "SKIPPED" ? "table-secondary" : "table-danger";
```

`Services/EmergencyDisableService.cs` `StepStatusFor` returns `"NOT REQUESTED"` for
`LockdownOutcome.NotRequested`, and `DisableAsync` adds that row on every run.
`Components/Pages/EmergencyDisable.razor:333` passes `moveToLockdownOu: false` in S1.

Triggering condition: any emergency disable that reaches the mutation phase.

Predicted observable failure: the operator sees a red `LockdownMove / NOT REQUESTED`
row inside an otherwise successful result. In S1 that happens on every single run with
no operator choice behind it; in S2 it happens whenever the operator legitimately
unticks the box. The plan's own design says declining is a decision the operator is
entitled to make, so painting it as a failure contradicts the feature.

## Approach

<pending>

## Files changed

<pending>

## Guard proof

<pending>

## Coder dispute

None. Confirmed against both files. The reviewer's second recommendation - treat
NOT REQUESTED as neutral rather than danger - is the fix taken, because the row
itself is wanted in S2 by the plan ("the checkbox state is audited on every run").

## Known gaps

The audit and notification also state "not requested" during S1, where no operator
choice exists. That is literally accurate and is not changed here: S2 lands the
checkbox immediately after, and suppressing then unsuppressing the record would be
churn. Recorded rather than silently accepted.

## Reviewer comments

Change review of `1bbbb73..1313fb4`, finding 1 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard

## Closeout

<pending>
