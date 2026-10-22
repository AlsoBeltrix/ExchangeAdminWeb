# aud-a23k: Migration - a failed report fetch is served as the report text, and a failed cloud mailbox size read shows 0 GB

**Severity**: LOW/MEDIUM - a failed read presented as data. (1) `GetMigrationUserReportAsync` turns a failed
fetch into report text ("Error retrieving report: ..."); the page stores that text as the report, shows it
as such, and its activity completes as succeeded. (2) For moves back on-prem, a failed
`GetCloudMailboxSizeAsync` is shown as 0 GB, so a size-based decision reads an empty mailbox.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23h/a23i agent, 2026-10-22, candidates 2 and 7 of its hand-back)

## Evidence

At `ab5361c8`: `Services/MigrationService.cs` `GetMigrationUserReportAsync` (the catch that returns text),
`GetCloudMailboxSizeAsync` (the 0 on failure) and the on-prem move path that consumes it;
`Components/Pages/Migration.razor` (the report pane and the size column; line-keyed by `ClickGateRegistry`,
4377 lines, edit in place only).

## Approach (to be written by the implementing agent before code)

- The report fetch returns a typed outcome (report text, or failed with a reason category); the page shows
  "the report could not be retrieved: <reason>" outside the report body and completes its activity Failed.
- The size read returns an "unreadable" marker (the `ReportedCount` shape) rather than 0; the page shows
  "could not be read" and any decision that depends on the size is refused with that reason.
- Tests through `MigrationStubExchange`; mutations at the call site: text-as-report again; 0 again.

## Guard proof

Pending.
