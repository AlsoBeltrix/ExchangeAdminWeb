# cpe-1: The module plan says the operation trace carries the reset's steps; nothing in the module writes a trace

**Severity**: LOW, pre-existing - documentation that claims a diagnostic record which does not exist. No operator-visible
behaviour, no security effect.
**Status**: Open - logged, not approved
**Branch**: -
**Commit**: the commit that adds this record (S4 of `docs/CloudPasswordResetEmployeeId-Plan.md`, branch `feat/cpr-employee-id`)
**Reviewer**: implementer (found while drafting the plan, section 14; recorded at its S4)

## Evidence

At `714c4128`:

- `docs/CloudPasswordReset-Plan.md` about line 701, "Diagnostic logging": "**The operation trace still carries the step
  transcript** (`OperationTraceService`, ...)".
- Neither `Components/Pages/CloudPasswordReset.razor` nor `Services/CloudPasswordResetService.cs` calls
  `OperationTraceService` or opens a trace scope (no match for `OperationTraceService` or `OperationTrace.` in either file).
- `docs/ProjectConstitution.md`, Auditing And Tracing: "Long-running or multi-step backend operations should use
  `OperationTraceService` for start/step/complete visibility." A reset is multi-step: ticket, protection, fresh read, derive,
  and from `1.2.0` the employee-ID save, then the password write and the delivery.

The employee-ID plan added no trace either (its J11) and logged this drift instead of fixing it.

## Approach

Either correct the module plan's sentence to say the steps are reported to the status frame and the app log but no
operation trace is written, or add a trace to the reset. The second is a logging/trace change, which the Constitution's
planning rules route through a written plan. Owner decision; not authorised.
