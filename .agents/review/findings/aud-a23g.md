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
**Status**: Merged to master (`cafef918`); closed on guard proof; codex ops-batch-11 clean
**Branch**: `worktree-agent-ac856fb3e1e8d9617` (forked at `d7d41457`)
**Commit**: `539f2ccf` (fix, tests, comments, record); plan `0c64d49c`; merged `cafef918`
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

Built as the Plan, with three mutations added to its list (M1e, M1f, M6c) and one test
(`AFailureWithNoReason_IsStillAFailure`). **A failed load clears the list** (the Plan argues it): a failed result and
a throw before the service returns set the same state - nothing listed, no Export, nothing ticked, the open batch
closed. Line numbers are at the final bytes; `Components/Pages/Migration.razor` is 4377 lines before and after.

- `Services/MigrationService.cs` `GetMigrationBatchesAsync` (612-736): `-ErrorAction Stop` (627), `InvokeOptional`
  (629) kept; the per-row catch (706) and the outer catch (722) take `when (!IsCancellation(ex))`; the outer catch
  returns `MigrationBatchList.Failed(FailureReason(ex))` (729). `IsCancellation` (737) searches the exception's chain;
  `FailureReason` (750) takes an `ActionPreferenceStopException`'s `ErrorRecord`, else `ResolvePsErrors`.
  `GetMigrationBatchUsersAsync`: `ItemsSynced`/`ItemsSkipped` through `ReportedCount.Read(...) ?? ReportedCount.Of(0)`
  (791-792).
- `Models/MigrationModels.cs`: `ScheduledCompletionUtc` (145) names an instant only for Kind Utc;
  `CompleteAfterZoneNotReported` (170) new; `MigrationBatchList` (209) gains `string? Failure = null` and `Failed`
  (215); `MigrationUserInfo.ItemsSynced`/`ItemsSkipped` (234-235) are `ReportedCount`.
- `Models/ReportedCount.cs`: `Count` is `long?` (36) and `Of` takes a `long` (56); `WholeNumber` (110) refuses a
  negative - the one sign check; `Integer` (116) reads Int64's range.
- `Models/ReportedTime.cs` `Compare` (117-130): only Kind Utc is an instant; two of a kind compare by ticks.
- `Models/LookupModels.cs` `ShowReported` (259-266): only Kind Utc converts; any other Kind is shown as reported,
  "(time zone not reported)" - for Check Status and Recipient Lookup as for Migration.
- `Components/Pages/Migration.razor`, in place - `git diff --numstat` 17/17, every changed region the same length on
  both sides (git shows 485 as +1 and 487-491 as -5 +4; together one region of seven lines):
  - 485: the failed-load branch - `alert-danger`, `role="alert"`, "The migration batches could not be read:
    <reason>", "No batch is listed until a load succeeds. Refresh to try again."; 486: the empty state's condition,
    unchanged in text; 488-491: the R28 comment, rewrapped from five lines to four;
  - 1226-1227: the R24a comment; 1229: "Taken" through `OutOfOfficeRequest.ShowReported(reportFetchedAtUtc.Value,
    TimeZoneInfo.Utc)`;
  - 1299: the item cell, `N0` for a count and the marker otherwise;
  - 1590: `batchListFailure`, the section comment its trailing comment; 1592: `unreadableBatches`' comment trailing;
  - 2292: `(migrationBatches, unreadableBatches, batchListFailure) = await ...`; 2297-2298: the page's catch through
    `MigrationBatchList.Failed(ex.Message)`, and `batchActionResult = null`;
  - 2311: the PruneSelection comment; 2492-2493: the Synced mailbox sort through `ReportedCount.Order`.
  - Unchanged: Export's guards (420, 4210), which withhold it once a failure leaves no list.

