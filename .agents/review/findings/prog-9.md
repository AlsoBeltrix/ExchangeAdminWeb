# prog-9: The progress yield opened a null window on MessageTrace's CSV export

**Severity**: MEDIUM - introduced by this work stream, not pre-existing
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: see Closeout

## Evidence

`Components/Pages/MessageTrace.razor:959` checks `response.Results`; the progress code added
in `2c6625e` awaits `Task.Yield()` at `:966`; the CSV loop dereferences `response.Results`
again at `:971`.

`ExportCsv` does not set `isLoading`, and the Run Trace button is disabled only by
`isLoading` (`:297`). `RunTrace` clears `response` at `:787` before awaiting the backend
search.

Triggering condition: an operator starts Export CSV and a trace action is processed while
the export is yielded for the progress repaint.

Predicted observable failure: `RunTrace` sets `response` to null before the export resumes,
and the export throws on the re-read.

**This is a defect the progress work created.** Before `2c6625e` the handler was synchronous
end to end - there was no suspension point between the guard and the loop, so no window.
Adding the yield is correct and necessary (a synchronous handler cannot paint), but it turns
a guarded read into an unguarded one.

## The same class, found independently and already fixed elsewhere

The agent on S5 batch 2 found this exact hazard on its own pages and closed it before
committing: `BitLockerRecovery.SearchAsync` clears `results` and rewrites `searchTicket` on
its first synchronous lines, so its export could have handed over an empty file under the
wrong ticket. Both it and `BlockedSenders` now snapshot before the yield.

It also characterised when the hazard does NOT arise: on pages where the handler's busy flag
is part of `IsBusy` and is raised ABOVE the yield, the page is shut for the whole window.
Migration and the three converted pages need no snapshot for that reason.

`MessageTrace.ExportCsv` is the one instance that predates that insight and did not get it.

## Approach

`MessageTrace.ExportCsv` captures `rows`, `rangeStart` and `rangeEnd` above the `Begin`,
matching `BitLockerRecovery.DownloadCsvAsync` and `BlockedSenders.DownloadCsvAsync`. The dates
go with the rows because the filename is built from them and their inputs (`:276`, `:280`) are
disabled only by `isLoading`, which this handler never raises - so an edited range would have
named a window the file does not contain.

**The sweep found two more, both on `Migration`, and both were cleared in `430e929` on a
premise that does not hold.** That commit wrote "no snapshot was added: `isDownloadingCsv` is a
member of this page's `IsBusy` and is raised ABOVE the yield, so the page is shut for the whole
window". The page is not shut. `ClickGateRegistry.Migration` itself records three of its
controls as deliberately ungated, and all three write fields these handlers read after the
yield:

- `ExemptControl(1210, "@onclick=\"DismissReportModal\"")` - the report pane's Close button.
  `DismissReportModal` nulls `reportUser`, `userReport` and `reportFetchedAtUtc` together, and
  `ExportOpenReportAsync` reads all three after the yield. `Encoding.UTF8.GetBytes(null)`
  throws, and `reportUser.Select(...)` would too. **This is the same null-dereference class as
  the reported `MessageTrace` case, not a milder one.**
- `UngatedDomSyncedControl(959, ...)` and `UngatedDomSyncedControl(1235, ...)` - the select-all
  and per-row mailbox tick boxes, ungated under owner ruling D2(a). Both write
  `selectedMailboxes`, which `DownloadReportExportAsync` reads after the yield to build the zip
  and to write the audit row, after its label has already counted it.
  `OnParametersSetAsync` -> `SyncOpenBatchFromUrl` writes `expandedBatch` on any navigation and
  is not `IsBusy`-gated either.

Snapshotting at the top of the handler does not contradict D2(a): the capture is taken
synchronously at click time, so it still reads the live selection the ruling is about. What it
stops is a click landing in the repaint window the progress yield opened.

## The sweep, every yield this work stream added

Eleven, over six pages, from `git diff d5f3b3b~1..HEAD -- Components/` (the three other
`+Task.Yield` hits in that diff are comment text; the yields on `Comms10k`, `AdminSettings` and
`ExchangeOnlineConfig` pre-date the stream and are out of scope).

