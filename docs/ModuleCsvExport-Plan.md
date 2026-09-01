# CSV export for five result-set modules

Status: Implemented 2026-09-01 (owner go the same day). D1 RULED 2026-09-01 (keys
ARE in the BitLocker export - owner wording in section 1). No owner decision is
outstanding. All seven slices landed (S1-S6 code, S7 README); section 9 below
completes the traceability check. NOT DEPLOYED - the four manual checks in
section 8 need a deployed instance and ride the next dev deploy.
Owner: Michael
Last verified against code: `72bfaf7` / 2026-08-31
Versions: five module bumps (`DhcpAuthorization` `1.2.3` -> `1.3.0`,
`NamedLocations` `1.0.3` -> `1.1.0`, `BlockedSenders` `1.3.0` -> `1.4.0`,
`BitLockerRecovery` -> next minor after `docs/BitLockerMandatoryTicket-Plan.md`
lands, `Migration` `1.7.1` -> `1.8.0`) AND a base app bump (shared CSV helper).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request 2026-08-27 (recorded in `.agents/state.md`, older queue item 6):
`DhcpAuthorization` exports the current authorized-server list; `NamedLocations`,
`BlockedSenders`, `BitLockerRecovery` and `Migration` status export their result
sets, with the export offered only when results are present.

Ordering: runs AFTER `docs/BitLockerMandatoryTicket-Plan.md` (owner queue order
2026-08-31). S5 reads that plan's `searchTicket` page field; if the order ever
flips, S5 must be re-planned, the other slices are unaffected.

## 1. Owner decisions

**D1 (RULED 2026-09-01): the BitLocker export CONTAINS the recovery keys.**
Owner, verbatim: *"the bitlocker csv export absolutely needs to contain the
actual keys otherwise what's the point of the export."* This supersedes both the
drafting agent's recommendation and the openreview reviewer's endorsement of the
no-keys default (Review log, 2026-08-31) - the export's purpose IS bulk key
retrieval.
What the ruling accepts, recorded so a later reader can weigh it (the owner's
accepted trade, not an oversight): the file discloses every key in the result set
in one act rather than through per-row audited Reveals, and it persists working
disk-decryption secrets wherever the operator's browser saves downloads. The
compensating controls, binding on S5: the export is itself an audited disclosure
event (AC4b), it is reachable only behind the module's fail-closed permission and
the mandatory validated ticket from `docs/BitLockerMandatoryTicket-Plan.md`, and
the keys still never enter the audit log (row count and computers are audited,
never key material - the blr-1 rule).

Settled by higher authority or existing code, not open:

- No ServiceNow call at export time; the ticket is audit metadata (Constitution,
  External Integrations).
- No HTTP download endpoint - the owner rejected that approach for MessageTrace
  (`docs/history/state-archive.md:756`); exports use the existing base64 +
  `downloadFile` JS path.
- No new granular permissions. Export rides each module's existing read surface;
  adding aliases would break `ModuleCatalogTests.cs:109` for no security gain -
  anyone who can see the results can already reveal each key on screen and
  transcribe it; the export changes the ergonomics of that disclosure, not who
  can perform it. D1 accepts that trade explicitly.

## 2. Non-goals

- Exports for any module beyond the five named.
- Per-user Migration rows (the expandable `batchUsers` sub-table,
  `Migration.razor:861`): the export is batch-level status only.
- Changing `EventLogCsvFormatter` or the Event Log export - it stays as the
  Event-Log-specific formatter it is (`Services/EventLogCsvFormatter.cs:16-19`
  pins its own contract).
- New ticket gates on export buttons. Three of the pages render their ticket
  input only inside action/confirm forms (NamedLocations `:179-181`,
  BlockedSenders `:98-111`, Migration status `:809-810`); exports there neither
  require nor record a ticket. BitLocker's export records the search ticket
  (S5) because that module's ticket gates the search that produced the rows.
- Touching the CsvHelper *readers* (upload paths in CalendarPermission /
  MailboxPermission / Migration / ConferenceRoom / LicensingUpdates services).
- Retiring the hand-rolled StringBuilder CSV writers (`MessageTraceDetailReport`,
  `BulkOperationResult`, `MessageTrace.razor`) - pre-existing, out of scope.
