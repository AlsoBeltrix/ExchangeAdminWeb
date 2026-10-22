# aud-a23i: Migration - an identity-less mailbox row is a targetable "Unknown", a throwing row loses the list, failed loads report progress as success, two generic error texts, and unzoned export file names

**Severity**: LOW. (1) `GetMigrationBatchUsersAsync`: a mailbox row with no Identity becomes
"Unknown", which an action can target; a row whose getter throws loses the whole mailbox list (the
aud-a23f shape on the mailbox list). (2) `LoadBatchList` and every load on the Migration page
complete their progress activity as `true` on failure, so the status bar reports success after a
failed load. (3) `ExchangeServiceBase.Invoke` fails with "An unknown error occurred." whenever
`HadErrors` is set with an empty error stream, which a nested suppressed error causes (not seen
live). (4) Under `-ErrorAction Stop` the error stream is empty, so app-wide `RunAsync` failure
messages carry the "The running command stopped because..." prefix instead of the cmdlet's own
message (aud-a23g unwrapped it for one path only). (5) The Migration export file names use the
server's clock with no zone.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23g agent, 2026-10-22, candidates 3 to 7 of its hand-back)

## Evidence

At `cafef918`: `Services/MigrationService.cs` `GetMigrationBatchUsersAsync` (identity default,
per-row throw); `Components/Pages/Migration.razor` `LoadBatchList` and the other loads' progress
completion; `Services/ExchangeServiceBase.cs` `Invoke` (the empty-stream branch) and `RunAsync`'s
failure message; the export file-name composition.

## Approach (to be written by the implementing agent before code)

- Mailbox rows: the aud-a23f shape - an identity-less row is listed as unreadable (not targetable);
  a throwing row is listed with its identity as far as read and the reason; the rest of the list
  loads.
- Progress: a failed load completes its activity as not succeeded with the failure text (the
  aud-a26h drain precedent); every load on the page.
- `ExchangeServiceBase`: when `HadErrors` is set with an empty stream, report that shape honestly
  ("a nested command reported an error it did not surface") rather than "unknown"; unwrap
  `ActionPreferenceStopException.ErrorRecord` in `RunAsync`'s failure path once, for every caller (the
  aud-a23g part-1 unwrapping moves into the base).
- Export file names in UTC with a `Z` suffix.
- Tests: identity-less and throwing mailbox rows; a failed load's activity outcome; the base class
  message shapes; a file name. Mutations at the call site per part. `Migration.razor` line-keyed
  entries: edit in place or re-anchor per the playbook.

## Guard proof

Pending.
