# aud-a23f: A migration batch whose row cannot be read is dropped from the list with only a log warning

**Severity**: MEDIUM - a silent omission from an operator-facing list (Known Failure Class 2: per-item
failures must be aggregated and shown, never swallowed). `GetMigrationBatchesAsync` catches a
per-row exception (for example a count that `Convert.ToInt32` cannot read) and skips the batch,
logging a warning; the page shows the remaining batches as if the list were complete, so an
operator looking for that batch concludes it does not exist.
**Status**: Merged to master (`90d57011`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-a92cf96af45d6d9c6` (forked at `9ef0a225`)
**Commit**: `73ba215d` (merged `90d57011`)
**Reviewer**: implementer (surfaced by the aud-a23d/a7i agent, 2026-10-22, candidate 3 of its hand-back)

## Evidence

At `4c54621b`, `Services/MigrationService.cs` `GetMigrationBatchesAsync`: the per-row try/catch that
logs and continues; the result carries only the batches that parsed.

## Approach (to be written by the implementing agent before code)

- The result carries, beside the batches, the rows that could not be read: the batch identity as far
  as it was read (name or id) and the reason. The page shows them as a visible warning row or banner
  ("N batches could not be read: <name>: <reason>") rather than omitting them; the CSV lists them too.
  Nothing is guessed about the unreadable fields; the aud-a23d `ReportedTime` marker already covers
  unreadable times - this covers the other fields (counts, status, type).
- Read each field defensively through the same pattern (a typed reader that returns a marker, not a
  throw), so one bad field does not lose the whole row; only a row with no identity at all goes to
  the unreadable list.
- `Migration.razor` has line-keyed `ClickGateRegistry` entries and `ExpectedLineCount` 4377: edit in
  place or re-anchor per the playbook. Tests: a stub batch with an unreadable count still appears
  with the marker; a row that throws is listed as unreadable with its reason and the page shows the
  warning; the CSV carries it. Mutation at the call site: swallow and skip again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Module-scoped: Migration (its version bump is the supervisor's; `Modules/ModuleCatalog.cs` and
`ExchangeAdminWeb.csproj` are not edited here). Lands before aud-a23e. No new JS interop, handler or
lifecycle method. Written before the code; line numbers are at `9ef0a225`.

**The rule.** A value this app cannot read is a marker in its own field, never a lost row. A row that
reports no identity, or whose reading throws anyway, is listed as unreadable with its identity as far
as it was read and the reason. No row is dropped.

1. `Models/ReportedCount.cs` (new), the `ReportedTime` shape for a count: `Count` (`int?`),
   `UnreadableType`, `UnreadableText`; `Show()` - the count (invariant culture) or the marker;
   `ToString()` returns `Show()`, so `@batch.TotalCount` and the CSV's `.ToString()` render it with
   their text unchanged; `Of(int)`; `Read(object?)` - null when nothing was reported, `PSObject`
   wrappers unwrapped one at a time, a whole number of any numeric type within Int32's range, text of
   one in the invariant culture's integer form, and anything else (a fraction, a flag, other text, an
   object) unreadable, named and quoted as `ReportedTime` names one (`Convert.ToInt32` threw on a
   wrapped value and rounded a fraction); `Order(bool ascending)` - counts in the direction asked, an
   unreadable one after every count in both directions.
2. `Models/ReportedTime.cs`: the unwrap, the naming and quoting of an unreadable value (`Read` 100-133,
   `FirstTypeName` 135, `TextOf` 145) and the marker's spelling (`Show` 54-59) become `internal static`
   helpers `ReportedCount` shares, so a page says one thing about any value it cannot read. Behaviour
   unchanged.
3. `Models/MigrationModels.cs`:
   - `MigrationBatchInfo.TotalCount`/`PendingCount`/`SyncedCount`/`FinalizedCount`/`FailedCount`
     (80-85) become `ReportedCount`, default `Of(0)`; `AutoStart` (87) becomes `bool?`, null when what
     Exchange reported cannot be read as a flag (nothing reads it today; false would be a guess).
   - New `UnreadableMigrationBatch(string? Identity, string Reason)`, with `Shown` and a static
     `Warning(rows)` - "N batch(es) could not be read: <name>: <reason>; ..." - spelled once.
   - New `MigrationBatchList(Batches, Unreadable)`, a positional record: its deconstruction keeps the
     page's load on its one line.
4. `Services/MigrationService.cs` `GetMigrationBatchesAsync` (612-694) returns `MigrationBatchList`:
   - the Identity is read first (630): absent or blank, the row goes to `Unreadable` as a row with no
     Identity - no longer a batch named "Unknown" that an action could be aimed at;
   - the five counts (632, 636-638, 669) through `ReportedCount.Read(...) ?? ReportedCount.Of(0)` -
     absent is still 0, as it always read here; `AutoStart` (648) through a flag reader (a bool, or
     True/False text; anything else null) - `Convert.ToBoolean` threw on a wrapped value;
   - the per-row catch (680-683) records the row - its identity as far as read and the exception's
     message - and logs, where it only logged. `Status`, `WorkflowStage` and `TargetEndpoint` stay
     `ToString()`: every value has a text, so their only failure is the row throwing, which this catch
     now lists rather than drops;
   - the outer catch (688-692) is unchanged: a failed query is not this finding (candidate).
5. `Components/Pages/Migration.razor`, in place - its line-keyed `ClickGateRegistry` entries and
   `ExpectedLineCount` (4377) mean every edit replaces lines one for one:
   - 467 (the second of two blank lines): the warning - `alert-warning`, `role="alert"` - rendered when
     the list is not loading and any row was unreadable;
   - 493-494: with no readable batch but unreadable rows, "No migration batch could be read" in place of
     "There are currently no migration batches";
   - 671: `FailedCount.Count is not 0`, so an unreadable failed count shows its marker, not "0";
   - 1049, 1052, 1053: `PendingCount.Count`/`TotalCount.Count` - an unreadable count is neither "N
     pending. Batch not started." nor "contains no mailboxes", so it reads "Mailbox details are not
     available yet.";
   - 1590-1592: the `// Migration status` comment moves into the blank line above it and
     `unreadableBatches` joins `migrationBatches` below it;
   - 2292: `(migrationBatches, unreadableBatches) = await MigrationSvc.GetMigrationBatchesAsync();` - a
     failed load leaves both as they were, as before;
   - 4161-4163: the Total, Synced and Failed sorts through `ReportedCount.Order`;
   - 4174-4182, 4203: `BuildCsv(rows, unreadable)` appends one row per unreadable row after the
     filtered, sorted batches - its identity (empty when none) under BatchName, "could not be read:
     <reason>" under Status, the other cells empty;
   - 4235: `BuildCsv(rows, unreadableBatches)`; 4244: the audit's target counts every row the CSV
     carries;
   - unchanged in text: the count cells (666, 668, 673, 777, 779, 832) and the CSV's `.ToString()`
     (4197-4200), which `ReportedCount.ToString()` makes show the marker; Export is still offered only
     with a readable batch (420, 4210) - with none, the warning names every row.
6. **Assumption, unverified (no live Exchange call):** the types `Get-MigrationBatch` hands its counts
   and `AutoStart` back in through the Exchange Online module. The old reads assumed what `Convert`
   accepts; the fix depends on no answer.

**Tests** - new `ExchangeAdminWeb.Tests/MigrationBatchReadTests.cs`: the real `MigrationService` over a
stub runspace whose `Get-MigrationBatch` emits the rows each test builds (`PSObject`s with note
properties, or a .NET object whose property getter throws), and the page's render tree and CSV.

- Each of the five counts reported as text in another form, a fraction and a deserialized object: the
  batch is listed and nothing is unreadable; the row's cell, the header and the CSV show the marker;
  the Total, Synced and Failed sorts put it after every count in both directions.
- A count as a `PSObject` wrapping it, an Int64, text "12" and 12.0: read as that count.
- An unreadable pending or total count: neither "Batch not started" nor "contains no mailboxes". An
  unreadable failed count: its marker, not "0". An unreadable `AutoStart`: the batch kept, `AutoStart`
  null.
- **The finding**: a row whose `TotalCount` getter throws, beside a readable one - the readable one
  listed, the other in `Unreadable` with its identity and the exception's message; the page shows "1
  batch could not be read: <name>: <reason>"; the CSV's last row carries it.
- A row with no Identity: in `Unreadable`, and no batch "Unknown". Only unreadable rows: the warning,
  and not "There are currently no migration batches".
- `MigrationReportedTimeTests`, `MigrationMailboxLoadTests`, `MigrationCsvTests`: `.Batches`,
  `ReportedCount.Of`, `BuildCsv(rows, [])`; expectations unchanged.

**Mutations at the call site** (md5 before and after, the failing tests named, restored, md5 equal,
touched, rebuilt, green): F1 the per-row catch logs and skips again; F2 the page's warning dropped (467
blank again). Also F3 `TotalCount` read with `Convert.ToInt32` again; F4 the CSV drops the unreadable
rows; F5 an absent Identity named "Unknown" again.

