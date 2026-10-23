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

### As implemented (implementer, 2026-10-23, branch `fix/a68-kd2` forked at `eb017102`; lines at `eb017102`)

One commit per numbered part. Module-scoped: Recipient Lookup (parts 1-4, 6) and Migration (parts 5, 7); the two
module bumps are in their own commit; `ExchangeAdminWeb.csproj` is not edited.

1. **The on-premises archive.** `GetOnPremMailboxSizeAsync` cannot say "there is an archive" today: Exchange's
   not-found and an archive that is empty both come back as 0 GB with no marker (`Services/ExchangeServiceBase.cs`
   666-683), so setting `ArchiveEnabled` from a size above zero would call an empty archive "Disabled". So:
   - `Services/ExchangeServiceBase.cs` 579-582: `MailboxSizes` gains `ArchiveFound` (init-only, default false) - the
     archive read answered with a row. 666-683: the on-premises archive read keeps its rows and sets it; nothing else
     in the read changes. Additive: Migration's quota check (`Services/MigrationService.cs` 203-228) and the Exchange
     Online size read (1644-1686) do not read or set it.
   - `Services/RecipientLookupService.cs` 169-174: for an on-premises mailbox the on-premises read decides the
     archive - `ArchiveEnabled` from `ArchiveFound`, `ArchiveUnreadable` from its marker (so the page's Archive row,
     `Components/Pages/RecipientLookup.razor` 92, reads "Could not be read" and never "Disabled" beside its warning),
     `ArchiveSizeGB` only for a found archive whose size was read. 176-179: a throw out of that read as a whole sets the
     archive's marker too. The page needs no change: its archive rows (93-98) follow `ArchiveEnabled`.
   - `Models/LookupModels.cs` 119-120: the two markers' comment says the archive's can come from the on-premises read.
   - Test stand-in: `ExchangeAdminWeb.Tests/MigrationStubExchange.cs` 343-349, the on-premises Get-MailboxStatistics
     gains `Behaviour["OnPremStatisticsCancelled"]` (the cloud stand-in's `StatisticsCancelled`), the one failure the
     size read's own catches let through, to reach the throw-as-a-whole path.
   - Mutation: `ArchiveEnabled` not set from the on-premises read (the record's); extras: the marker not copied; the
     size read's `ArchiveFound` not set.

## Guard proof

Protocol, every part: the file's md5 (as checked out, CRLF) before, the fix reverted or mutated, the filtered tests run
and the failures named, the file restored byte-exact from a copy outside the tree with a fresh mtime, md5 equal, the
same filter green again. Drivers in the session scratchpad (`work/runf.sh`, `work/mut.sh`). No Exchange, no directory:
every cmdlet is a stand-in in an in-process runspace.

**Part 1** - new `ExchangeAdminWeb.Tests/RecipientLookupOnPremArchiveTests.cs`, 4 tests (9 cases), the real service over
`MigrationStubExchange` (a mail user in Exchange Online, a mailbox on-premises) and the page's compiled render tree:
`AnOnPremArchive_IsEnabled_AndItsRowsAreShown` (Enabled, "Archive Size 0.50 GB", never "Disabled");
`AnEmptyOnPremArchive_IsEnabled_AtZero` (found, not a size above zero, says there is one);
`NoOnPremArchive_IsDisabled` (Exchange's not-found by category, type, identifier: Disabled, no rows, no warning);
`AnOnPremArchiveThatCannotBeRead_IsCouldNotBeRead_NeverDisabled` (the read fails, answers no row, a size that does not
parse, the size read throwing as a whole: "Could not be read" beside the warning, never "Disabled"). Filter
`RecipientLookup|ExchangeServiceBaseOnPremRead`, 77 tests.

| Round | File | md5 before / mutated | Failed (of 77) | Restored |
| --- | --- | --- | --- | --- |
| The fix reverted (service and base back to `eb017102`) | both | `07012f76...` / `d63fafcf...`; `40e41bb3...` / `653b18af...` | 9: every new case (`NoOnPremArchive_IsDisabled` on `ArchiveSizeGB`: 0 for no archive before, null now) | md5 equal; 77/0 |
| M1 (the record's): `ArchiveEnabled` not set from the on-premises read | service | `07012f76...` / `21b885f6...` | 2: `AnOnPremArchive_IsEnabled...`, `AnEmptyOnPremArchive...` | md5 equal; 77/0 |
| M1b: the on-premises marker not copied | service | `07012f76...` / `1421d9a1...` | 3: the cannot-be-read cases "fails", "no row", "does not parse" | md5 equal; 77/0 |
| M1c: the size read never sets `ArchiveFound` | base | `40e41bb3...` / `bc1afdbc...` | 4: the two new found cases, and aud-a23o's `RecipientLookup_AnOnPremSizeThatCannotBeRead...`, `RecipientLookup_ReadableOnPremSizes...` | md5 equal; 77/0 |
| M1d: the throw-as-a-whole catch sets no marker | service | `07012f76...` / `dfaa57f9...` | 1: the "throws as a whole" case | md5 equal; 77/0 |

On the part-1 commit's tree: full suite 6702 passed / 0 failed / 0 skipped (6693 on master at `eb017102`, plus these 9).

## Also held (codex, mig-batch-9, 2026-10-23) - LOW

(6) `ExchangeAdminWeb.Tests/RecipientLookupReportedTimeTests.cs` 103: the stand-in's archive read answers no row,
which aud-a54 now treats as unreadable, so those page-render tests pass while rendering an archive warning a
no-archive mailbox would not; model "no archive" with a recognised not-found shape and assert `Warnings` empty.

## Also held (codex, misc-batch-1, 2026-10-23) - LOW

(7) `Components/Pages/Migration.razor` 3536 and 3702: the bulk executors' authorization-denial audit rows pass no
`extra`, so they lack `mutationAttempted: false` (the single-action denials through `MigrationService.RecordDenial`
carry it); route both through `RecordDenial` and add the two denied-authorization tests.
