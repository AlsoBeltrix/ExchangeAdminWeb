# aud-a23k: Migration - a failed report fetch is served as the report text, and a failed cloud mailbox size read shows 0 GB

**Severity**: LOW/MEDIUM - a failed read presented as data. (1) `GetMigrationUserReportAsync` turns a failed
fetch into report text ("Error retrieving report: ..."); the page stores that text as the report, shows it
as such, and its activity completes as succeeded. (2) For moves back on-prem, a failed
`GetCloudMailboxSizeAsync` is shown as 0 GB, so a size-based decision reads an empty mailbox.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`)
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

Pending.