**Gates**: Release build; full suite with `--blame-hang`; `dotnet format --verify-no-changes`;
`git diff --check HEAD`; ASCII lint after `git add -A`; `ClickGate*` and `ProgressRegistry*` passing
(Migration's `ExpectedLineCount` unchanged). Comments written after the mutation round.

## Guard proof

Built as the Plan. Line numbers are at the final bytes; `Components/Pages/Migration.razor` is 4377 lines
before and after.

- `Models/ReportedCount.cs` (new): `Count`, `UnreadableType`, `UnreadableText`; `Show()`, and `ToString()`
  returning it; `Of`; `Read` - wrappers unwrapped, a whole number of any numeric type within Int32's range,
  invariant-culture integer text, anything else named and quoted by `ReportedTime.Describe`;
  `Order(bool ascending)`.
- `Models/ReportedTime.cs`: `Marker`, `Unwrap` and `Describe` are `internal static`, moved out of `Show` and
  `Read` with their behaviour unchanged; `Show` and `Read` call them. The aud-a7h, aud-a7i and aud-a23d
  tests pass unchanged.
- `Models/MigrationModels.cs`: the five counts are `ReportedCount`, default `Of(0)`; `AutoStart` is `bool?`;
  `UnreadableMigrationBatch` (`Shown`, `Warning`) and `MigrationBatchList` are new.
- `Services/MigrationService.cs` `GetMigrationBatchesAsync` returns `MigrationBatchList`: the Identity is read
  first, and absent or blank lists the row as "a row with no Identity"; the counts go through
  `ReportedCount.Read(...) ?? ReportedCount.Of(0)` and `AutoStart` through `ReadFlag`; the per-row catch logs
  the row and lists it - its identity as far as read, the exception's message, and "a row whose Identity
  could not be read: <message>" when the Identity is what threw. Text fields and the outer catch unchanged.
- `Components/Pages/Migration.razor`, in place: `git diff --numstat` 30/30 (16 lines of code, 14 of comments
  rewritten in their own lines), and every changed region is the same length on both sides. Git shows
  1590-1592 as two hunks (-2 +1, then +1): the `// Migration status` comment moved up into the blank line and
  the new field followed `migrationBatches`, so no line outside 1590-1592 moved:
  - 466: the warning (the second of two blank lines); 487-491: the R28 comment rewritten; 493-494: the
    empty state;
  - 670-671: the failed cell, `Count is not 0`; 1048-1049, 1052, 1054: the pending and total patterns;
  - 1590-1592: `unreadableBatches`; 2292: `(migrationBatches, unreadableBatches) = await ...`;
  - 4161-4163: the three count sorts through `ReportedCount.Order`; 4175-4180: the CSV summary; 4182:
    `BuildCsv(rows, unreadable)`; 4203: the unreadable rows appended; 4235: the call; 4244: the audit target.
  - The count cells (666, 668, 673, 777, 779, 832) and the CSV's `.ToString()` (4197-4200) are unchanged in
    text and show the marker through `ReportedCount.ToString()`.

| Test (`ExchangeAdminWeb.Tests/MigrationBatchReadTests.cs`) | Pins |
| --- | --- |
| `ARowThatThrowsWhileBeingRead_IsListedWithItsIdentityAndReason_BesideTheBatchesThatRead` | **the finding**: a .NET row whose `TotalCount` getter throws, as a live module object's could, beside a readable row. The readable one is listed; the other is in `Unreadable` with its identity and the exception's message; the page's warning reads "1 batch could not be read: Wave 13 / Finance: <message>"; the CSV's last row is its identity, then "could not be read: ...", then nine empty cells |
| `ARowWhoseIdentityCannotBeRead_IsListedAsSuch_WithTheReason` | the Identity getter throws: listed with no identity, "a row whose Identity could not be read: <message>" |
| `ARowWithNoIdentity_IsListedAsUnreadable_NotAsABatchNamedUnknown` | an absent and a blank Identity: two unreadable rows and no batch "Unknown". With nothing readable the page shows the warning and "No migration batch could be read", not "There are currently no migration batches"; the CSV has both rows |
| `NoUnreadableRow_ShowsNoWarning` | the control |
| `ACountThisAppCannotRead_KeepsItsBatch_AndIsShownAsTheMarkerWhereTheCountGoes` (text "1,205", 12.5, a deserialized object) | all five counts unreadable. The batch is listed, nothing is unreadable, every `Count` is null; the marker shows in the row's Total, Synced and Failed and the header's Total (4) and in the CSV's Total, Synced, Finalized and Failed (4); the page says neither "Batch not started" nor "contains no mailboxes" but "Mailbox details are not available yet." |
| `ACountReportedInAnotherForm_IsReadAsThatCount` (a wrapped Int32, Int64, Int16, text " 12 ", 12.0, 12m) | each read as 12, in the model and the CSV |
| `TheCountSorts_PutACountThisAppCannotReadAfterEveryCount_InBothDirections` (Total, Synced, Failed; both directions) | the page's own `SortBatches`; the input puts the unreadable row first |
| `AnAutoStartThisAppCannotRead_KeepsItsBatch_AndIsNotTakenForFalse` ("yes", a wrapped true, none) | null, true, false; the batch kept each time |
| `MigrationReportedTimeTests`, `MigrationMailboxLoadTests`, `MigrationCsvTests` (changed) | `.Batches`, `ReportedCount.Of`, `BuildCsv(rows, [])`; every expectation unchanged and passing |

**Against the unfixed code: not runnable as a whole.** The model's types changed (`int` to `ReportedCount`,
the list to `MigrationBatchList`), so the new tests cannot compile against the old model. Instead each
behaviour was put back at its own call site (table below), and each revert fails the tests that pin it.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the
solution rebuilt (Release) and the 828 `Migration*`/`ClickGate*`/`ProgressRegistry*` tests run, the file
restored from its saved bytes, touched (a restored copy keeps its old mtime and MSBuild would skip the
rebuild), md5 re-confirmed equal, rebuilt, and the 828 re-run green. First round on the pre-comment bytes
(LF: `Services/MigrationService.cs` `37567c1d5e2fc878549abc5cd39f1736`, `Components/Pages/Migration.razor`
`fa9c8c5cbd67f3a2dce830e15a016e05`), then re-run on the final bytes with the same results; the md5s below
are the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| F1 (the brief's): the per-row catch logs and skips again | service `1f7d5676be75576bbd11b09985972da6` | `7371b12ed64f30531e6d25d4e737b518` | 2: `ARowThatThrows...`, `ARowWhoseIdentityCannotBeRead...` |
| F2 (the brief's): the page's warning dropped (466 blank again) | page `682b1b1506e8546bb6d928431b1bd5f1` | `57667f5d21280a9f49cbc75f274e09bb` | 2: `ARowThatThrows...`, `ARowWithNoIdentity...` |
| F3: `TotalCount` read with `Convert.ToInt32` again | service | `387b8627af459ffc530b76c354297112` | 6: the three unreadable forms (the batch is lost again), the wrapped Int32, the Total sort both ways |
| F4: the CSV drops the unreadable rows | page | `d8dddfe068e2e8450d53d66b7687b3c1` | 2: `ARowThatThrows...`, `ARowWithNoIdentity...` |
| F5: an absent Identity named "Unknown" again | service | `331be944359870caca8eb787d03fb1f0` | 1: `ARowWithNoIdentity...` |
| F6: the empty state says "There are currently no migration batches" again | page | `96723c582fedfd97fc34e28fa0249270` | 1: `ARowWithNoIdentity...` |
| F7: the failed cell back to `> 0` (an unreadable count shows "0") | page | `1d53fcdbf8a69e9735bedce8d3df1836` | 3: the three unreadable forms |
| F8: an unreadable pending count taken for more than 0 (`not 0`) | page | `6a52c4873ed12b616f0d76701d78380a` | 3: the three unreadable forms ("Batch not started") |

F1 and F2 are the two the brief names. F3 to F8 pin the reader, the CSV, the identity rule and each in-place
page edit. Comments were written after the first round. Final md5s (LF) of the other files:
`Models/ReportedCount.cs` `d9cda7a39e711d02e1144fee1fff87be`, `Models/ReportedTime.cs`
`e8e12aedaa8bdcfcfd894afffc5575c5`, `Models/MigrationModels.cs` `3526bbc6bf4121f141c2144a566c2bce`; tests
`MigrationBatchReadTests.cs` `cf1e3aef3349ee0282afeba4e5223012`, `MigrationCsvTests.cs`
`1538d314685139ee21d085d6afd4820f`, `MigrationMailboxLoadTests.cs` `d209aa5869860d94cee0692a1c66c909`,
`MigrationReportedTimeTests.cs` `4bef0d61f14e1ce8a43f5e7fe683f586`.

**Pins.** `ClickGateRegistry`: `Migration.razor` is 4377 lines before and after, no edited line is a
registered line (the registered lines run to 1540, and none is 466, 487-494, 670-671 or 1048-1054), and no
registered snippet changed; `ExpectedLineCount` untouched - the `ClickGate*` tests pass. `ProgressRegistry`:
`LoadBatchList` still pins `MigrationSvc.GetMigrationBatchesAsync`, `DownloadCsvAsync` still `BuildCsv(`, and
no handler or lifecycle method was added. `MigrationStatusPageTests` pass unchanged: the count cell
`<td class="mig-count">@batch.TotalCount</td>` twice, `@DirectionIcon(batch.Direction)` twice, "No migration
batches found", and a `"Total" =>` arm in `SortBatches`. `MigrationCsvTests.Migration_WiresDownloadCsv`
passes: the export's guard is unchanged.

## Gates

On the final bytes (the branch at the aud-a23f commit, on `9ef0a225`):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings, the count aud-a23d
  recorded. The one in a changed file is `Migration.razor`'s existing CS8604 at 4051:56, the line it was on
  before.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5367 passed, 3 skipped (the known
  `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5370 in all, 22 of them new cases;
  5 m 20 s, no hang. The `ClickGate*` and `ProgressRegistry*` tests are among them.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `Models/ReportedCount.cs` - new.
- `Models/ReportedTime.cs` - `Marker`, `Unwrap`, `Describe` shared; comments.
- `Models/MigrationModels.cs` - the counts, `AutoStart`, `UnreadableMigrationBatch`, `MigrationBatchList`.
- `Services/MigrationService.cs` - `GetMigrationBatchesAsync`; `ReadFlag`.
- `Components/Pages/Migration.razor` - in place, 4377 lines.
- `ExchangeAdminWeb.Tests/MigrationBatchReadTests.cs` - new, 8 tests (22 cases).
- `ExchangeAdminWeb.Tests/MigrationCsvTests.cs`, `MigrationMailboxLoadTests.cs`, `MigrationReportedTimeTests.cs`
  - the new types and signatures.
- This record.

## Known gaps

- **Assumption, unverified (no live Exchange call):** the types the live module reports the counts and
  `AutoStart` in.
- **No circuit or browser ran**: the page's compiled render tree was read directly, as in
  `MigrationMailboxLoadTests`.
- Export is offered only when at least one batch could be read (the guard is unchanged). With only unreadable
  rows the warning names every one, but no CSV can be taken. The "Export batch list (N)" label counts the
  listed batches; the file carries the unreadable rows after them, and the audit target counts both.
- A row whose reading throws goes to the warning even when its identity was read: a deserialized row has no
  property that can throw, so this is the backstop, not the path.
- **A failed `Get-MigrationBatch` still reads as no batches**: the outer catch returns an empty result, which
  the page shows as "No migration batches found". Pre-existing, not this finding. Candidate.
- `GetMigrationBatchUsersAsync` reads `ItemsSynced`/`ItemsSkipped` with `Convert.ToInt64`; a value it cannot
  convert fails the whole mailbox load. That is shown ("Unable to load mailboxes"), not silent. Candidate.
- Test harness: `new PSObject(12)` does not wrap the int (its base is an empty `PSCustomObject`), so the
  wrapped-count case is built with `PSObject.AsPSObject`.
- The Migration version bump is the supervisor's. So is whether the shared helpers in `Models/ReportedTime.cs`
  count as a shared-infrastructure change for the base version.

## Closeout

Fixed on branch; not merged, not pushed.
