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
**Status**: Merged to master `44fb74aa`; codex mig-batch-10 raised kd-17 (MEDIUM) against part (7), clean on parts 1-6; closes when kd-17 lands
**Branch**: `fix/a68-kd2` (forked at `eb017102`; worktree `D:\source\ExchangeAdminWeb-wt\a68-kd2`)
**Commit**: part 1 `3ba9dfa4`, 2 `1963cb79`, 3 `28256d97`, 4 `37a2696b`, 6 `6f0d6dfd`, 5 `ffe2d99b`, 7 `39a878cd`; the module bumps and this record's close: the commit that completes this record (`git log -1 -- .agents/review/findings/aud-a68.md`)
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
2. **The mailbox statistics warning** (`Services/RecipientLookupService.cs` 74-93): the catch (90-93) words its warning
   through `ResolveFailure(ex).primary` (aud-a23i's helper; "Could not retrieve mailbox statistics: <the cmdlet's own
   words>.") and takes no cancellation (`when (!IsCancellation(ex))`, aud-a23g's rule - one reaches the lookup's own catch,
   as the archive read's already does); an answer with no row (81, `stat == null`) is the warning "Could not retrieve
   mailbox statistics: Exchange returned no mailbox statistics." (the marker the Exchange Online size reader gives the same
   answer) where every figure read "N/A" with nothing said. Tests: `ExchangeAdminWeb.Tests/RecipientLookupFailureTests.cs`.
   Mutation: raw `ex.Message` again (the record's); extras: the cancellation filter dropped; the no-row warning dropped.
3. **The lookup's own error** (`Services/RecipientLookupService.cs` 134-138; the friendly words at 26-30): the block's
   catch sets `r.Error` to "Recipient '<x>' not found." when Exchange said there is no such recipient (`IsNotFound`, read
   from the error's structure - the words only an empty answer reached), else to `ResolveFailure(ex).primary`, the
   cmdlet's own words without the Stop prefix. Every later read in the block takes its own not-found (Get-Mailbox through
   `ReadUnlessNotFound`, the archive read's `IsNotFound` catch, the statistics catch), so Get-Recipient's is the only one
   that reaches this catch. ASSUMPTION, unverified (aud-a23h's, no live call): Exchange Online reports a missing recipient
   from Get-Recipient in a shape `IsNotFound` reads; if not, the error reads in Exchange's own words - visible, never a
   guess. Mutation: raw `ex.Message` again (the record's); extra: the not-found reading dropped.
4. **A lookup that throws** (`Components/Pages/RecipientLookup.razor` `RunLookup`, 208-232): only the service call (220)
   is wrapped; a throw becomes the lookup's result with `Error` = `ExchangeServiceBase.ResolveFailure(ex).primary` (226,
   the third raw wording), and then, on every path, the activity completes with the outcome (221) and the audit row is
   written (222) - Success, or Failed with that error. The activity still opens before the call and completes after it,
   as `ProgressRegistry`'s `RunLookup` entry requires. The audit write has its own try: a throw from it is logged and
   cannot turn a lookup that was made into a failed one (the Constitution's audit rule; before, the shared catch would
   have shown it as the lookup's error). The page is not line-keyed (`ClickGateRegistry.NotYetConverted`, tier 4).
   Test stand-in: `MigrationStubExchange.RecipientLookup(bool connects = true)` - false, the Exchange Online connection
   cannot be opened, so the service call throws before any cmdlet - and `MigrationStubExchange.ConnectFailure`, what that
   connect throws: the live connect runs Connect-ExchangeOnline under -ErrorAction Stop and the pool rethrows its
   exception as it is (`Services/ExoConnectionPool.cs` 791-796, 829-835, 374-379), so a failed sign-in reaches this catch
   as PowerShell's ActionPreferenceStopException, "The running command stopped because ..." - the third wording, and the
   one the service's own catch never sees. Mutation: the audit row dropped (the record's); extras: the activity left to
   end "This did not finish."; raw `ex.Message` again.
5. **The bulk batch run's name** (`Components/Pages/Migration.razor` `ExecuteBulkBatchAction` 3509-3668 - the busy
   reason at 3520, the activity label at 3525, the clear at 3664 - the busy text at 474, the field at 1635): the run is
   named from its action as a single action's activity is ("<action> on <target>", 3295-3296):
   "<auditAction> on N batch(es)", N the batches it will run. That name is the busy reason (`actionInProgress`), the
   activity's label, and - through a new `bulkBatchActivity` field, declared on `actionInProgress`'s line and cleared
   beside it in the finally - the busy text over the batch list ("<name> and reloading...", where it read "Removing
   completed batches and reloading..." whatever ran). A field, not a test of `actionInProgress`'s value, because a single
   batch action sets that to its batch's name through its own reload. All in place: the page keeps 4377 lines and no
   registered line moves (474 is text, not a control). The two `MigrationVerdictAndWriteOutcomeTests` that pinned
   "Removing completed batches" for a Stop run pin the run's own name. Tests: new
   `ExchangeAdminWeb.Tests/MigrationBulkBatchActivityTests.cs`. Mutation: the fixed label again (the record's); extra:
   the busy text fixed again.
6. **The ReportedTime tests' stand-in** (`ExchangeAdminWeb.Tests/RecipientLookupReportedTimeTests.cs` 104 and 149;
   test-only, no module behaviour): the stand-in's archive read writes Exchange's own not-found (category
   ObjectNotFound) where it answered no row - since aud-a54 no row is "could not be read", so the mailbox those tests
   render had an archive warning a mailbox without an archive never shows; and `LookedUp` asserts `Warnings` empty beside
   `Error` null, so every page-render test there now checks it. Proof: the stand-in's old answer back.
7. **The bulk denials** (`Components/Pages/Migration.razor` 3531-3543 in `ExecuteBulkBatchAction` and 3697-3711 in
   `ExecuteBulkMailboxAction`; `Services/MigrationService.cs` `RecordDenial` 977-992, called, not changed): each denial
   returns through `MigrationService.RecordDenial`, in the single actions' one-line shape (3301-3302). The row is the
   same `<auditAction>_Denied` - its target the run's notification action, as before; the ticket; "MigrationManage
   authorization denied" - now with `mutationAttempted: false`; a failed audit write is still logged and swallowed
   (inside `RecordDenial`). The banner and the activity say "Not authorized to <the run>; nothing was done." (the batch
   run's name from part 5; the mailbox run's "<auditAction> on N mailbox(es)"), where they said "Authorization denied.".
   The page shrinks 24 lines, all below the last line-keyed entry (1540): re-anchored per
   `.agents/playbooks/clickgate-reanchor.md` - the diff's only hunks start below 1540, so every registered line maps to
   itself - and `ExpectedLineCount` 4377 -> 4353. `MigrationStatusPageTests.TheBulkExecutorReChecksAuthorizationAndAuditsADenial`
   looked for the literal "_Denied" in the batch executor's body; it now requires the `MigrationService.RecordDenial(`
   call, comments stripped. Tests: `MigrationDenialTests`, the two denied-authorization cases. Mutation: each executor's
   denial back to its old inline row, one at a time - a row without `mutationAttempted` again.

## Files changed

- `Services/ExchangeServiceBase.cs` (part 1): `MailboxSizes.ArchiveFound`, set by the on-premises archive read. Additive;
  nothing else reads it.
- `Services/RecipientLookupService.cs` (parts 1-3); `Models/LookupModels.cs` (part 1, comment only).
- `Components/Pages/RecipientLookup.razor` (part 4): `RunLookup`, 233 -> 254 lines; not line-keyed
  (`ClickGateRegistry.NotYetConverted`), and `ProgressRegistry`'s one pin unchanged.
- `Components/Pages/Migration.razor` (parts 5 and 7): part 5 in place (474, 1635, 3520, 3525, 3664 at `eb017102`); part
  7 the two denials, 4377 -> 4353 lines, every hunk below the highest line-keyed entry (1540), so no key moved;
  `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` `ExpectedLineCount` 4377 -> 4353.
- `Modules/ModuleCatalog.cs`: Recipient Lookup 1.0.4 -> 1.1.0, Migration 1.23.3 -> 1.24.0 (their own commit; covers kd-2
  too). `ExchangeAdminWeb.csproj` not edited.
- Tests, new: `RecipientLookupOnPremArchiveTests.cs`, `RecipientLookupFailureTests.cs`, `MigrationBulkBatchActivityTests.cs`.
  Changed: `MigrationStubExchange.cs` (`OnPremStatisticsCancelled`; `RecipientLookup(bool connects)`; `ConnectFailure`),
  `RecipientLookupReportedTimeTests.cs` (part 6), `MigrationVerdictAndWriteOutcomeTests.cs` (part 5's two labels),
  `MigrationDenialTests.cs` (part 7), `MigrationStatusPageTests.cs` (part 7's tripwire).

## Guard proof

Protocol, every part: the file's md5 (as checked out, CRLF) before, the fix reverted or mutated, the filtered tests run
and the failures named, the file restored byte-exact from a copy outside the tree with a fresh mtime, md5 equal, the
same filter green again. Drivers in the session scratchpad (`work/runf.sh`, `work/mut.sh`). No Exchange, no directory:
every cmdlet is a stand-in in an in-process runspace. Each code commit's full suite ran on its own tree; many tests read
the pages from disk as they run, so where a run overlapped a later edit (kd-2's; part 5's first) the page-reading
classes had also run on the exact tree first, and part 5 was re-run clean.

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

**Part 2** - new `ExchangeAdminWeb.Tests/RecipientLookupFailureTests.cs`, its part-2 tests, the real service over
`MigrationStubExchange` (a cloud mailbox): `AStatisticsReadThatFails_IsAWarningInTheCmdletsOwnWords` (an error written
under Stop: exactly "Could not retrieve mailbox statistics: the mailbox database is offline."); `AStatisticsReadWithNoRow_IsAWarning`
(exactly "... Exchange returned no mailbox statistics."); `ACancelledStatisticsRead_IsNotAStatisticsWarning` (the lookup
fails; no statistics warning); `StatisticsThatRead_GiveTheirFigures_AndNoWarning` (the control). Same filter, 81 tests.

| Round | md5 before / mutated | Failed (of 81) | Restored |
| --- | --- | --- | --- |
| The fix reverted (the service back to part 1's) | `2032c29d...` / `07012f76...` | 3: the three finding tests; the first read "... statistics: The running command stopp..." | md5 equal; 81/0 |
| M2 (the record's): raw `ex.Message` again | `2032c29d...` / `76046751...` | 1: `AStatisticsReadThatFails...` | md5 equal; 81/0 |
| M2b: the catch takes a cancellation again | `2032c29d...` / `633b8917...` | 1: `ACancelledStatisticsRead...` | md5 equal; 81/0 |
| M2c: a no-row answer says nothing again | `2032c29d...` / `184657aa...` | 1: `AStatisticsReadWithNoRow...` | md5 equal; 81/0 |

On the part-2 commit's tree (`1963cb79`): full suite 6706 passed / 0 failed / 0 skipped.

**Part 3** - `RecipientLookupFailureTests`' part-3 tests: `ARecipientReadThatFails_IsTheErrorInTheCmdletsOwnWords` (an error
Get-Recipient writes under Stop: `Error` exactly "the directory is busy"); `ARecipientThatDoesNotExist_IsNotFound`
(Exchange's not-found by category, type, identifier: exactly "Recipient 'alice@contoso.test' not found.", and nothing but
Get-Recipient asked). Same filter, 85 tests.

| Round | md5 before / mutated | Failed (of 85) | Restored |
| --- | --- | --- | --- |
| The fix reverted (the service back to part 2's) | `0172a003...` / `2032c29d...` | 4: every part-3 case | md5 equal; 85/0 |
| M3 (the record's): raw `ex.Message` again | `0172a003...` / `7694e901...` | 1: `ARecipientReadThatFails...` | md5 equal; 85/0 |
| M3b: the not-found reading dropped | `0172a003...` / `f32e1788...` | 3: the three not-found shapes | md5 equal; 85/0 |

On the part-3 commit's tree (`28256d97`): full suite 6710 passed / 0 failed / 0 skipped.

**Part 4** - `RecipientLookupFailureTests`' part-4 tests, the page's own `RunLookup` with its services set by hand, a
recording progress channel and the audit file read back: `ALookupThatThrows_IsAuditedAsFailed_AndItsActivityEndsWithTheError`
(the connection cannot be opened: the error, one Lookup row Failed with it, the activity (false, the error), not left
loading); `ALookupThatThrowsUnderStop_IsShownInTheCmdletsOwnWords` (a real ActionPreferenceStopException from a stand-in
writing an error under Stop: shown, audited and ended in the error's own words); `ALookupThatAnswers_IsAuditedWithItsOutcome`
(found, not found: the controls); `AnAuditWriteThatThrows_LeavesTheLookupsResult` (no audit service: the lookup keeps its
result). Filter `RecipientLookup|ExchangeServiceBaseOnPremRead|ProgressRegistry`, 125 tests.

| Round | md5 before / mutated | Failed (of 125) | Restored |
| --- | --- | --- | --- |
| The fix reverted (the page back to `eb017102`'s) | `3e9ecada...` / `45c8ccf3...` | 3: both thrown cases, and the audit one (the NullReferenceException shown as the lookup's error) | md5 equal; 125/0 |
| M4 (the record's): the audit row dropped | `3e9ecada...` / `1e64502f...` | 4: both thrown cases, both controls | md5 equal; 125/0 |
| M4b: the activity not completed ("This did not finish.") | `3e9ecada...` / `e2390964...` | 5: every part-4 case | md5 equal; 125/0 |
| M4c: raw `ex.Message` again | `3e9ecada...` / `3d2ecd65...` | 1: `ALookupThatThrowsUnderStop...` | md5 equal; 125/0 |

On the part-4 commit's tree (`37a2696b`): full suite 6714 passed / 1 failed / 0 skipped - the one failure
`SettingsStoreBusyTests.AdminSettings_ASecretIdCompareThatFindsTheStoreBusy_IsRefused_SaysSo_WritesNothing_AndARetrySaves`,
its wall-clock bound ("the save waited 11.26 s for the busy store"; the bound is twice the busy timeout less a second)
missed while three to five other agents' test hosts ran on this machine; Admin Settings is untouched here, and that
class alone passed 19/19 straight after on the same tree.

**Part 6** - test-only. `RecipientLookupReportedTimeTests`, its 14 cases through `LookedUp`; filter `RecipientLookup`,
52 tests.

| Round | md5 before / mutated | Failed (of 52) | Restored |
| --- | --- | --- | --- |
| The stand-in's old answer back (no row for the archive) | `19306aa3...` / `fe4a8ab8...` | 14: every test through `LookedUp`, on the new `Warnings` assertion (the archive warning the no-row answer raises) | md5 equal; 52/0 |

The part-6 commit (`6f0d6dfd`) was run on the filter above (the change is confined to that class); the next commit's tree
(kd-2, `14bf6a4f`) carries it and ran the full suite: 6720 passed / 0 failed / 0 skipped.

**Part 5** - new `ExchangeAdminWeb.Tests/MigrationBulkBatchActivityTests.cs`, 2 tests (7 cases), the page's own handlers
over `MigrationStubExchange` with a recording progress channel: `EachBulkBatchAction_NamesItsActionInItsActivity` (each of
the six callers - Delete, Complete, Schedule, Stop, Remove Completed, Resume - opens its activity as "<its cmdlet's
action> on 1 batch(es)", never "Removing completed batches"); `TheBusyText_NamesTheRunUnderWay` (the batch list rendered
from inside a run reads "StopMigrationBatch on 1 batch(es) and reloading...", and no busy text after it). And the two
`MigrationVerdictAndWriteOutcomeTests` that pinned the old label now pin the run's name. Filter
`MigrationBulkBatchActivityTests|MigrationVerdictAndWriteOutcomeTests|MigrationStatusPageTests|ClickGate`, 450 tests.

| Round | md5 before / mutated | Failed (of 450) | Restored |
| --- | --- | --- | --- |
| The fix reverted (the page back to kd-2's `14bf6a4f`) | `a127f9be...` / `7fd3115d...` | 10: the 7 new cases and the 3 bulk-batch cases of `MigrationVerdictAndWriteOutcomeTests` | md5 equal; 450/0 |
| M5 (the record's): the fixed label again | `a127f9be...` / `d6b70665...` | 9: the six callers and those 3 | md5 equal; 450/0 |
| M5b: the busy text fixed again (`actionInProgress == "clearing"`) | `a127f9be...` / `5750f002...` | 1: `TheBusyText_NamesTheRunUnderWay` | md5 equal; 450/0 |

With the fix: `Migration|ClickGate|ProgressRegistry`, 1114 passed / 0 failed; the page 4377 lines, nothing registered
moved. On the part-5 commit's tree (`ffe2d99b`): full suite 6727 passed / 0 failed / 0 skipped. (A first run of it read
1 failed - `ClickGateTests.TheRegisteredLineCountStillMatchesTheFile(Migration.razor)` - because part 7's page edit was
made while it ran and the page-reading tests read the file from disk; it was re-run on the exact tree, part 7's files
set aside and restored byte-exact after, nothing edited during the run.)

**Part 7** - `MigrationDenialTests`' two new cases, `ADeniedBulkAction_IsShown_AuditedAsNotAttempted_AndNothingRuns`
(`ExecuteBulkBatchAction` and `ExecuteBulkMailboxAction` with MigrationManage refused: the banner and the activity
exactly "Not authorized to StopMigrationBatch on 1 batch(es); nothing was done." / "... StopMigrationUser on 1
mailbox(es) ..."; nothing run; one `<action>_Denied` row, Failed, `mutationAttempted` false, its target the run's
notification action, the ticket, "MigrationManage authorization denied"; rendered on the status tab); and
`MigrationStatusPageTests.TheBulkExecutorReChecksAuthorizationAndAuditsADenial`, now requiring the `RecordDenial` call.
Filter `MigrationDenialTests|MigrationStatusPageTests|ClickGate`, 441 tests. The reverted round puts back the page and
`ClickGateRegistry.cs` together (the line count is part of the fix).

| Round | md5 before / mutated | Failed (of 441) | Restored |
| --- | --- | --- | --- |
| The fix reverted (page and registry back to part 5's) | page `db977314...` / `a127f9be...`, registry `483a950d...` / `d0f44ecf...` | 3: both new cases and the tripwire | md5 equal; 441/0 |
| M7a: the batch executor's denial back to its old inline row (no `extra`), on its one line | `db977314...` / `7c9b3603...` | 2: the batch case and the tripwire | md5 equal; 441/0 |
| M7b: the mailbox executor's the same | `db977314...` / `b8394eea...` | 1: the mailbox case | md5 equal; 441/0 |

Re-anchor (`.agents/playbooks/clickgate-reanchor.md`): `git diff -U0` gives two hunks, at old lines 3532 and 3698, so
every line up to 3531 maps to itself and every registered line (the highest is 1540) is unchanged; `ExpectedLineCount`
4377 -> 4353, and the ClickGate filter is green. With the fix: `Migration|ClickGate|ProgressRegistry`, 1116 passed / 0
failed. On the part-7 commit's tree (`39a878cd`), nothing edited during the run: full suite 6729 passed / 0 failed / 0
skipped.

## Also held (codex, mig-batch-9, 2026-10-23) - LOW

(6) `ExchangeAdminWeb.Tests/RecipientLookupReportedTimeTests.cs` 103: the stand-in's archive read answers no row,
which aud-a54 now treats as unreadable, so those page-render tests pass while rendering an archive warning a
no-archive mailbox would not; model "no archive" with a recognised not-found shape and assert `Warnings` empty.

## Also held (codex, misc-batch-1, 2026-10-23) - LOW

(7) `Components/Pages/Migration.razor` 3536 and 3702: the bulk executors' authorization-denial audit rows pass no
`extra`, so they lack `mutationAttempted: false` (the single-action denials through `MigrationService.RecordDenial`
carry it); route both through `RecordDenial` and add the two denied-authorization tests.

## Coder dispute

None on the findings. Two readings, settled by reading the files and stated rather than done silently:

- Part 1 needed the shared base: a size above zero cannot say there is an archive (Exchange's not-found and an empty
  archive are both 0 GB with no marker), so `MailboxSizes` gained `ArchiveFound`, additive and read by Recipient Lookup
  alone. The brief named module files only and said not to bump the csproj; whether this shared-file touch wants a base
  bump is the supervisor's call. Not bumped.
- "Three raw-message wordings": the statistics warning (part 2), `r.Error` (part 3) and the page's own catch (part 4),
  which a failed sign-in reaches as PowerShell's Stop exception because the pool rethrows it unchanged. The location
  warning and the on-premises size warning also format `ex.Message`, but every message that reaches them is one the
  base composed, with no prefix; left as they were (named as candidates for their cancellation handling).
- Part 3 also makes the lookup's own "Recipient '<x>' not found." reachable for Exchange's not-found by structure: the
  record's (3) names that dead path as half the defect, while its Approach bullet named only the helper.

## Known gaps

- Assumptions, labelled, no live call: Exchange Online reports a missing recipient from Get-Recipient in a shape
  `IsNotFound` reads (part 3, aud-a23h's); on-premises Exchange answers an archive statistics row, through remoting, for
  a mailbox that has an archive (part 1, as the stand-in does). If either is wrong the page says so in Exchange's words.
- Part 1: `ArchiveFound` is false from the Exchange Online size read in MigrationService, which does not set it and whose
  callers do not ask (said on the property). The on-premises read asks only TotalItemSize, so an on-premises archive's
  "Archive Deleted" and "Archive Items" read "N/A", as the on-premises mailbox's deleted-item rows already did. An
  on-premises mailbox whose archive is in Exchange Online shows what the on-premises read answers; not modelled.
- Part 4: `Progress.Begin` stays outside the lookup's try, as in the page's other handlers; it does not throw.
- Part 5: the mailbox twin's activity stays "Applying the bulk mailbox action" - generic, not wrong, not in the record.
- Part 7: the bulk denials' row target stays the run's notification action ("Migration_StopSelected"), as before.
- One full-suite run (part 4's tree) missed `SettingsStoreBusyTests`' wall-clock bound under other agents' load; that
  class passed alone straight after. Named as a candidate.
- No circuit or browser ran: the pages' handlers and compiled render trees were driven directly.

## Branch-tip gates

On the final tree (every code commit, the two module bumps, both records; nothing edited during the run):
`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors / 19 warnings (the baseline's 19, none in a touched file); `dotnet
test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` 6729 passed / 0 failed / 0 skipped (6693 at `eb017102` plus the branch's 36 new cases); `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore` exit 0; `git diff --check` exit 0; `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1`
exit 0. No PowerShell changed, so PSScriptAnalyzer and Pester were not run. No manual check: nothing here is
deployable to a live system from a worktree, and nothing was deployed.

## Reviewer comments

Pending: the codex defect review of the merged group (the supervisor dispatches it).

## Closeout

Fixed on branch `fix/a68-kd2`; not merged, not pushed. Pending step: the supervisor's merge into master, then the codex
review; at merge take this branch's copy of this record.

## Reviewer comments (supervisor, mig-batch-10)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 86953582 (merged 44fb74aa); 2026-10-23 evening; capability proof: AGENTS.md read and git show --stat 86953582 both succeeded. One candidate, admitted as kd-17 (bulk denial rows name the action as target). Codex found nothing else across archive states, not-found classification, the no-row statistics warning, lookup failure audit and activity handling, cancellation outside the admitted kd-14 paths, bulk action labels, kd-2 selection clearing, the ClickGate re-anchoring and the named test harness shapes. Artefacts: `.agents/review/mig-batch-10.result.txt`, `.stream.log`, `prompt-mig-batch-10.md`.