| Test | Pins |
| --- | --- |
| `MigrationBatchReadTests.AFailedBatchQuery_IsShownAsAFailure_NeverAsNoBatches` (throw, error) | **the finding**: `Failure` is the message - without the Stop prefix - and there are no batches or unreadable rows; through the page's own `LoadBatchList`, "The migration batches could not be read: <message>", and no "No migration batches found", no "There are currently no migration batches", no Export, and not the row the stub held |
| `AFailedReload_ListsNothing_OffersNoExport_AndLeavesNothingTicked` | a load that read (the batch listed, "Export batch list (1)"), then one that failed: the banner; the batch gone; no Export; nothing ticked; `migrationBatches` empty |
| `ALoadThatFailsBeforeTheQueryRuns_IsShownAsTheSameFailure` | the connect throws, so the page's catch: the same banner; not the empty state, not "Failed to load migration batches"; `batchActionResult` null |
| `AFailureWithNoReason_IsStillAFailure` (null, empty, blank) | "no reason was given" |
| `AMailboxCountThisAppCannotRead_IsTheMarkerInItsCell_AndEveryMailboxLoads` | **part 2**: "1,205" and "a few" beside readable counts. All three mailboxes listed; `ItemsSynced` 10, null, 7 and `ItemsSkipped` 2, null, 0 (absent); the marker once on the page, in the synced cell; no "Unable to load mailboxes" |
| `TheMailboxSyncedSort_PutsACountThisAppCannotReadAfterEveryCount_InBothDirections` (both directions) | the page's own `SortMailboxes`; the input puts the unreadable mailbox first |
| `ANegativeCount_IsTheMarker_NotACount` (Int32, Int64, text, Double) | **part 5**: `Count` null; the marker in the failed cell and the CSV; the Failed sort puts it after 0 and 2, both ways |
| `ACancellationWhileReadingARow_EscapesTheLoad` (`OperationCanceledException`, `PipelineStoppedException`) | **part 6**: the call throws, the cancellation in its chain, under PowerShell's `GetValueInvocationException` |
| `MigrationReportedTimeTests.TheReportsTakenTime_IsShownInUtc_AndSaysSo` (taken now; read back from the store) | **part 3**: "Taken yyyy-MM-dd HH:mm (UTC, UTC+00:00)"; the store's JSON hands the time back as Kind Utc |
| `ATimeOfKindLocal_IsShownAsReported_AndGivenNoZone` | **part 7**, Migration: all five reads of Kind Local - the header's three, the row's, the CSV's three - as reported, "(time zone not reported)", no "UTC"; the CompleteAfter is `AutoComplete`, `CompleteAfterZoneNotReported`, no schedule |
| `ACompleteAfterThatNamesNoZone_IsAutoCompleting_ButNotASchedule` (Unspecified, Local) | **part 4** through the real read |
| `TheTimeSorts_CompareInstants_AndPutTimesNamingNoZoneAfterThem` (changed; Created, LastSync; both directions) | part 7's sort: a Utc time and a `DateTimeOffset`'s instant first, by instant; the Local time among the Unspecified, by its clock, between them |
| `EveryReadableTime_IsShownInUtc...`, `EachTimeReportedInAnotherForm...` (changed) | the first takes `DateTimeOffset`s where it took Local values; the second drops its Local case, now the test above; the instant one and the two CompleteAfter controls assert the new flag false |
| `MigrationScheduleTests.ACompleteAfterThatNamesNoZone_IsNotASchedule_AndSaysSo` (Unspecified, Local) | **part 4** at the model, a day ahead: no schedule, the flag, not unreadable, `AutoComplete`. Replaces `ALocalTimeIsComparedInUtcRatherThanAsWritten`, whose premise ("Exchange returns local times") nothing proved |
| `OutOfOfficeRequestCaptureTests.AReadScheduleOfKindLocal_IsShownAsReported_AndIsGivenNone` (was `...IsShownAsTheSameInstant`) | part 7, Check Status: as reported, no zone, no "UTC" |
| `RecipientLookupReportedTimeTests.WithTheBrowsersZone_TimesOfKindLocal_AreShownAsReported_AndGivenNoZone` (New York, Kolkata) | part 7, Recipient Lookup; the browser-zone theory keeps its Kind-Utc cases |

