# aud-a23g: A failed Get-MigrationBatch call shows as "No migration batches found", a bad count fails the whole mailbox load, and two Migration times are still unzoned

**Severity**: MEDIUM for the first part (a silent empty list, Known Failure Class 2: a failed read
reported as "nothing there"), LOW for the rest. (1) `GetMigrationBatchesAsync`'s outer catch turns a
failed `Get-MigrationBatch` call (connection, permission, throttling) into an empty result, and the
page shows "No migration batches found" as if the tenant had none. (2) `GetMigrationBatchUsersAsync`
reads `ItemsSynced`/`ItemsSkipped` with `Convert.ToInt64`; a value it cannot convert fails the whole
mailbox load (shown, not silent, but one bad row loses the list - the aud-a23f shape). (3) The
Migration mailbox report's "Taken <time>" is the server's clock with no zone named (aud-a23e fixed
the other Migration times). (4) `ScheduledCompletionUtc` treats a zone-less `CompleteAfter` as
server-local time; no page shows it yet, but the model states an instant it does not know.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23f/a23e agent, 2026-10-22, its four candidates)

## Evidence

At the aud-a23f/a23e branch tip `022014aa`: `Services/MigrationService.cs` `GetMigrationBatchesAsync`
(outer catch returning an empty list), `GetMigrationBatchUsersAsync` (`Convert.ToInt64` reads);
`Components/Pages/Migration.razor` the mailbox report's "Taken" line (line-keyed `ClickGateRegistry`
entries; edit in place); `Models/MigrationModels.cs` `ScheduledCompletionUtc`.

## Approach (to be written by the implementing agent before code)

- The batch list result distinguishes "read failed" from "none": a failed call returns a failure the
  page shows as an error banner ("The migration batches could not be read: <reason>"), never the
  empty-state text; the CSV export is disabled or marked. Test: a throwing `Get-MigrationBatch` shows
  the error, not "No migration batches found". Mutation: swallow into empty again.
- Mailbox counts through `ReportedCount.Read` (aud-a23f) so one bad value is a marker in its cell,
  not a lost list.
- "Taken" through `ShowReported` in UTC labelled UTC (the Migration page's aud-a23e choice).
- `ScheduledCompletionUtc`: a zone-less `CompleteAfter` yields null plus a flag saying the zone was
  not reported, as `CompleteAfterUnreadable` does for an unreadable one; no server-local guess.
- `Migration.razor` stays 4377 lines or is re-anchored per the playbook. Mutations at the call site
  for each part.

## Guard proof

Pending.
