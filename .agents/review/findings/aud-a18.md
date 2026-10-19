# aud-a18: a failed calendar read renders as "No calendar delegates" and audits as success

**Severity**: HIGH - the operator is shown a confident, wrong answer about who can access
someone's calendar, and the audit trail agrees with it.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 18 of the 2026-10-16 audit, verified against the code.

`Services/DelegationReportService.cs` wraps the calendar section in its own catch:

```csharp
catch (Exception ex)
{
    _logger.LogWarning(ex, "Could not retrieve calendar permissions for {Email}", emailAddress);
}
```

**The catch itself is right** - a mailbox can legitimately have no calendar folder, and
failing the whole report for that would be wrong. What was wrong is that it recorded nothing.
`result.Calendar` stayed empty and `result.Error` stayed null, so:

- `Components/Pages/DelegationReport.razor` rendered the `else` branch, "No calendar
  delegates."
- the page completed its progress activity as a success, and
  `Audit.LogLookupAction(..., success: result.Error == null, ...)` wrote a clean success row.

Known Failure Class 2 in `.agents/repo-guidance.md`: a failure that reads as a clean result.

## Predicted observable failure

An operator checks who has delegate access to an executive's calendar during an access review.
Exchange throttles or the folder read fails. The page says there are no calendar delegates,
the audit records a successful lookup, and the review concludes the calendar is not shared -
when nobody actually looked.

## Approach

`DelegationReportResult` gains `CalendarError`, separate from `Error` on purpose: the full
access and send-as sections are still valid and still worth showing. It says one section is
**unknown**, not that the report failed.

- The service records the first line of the exception there.
- The page renders a third state between "has delegates" and "none": a warning saying the
  section could not be read.
- The page's audit and progress completion use `result.Error ?? result.CalendarError`, so a
  report missing a section it could not read is no longer recorded as a success.

## Files changed

- `Models/LookupModels.cs`
- `Services/DelegationReportService.cs`
- `Components/Pages/DelegationReport.razor`
- `ExchangeAdminWeb.Tests/DelegationReportCalendarFailureTests.cs`

## Guard proof

**Behaviour, not a source scan.** The real service runs against a real runspace with the
Exchange cmdlets replaced by PowerShell functions - the harness shape already used by
`MigrationMailboxLoadTests` - so the service's own try/catch, result mapping and error text
are exercised. Only the network is absent.

| Test | What it pins |
| --- | --- |
| `AFailedCalendarRead_IsReportedAsUnknown_NotAsNoDelegates` | empty list AND an error, with the rest of the report still intact and `Error` still null |
| `AMailboxThatGenuinelyHasADelegate_StillReportsIt` | the fix did not turn a working read into an error |

The second matters as much as the first: a guard that only checks the failure path is
satisfied by code that always reports failure.

| Mutation | Result |
| --- | --- |
| swallow the exception again (delete the `CalendarError` assignment) | FAIL - `AFailedCalendarRead_...`, and only it |

Source restored byte-identical (md5 verified), touched, rebuilt.

`DelegationReport.razor` is `tier 4, not approved` in `ClickGateRegistry`, so the added lines
needed no re-anchor. Checked, not assumed.

Gate: Release 0 errors; suite 4340 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. Module version 1.0.1 -> 1.1.0 (the result contract gained a field).

## Known gaps

- The page rendering is not tested - no harness drives this component - so "the warning is
  actually shown" rests on reading the markup. The service contract underneath it is tested.
- `A17` is the same class in `M365GroupManagementService` and `NamedLocationsService` and is
  NOT covered here. Those return empty or truncated collections after an HTTP failure, which
  is the same lie in a different shape.
- Nothing here ran against a real tenant.

## Closeout

Fixed directly on `master`. Independent review pending.
