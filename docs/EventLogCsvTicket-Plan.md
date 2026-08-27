# Event Log CSV ticket column

Status: Implemented 2026-08-27 (owner go the same day). S1 `d54b33f`; S2 is the commit
that set this status. NOT DEPLOYED, and the four manual checks in section 8 have not
been run - they need a deployed instance and ride the next deploy.
Owner: Michael
Last verified against code: `d54b33f` / 2026-08-27
Module: `AdminEventLog` `1.0.3` -> `1.1.0` (module-scoped; no base app bump)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request 2026-08-20: *"we need a plan to add ticket info to the .csv exports from
the eventlog page"*.

## 1. Goal

The Event Log page already stores a ticket number on nearly every audited action. The
on-screen expanded row already shows it. The Download CSV button does not write it.

Done means: a CSV downloaded from `/admin-event-log` includes the ticket number that
was recorded with each event, in a column named `Ticket`, so an operator can sort or
filter the file by ticket without opening each row in the app.

"Ticket info" is the `ticket` string already written on the audit (and, when
diagnostics are on, trace) JSONL event. It is not a live ServiceNow title, state, or
assignee. Constitution, External Integrations: *"Ticket fields are plain audit
metadata unless ServiceNow validation or writeback is explicitly requested."* This
request did not ask for ServiceNow.

## 2. Non-goals

- A Ticket column on the on-screen table. Expanded-row details already surface the
  field (`ExtractAuditDetails` does not treat `ticket` as a summary field,
  `AdminEventLog.razor:973-984`). Pull this in only if the owner asks.
- A ticket filter on the page.
- Changing how tickets are captured at write time (`AuditService`, module pages).
- Backfilling historical events that omitted `ticket` (empty ticket is already omitted
  from JSONL: `JsonlLogService.cs:19,68` and `AuditServiceTests.LogMailboxPermission_EmptyTicketOmitsField`).
- Calling ServiceNow at export time.
- Changing `docs/CSV_FORMAT.md` (that file is bulk *upload* format, not Event Log export).
- Changing the Download CSV button's enablement, filename, or `downloadFile` JS path.
- Adding ticket to diagnostic (extended-log) rows, which have no ticket field.

## 3. Acceptance criteria

- AC1: The CSV header is exactly
  `Time,Source,User,IP,Action,Category,Target,Result,Ticket` in that order. The first
  eight names and their order are unchanged; `Ticket` is appended.
- AC2: For an audit event whose JSONL object has `"ticket":"INC001"`, the matching CSV
  row's `Ticket` cell is `INC001`.
- AC3: For an event with no `ticket` property, or a blank ticket, the matching CSV row
  is still written and its `Ticket` cell is empty.
- AC4: The CSV still contains the filtered set the button already exports: current
  filters, Audit-only when diagnostics are off, Audit+Trace when diagnostics are on
  (`AdminEventLog.razor:1019-1022`). Trace rows that carry `ticket` (operation traces
  do, `OperationTraceService.cs:169`) get that value; trace/diagnostic rows that do
  not get an empty cell.
- AC5: A `Target` (or any other field) containing a comma or quote is still quoted by
  CsvHelper the way today's export already quotes it. Adding the column must not
  switch the writer.
- AC6: `AdminEventLog` catalog version is `1.1.0`. Base app version is unchanged.
- AC7: `DownloadCsv` in `AdminEventLog.razor` calls the extracted formatter. A page
  that still has its own eight `WriteField` calls fails the wiring test even if the
  unused formatter is correct.

## 4. Failure behavior

No new external dependency. The export already runs in-process against in-memory
`filteredEvents`.

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|
| Event JSON has no `ticket` (legacy, empty, diagnostic) | Formatter writes an empty cell | A row with a blank Ticket column | Logs unchanged |
| `ticket` is present but not a JSON string | Treated as missing (`TryGetString` already requires `ValueKind.String`, `AdminEventLog.razor:873-874`) | Blank Ticket cell | Logs unchanged |
| `filteredEvents` is empty | Button stays disabled; method returns at the existing `Count == 0` guard (`:1017`) | No download | Unchanged |
| CsvHelper / JS `downloadFile` throws | Same as today: uncaught, Blazor error UI. Do not add a new catch | Existing error UI | No partial file is the JS helper's problem, not this change |
| Formatter given zero rows | Returns a header-only CSV. The page never calls it in that case (`:1024`) | N/A from the button | Unchanged |

