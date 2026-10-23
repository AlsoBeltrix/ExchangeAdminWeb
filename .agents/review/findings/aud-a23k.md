# aud-a23k: Migration - a failed report fetch is served as the report text, and a failed cloud mailbox size read shows 0 GB

**Severity**: LOW/MEDIUM - a failed read presented as data. (1) `GetMigrationUserReportAsync` turns a failed
fetch into report text ("Error retrieving report: ..."); the page stores that text as the report, shows it
as such, and its activity completes as succeeded. (2) For moves back on-prem, a failed
`GetCloudMailboxSizeAsync` is shown as 0 GB, so a size-based decision reads an empty mailbox.
**Status**: Fixed on branch, guard proof below; not merged, not pushed
**Branch**: `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`)
**Commit**: `e455a37f` (fix, tests, record); plan `f62462b9`; comments `97ca9448` (shared with aud-a23n, aud-a23j, aud-a23m, aud-a23l)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `fffdc451`. One commit `fix(migration): ...
(aud-a23k)` on branch `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`), after aud-a23m. Module-scoped:
Migration (the bump is the supervisor's). The page in place, 4377 lines, no registered line or snippet among the
edits. Line numbers at `b16898dc`. Wording of a failure stays `ex.Message` here; the cmdlet's own words (no Stop
prefix) are aud-a23l's, the next commit.

