# aud-a23f: A migration batch whose row cannot be read is dropped from the list with only a log warning

**Severity**: MEDIUM - a silent omission from an operator-facing list (Known Failure Class 2: per-item
failures must be aggregated and shown, never swallowed). `GetMigrationBatchesAsync` catches a
per-row exception (for example a count that `Convert.ToInt32` cannot read) and skips the batch,
logging a warning; the page shows the remaining batches as if the list were complete, so an
operator looking for that batch concludes it does not exist.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-a92cf96af45d6d9c6` (forked at `9ef0a225`)
**Commit**: -
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

Pending.