## 5. Rollback / blast radius

Revert the commit(s). No schema, no config, no JSONL format change, no authorization
change. Historical audit files already carry `ticket`; this work only reads a field
the writer has been emitting.

Blast radius is the Event Log CSV and any consumer of `event_log_*.csv`. Named-header
consumers pick up `Ticket`. Positional consumers of the old eight-column file see a
ninth column at the end; they do not see the first eight columns reorder. The
on-screen table, filters, undo path, and audit writers are untouched.

## 6. Design sketch

### Current code (read, not remembered)

`Components/Pages/AdminEventLog.razor` `DownloadCsv` (`:1015-1056`) writes eight
fields with CsvHelper and never reads `ticket`:

```
Time, Source, User, IP, Action, Category, Target, Result
```

`MergedLogEntry` (`:1087-1107`) has no `Ticket` property.

`ParseAuditLine` (`:535-590`) copies `user`/`ip`/`action`/`category`/`target`/`result`
and stuffs the rest into `RawFields` + `Details`. Ticket therefore appears in the
expanded-row details as the humanized key `ticket` (all-lowercase, so
`HumanizePropertyName` does not insert a space) and is dropped on the floor for CSV.

`ParseTraceLine` (`:598-654`) skips `ts`/`eventType`/`stage`/`module`/`action`/`user`/
`ip`/`operationId` when building details, so `ticket` already lands in Details for
traces, and is likewise not a first-class field.

Audit writers always set `["ticket"]` to the number or `null`
(`AuditService.cs:60` and the sibling log methods). `JsonlLogService.WriteToFile`
drops nulls (`:68`) and `DefaultIgnoreCondition = WhenWritingNull` (`:19`), so a
missing property and a blank ticket are the same on disk. Trace events carry
`["ticket"] = context.Ticket` (`OperationTraceService.cs:169`), also dropped when
null.

### What to add

1. `string Ticket { get; set; } = "";` on `MergedLogEntry`.
2. `Ticket = TryGetString(root, "ticket") ?? ""` in `ParseAuditLine` and
   `ParseTraceLine`. `ParseExtendedLine` leaves the default empty string.
3. A static formatter, no DI, used only by this page:

   `Services/EventLogCsvFormatter.cs`

   ```
   public sealed record EventLogCsvRow(
       string Time, string Source, string User, string Ip,
       string Action, string Category, string Target, string Result,
       string Ticket);

   public static class EventLogCsvFormatter
   {
       public static string Write(IEnumerable<EventLogCsvRow> rows);
   }
   ```

   `Write` is the current CsvHelper loop plus the ninth field. Header names are
   literals in this method and nowhere else. Do not introduce a CsvHelper
   `ClassMap`; the current code uses `WriteField` and AC5 requires keeping that.

4. `DownloadCsv` maps each `MergedLogEntry` to `EventLogCsvRow` (use `FullTarget`
   for `Target`, as today at `:1047`) and passes the list to
   `EventLogCsvFormatter.Write`. Bytes / base64 / `downloadFile` stay in the page.

Do not move parsing, filtering, or the JS download out of the page. Do not register
anything in DI. Do not bump the base app version: a helper used by one module is
module-scoped (Constitution, Deployment And Versioning).

### Why extract the loop

There is no bUnit harness. A source-text guard that the page contains
`WriteField("Ticket")` can be satisfied by a comment (this repo has paid that twice:
`blr-3`, `blr-4`). The formatter is the existing loop pulled sideways so a test can
call it. The page wiring guard (AC7) is what stops the page from ignoring it.

## 7. Task breakdown

One commit per slice.

**S1 - formatter, parse, page wiring, tests.** Serves AC1-AC5, AC7.

