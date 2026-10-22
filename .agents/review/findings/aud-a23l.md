# aud-a23l: Migration - three message leftovers: the "running command stopped" prefix in callers that format `ex.Message` themselves, "is a protected principal" when the protection check failed, and Undetermined filed under result "Ineligible"

**Severity**: LOW - operator messages and audit filing. (1) `FindMigrationUserBatchAsync`,
`GetMigrationUserReportAsync` and the page's own catches format `ex.Message` themselves, so they still show
PowerShell's "The running command stopped because the preference variable ..." prefix that aud-a23i removed
from `RunAsync`. (2) When every target is excluded because the protection check FAILED, `CreateMigrationBatchAsync`
says the target "is a protected principal". (3) `AuditService.LogMigrationCheck` files an Undetermined
verdict under result "Ineligible" (the `status` field does say Undetermined), so the Admin Event Log's
result filter cannot find it.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23h/a23i agent, 2026-10-22, candidates 3, 4 and 8 of its hand-back)

## Evidence

At `ab5361c8`: `Services/MigrationService.cs` (the two methods; the "is a protected principal" text);
`Components/Pages/Migration.razor` catches (line-keyed, edit in place only); `Services/AuditService.cs`
`LogMigrationCheck`; `Components/Pages/AdminEventLog.razor` result filter (NotYetConverted).

## Approach (to be written by the implementing agent before code)

- The three sites use the base's error-text helper (aud-a23i) instead of raw `ex.Message`.
- "could not be verified as unprotected: <reason>; nothing was written" when the check failed; "is a
  protected principal" only when it answered.
- A third result value for Undetermined in `LogMigrationCheck` and in the Admin Event Log filter.
- Tests through `MigrationStubExchange` and the audit capture; mutations at the call site: raw
  `ex.Message` again at one site; the single wording again; "Ineligible" again.

## Guard proof

Pending.