| Yield | Verdict |
|---|---|
| `AdminBulkJobs.RefreshJobs` | SAFE - reads nothing captured before the yield; `LoadJobsFromStore` re-reads `detailsJobId` under its own inline null check and a reload is idempotent |
| `AdminBulkJobs.ToggleDetails` | SAFE - everything below the yield is derived from the `jobId` parameter; the only field read is written, not read back |
| `AdminEventLog.LoadEvents` | SAFE - `Begin` and yield are the first two statements, so nothing is carried across; the body is synchronous to the end, so the dates are read atomically at resume |
| `AdminEventLog.DownloadCsv` | SAFE - already captures `csvEntries` above the `Begin` |
| `AdminEventLog.LoadUsage` | SAFE - same shape as `LoadEvents` |
| `BitLockerRecovery.DownloadCsvAsync` | SAFE - captures `rows` and `ticket` (`4f06fbf`, where the insight came from) |
| `BlockedSenders.DownloadCsvAsync` | SAFE - captures `rows` (`4c2ace2`) |
| `MessageTrace.ExportCsv` | **FIXED** - the reported case |
| `Migration.DownloadReportExportAsync` | **FIXED** - ungated tick boxes at `:959`/`:1235`; wrong-set zip and wrong-count audit |
| `Migration.DownloadCsvAsync` | SAFE, and checked rather than inherited - every input to `GetSortedBatches()` is shut: filter box `:518`, sort select `:529`, direction button `:539` all `disabled="@IsBusy"`, and `migrationBatches` is written only by `LoadBatchList`, which raises `isLoadingStatus` above its own first await |
| `Migration.ExportOpenReportAsync` | **FIXED** - ungated Close button at `:1210`; null dereference |

Handlers this stream made `async` WITHOUT adding a yield were checked too and none carries the
hazard: `AdminEventLog.ShowEventsView`, `ShowUsageView` and `OnDateRangeChanged`, and
`AdminBulkJobs.CancelJob` and `RemoveJob`, all read everything they need before their single
await and do nothing after it.

**One thing the sweep did not do.** It is scoped to yields this work stream added, as the brief
set it. `BlockedSenders.ConfirmUnblock`'s yield pre-dates the stream and reads `confirmAddress`
after it, which `CancelUnblock` can null - the label would read "Unblocking " rather than
throwing, because the field is `string?`. Recorded, not fixed: out of this finding's scope.

## Reviewer comments

Raised by codex reviewing `6a561df..c15680a` (S5 batch 1), which otherwise checked out: the
`UsageTracker` test was re-anchored and not weakened, `ExecuteUndo` correctly completes
before its self-reporting refresh, `LoadEvents`' first `Complete` does sit below all three
named calls, `CsvEscape` is within the operation-scale rule, and the `Unknown` sizing choices
match a live-only progress UI.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard.
No build run; no-build filtered tests passed 38 and 66; `git diff --check` clean.
Raw output: `.agents/review/prog-s5a.result.json`.

## Files changed

- `Components/Pages/MessageTrace.razor` - `ExportCsv` captures `rows`, `rangeStart`,
  `rangeEnd` above the `Begin`.
- `Components/Pages/Migration.razor` - `DownloadReportExportAsync` captures `batch` and
  `mailboxes`; `ExportOpenReportAsync` captures `user`, `reportText`, `fetchedAtUtc`.
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs` - the four affected notes corrected. Three said
  or implied no snapshot was needed; the fourth (`Migration.DownloadCsvAsync`) now records WHY
  it genuinely is not, with the three controls checked.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - `Migration` `ExpectedLineCount` 4149 -> 4170.
- `Modules/ModuleCatalog.cs` - `MessageTrace` 1.5.6 -> 1.5.7, `Migration` 1.22.7 -> 1.22.8.

## Guard proof

**A RUNTIME probe, not a source scan, and that is unusual here on purpose.** Every other guard
in this work stream is a source-level assertion, which is exactly what could not have caught
this: the registry proves the activity's window, not what the handler reads inside it.

The probe drives the real `MessageTrace.ExportCsv` by reflection on a dedicated thread under a
custom `SynchronizationContext` whose first `Post` - the `await Task.Yield()` continuation -
sets `response` to null before running it. That is precisely what the Blazor dispatcher does
when a queued Run Trace click is processed during the progress repaint. `Progress` and `JS`
are NSubstitute stubs; the pushed base64 is captured and decoded.

- **Before the fix:** `System.NullReferenceException at MessageTrace.ExportCsv() in
  Components/Pages/MessageTrace.razor:line 971` - the `foreach (var msg in response.Results)`
  line, the exact line this finding named.
- **After the fix:** passes; the decoded CSV contains the snapshot's row.

The probe file was temporary and is not committed. The repo has no bUnit harness and this
work stream was not scoped to add one; whether a reflection-plus-pump harness should become a
permanent regression test is an owner call, not a thing to slip in under a bug fix.

Full-suite verification: Release build 0 errors and no new warning on either page; `dotnet test
ExchangeAdminWeb.slnx` 3674 passed / 0 failed / 3 skipped (unchanged); `dotnet format
--verify-no-changes --no-restore` exit 0; `git diff --check HEAD` clean; ClickGate filter 316
passed; every added line ASCII.

## Closeout

Fixed and verified in one commit, as one finding plus the sweep it implies. The sweep's result
is the table above: eight SAFE, three FIXED. `.agents/review/index.md` row moved to Verified.