- Localization, column pickers, Excel formats, or export filters.

## 3. Acceptance criteria

- AC1: A shared `CsvExport.Write(header, rows)` exists, uses CsvHelper
  `WriteField` per cell (the `EventLogCsvFormatter.cs:23-54` quoting contract:
  commas, quotes, newlines in any cell survive round-tripping), and is the only
  CSV writer the five modules use.
- AC1b (csv-1): `CsvExport.Write` neutralizes CSV/formula injection before
  quoting: a cell whose first character is `=`, `+`, `-`, `@`, tab, CR, or LF is
  prefixed with a single quote, matching the repo's existing exporter standard
  (`Services/MessageTraceDetailReport.cs:216-225`, `CsvEscape`, which documents
  exactly this rule). Exported cells here carry externally influenced strings
  (sender addresses, location names, batch names, reasons), and CsvHelper quoting
  alone does not stop Excel treating a leading `=` as a formula.
- AC2: Each of the five pages has a Download CSV button that is absent-or-disabled
  when its result set is empty, enabled when at least one row is shown, and
  downloads via the existing global `downloadFile` JS helper
  (`Components/App.razor:103-119`) with filename `<module>_yyyyMMdd_HHmmss.csv`.
- AC3: Each module's CSV columns match section 6's table for that module,
  produced by an `internal static` per-page projector a test can call with
  sample rows.
