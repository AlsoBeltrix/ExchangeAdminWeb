# aud-a68: Recipient Lookup and Migration leftovers - an on-premises archive is never shown, three raw-message wordings, an unaudited thrown lookup, and a mislabelled bulk batch activity

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) `RecipientLookupService`
sets `ArchiveSizeGB` from the on-premises read but never `ArchiveEnabled` (only the cloud read sets it), so
the page shows "Archive: Disabled" and hides the archive rows for every on-premises archive; an on-premises
archive that could not be read also shows "Disabled" beside its warning (confirmed by a stand-in probe).
(2) The statistics warning is built from `ex.Message` and carries the "The running command stopped ..."
prefix (the aud-a23l class); the catch also takes a cancellation; a statistics read with no row shows "N/A"
with no warning. (3) `r.Error = ex.Message` for a missing recipient carries the same prefix; the friendly
"Recipient '<x>' not found." is reachable only by an empty answer. (4) `RecipientLookup.razor` `RunLookup`'s
catch writes no audit row when the lookup throws, and the activity ends as "This did not finish." though the
page says all lookups are logged. (5) `Migration.razor` labels every `ExecuteBulkBatchAction` activity
"Removing completed batches" and hard-codes the busy reason "clearing", though the executor runs Delete,
Complete, Schedule, Stop, Remove Completed and Resume (line-keyed page; edit in place).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a55/a54 agent, 2026-10-23, candidates 1-5 of its hand-back)

## Evidence

At `671d9bc9`: `Services/RecipientLookupService.cs` 171-172 and 114 (`ArchiveEnabled`), 90-93 (the statistics
warning), 134-138 and 28 (`r.Error`); `Components/Pages/RecipientLookup.razor` 92-98, 208-232, 20;
`Components/Pages/Migration.razor` 3520-3525 and the calls at 3412-3479 (line-keyed, 4377 lines).

## Approach (to be written by the implementing agent before code)

- The on-premises read sets `ArchiveEnabled` (and the unreadable marker when it failed); the page shows the
  archive rows for either location.
- The three wordings go through the aud-a23i error-text helper; the catch excludes cancellation; a no-row
  statistics read is a warning.
- `RunLookup`'s catch writes the lookup's audit row as Failed and completes the activity with the error.
- The bulk batch activity label and busy reason come from the action (in place).
- Mutations at the call site: `ArchiveEnabled` not set again; raw `ex.Message` at one site; the audit row
  dropped; the fixed label again.

## Guard proof

Pending.

## Also held (codex, mig-batch-9, 2026-10-23) - LOW

(6) `ExchangeAdminWeb.Tests/RecipientLookupReportedTimeTests.cs` 103: the stand-in's archive read answers no row,
which aud-a54 now treats as unreadable, so those page-render tests pass while rendering an archive warning a
no-archive mailbox would not; model "no archive" with a recognised not-found shape and assert `Warnings` empty.