1. **Part 1, the report fetch returns a typed outcome.**
   - `Models/MigrationModels.cs`: new `MigrationUserReport(string? Text, string? Failure)` - `Of(text)`, or
     `Failed(reason)` with no text (a blank reason is given one, the aud-a23g rule).
   - `Services/MigrationService.cs` `GetMigrationUserReportAsync` (1257-1307) returns it: the report as before;
     a failed `Get-MigrationUserStatistics` - `Failed(<reason>)` where it returned the text "Error retrieving
     report: ..."; no statistics row - `Failed("Exchange returned no migration statistics for <address>")` where it
     returned null (which the page stored and then showed nothing for).
   - **Found while planning:** the bulk export (`Services/Jobs/MigrationReportExportProcessor.cs` 82-87) is the
     method's other caller and stored the failure text as the report, its row Succeeded and the zip carrying it. A
     failure is now a Failed row, "Report could not be fetched: <reason>", nothing stored.
   - Page `FetchUserReport` (4028-4080): a failed outcome is not stored (`ReportStore.Write` only for a report), shown
     as a failure, and its activity completes not succeeded ("The report for <address> could not be fetched:
     <reason>"); the page's own catch the same. New field `reportFailure` (on 1638's line), set with the outcome and
     cleared wherever `userReport` is (`DismissReportModal`, `InvalidateStoredReports`, `LoadUserReport`'s stored
     path), every write still behind the generation check.
   - The dialog (1211-1260) opens for a report or a failure (1211). Its body (1256): a failure is an
     `alert-danger`, `role="alert"`, "The report could not be retrieved: <reason>", OUTSIDE the report body, which is
     shown only for a report; no "Taken", no "Fetch again" (both need a fetch time, as before). Export (1235) is
     disabled while there is no report, where it would silently do nothing.
   - `MigrationStatusPageTests`' source scans anchored on the old dialog condition (three) and on the old error text
     (`$"Error: {ex.Message}"`) are re-anchored on the new ones; every assertion they make holds.
2. **Part 2, the cloud size read returns an "unreadable" marker, not 0.**
   - `Models/MigrationModels.cs` `MigrationEligibilityResult`: `MailboxSizeUnreadable`, `ArchiveSizeUnreadable` -
     why each could not be read, null when read (or not asked for). The sizes stay doubles; a size with a marker is
     never shown or summed.
   - `GetCloudMailboxSizeAsync` (1224-1255): the mailbox read failing, returning no row, or a `TotalItemSize` that
     does not parse - the mailbox marker with that reason, where it was 0 (`?? 0`) or a throw; the archive read: a
     not-found (aud-a23h's `IsNotFound`, structure only) is no archive, as `catch { }` meant; any other failure, or a
     size that does not parse, is the archive marker, where it was 0. **Assumption, labelled:** `Get-MailboxStatistics
     -Archive` reports a mailbox with no archive as not-found in one of the shapes `IsNotFound` reads; if not, such a
     mailbox shows "Archive: could not be read (...)" - visible, and no decision reads it.
   - `CheckMigrationEligibilityAsync` (217-235): ToOnPrem copies the markers; a size read that throws (the pool, the
     connection) marks both with its reason, where the catch dropped it silently. No ToOnPrem decision reads the size
     today (no quota applies on-premises), so none is refused and the verdict is unchanged; the decisions that do read
     a size are ToCloud's quota checks, which use the on-premises read in `ExchangeServiceBase` - not this finding
     (named in the hand-back).
   - Page, in place: the single result (125-135) and the bulk table (296-308) show "could not be read (<reason>)"
     for a marked size, and no total when either is marked.
3. **Tests** - `MigrationStubExchange`: switches for `Get-MigrationUserStatistics` returning no row, and for
   `Get-MailboxStatistics` (and `-Archive`) reporting a size that does not parse or no archive. New
   `ExchangeAdminWeb.Tests/MigrationReportAndSizeReadTests.cs`, fakes only:
   - **part 1**: the fetch failing (thrown, written) - `Failure` is the reason, no text; no statistics row - the
     "no migration statistics" failure; a report - its text. Through the page's `FetchUserReport`: the failure shown
     in the alert and NOT in the report body, `userReport` null, nothing stored (`ReportStore.TryRead` null), the
     activity not succeeded; a later report clears the failure. Through the processor: a Failed row naming the reason,
     nothing stored.
   - **part 2**: ToOnPrem over a cloud mailbox whose statistics read fails, whose size does not parse, whose archive
     read fails, and with no archive - the markers set, the sizes not shown as numbers, "could not be read (...)" on
     the page, the verdict unchanged; a readable mailbox and archive - the sizes and the total as before.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green):
K1 (the record's (1)) text-as-report again - the service returns the failure as `Of("Error retrieving report: ...")`;
K2 (the record's (2)) 0 again - an unparseable mailbox size reads 0 with no marker. Extras: K1b the page stores and
shows a failed outcome as the report; K1c the processor stores a failed outcome; K2b the archive's failure is no
archive again; K2c the page shows a marked size as a number.

**Gates** as the brief; comments after the mutation round.

## Guard proof

Built as the Plan, with one wording choice the Plan left open: the size read is new code, so its failure reasons are
written in the cmdlet's own words (`ResolveFailure`) from the start; the report fetch keeps `ex.Message`, as planned,
for aud-a23l to convert. The fix commit carries no new code comments. `Components/Pages/Migration.razor` is 4377 lines
before and after: `git diff --numstat` 22/22, every change in place; none of the edited lines (125-133, 296-302, 1211,
1235, 1256, 1638, 3925, 3940, 4009, 4043-4068) is registered, and 1252 (Close, registered) is untouched.

- `Models/MigrationModels.cs`: `MigrationEligibilityResult.MailboxSizeUnreadable` / `ArchiveSizeUnreadable` (11-12);
  `MigrationUserReport(Text, Failure)` with `Of` and `Failed` (264; a blank reason is "no reason was given").
- `Services/MigrationService.cs`:
  - `GetCloudMailboxSizeAsync` (1350) returns `CloudMailboxSize` (1348). The mailbox read under Stop through `Invoke`:
    its failure - "Get-MailboxStatistics failed: <reason>"; no row - "Exchange returned no mailbox statistics"; no
    TotalItemSize, or one that does not parse - `ReadSize` (1389): "Exchange reported no TotalItemSize" / "the
    reported TotalItemSize '<text>' is not a size this app can read". The archive read through `ReadUnlessNotFound`:
    Exchange's not-found is no archive; any other failure - "Get-MailboxStatistics -Archive failed: <reason>"; a size
    that does not parse - its marker. Neither read's catch takes a cancellation (the aud-a23g rule).
  - `CheckMigrationEligibilityAsync`: ToOnPrem copies the markers (225); a size read that throws as a whole marks both,
    "the size read failed: <reason>" (239-240), where the catch dropped it. The verdict is unchanged; ToCloud is
    unchanged.
  - `GetMigrationUserReportAsync` (1404) returns `MigrationUserReport`: no statistics row -
    `Failed("Exchange returned no migration statistics for <address>")` (1418); a failed fetch - `Failed(ex.Message)`
    (1452), where it returned the text "Error retrieving report: ...".
- `Services/Jobs/MigrationReportExportProcessor.cs` (85): a failed outcome is a Failed row, "Report could not be fetched:
  <reason>", logged, nothing stored.
- `Components/Pages/Migration.razor`: the sizes (125-133 single, 296-302 bulk) - "could not be read (<reason>)" for a
  marked size, no total beside one; the dialog opens for a report or a failure (1211); Export disabled with no report
  (1235); the body (1256) - a failure in its own `alert-danger`, `role="alert"`, "The report could not be retrieved:
  <reason>", the `<pre>` report body only for a report; `reportFailure` (1638), cleared with `userReport` (3925,
  3940, 4009); `FetchUserReport` (4043-4068) - the outcome's failure is the activity's (set before the superseded
  return, the aud-a23i rule), the store write only for a report, the page catch's failure the same shape, every
  `userReport` write still behind the generation check.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs`: the three source scans anchored on the dialog's old condition
  now anchor on the new one; the store-write scan anchors on the new failure branch and asserts the write is for a
  report only. Every assertion they made holds.
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: `Get-MailboxStatistics` honours `MailboxSize` / `ArchiveSize` (the
  TotalItemSize reported; null - no row), `NoArchive` (Exchange's not-found) and `StatisticsCancelled`; the archive read
  logs as its own step (`-Archive`); `Get-MigrationUserStatistics` honours `NoStatistics`.

New `ExchangeAdminWeb.Tests/MigrationReportAndSizeReadTests.cs`, 14 tests (19 cases):

| Test | Pins |
| --- | --- |
| `AFetchThatFails_IsAFailedOutcome_NotReportText` (throw, written) | **part 1, the finding**: no text, the failure carries the reason |
| `NoStatistics_IsAFailedOutcome_NotAnEmptyReport` | "Exchange returned no migration statistics for <address>" |
| `AReport_IsItsText` | the control: the report text, no failure |
| `AFailedFetch_IsShownAsAFailure_NotStored_AndItsActivityFails` | through `FetchUserReport`: `userReport` null, `reportFailure` the reason, no fetch time, nothing stored; rendered "The report could not be retrieved: <reason>", no "Error retrieving report", no "Taken"; the activity (false, "The report for <address> could not be fetched: ...") |
| `AReportFetchedAfterAFailure_ClearsIt` | the failure gone, the report shown and stored |
| `TheExportJob_RecordsAFailedFetchAsAFailedRow_AndStoresNothing` | found while planning: the bulk export's row Failed, "Report could not be fetched: <reason>", nothing stored |
| `TheExportJob_StoresAFetchedReport` | the control |
| `AMailboxSizeThatCannotBeRead_IsMarked_NeverZero` (thrown, written, no row, does not parse) | **part 2, the finding**: the mailbox marker with its reason; the verdict Eligible, unchanged |
| `AnArchiveSizeThatCannotBeRead_IsMarked_NotNoArchive` (written, does not parse) | the archive marker; the mailbox read as 1.5 |
| `NoArchive_IsNoArchive` | Exchange's not-found: no marker, 0, the mailbox read |
| `ASizeReadThatFailsAsAWhole_MarksBothSizes` | a cancellation past both reads' catches: both marked "the size read failed: ..." |
| `APageWithAMarkedSize_SaysItCouldNotBeRead_AndGivesNoTotal` | "could not be read (the reported TotalItemSize 'Unlimited' ...)", no "0.00 GB", no "Total:" |
| `APageWithReadableSizes_ShowsThemAndTheirTotal` | the control: 1.50 GB, Total 3.00 GB (current culture) |
| `TheBulkTable_SaysAMarkedSizeCouldNotBeRead` | the bulk cells: "could not be read (...)", "archive could not be read (...)" |

**Against the unfixed code: not runnable as a whole** - the service's return types changed, so each behaviour was put
back at its own call site instead; each revert fails the tests that pin it.

Mutations at the call site, by the same driver and protocol (moved to the session scratchpad's `a23jklmn-work`
folder: `mutate.ps1`, `round-a23k.ps1`), over the 1129 tests of the same filter. Bytes (LF) before every mutation and
after every restore: service `cbd557f6361a4f531ad9dfa1896fcf1b`, page `88696b1a483741d85ab30e2d9eb57ec4`, processor
`43d3767b83878c24f18759fae9f65cdb`.

| Mutation | File | md5 mutated | Failed (of 1129) | Restored, rerun |
| --- | --- | --- | --- | --- |
| K1 (the record's (1)): text-as-report again (`Of("Error retrieving report: ...")`) | service | `73ee23589445c9c74edd1def2cd02e40` | 4: `AFetchThatFails...` (both), `AFailedFetch_IsShownAsAFailure...`, `TheExportJob_RecordsAFailedFetch...` | equal; 1129/0 |
| K2 (the record's (2)): 0 again - a size that does not parse, no marker | service | `02efbe10916e5aa1bc8c18bf33a18305` | 3: `AMailboxSizeThatCannotBeRead...` (does not parse), `AnArchiveSizeThatCannotBeRead...` (does not parse), `APageWithAMarkedSize...` | equal; 1129/0 |
| K1b: the page stores and shows a failed outcome as the report | page | `d712939bb203a0d7374051780e306a2b` | 2: `AFailedFetch_IsShownAsAFailure...`, `MigrationStatusPageTests.ReopeningAReportDoesNotRefetchIt...` | equal; 1129/0 |
| K1c: the bulk export stores a failed outcome | processor | `a04c62230fb0a6a4ca7a8125abb3c3f1` | 1: `TheExportJob_RecordsAFailedFetch...` | equal; 1129/0 |
| K2b: the archive read's failure is no archive again | service | `54c0de5cedb9ceb73d1ab0d300309550` | 1: `AnArchiveSizeThatCannotBeRead...` (written) | equal; 1129/0 |
| K2c: the page shows a marked size as a number | page | `348de222488fd4eb996bf5f4dd5fbdb1` | 1: `APageWithAMarkedSize...` | equal; 1129/0 |
| K2d: a size read that fails as a whole dropped again | service | `1961d6309b6be7b21f9e0a93d8636427` | 1: `ASizeReadThatFailsAsAWhole...` | equal; 1129/0 |

md5s (LF) of the other files at the fix commit: `Models/MigrationModels.cs` `8daab313723deb16fedce78816af219e`,
`MigrationReportAndSizeReadTests.cs` `e2fda28402ff50ff7a94dc4aedfbb871`, `MigrationStubExchange.cs`
`0d4ae546a9f59e95f2c2421090d0a25e`, `MigrationStatusPageTests.cs` `5ccadeeca6599e4817485c1b73d6d7ea`.

On the fix commit's tree: Release build 0 errors; the 1129 focused tests 1129/0; `dotnet format --verify-no-changes`
exit 0; `git diff --check HEAD` exit 0; `wc -l` 4377. Known gap, unchanged here: the ToCloud size gate reads the
on-premises size through `ExchangeServiceBase.GetOnPremMailboxSizeAsync`, which reads an unparseable size as 0 and any
archive failure as no archive - named in the hand-back; the base class is outside this brief.

## Known gaps

- **Assumption, labelled (no live call):** `Get-MailboxStatistics -Archive` reports a mailbox with no archive in a shape
  `IsNotFound` reads (aud-a23h). If not, such a mailbox shows "Archive: could not be read (...)" - visible, and no
  decision reads it.
- The ToCloud size gate is unchanged and reads the on-premises size through `ExchangeServiceBase.GetOnPremMailboxSizeAsync`,
  which turns a TotalItemSize it cannot parse into 0 (521, `ParseExchangeSize`) and any archive read failure into no
  archive (538-541) - so the quota check can pass a mailbox whose size could not be read. Outside this brief (base
  class); named in the hand-back.
- A failed fetch's dialog offers no "Fetch again" (it needs a fetch time, as for the page catch's failures before);
  closing it and opening the row's report again fetches.
## Comments and branch-tip gates

Shared by the five records of this branch; the canonical copy is in `aud-a23n.md` ("Comments and branch-tip gates"):
comments `97ca9448`, comment-only by a stripped-code comparison; at the tip, Release build 0 errors / 59 warnings,
full suite 5872 passed / 3 skipped / 0 failed (5875), format, `git diff --check`, ASCII lint all exit 0, page 4377 lines.

## Closeout

Fixed on branch `worktree-agent-a03d6bd111b571e5a`; not merged, not pushed.
