# aud-a55: Migration - approving a mailbox that is not in its own batch writes without reading any status, the bulk executors' "no list loaded" guards show nothing, and a cloud archive read with no rows reads as no archive

**Severity**: MEDIUM (part 1), LOW (parts 2 and 3). (1) `ApproveMigrationUserAsync` for a mailbox in a
batch of many (no batch named for it) runs `Set-MigrationUser -CompleteAfter`, `Set-MoveRequest` and
`Resume-MoveRequest` without reading the mailbox's or its batch's status; only the UI planner (Approve
offered on NeedsApproval) gates it, so a stale page or a second operator can approve a mailbox whose state
no longer allows it (the aud-a23r class for the many-user case). (2) `Migration.razor`'s bulk executors
return silently on "no list loaded" guards. (3) `MigrationService.GetCloudMailboxSizeAsync` reads an archive
read that returns no rows and no error as no archive (minor: no decision reads the cloud size).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23r/o/q/p agent, 2026-10-22, candidates 3, 4 and 5 of its hand-back)

## Evidence

At `d690a6c1`: `Services/MigrationService.cs` `ApproveMigrationUserAsync` from around line 1308 (the writes at
around 1387), `GetCloudMailboxSizeAsync` (the archive read's no-row case); `Components/Pages/Migration.razor`
around 3545 and 3713 (the guard returns) - line-keyed by `ClickGateRegistry`, 4377 lines, edit in place only;
`Services/MigrationBatchActionPlanner.cs` (what Approve requires of a migration user's status).

## Approach (to be written by the implementing agent before code)

- The many-user approval reads the migration user's status (`Get-MigrationUser`) before any write and
  refuses, naming the status and that nothing was changed, unless the planner's Approve rule accepts it (the
  planner is the one owner; call it); a failed read refuses (the aud-a23h shape).
- The two guard returns set the banner and the aud-a23m local ("no mailbox list is loaded; nothing was done")
  through the aud-a23q refusal helpers; not audited (nothing attempted).
- The archive read with no rows and no error is "could not be read: Exchange returned no statistics row"
  (the aud-a23k marker), not no archive.
- Tests through `MigrationStubExchange`; mutations at the call site: the status read removed; one guard
  return silent again; no-rows as no-archive again.

## Guard proof

Pending.
