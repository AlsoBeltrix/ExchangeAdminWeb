# kd-1: Message Trace detail exports are listed and downloadable by any policy holder, not only their submitter

**Severity**: MEDIUM - an authorization gap. `Services/MessageTraceExportListing.cs` `GetExports` lists every operator's finished exports and `TryDownloadAsync` checks the ticket and the export's state but never the submitter, so any operator holding the Message Trace policy can download any other operator's detail export (message metadata for addresses they never searched).
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

At `4498da49`: `Services/MessageTraceExportListing.cs` `GetExports` (about line 119) and `TryDownloadAsync` (about 173). Trigger: operator B opens the exports list after operator A's export finished. Predicted failure: B sees and downloads A's export; the audit row, if any, names B as a download of a file B did not request.

## Approach (to be written by the implementing agent before code)

- Exports carry their submitter; the listing shows an operator only their own unless a separately granted permission (an existing admin alias, never a new one invented here) widens it; `TryDownloadAsync` refuses a non-submitter with an audited denial.
- Mutation: the submitter comparison removed.

## Guard proof

Pending.