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
**Branch**: `worktree-agent-ac856fb3e1e8d9617` (forked at `d7d41457`)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it, and parts 5-7
(below) were folded in by the supervisor from the codex review of aud-a23f/a23e. Module-scoped: Migration,
and through the shared `ShowReported` (part 7) Out of Office and Recipient Lookup, whose pages are not
edited; version bumps are the supervisor's (`Modules/ModuleCatalog.cs`, `ExchangeAdminWeb.csproj` not
edited here). No new JS interop, handler or lifecycle method. Written before the code; line numbers are at
`d7d41457`.

**Found while planning** (a probe on this host's PowerShell 7 SDK; no Exchange): `Get-MigrationBatch` runs
with `-ErrorAction Ignore` (623), and under `Ignore` an advanced function's `throw` does not throw - it runs
on past it - and a `Write-Error` reaches neither an exception nor the error stream (`HadErrors` only). So
most failed calls never reach the outer catch at all: they come back as an empty list. Only a
`ThrowTerminatingError` or a .NET-level failure (a missing command, a broken runspace) reaches it. Under
`-ErrorAction Stop` both throw (`RuntimeException`; `ActionPreferenceStopException`, whose message prefixes
"The running command stopped because..." and whose `ErrorRecord` holds the original). A nested command's
own `-ErrorAction SilentlyContinue` error sets `HadErrors` with an empty stream and no exception.

1. **Part 1, the read** - `Services/MigrationService.cs` `GetMigrationBatchesAsync` (612-723):
   - 623: `-ErrorAction Stop`, so a failed call throws. `InvokeOptional` (625) stays: it fails on an
     exception, not on `HadErrors` alone, which a module's own suppressed error sets (the probe).
     **Assumption, unverified (no live call):** `Get-MigrationBatch` with no batches writes no error.
   - 717-721: the outer catch returns `MigrationBatchList.Failed(reason)` where it returned an empty list;
     the reason is the error itself (an `ActionPreferenceStopException`'s `ErrorRecord`, else
     `ResolvePsErrors`). The tracker keeps the connection classification `InvokeOptional` made, so the
     pool's discard and precheck retry are unchanged.
   - `Models/MigrationModels.cs` `MigrationBatchList` (185): a third positional `string? Failure = null`;
     `Failed(reason)` - no batches, no unreadable rows, the reason (a blank one becomes "no reason was
     given": a failure must never read as none).
2. **Part 1, the page** - `Components/Pages/Migration.razor`, in place, 4377 lines; no registered line or
   snippet changes (the registered lines run to 1540):
   - 485-491: a failed load is its own branch, after "not loaded", before the empty state, one line:
     `alert-danger`, `role="alert"`, "The migration batches could not be read: <reason>", "No batch is
     listed until a load succeeds. Refresh to try again." The empty state follows (486) unchanged; its R28
     comment (487-491) is rewrapped from five lines to four. A failure shows neither "No migration batches
     found" nor the panes.
   - 1590-1592: `batchListFailure`, the reason, null when the last load read. The section comment becomes
     its trailing comment, so no line moves.
   - 2292: `(migrationBatches, unreadableBatches, batchListFailure) = await ...`. 2297-2298, the page's
     catch (a throw before the service returns - the borrow, the connect): the same state through
     `MigrationBatchList.Failed(ex.Message)`, and `batchActionResult = null` as on success, so a bulk
     action's own result comes back through its `?? savedResult` beside the failure instead of being
     replaced by "Failed to load migration batches".
   - **A failed load clears the list, on both paths.** The page shows what the last load returned, and a
     failed load returned none. A previous list kept under "could not be read" would read as Exchange's
     current answer - statuses, counts, batches since removed. With no list, Export is withheld by its
     existing guards (420, 4210, unchanged, so `Migration_WiresDownloadCsv` holds), PruneSelection drops the
     ticks and the open batch closes, so nothing can be aimed at a batch the load could not confirm. The
     cost: a failed reload loses the selection and the open batch; Refresh restores the list. Before, a
     throw kept the list (`??=`) and put the failure in the dismissible result banner, and dismissing it on
     a first load revealed "No migration batches found".
   - 2309-2311: the PruneSelection comment no longer says a failed load keeps the list.
3. **Part 2, the mailbox counts.** `MigrationUserInfo.ItemsSynced`/`ItemsSkipped` (198-199) become
   `ReportedCount`, default `Of(0)`; `GetMigrationBatchUsersAsync` (760-761) reads both through
   `ReportedCount.Read(...) ?? ReportedCount.Of(0)` (no other `Convert.*` on that path); the cell (1299)
   shows a count `N0` as before and the marker otherwise; the Synced sort (2492-2493) through
   `ReportedCount.Order`. `ReportedCount.Count` widens to `long?` (`Of(long)`): these were Int64 reads, and
   an Int32 reader would make a readable count above 2,147,483,647 the marker. Batch counts are unaffected
   within the range they use.
4. **Part 3, "Taken"** (1229): `OutOfOfficeRequest.ShowReported(reportFetchedAtUtc.Value, TimeZoneInfo.Utc)`,
   `yyyy-MM-dd HH:mm (UTC, UTC+00:00)`. The value is this app's own `DateTime.UtcNow` (Kind Utc), or the
   report store's copy, which `System.Text.Json` reads back as Kind Utc.
5. **Part 4, the schedule.** `ScheduledCompletionUtc` (138-141) states an instant only for a `CompleteAfter`
   of Kind Utc, with no `ToUniversalTime()` guess; any other Kind is null, and new
   `CompleteAfterZoneNotReported` (beside `CompleteAfterUnreadable`, 152) says so. Consumers: none outside the
   model and its tests; `AutoComplete` unchanged.
6. **Part 5, negatives.** `ReportedCount.WholeNumber` (98-119) reads `0..long.MaxValue` only (the range of
   3); a negative number or text is unreadable, the same marker.
7. **Part 6, cancellation.** The per-row catch (702) and the outer catch (717) take
   `when (!IsCancellation(ex))` - an `OperationCanceledException` or `PipelineStoppedException` anywhere in
   the exception's chain, since PowerShell's adapter wraps a getter's - so a cancelled load escapes the
   service, neither an unreadable row nor a failed read. The page's catch shows it as not read.
8. **Part 7, Kind Local.** **Assumption, unverified (no live call):** what a Local value from the Exchange
   Online module means. Nothing here proves it is this server's clock, so it is treated as Unspecified:
   shown as reported with "(time zone not reported)", grouped with Unspecified, never converted.
   `Models/LookupModels.cs` `ShowReported` (254-262) converts only Kind Utc; `Models/ReportedTime.cs`
   `Compare` (114-121) takes only Kind Utc as an instant, the rest compared by clock after the instants;
   part 4 the same. **Shared:** Out of Office's Check Status (aud-a7f) and Recipient Lookup (aud-a23e) show
   reported times through this `ShowReported`, so a Local read there is shown as reported too - one rule on
   all three pages.

**Tests** (fakes only; the stub-runspace idiom of `MigrationBatchReadTests`):

- `MigrationBatchReadTests`, extended - its stub gains a failure switch (`throw`, `Write-Error`), a
  `Get-MigrationUser` row list and a connect that can fail:
  - **the finding**: a `Get-MigrationBatch` that throws, and one that writes an error: `Failure` is the
    message (no Stop prefix), no batches; through the page's own `LoadBatchList`, "The migration batches
    could not be read: <message>", not "No migration batches found", no Export;
  - a good load, then a failed one: the old batch no longer listed, no Export, nothing ticked;
  - a connect that fails (the page's catch): the same banner;
  - part 2: a mailbox whose `ItemsSynced` is "1,205" beside readable ones - all listed, its cell the marker;
    `ItemsSkipped` the same in the model; the Synced sort puts it after every count, both directions;
  - part 5: `FailedCount` -1 and "-1": the marker in the failed cell and the CSV, sorted after every count;
  - part 6: a row whose getter throws `OperationCanceledException` or `PipelineStoppedException`: the call
    throws, the cancellation in its chain.
- `MigrationReportedTimeTests`: "Taken" names UTC, taken now and read back from the store; a Local time on
  all five reads is shown as reported, "(time zone not reported)", no "UTC", `ScheduledCompletionUtc` null
  with `CompleteAfterZoneNotReported`; the instant theory drops its Local case; the UTC-label test takes
  `DateTimeOffset`s where it took Local values; the Kind-mixed sort puts the Local time among the zone-less,
  by its clock.
- `MigrationScheduleTests`: a future `CompleteAfter` of Kind Unspecified or Local is null, flagged, still
  `AutoComplete`; Utc unchanged; `ALocalTimeIsComparedInUtcRatherThanAsWritten` becomes the Local case.
- `OutOfOfficeRequestCaptureTests`: a Local read schedule is shown as reported, no zone, no "UTC".
- `RecipientLookupReportedTimeTests`: the Local cases leave the browser-zone theory; a Local value with the
  browser's zone known is shown as reported, no zone.

**Mutations at the call site** (md5 before and after, the failing tests named, restored, md5 equal, touched,
rebuilt, green): M1 the outer catch returns an empty list again (the brief's (1)); M1b `-ErrorAction Ignore`
again; M1c the page's catch keeps the list and the old banner; M1d the failure branch dropped; M2
`Convert.ToInt64` again on `ItemsSynced` (the brief's (2)); M2b on `ItemsSkipped`; M3 "Taken" on the
server's clock, unnamed (the brief's (3)); M4 a zone-less `CompleteAfter` read as server-local (the brief's
(4)); M5 negatives accepted; M6 the per-row catch catches everything again; M6b the outer catch the same; M7a
`ShowReported` converts Local again; M7b `Order` converts Local again.

**Gates**: Release build; the full suite with `--blame-hang`; `dotnet format --verify-no-changes`;
`git diff --check HEAD`; ASCII lint after `git add -A`; `ClickGate*` and `ProgressRegistry*` passing
(`ExpectedLineCount` unchanged). Comments written after the mutation round.

## Guard proof

Pending.

## Folded in: three LOW items from the codex review of aud-a23f/a23e (ops-batch-10, 2026-10-22)

5. **Negative counts render as real counts.** `Models/ReportedCount.cs` `Read` (around 102) accepts any
   whole number; `FailedCount = -1` or `"-1"` shows as a factual count, in the red failed cell, and sorts
   before zero. Accept only `0..int.MaxValue`; negative numeric or text values are unreadable with the
   same marker. Test: `-1` renders the marker. Mutation: accept negatives again.
6. **Cancellation becomes an unreadable row.** `Services/MigrationService.cs` per-row catch (around 702)
   turns `OperationCanceledException` / `PipelineStoppedException` thrown while reading a row into
   "could not be read" and returns the rest as a valid partial result. Let cancellation-like exceptions
   escape the per-row catch (exception filter) so a cancelled load is cancelled. Test: a cancellation
   thrown from a row's getter propagates. Mutation: catch everything again.
7. **A `DateTimeKind.Local` from Exchange is read in the web server's zone.** `Models/ReportedTime.cs`
   (around 119) and `ShowReported` (`LookupModels.cs` around 259) call `ToUniversalTime()` on a `Local`
   value, interpreting a clock that came from another origin in this server's zone, so instants, sorts
   and labels can be wrong. Unless the live module contract proves `Local` means this server's zone
   (label it an assumption either way), treat an externally reported `Local` like an unzoned value
   ("time zone not reported"; grouped with `Unspecified`), or carry an explicit origin offset before
   comparing or labelling. Test: a `Local` value is shown as reported with no zone label and sorts in
   the zone-less group. Mutation: `ToUniversalTime()` again. The aud-a7f Check Status path shares
   `ShowReported`; keep both consistent and say so.