**Against the unfixed code: not runnable as a whole.** The model changed (`MigrationBatchList`'s third member,
`ReportedCount` on the mailbox counts, the new flag), so the new tests cannot compile against the old one. Instead each
behaviour was put back at its own call site, and each revert fails the tests that pin it.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the solution rebuilt
(Release) and the 948 `Migration*`/`OutOfOffice*`/`RecipientLookup*`/`ClickGate*`/`ProgressRegistry*`/`OofSchedule*`
tests run, the file restored from its saved bytes, touched (a restored copy keeps its old mtime and MSBuild would skip
the rebuild), md5 re-confirmed equal; then rebuilt and the 948 re-run green. First round on the pre-comment bytes (LF:
service `542b788cda07fd61be8d20a39a5baad1`, page `b05ecbf18a9840b630707d05af9f541e`, `Models/MigrationModels.cs`
`91d6357e63ea20091d9dbe1ad85cab90`, `Models/ReportedCount.cs` `b73ffc22b740c88a530119efa8a83c32`,
`Models/LookupModels.cs` `73b79a812db4e254559ebf86a0a62fa6`, `Models/ReportedTime.cs`
`f33a8a1bec6b0f8be83b9638209ff20f`); there M7a's and M7b's mutated files were byte for byte the `d7d41457` ones
(`f1a42ca96c63a57dcbb648296200da72`, `34c94e7990a0d9039def8d27897a5da5`). Re-run on the final bytes with the same
results; the md5s below are the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| M1 (the brief's (1)): the outer catch returns an empty list again | service `f1f0656fb527b89145a6f603a5ec7198` | `0deefb99c7b4ad7d19b7979eba5e155f` | 3: `AFailedBatchQuery...` (throw, error), `AFailedReload...` |
| M1b: `-ErrorAction Ignore` again | service | `c7b48fffd5b691747de296ed7b09319e` | 3: the same - the stub's throw and its written error are swallowed |
| M1c: the page's catch keeps the list and the old dismissible banner | page `f35c335d8b18096c003d71178700a3b0` | `2f6bbdd90a5c15501ce3891d78ac7a3d` | 1: `ALoadThatFailsBeforeTheQueryRuns...` |
| M1d: the failed-load branch dropped (`else if (false)`) | page | `cc88fa929ac9b541724344e4bb914a3e` | 4: `AFailedBatchQuery...` (2), `AFailedReload...`, `ALoadThatFails...` - the empty state again |
| M1e: a failed result keeps the previous list | page | `126f47191b83ca33320d05e2e70e0972` | 1: `AFailedReload...` (Export offered, the tick kept) |
| M1f: the reason not unwrapped from the Stop exception | service | `0a781f0efe209f8572fbfee253b8cb2d` | 1: `AFailedBatchQuery...` (error) |
| M2 (the brief's (2)): `Convert.ToInt64` again on `ItemsSynced` | service | `2e1d7d9d3a903bb21df14f7fe152b9ed` | 3: `AMailboxCount...`, the Synced sort both ways - the whole list lost again |
| M2b: the same on `ItemsSkipped` | service | `88e9548e258a3b0d872522adbbe2f9c2` | 1: `AMailboxCount...` |
| M3 (the brief's (3)): "Taken" on the server's clock, unnamed | page | `f46347d5c34b04a781d3d1b4d8982741` | 2: `TheReportsTakenTime...` both |
| M4 (the brief's (4)): a zone-less `CompleteAfter` read as server-local (`ToUniversalTime()` for any Kind) | `Models/MigrationModels.cs` `a5b4416b8f0e1cf1f8bbb52c9b950f8f` | `d2a146f3345951e3fb3ddbfe7945c569` | 5: `ACompleteAfterThatNamesNoZone...` through the read (2) and at the model (2), `ATimeOfKindLocal...` |
| M5 (part 5): negatives accepted again | `Models/ReportedCount.cs` `1afec7da8750b7bbd7bab9f0b9e2ad8e` | `27ad7a4f521f5b5e1f43e60a45b23fff` | 4: `ANegativeCount...`, all four |
| M6 (part 6): the per-row catch catches everything again | service | `6f6e98e233b1d9fbbbeb59368cf6629d` | 2: `ACancellation...` both |
| M6b: the outer catch catches everything | service | `97526b1845705d5764d714cfaac4078a` | 2: the same - the cancellation became a failed read |
| M6c: `IsCancellation` looks at the exception only, not its chain | service | `85a00e788da890686d8d813299460957` | 2: the same - the adapter's wrapper hid it |
| M7a (part 7): `ShowReported` converts Local again (its `d7d41457` text) | `Models/LookupModels.cs` `fa8b54befd637c5bcff9a269c8995b79` | `540fdec41a64ea56513865a1938d5a39` | 4: `ATimeOfKindLocal...`, Check Status's Local case, Recipient Lookup's Local cases (2) |
| M7b (part 7): `Order` converts Local again (its `d7d41457` text) | `Models/ReportedTime.cs` `568c1be606a3c89e785755ec645b11d9` | `0d2fa9478beb5039fb4e5de89016f0d0` | 4: `TheTimeSorts...`, all four |

M1 to M4 are the brief's four; M5, M6 and M7a/M7b the folded-in parts' own. Final md5s (LF) of the tests:
`MigrationBatchReadTests.cs` `1c5b87fffe87e8337b89435e3adb8066`, `MigrationReportedTimeTests.cs`
`0b075814129fe6c660f3d04c0d926c97`, `MigrationScheduleTests.cs` `3f5b5eb64a3cf88d0880a798b0ed679a`,
`OutOfOfficeRequestCaptureTests.cs` `7f6199f391b02bc98d9ed42adba6b0e1`, `RecipientLookupReportedTimeTests.cs`
`6d69448f51c10de70ecfd93885d10a85`.

**Pins.** `ClickGateRegistry`: `Migration.razor` is 4377 lines before and after; the registered lines run to 1540 and
none is among 485-491, 1226-1229 or 1299; no registered snippet changed; `ExpectedLineCount` untouched - the
`ClickGate*` tests pass. `ProgressRegistry`: `LoadBatchList` still pins `MigrationSvc.GetMigrationBatchesAsync`
(2292), `DownloadCsvAsync` is unchanged, no handler or lifecycle method was added, and `migrationBatches` is still
written only by `LoadBatchList`. `MigrationStatusPageTests` pass unchanged ("No migration batches found" is still in
the page; `SortMailboxes` keeps its `"Synced" =>` arm); `MigrationCsvTests.Migration_WiresDownloadCsv` passes, the
export guard unchanged.

## Gates

On the final bytes (the branch at the aud-a23g fix commit, on `d7d41457`):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings, the count before this slice.
  The one in a changed file is `Migration.razor`'s existing CS8604 at 4051:56, the line it was on before.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5528 passed, 3 skipped (the known
  `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5531 in all; 5 m 8 s, no hang. The
  `ClickGate*` and `ProgressRegistry*` tests are among them.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `Services/MigrationService.cs` - `GetMigrationBatchesAsync` (`-ErrorAction Stop`, both catches, `Failed`);
  `IsCancellation`, `FailureReason`; the mailbox counts.
- `Models/MigrationModels.cs` - `ScheduledCompletionUtc`, `CompleteAfterZoneNotReported`; `MigrationBatchList.Failure`
  and `Failed`; the mailbox counts' type.
- `Models/ReportedCount.cs` - Int64; no negatives.
- `Models/ReportedTime.cs` - `Compare`: Local names no instant.
- `Models/LookupModels.cs` - `ShowReported`: Local shown as reported (part 7; the supervisor cleared the file).
- `Components/Pages/Migration.razor` - in place, 4377 lines.
- `ExchangeAdminWeb.Tests/MigrationBatchReadTests.cs` - the stub's failure switch, mailbox rows and failing connect;
  8 tests (16 cases).
- `ExchangeAdminWeb.Tests/MigrationReportedTimeTests.cs` - 3 tests (5 cases); four changed.
- `ExchangeAdminWeb.Tests/MigrationScheduleTests.cs` - the Local test replaced by a theory (2 cases); two asserts.
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs`, `RecipientLookupReportedTimeTests.cs` - their Kind-Local
  expectations, which part 7's shared `ShowReported` reverses; one new theory (2 cases) in the second.
- This record.

## Known gaps

- **Assumptions, unverified (no live Exchange call):** (a) that `Get-MigrationBatch` with no batches writes no error -
  if it did, an empty tenant would show the failure banner, which is visible, not silent; (b) what a Kind-Local value
  from the Exchange Online module means. If the module hands back Local values that ARE this server's clock (as
  PowerShell's deserializer and `System.Text.Json` produce from a time with an offset), every such time on Migration,
  Check Status and Recipient Lookup now reads "(time zone not reported)" where it showed the instant: honest, but
  less. One live read would settle it, and with that proof the Local branch could come back; (c) the probe's
  PowerShell behaviour (`Ignore` running past a throw; `Stop`'s `ActionPreferenceStopException`) was seen on this
  host's PowerShell 7 SDK with script functions, not with the Exchange Online module's own.
- **`ReportedCount` reads up to Int64's maximum, not the folded-in part 5's `int.MaxValue`**: the mailbox item counts
  were Int64 reads, and an Int32 count would make a readable one above 2,147,483,647 the marker. Negatives are refused
  as part 5 asks.
- **A failed reload clears the selection and closes the open batch** - the cost the Plan accepts for listing nothing
  stale. Refresh restores the list; the batch has to be opened again.
- No circuit or browser ran: the page's compiled render tree was read directly.
- The page's catch shows a cancellation as a load that failed ("could not be read: ..."): never a partial list.
- The status bar still says a failed batch-list load finished: `LoadBatchList` completes its activity `true` in its
  `finally`, as every load on this page does; the page shows the failure. Candidate.
- Candidates seen on the way, not fixed here:
  - `CheckMigrationEligibilityAsync` reads `Get-MigrationUser -Identity` and `Get-Mailbox -Identity` under
    `-ErrorAction Ignore` (76-89): by the probe, a failed read - a throw included - comes back as no result, which the
    check reads as "no migration in progress" or "no cloud mailbox".
  - `ApproveMigrationUserAsync` runs `Set-MigrationBatch` twice and `Complete-MigrationBatch` under
    `-ErrorAction Ignore` with `InvokeOptional`: none of them can fail, so "initiated completion" is reported whether
    or not `Complete-MigrationBatch` ran.
  - `GetMigrationBatchUsersAsync`: a row with no Identity is a mailbox named "Unknown", which an action can be aimed
    at, and a row whose getter throws loses the whole list - the aud-a23f shape on the mailbox side.
  - `ExchangeServiceBase.Invoke` fails with "An unknown error occurred." on `HadErrors` with an empty error stream,
    which a nested command's suppressed error sets (the probe); not seen live.
  - Under `-ErrorAction Stop` the error stream is empty (the probe), so `RunAsync`'s failure message for an error a
    cmdlet wrote carries "The running command stopped because..." across the app.
  - The Migration export file names (`Migration_<server clock>.csv`, the report zip's) are the server's clock, unnamed.
- Version bumps are the supervisor's: Migration; Out of Office and Recipient Lookup, whose Kind-Local times now read
  as reported; and whether the shared models count as shared infrastructure for the base version.

## Closeout

Fixed on branch; not merged, not pushed.

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