- AC4: Every export writes one audit event via `AuditService.LogModuleAction` -
  action `ExportCsv`, category = the module id, target = a row-count summary
  (e.g. `12 rows`), success true. Audit failure does not block the download
  (Constitution: audit failure must not mask a completed operation - the
  BitLocker page's `SafeAudit` shape).
- AC4b (BitLocker only): its export audit is a distinct bulk-disclosure event -
  action `ExportRecoveryKeysCsv` (not the generic `ExportCsv`), target the key
  count, `ticketNumber: searchTicket`, and NEVER any key material in the event
  (the blr-1 rule: row count and computer names may be audited, keys may not).
- AC5 (D1, ruled 2026-09-01): the BitLocker CSV contains the `RecoveryKey` column
  with each row's `RecoveryPassword` verbatim - a projector test feeding rows
  with a known 48-digit key asserts it appears on its row. The formula-injection
  rule (AC1b) must not corrupt keys: a recovery password starts with a digit, so
  the leading-character neutralization never touches it (asserted in the same
  test).
- AC6: Five module version bumps per the header; base app version bumped IN S1,
  the slice that lands the shared helper (Constitution: shared infrastructure
  changes bump the base app version - the bump ships with the change, not at the
  end; csv-2); no ModuleCatalog permission/alias changes
  (`ModuleCatalogTests.cs:16,109` untouched and green).
- AC7: README gains one export bullet per module section.

## 4. Failure behavior

| Step / dependency | If it fails | The operator sees | System state afterward |
|---|---|---|---|
| Result set empty | Button absent/disabled (AC2) | No export control | Nothing runs |
| JS `downloadFile` or base64 conversion throws | Same as Event Log export: uncaught, Blazor error UI; no new catch (`docs/EventLogCsvTicket-Plan.md` section 4 precedent) | Existing error UI | No partial file |
| Audit write throws | Caught and logged separately; download already delivered | Nothing | Export un-audited, logged as audit failure |
| A cell contains comma/quote/newline (NamedLocations lists, BlockedSenders `Reason`, Migration `TargetEndpoint`) | CsvHelper quoting via `WriteField` (AC1) | Correct file | n/a |
| Migration status not yet loaded (`migrationBatches` null, `Migration.razor:857`) | Button rendered only inside the loaded, non-empty status view | No control until rows exist | Nothing runs |
| BitLocker results present but ticket box since edited | Export audits `searchTicket` (the captured value), same rule as the reveal audit | Normal export | Audit ties the file to the ticket that produced the rows |

## 5. Rollback / blast radius

Revert the commit(s). No schema, config, authorization, or stored-state change.
Blast radius per module is one page (button + projector) and its version string;
shared blast radius is one new static class no existing code calls. Consumers of
the files are humans; no downstream parser exists yet.

## 6. Design sketch

### Current code (read at `72bfaf7` via survey, spot-verify at implementation)

- Shared download JS: `downloadFile` is global on every page
  (`Components/App.razor:103-119`); pages need only `@inject IJSRuntime JS`.
  Working example: `AdminEventLog.razor:1015-1034` (bytes -> base64 ->
  `JS.InvokeVoidAsync("downloadFile", name, "text/csv", base64)`).
- Quoting precedent: `Services/EventLogCsvFormatter.cs` - `WriteField` per cell,
  no `ClassMap`, returns `string`. CsvHelper 33.1.0 is already referenced
  (`ExchangeAdminWeb.csproj:23`); the test project sees it transitively.
- Result sets and catalog versions:
  - DhcpAuthorization: `List<DhcpServerEntry> servers` (`DhcpAuthorization.razor:136`),
    row `DhcpServerEntry { DnsName, IpAddress }`
    (`Services/DhcpAuthorizationService.cs:233-236`); no IJSRuntime injected yet;
    Version at `ModuleCatalog.cs:493`.
  - NamedLocations: `List<NamedLocation> locations` (`NamedLocations.razor:219`),
    row `NamedLocation` (`Services/NamedLocationsService.cs:240-250`; note
    `CreatedDateTime`/`ModifiedDateTime` are strings and not shown on screen);
    no IJSRuntime yet; Version at `ModuleCatalog.cs:456`.
  - BlockedSenders: `List<BlockedSenderInfo> blockedSenders`
    (`BlockedSenders.razor:136`), row `BlockedSenderInfo { SenderAddress, Reason?,
    BlockedDateRaw? }` (`Models/BlockedSenders/BlockedSenderInfo.cs:11-15`); list
    loads in `OnAfterRenderAsync` (`:170-177`); no IJSRuntime yet; Version at
    `ModuleCatalog.cs:276`.
  - BitLockerRecovery: `List<BitLockerRecoveryKey> results`
    (`BitLockerRecovery.razor:236`), row holds `RecoveryPassword`
    (`Services/BitLockerRecoveryService.cs:383`) plus `ComputerName`, `KeyId`,
    `CreatedUtc`, `StatusLabel`, `LastSeenInAdUtc`; no IJSRuntime yet; Version at
    `ModuleCatalog.cs:513`. After the ticket plan lands the page holds
    `searchTicket`.
  - Migration status: `List<MigrationBatchInfo>? migrationBatches`
    (`Migration.razor:857`), row `MigrationBatchInfo`
    (`Models/MigrationModels.cs:55-70`); the page already injects IJSRuntime
    (`Migration.razor:13`) for the sample-CSV download (`:1848-1852`); Version at
    `ModuleCatalog.cs:183`.

### New shared file: `Services/CsvExport.cs`

```
public static class CsvExport
{
    public static string Write(
        IReadOnlyList<string> header,
        IEnumerable<IReadOnlyList<string>> rows);
}
```

The `EventLogCsvFormatter.Write` loop generalized: `StringWriter` + `CsvWriter` +
`WriteField` per cell, `NextRecord` per row, returns the CSV string. Before
`WriteField`, each cell passes the AC1b neutralization (leading `=`/`+`/`-`/`@`/
tab/CR/LF prefixed with `'` - the `MessageTraceDetailReport.CsvEscape:216-225`
rule; quoting itself stays CsvHelper's). Throws `ArgumentException` if a row's
cell count differs from the header's - a projector bug must fail loudly, not
produce a misaligned file. No DI registration (static, like
`EventLogCsvFormatter`). Used by five modules, so it is shared infrastructure:
base app bump (Constitution, Deployment And Versioning).

Recorded, not fixed here (survey is not authorization): `EventLogCsvFormatter`
itself has no formula neutralization, and Event Log cells carry user/target
strings. Pre-existing, owner's call whether to schedule.

### Per-module projector + button (the repeating shape)

Each page gets, in `@code`:

- `internal static string BuildCsv(IReadOnlyList<RowType> rows)` - maps rows to
  cells per the column table below and calls `CsvExport.Write`. Static and
  argument-fed so tests call it directly (the `NoResultsMessage` /
  `AuditSearchTarget` page-static precedent, tested without bUnit).
- `private async Task DownloadCsvAsync()` - guard empty, build, UTF8 bytes,
  base64, `downloadFile` with `<module>_yyyyMMdd_HHmmss.csv`, then the AC4 audit
  in a try/catch that logs and never rethrows.
- A `Download CSV` button beside the results table, rendered/enabled per AC2,
  `btn btn-outline-secondary btn-sm` (the AdminEventLog look, `:92`).
- `@inject IJSRuntime JS` and `@inject AuditService Audit` where missing
  (Dhcp/NamedLocations/BlockedSenders pages have no IJSRuntime today; all five
  already have or gain the audit injection - verify per page at implementation).

### Columns per module

| Module | Header (in order) | Cell notes |
|---|---|---|
| DhcpAuthorization | `DnsName,IpAddress` | verbatim |
| NamedLocations | `Name,Type,Trusted,IpRanges,CountryCodes,IncludeUnknownCountries,Created,Modified` | Type = `Ip`/`Country` enum name; lists joined with `"; "`; booleans `true`/`false`; Created/Modified are the raw strings (on-screen table omits them; the CSV is the better place for them) |
| BlockedSenders | `SenderAddress,Reason,Blocked` | nulls -> empty cell (not the on-screen em-dash) |
| BitLockerRecovery | `Computer,RecoveryKey,Created,KeyId,Source,LastSeenInAd,Ticket` | `RecoveryKey` = `RecoveryPassword` verbatim (D1 ruling); `Source` = `StatusLabel`; dates `yyyy-MM-dd HH:mm` local, matching the on-screen format; `Ticket` = the page's `searchTicket`, same value on every row |
| Migration status | `BatchName,Status,Direction,Created,Started,Completed,Total,Synced,Finalized,Failed,TargetEndpoint` | nullable dates -> empty cell; export the sorted view the operator sees (`GetSortedBatches()`, `Migration.razor:525`) |

The BitLocker `Ticket` column is the queue-item interaction recorded in
`.agents/state.md` item 6: whatever ticket the module gained must appear in its
export. It is a projector parameter (`BuildCsv(rows, ticket)`) so the test can
assert it.

## 7. Task breakdown

One commit per slice; S2-S6 are independent of each other, all depend on S1.
Module version bumps land IN the slice that changes the module (one module, one
commit, its paperwork with it).

**S1 - `Services/CsvExport.cs` + tests + base app bump.** Serves AC1, AC1b, and
the base-bump half of AC6. The csproj triple (`<VersionPrefix>` +
`AssemblyVersion` + `FileVersion`) bumps here: the shared helper IS the
shared-infrastructure change, and a deploy cut after S1 must not carry new shared
code under the old base version (csv-2; Constitution, Deployment And Versioning).

**S2 - DhcpAuthorization export.** Serves AC2-AC4 for this module. Projector,
button, audit, `@inject IJSRuntime`, tests; `ModuleCatalog.cs:493` -> `1.3.0`.

**S3 - NamedLocations export.** Same shape; `ModuleCatalog.cs:456` -> `1.1.0`.

**S4 - BlockedSenders export.** Same shape; `ModuleCatalog.cs:276` -> `1.4.0`.

**S5 - BitLockerRecovery export.** Same shape plus: `RecoveryKey` column (D1),
`Ticket` column from `searchTicket`, the AC4b bulk-disclosure audit
(`ExportRecoveryKeysCsv`, key count, `ticketNumber: searchTicket`, no key
material), AC5 keys-present test. Version -> next minor above whatever the
ticket plan set (expected `1.1.0` -> `1.2.0`). BLOCKED until
`docs/BitLockerMandatoryTicket-Plan.md` is implemented (D1 is ruled).

**S6 - Migration status export.** Same shape; button lives in the status tab
beside the batch table; exports `GetSortedBatches()`; `ModuleCatalog.cs:183` ->
`1.8.0`.

**S7 - README.** Serves AC7. One README bullet per module section (locate by
reading). The base bump already landed in S1.

## 8. Test plan

`ExchangeAdminWeb.Tests/CsvExportTests.cs` (S1):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1 | `Write_QuotesCommaQuoteNewlineCells` | A cell `a,"b`+newline survives as one quoted cell | Swap to `string.Join`; FAIL |
| AC1 | `Write_HeaderThenRowsInOrder` | Output line 1 is the header, rows follow in order | Reorder; FAIL |
| AC1 | `Write_MismatchedRowThrows` | 3-cell row under a 2-cell header throws | Remove the guard; FAIL |
| AC1 | `Write_EmptyRowsYieldsHeaderOnly` | Header-only string, no trailing garbage | n/a (shape check) |
| AC1b | `Write_NeutralizesFormulaLeadingCells` | Each of `=`, `+`, `-`, `@`, tab, CR, LF as a cell's first character comes out `'`-prefixed; a mid-cell `=` is untouched | Remove the neutralization; FAIL |

Per module (S2-S6), in each module's existing test file (or a new
`<Module>CsvTests.cs` where none fits):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC3 | `BuildCsv_HeaderMatchesSpec` | Exact header per section 6 | Change a name; FAIL |
| AC3 | `BuildCsv_MapsARow` | One populated sample row lands in the right cells (null handling per the notes) | Swap two cells; FAIL |
| AC5 | (BitLocker only) `BuildCsv_ContainsRecoveryKeyVerbatim` | A row carrying a known 48-digit key yields that key, unmodified (no `'` prefix), in its `RecoveryKey` cell | Drop the key column; FAIL |
| AC3 | (BitLocker only) `BuildCsv_StampsTicketOnEveryRow` | `BuildCsv(rows, "INC0001")` puts `INC0001` on each row | Drop the parameter; FAIL |
| AC4b | (BitLocker only) `<Page>_WiresDownloadCsv` source guard additionally | Page audits `ExportRecoveryKeysCsv` with `ticketNumber: searchTicket` and the audit call references no key value | Remove either; FAIL |
| AC2/AC4 | `<Page>_WiresDownloadCsv` (source guard, `EventLogCsvWiringTests` mechanism) | Page contains `BuildCsv`, `downloadFile`, the empty-set guard, and the `LogModuleAction` call with `ExportCsv` | Remove any; FAIL |

Source-guard caveat as in the sibling plans: guards pin wiring, behavior lives in
the projector tests, and the manual checks are the end-to-end proof.

Manual checks after deploy (no bUnit):

1. Each of the five pages: no results -> no enabled export control; produce
   results -> Download CSV yields a file whose columns match section 6 and whose
   rows match the screen.
2. Open the Admin Event Log after each export: one `ExportCsv` event per download
   (BitLocker: `ExportRecoveryKeysCsv`, carrying the ticket and no key material).
3. BitLocker: open the downloaded file and confirm every row's recovery key is
   present, unmodified, and matches the on-screen Reveal for a sampled row (D1).
4. NamedLocations: export a location whose name contains a comma; the file opens
   with columns intact.

Verification commands (from `.agents/repo-guidance.md`):

```
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity: revert the named target of each new test, confirm FAIL, restore,
confirm PASS.

## 9. Traceability check

- AC1 (shared `CsvExport.Write`, `WriteField` quoting, only writer the five
  modules use): S1 `45f14e5`,
  `ExchangeAdminWeb.Tests/CsvExportTests.cs`
  (`Write_QuotesCommaQuoteNewlineCells`, `Write_HeaderThenRowsInOrder`,
  `Write_MismatchedRowThrows`, `Write_EmptyRowsYieldsHeaderOnly`). "Only writer"
  half: each module's `BuildCsv` (S2-S6 below) calls `CsvExport.Write` and no
  module writes CSV any other way - verified per slice at implementation.
- AC1b (formula-injection neutralization): S1 `45f14e5`,
  `CsvExportTests.Write_NeutralizesFormulaLeadingCells`.
- AC2 (Download CSV button absent/disabled when empty, enabled with rows,
  `downloadFile` JS, `<module>_yyyyMMdd_HHmmss.csv` filename) and AC4 (every
  export audits `LogModuleAction` with action `ExportCsv`, audit failure does
  not block the download) together, one source-guard test per module asserting
  `BuildCsv`, `downloadFile`, the empty-set guard, `"ExportCsv"`, and
  `LogModuleAction` all appear in that page's `DownloadCsvAsync` body:
  - DhcpAuthorization: S2 `33ba4ea`, `DhcpAuthorizationCsvTests.DhcpAuthorization_WiresDownloadCsv`.
  - NamedLocations: S3 `6b96d5c`, `NamedLocationsCsvTests.NamedLocations_WiresDownloadCsv`.
  - BlockedSenders: S4 `71d25b0`, `BlockedSendersCsvTests.BlockedSenders_WiresDownloadCsv`.
  - Migration status: S6 `134c90c`, `MigrationCsvTests.Migration_WiresDownloadCsv`.
  - BitLockerRecovery does not carry the generic `ExportCsv` action - see AC4b.
- AC3 (each module's CSV columns match section 6's table, produced by an
  `internal static` per-page projector): S2-S6, each module's
  `BuildCsv_HeaderMatchesSpec` and `BuildCsv_MapsARow`:
  `DhcpAuthorizationCsvTests` (S2 `33ba4ea`), `NamedLocationsCsvTests` (S3
  `6b96d5c`), `BlockedSendersCsvTests` (S4 `71d25b0`),
  `BitLockerRecoveryCsvTests` (S5 `8061610`), `MigrationCsvTests` (S6
  `134c90c`).
- AC4b (BitLocker's distinct bulk-disclosure audit: action
  `ExportRecoveryKeysCsv`, key count target, `ticketNumber: searchTicket`, and
  never any key material in the event): S5 `8061610`,
  `BitLockerRecoveryCsvTests.BitLockerRecovery_WiresDownloadCsv`, which asserts
  `"ExportRecoveryKeysCsv"`, `LogModuleAction`, `ticketNumber: searchTicket` are
  present and `RecoveryPassword` is absent from the audited call (the blr-1
  rule).
- AC5 (BitLocker CSV contains `RecoveryKey` with `RecoveryPassword` verbatim,
  unmangled by the AC1b leading-character rule since a recovery password
  starts with a digit): S5 `8061610`,
  `BitLockerRecoveryCsvTests.BuildCsv_ContainsRecoveryKeyVerbatim` and
  `BuildCsv_StampsTicketOnEveryRow`.
- AC6 (five module version bumps per the header; base app version bumped IN
  S1; no `ModuleCatalog` permission/alias changes): base app `2.12.0` ->
  `2.13.0` landed in S1 `45f14e5` (the shared-helper slice, per the csv-2
  rule). Five module bumps, each in its own slice: `DhcpAuthorization`
  `1.2.3` -> `1.3.0` (S2 `33ba4ea`), `NamedLocations` `1.0.3` -> `1.1.0` (S3
  `6b96d5c`), `BlockedSenders` `1.3.0` -> `1.4.0` (S4 `71d25b0`),
  `BitLockerRecovery` `1.1.0` -> `1.2.0` (S5 `8061610`), `Migration` `1.7.1`
  -> `1.8.0` (S6 `134c90c`). `ModuleCatalogTests.cs` untouched across all six
  slices and green (no permission/alias assertions changed).
- AC7 (README gains one export bullet per module section): S7 `b84cfb5` - one
  bullet added to the DhcpAuthorization, NamedLocations, BitLockerRecovery,
  and Migration sections; BlockedSenders had no existing README section (a
  pre-existing gap unrelated to this plan), so S7 added a minimal one to hold
  its bullet.
- Non-vacuity: every slice (S1-S6) was mutation-probed by design at
  implementation time (each new test's named target reverted, confirmed FAIL,
  restored, confirmed PASS) before its commit landed. Full suite after S6:
  1881 passed / 0 failed / 3 skipped.

## 10. Review log

- 2026-08-31: openreview codex (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh,
  grade fallback, owner-named dispatch; codex-cli 0.150.1) over `a9b0ebc..533c1fe`
  (this plan together with `docs/BitLockerMandatoryTicket-Plan.md`): verdict
  `acceptable_with_changes`, capability_ok, both SHAs echoed. Two findings against
  this plan, both admitted and folded in: **csv-1 (MEDIUM)** - the shared writer
  omitted formula-injection neutralization the repo's own
  `MessageTraceDetailReport.CsvEscape` already standardizes (now AC1b + test);
  **csv-2 (LOW)** - the base app bump was deferred to S7 past the S1 slice that
  introduces the shared infrastructure (now bumps in S1). The reviewer endorsed
  the D1 default (no recovery keys in the BitLocker export: "excluding BitLocker
  recovery keys unless explicitly approved"). Records:
  `.agents/review/findings/csv-{1,2}.md`.