- Add `EventLogCsvFormatter` + `EventLogCsvRow` as specified in §6.
- Add `Ticket` to `MergedLogEntry`; set it in `ParseAuditLine` and `ParseTraceLine`.
- Point `DownloadCsv` at the formatter. Delete the inline `WriteField` loop.
- Add `ExchangeAdminWeb.Tests/EventLogCsvFormatterTests.cs` (see §8).
- Add the AC7 wiring assertion to `AuditCategoryFilingTests` (same `FindRepoFile`
  helper, same file already scanning `AdminEventLog.razor`) or a sibling test class
  in that file. Assert `DownloadCsv`'s body contains `EventLogCsvFormatter.Write`
  and does not contain `csv.WriteField("Time")`.

**S2 - version and README.** Serves AC6.

- `Modules/ModuleCatalog.cs` `AdminEventLog` `Version` `1.0.3` -> `1.1.0` (`:594`).
  NamedLocations is also `1.0.3` (`:439`); do not touch it.
- `README.md` Admin Event Log section (`:89-95`) gains one bullet: Download CSV
  includes a `Ticket` column from the stored audit/trace field. Do not "fix" the
  stale `Security:AdminGroups` sentence in the same heading; that is unrelated
  drift.

## 8. Test plan

Every AC appears at least once.

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1 | `Write_HeaderIsEightExistingColumnsThenTicket` | Header line splits to those nine names in that order | Delete the Ticket `WriteField`; FAIL |
| AC2 | `Write_CopiesTicketValue` | A row with `Ticket = "INC001"` produces a cell `INC001` on that row | Hardcode Ticket to `""` in the formatter; FAIL |
| AC3 | `Write_EmptyTicketStillEmitsNineColumns` | `Ticket = ""` yields an empty last cell, not a dropped column | `if (string.IsNullOrEmpty(row.Ticket)) continue;` skipping the field; FAIL (column count) |
| AC4 | `ParseAuditLine_and_ParseTraceLine_assign_Ticket` (source guard) | Both parsers contain `Ticket = TryGetString(root, "ticket")` | Remove either assignment; FAIL |
| AC4 | (manual) | Diagnostics-off CSV is audit rows only; diagnostics-on CSV includes traces with their ticket | Not automatable; no bUnit |
| AC5 | `Write_QuotesTargetContainingComma` | Target `a,b` is CsvHelper-quoted; writer is still `CsvWriter` | Swap to unquoted `string.Join`; FAIL |
| AC6 | (implementation check, not a test) | Catalog reads `1.1.0`; csproj `<VersionPrefix>` unchanged | n/a |
| AC7 | `AdminEventLog_DownloadCsv_CallsEventLogCsvFormatter` | Page method body contains `EventLogCsvFormatter.Write` and does not contain `csv.WriteField("Time")` | Restore the inline loop next to a live formatter; FAIL |

Manual checks after deploy (suite cannot render the page):

1. Open Event Log, pick a day with known ticketed actions, Download CSV, open the
   file, confirm `Ticket` is the last header and matches the expanded-row ticket
   for a sampled audit row.
2. Download with diagnostics off: no TRACE/DIAG rows; tickets present on audit rows
   that have them.
3. Download with diagnostics on: trace rows present; those with a ticket show it,
   others have an empty cell.
4. Confirm an event with no ticket (legacy or a read that omitted one) still
   exports, with a blank Ticket cell.

Verification commands (from `.agents/repo-guidance.md`):

```
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity: revert the named target of each new test, confirm FAIL, restore,
confirm PASS. Prove the revert actually applied before trusting the result.

## 9. Traceability check

- AC1, AC2, AC3, AC5: `ExchangeAdminWeb.Tests/EventLogCsvFormatterTests.cs`, the four
  tests named in section 8.
- AC4: `EventLogCsvWiringTests.ParseAuditLine_and_ParseTraceLine_assign_Ticket` (source
  guard); the filtered-set half is unchanged code in `DownloadCsv` plus the manual
  checks, which have not been run.
- AC6: `Modules/ModuleCatalog.cs` AdminEventLog `Version = "1.1.0"`; csproj
  `<VersionPrefix>` untouched (verified at commit time).
- AC7: `EventLogCsvWiringTests.AdminEventLog_DownloadCsv_CallsEventLogCsvFormatter`.
- Non-vacuity: all six new tests mutation-proven in two batches (4 then 2 targeted
  failures, each mutation failing exactly its own test), then restored green. Full
  suite after S1: 1707 passed / 0 failed / 3 skipped.

## 10. Review log

None yet.
