# aud-a23e: Migration and Recipient Lookup show times with no zone, an absent creation time shows year 0001, and sorts ignore Kind

**Severity**: LOW - display and ordering, no wrong write. (1) Migration renders readable times in
the server's zone (`ToLocalTime`) with no zone named, and Recipient Lookup prints them as read, so a
`Local` and a `Utc` value of the same instant show different clocks and the operator cannot tell
which zone a time is in - the aud-a7f ambiguity in two more modules. (2) An absent
`CreationDateTime` still shows "Jan 01, 0001 00:00" in the batch header and CSV and sorts first
ascending (`ReportedTime.Of(DateTime.MinValue)` preserved the old default). (3) Readable times are
compared ignoring their Kind, so a list mixing kinds could sort wrongly (hypothetical; the live
module's kind is unverified).
**Status**: Merged to master (`90d57011`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-a92cf96af45d6d9c6` (forked at `9ef0a225`)
**Commit**: `022014aa` (merged `90d57011`)
**Reviewer**: implementer (surfaced by the aud-a23d/a7i agent, 2026-10-22, candidates 1, 2 and 4 of its hand-back)

## Evidence

At `4c54621b`: `Components/Pages/Migration.razor` `FormatBatchDate` (`ToLocalTime`, no zone text),
the Created/LastSync sorts (`ReportedTime.SortGroup` then the raw `DateTime`); `Services/MigrationService.cs`
`?? ReportedTime.Of(DateTime.MinValue)` for an absent `CreationDateTime`; `Components/Pages/RecipientLookup.razor`
lines ~73-74 (`Show` as read).

## Approach (to be written by the implementing agent before code)

- Render every readable time through `ShowReported` with a named zone: the operator's browser zone
  where the page already captures one (aud-a7e's interop lives on the Out of Office page; reuse the
  same JS helper and read pattern, or render UTC labelled "UTC" where capturing a zone would be a
  larger page change - state the choice per page). Never an unlabelled clock.
- An absent `CreationDateTime` is `null`, shown as "(not reported)", and sorts with `SortGroup` 2
  (none), not as year 0001. Keep the CSV cell empty for absent, the marker for unreadable.
- Compare readable times as instants (`ToUniversalTime` for `Local`/`Utc`; `Unspecified` grouped
  separately, since it names no instant), so Kind-mixed lists sort by instant.
- `Migration.razor` has line-keyed `ClickGateRegistry` entries and `ExpectedLineCount` 4377: edit in
  place or re-anchor per the playbook. Tests: render-tree and CSV assertions for the zone label; the
  absent creation time; a Kind-mixed sort. Mutations at the call site: drop the zone label; restore
  the MinValue default; compare raw `DateTime` again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Module-scoped: Migration and Recipient Lookup (their version bumps are the supervisor's;
`Modules/ModuleCatalog.cs` and `ExchangeAdminWeb.csproj` are not edited here). Lands after aud-a23f, on
its bytes. One JS read on Recipient Lookup through the existing `getBrowserTimeZone`
(`Components/App.razor`, unchanged); no handler added. Written before the code; line numbers are at
`9ef0a225` (aud-a23f edits Migration in place, so its numbers hold).

**Zone, per page.**

- **Migration: UTC, labelled.** Each readable time is `OutOfOfficeRequest.ShowReported(time,
  TimeZoneInfo.Utc)` - `2026-10-01 20:15 (UTC, UTC+00:00)`, the form aud-a7f's Check Status shows
  without a browser zone. Reading the browser's zone here would add a lifecycle method, its JS read and
  failure path, a `ProgressRegistry` entry and a zone parameter through the static CSV builder, on a
  page held at 4377 lines because its ClickGate entries are keyed by line - not a moderate change;
  UTC changes one helper. The CSV leaves the browser, so UTC suits it regardless.
- **Recipient Lookup: the operator's browser zone**, read as aud-a7e reads it -
  `OnAfterRenderAsync(firstRender)` awaits `ReadBrowserZoneAsync`, then `StateHasChanged`;
  `getBrowserTimeZone`, resolved with `TimeZoneInfo.FindSystemTimeZoneById`. A tier-4 page with no
  line-keyed entries, so the change is moderate. Until the zone is known, and when the browser does not
  say or this server cannot resolve its id, each time is shown in UTC, labelled UTC - a stated
  reference for a known instant, never a guessed zone (aud-a7f's rule); the failure is logged.
- On both, a time of Kind Unspecified names no instant and is shown as reported with "(time zone not
  reported)" (`ShowReported`'s rule), never given a zone.

1. `Components/Pages/Migration.razor`, in place:
   - 4206: `FormatBatchDate` becomes `ShowReported(value, TimeZoneInfo.Utc)`; the header (833-840) and the
     CSV (4194-4196) already go through it;
   - 1300, the mailbox row: through `FormatBatchDate`, "-" when none was reported, as before;
   - 833: an absent creation time is "(not reported)"; 4194: its CSV cell is empty;
   - 2496-2498, the LastSync sort, and 4164, the Created sort: through `ReportedTime.Order`; the comment
     at 2494-2495 rewritten in its two lines.
2. `Models/MigrationModels.cs`: `CreatedDateTime` (68) becomes `ReportedTime?`, null when none was
   reported. `Services/MigrationService.cs` (644): the `?? ReportedTime.Of(DateTime.MinValue)` goes.
3. `Models/ReportedTime.cs`: `Order(bool ascending)`, an `IComparer<ReportedTime?>`. `SortGroup` first - a
   time, then one this app cannot read, then none, in both directions, as aud-a23d set it; among the
   times, those that name an instant (Utc; Local through `ToUniversalTime`) are compared as instants,
   then those of Kind Unspecified, which name none, among themselves by their clock - each in the
   direction asked; unreadable and none keep list order.
4. `Components/Pages/RecipientLookup.razor`: `@inject IJSRuntime JS` and an `ILogger`; `browserZone`;
   `OnAfterRenderAsync` and `ReadBrowserZoneAsync`; the two rows (75-76) through one `ShowTime` - "N/A"
   when none was reported, as before. Created shows its time now as well as its date: a date alone
   cannot say whose day it is.
5. `ExchangeAdminWeb.Tests/ProgressRegistry.cs` (2220-2229): Recipient Lookup's new `OnAfterRenderAsync`
   gets an `Exempt` entry with aud-a7e's reason (one JS call the browser answers; nothing remote).
6. **Assumption, unverified (no live Exchange call):** which Kind the Exchange Online module gives these
   times. Each Kind is shown for what it states, so the fix depends on no answer.

**Tests.**

- `MigrationReportedTimeTests`: the a23d instant test expects the UTC-labelled text in the header, the
  mailbox row and the CSV (a Kind-Local value of the instant shows what the Kind-Utc one does). New:
  every readable time on the page and in the CSV names its zone; a Kind-Unspecified one shows "(time
  zone not reported)" and no "UTC"; an absent creation time shows "(not reported)", leaves its CSV cell
  empty and sorts after every time and after an unreadable one, both directions; a Kind-mixed list
  (Utc, Local, Unspecified) sorts by instant, Unspecified after the instants, both directions, in the
  Created and LastSync sorts. The Local/Utc pair is placed by this host's offset so that its clocks
  order opposite to its instants; on a UTC host that pair cannot differ and the Unspecified element
  carries the proof.
- `RecipientLookupReportedTimeTests`: with a fake `IJSRuntime` answering America/New_York (across the
  DST end) or Asia/Kolkata, both rows show the instant in that zone with the zone and its offset;
  without an answer, or with an id this server cannot resolve, UTC labelled; the existing expectations
  become the UTC-labelled text.
- `MigrationCsvTests`: the expected Created cell becomes the UTC-labelled text.

**Mutations at the call site** (the a23f procedure): E1 `FormatBatchDate` drops the zone (its pre-fix
server-clock text); E1b Recipient Lookup's rows drop it; E2 `?? ReportedTime.Of(DateTime.MinValue)`
restored; E3 the Created sort compares the raw `DateTime` again; E3b the LastSync sort likewise.

**Gates**: as aud-a23f, plus `ProgressRegistry*` with the new entry.

## Guard proof

Built as the Plan. **Zone, per page: Migration - UTC, labelled UTC. Recipient Lookup - the operator's
browser zone, read as aud-a7e reads it, and UTC labelled UTC until it is known or when it cannot be.** The
Plan argues each choice. A time of Kind Unspecified is shown as reported, "(time zone not reported)", on both.
Line numbers are at the final bytes.

- `Components/Pages/Migration.razor`, in place - 4377 lines before and after; against the aud-a23f commit
  `git diff --numstat` is 13/13 and every hunk is the same length on both sides:
  - 4206: `FormatBatchDate` is `OutOfOfficeRequest.ShowReported(value, TimeZoneInfo.Utc)`, so the header
    (833-840) and the CSV (4194-4196) show UTC, labelled;
  - 1300: the mailbox row goes through `FormatBatchDate`, and is "-" when nothing was reported, as before;
  - 833: an absent creation time is "(not reported)" (a Razor comment after the span); 4194: its CSV cell is
    empty;
  - 2497-2498 and 4164: the LastSync and Created sorts go through `ReportedTime.Order`. The sort comment
    (2494-2495) and the CSV summary (4177-4180) are rewritten in their own lines.
- `Models/ReportedTime.cs`: `Order(bool ascending)`; `SortGroup`'s summary points at it.
- `Models/MigrationModels.cs`: `CreatedDateTime` is `ReportedTime?`, with no default.
  `Services/MigrationService.cs`: the `?? ReportedTime.Of(DateTime.MinValue)` is gone.
- `Components/Pages/RecipientLookup.razor`, 176 lines to 232 (tier 4, no line-keyed entries): `IJSRuntime
  JS` and an `ILogger` injected; `browserZone`; `OnAfterRenderAsync` -> `ReadBrowserZoneAsync` ->
  `StateHasChanged`; `ShowTime`, which the two rows go through.
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs`: Recipient Lookup's `OnAfterRenderAsync` is `Exempt`, with
  aud-a7e's reason.

| Test | Pins |
| --- | --- |
| `MigrationReportedTimeTests.EachTimeReportedInAnotherForm_IsShownAsTheSameInstant` (4 forms; changed) | each form shows its instant as `yyyy-MM-dd HH:mm (UTC, UTC+00:00)` in the header, the mailbox row and the CSV; the Kind-Local control the same |
| `MigrationReportedTimeTests.EveryReadableTime_IsShownInUtc_AndSaysSo_OnThePageAndInTheCsv` | **the finding, Migration**: Kind Utc and Local mixed. Four labelled times on the page (the header's three, the row's one) and three in the CSV; a Local value is shown as its UTC instant |
| `MigrationReportedTimeTests.ATimeThatNamesNoZone_IsShownAsReported_AndGivenNone` | Kind Unspecified: "2026-10-01 20:15 (time zone not reported)" four times on the page and three in the CSV, and no "UTC" in either |
| `MigrationReportedTimeTests.AnAbsentCreationTime_IsNotReported_NotTheYearOne` | `CreatedDateTime` null; "Created: (not reported)"; no "0001"; the CSV row in full, with its Created cell empty |
| `MigrationReportedTimeTests.TheCreatedSort_PutsAnAbsentCreationTimeLast_AfterOneThisAppCannotRead` (both directions) | the page's own `SortBatches`: every time, then the unreadable one, then none, both ways; the input puts none first |
| `MigrationReportedTimeTests.TheTimeSorts_CompareInstants_AndPutTimesNamingNoZoneAfterThem` (Created, LastSync; both directions) | **the Kind-mixed sort**, through the page's own `SortBatches` and `SortMailboxes`. A Utc time; a Local one half this host's offset away, whose clock and instant order opposite ways against it; two Unspecified clocks either side of both. Result: by instant, then Unspecified by clock, in the direction asked |
| `RecipientLookupReportedTimeTests.WithTheBrowsersZone_BothTimesAreShownInIt_WithTheZoneAndItsOffset` (New York, Kolkata; Kind Utc and Local) | **the finding, Recipient Lookup**: the page's own `ReadBrowserZoneAsync` against a fake browser. Created `2026-10-01 16:15 (America/New_York, UTC-04:00)`; Last Logon after the US clocks go back, `2026-11-06 09:00 (America/New_York, UTC-05:00)`; Kolkata at `UTC+05:30`; a Local value shown as the Utc one is |
| `RecipientLookupReportedTimeTests.WithoutTheBrowsersZone_BothTimesAreShownInUtc_AndSaySo` (the call fails, a blank answer, an id this server cannot resolve) | UTC, labelled |
| `RecipientLookupReportedTimeTests.BothTimesReportedInAnotherForm_AreShownAsTheSameInstant` (4 forms; changed) | no browser zone: the UTC-labelled text |
| `MigrationCsvTests.BuildCsv_MapsARow` (changed) | the Created cell is `"2026-01-02 10:30 (UTC, UTC+00:00)"` |

**Host facts the tests stand on.** This host's zone is Eastern (UTC-04:00 in July 2026), so the Kind-mixed
Local and Utc pair differ here: the Utc time is 12:00Z, and the Local one's instant is 14:00Z with a clock of
10:00. On a UTC host (CI's `windows-latest`) the pair coincides and only the Unspecified elements bite; E5
below would not bite there.

**Against the unfixed code: not run as a whole.** The new Recipient Lookup tests drive a read the old page
does not have, and the sorts' comparer is new. Instead each behaviour was put back at its own call site.

Mutations at the call site, by the aud-a23f procedure, with the 851 `Migration*`/`RecipientLookup*`/
`ClickGate*`/`ProgressRegistry*` tests. The first round ran on the pre-comment bytes (LF: page
`b58b15890393f93eab76e2781154d28a`, `RecipientLookup.razor` `094d47347ca52c9efc4054e455d39937`, service
`ea6c82f7bdea655483fbc7e8e530ac8e`, `Models/ReportedTime.cs` `cad18cd94341b858a0d041320fef77f4`). It was
re-run on the final bytes with the same results; the md5s below are the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| E1 (the brief's, drop the zone label): `FormatBatchDate` back to the server's clock, unnamed | page `9fbe8833b0e5bba3974214de3a2627aa` | `ac5cd05f20e1ebd2648030c291be0ded` | 7: the four instant forms, `EveryReadableTime...`, `ATimeThatNamesNoZone...`, `BuildCsv_MapsARow` |
| E1b (the same, Recipient Lookup): `ShowTime` prints the time as read, unnamed | `RecipientLookup.razor` `620639856247851651e91db69c90d797` | `72e68e788d39d315e78469da1008ad26` | 11: the four browser-zone cases, the three without a zone, the four instant forms |
| E2 (the brief's, restore the MinValue default): `?? ReportedTime.Of(DateTime.MinValue)` again | service `aa6e15f168361ddf460f478d19b835de` | `dc12f56f1a69d3cbf78d66c81db671dd` | 3: `AnAbsentCreationTime...`, the none-last Created sort both ways |
| E3 (the brief's, compare raw `DateTime` again): the Created sort by `SortGroup` then `.Time` | page | `edf1a5322abbafa344e0aaeeeeb352f5` | 2: the Kind-mixed Created sort, both ways |
| E3b: the LastSync sort likewise | page | `8f767fe908f6ca1dfb2356e6d18287cd` | 2: the Kind-mixed LastSync sort, both ways |
| E4: `ShowTime` ignores the browser's zone (always UTC) | `RecipientLookup.razor` | `2da411982f9145ff43a4e5d1bd500e15` | 4: the four browser-zone cases |
| E5: an id this server cannot resolve falls back to the server's zone | `RecipientLookup.razor` | `d3984d647c03648c3d7f2e8161089d11` | 1: the unresolvable-id case (this host's zone is not UTC) |
| E6: `Order` gives Kind Unspecified no group of its own | `Models/ReportedTime.cs` `34c94e7990a0d9039def8d27897a5da5` | `70937ad592bbe2c7495e3dd310cb85c9` | 4: the Kind-mixed sorts, both columns and directions |

E1, E2 and E3 are the three the brief names. E1b, E3b and E4-E6 pin the second page, the second sort, the
zone read's two failure rules and the comparer's Unspecified rule. Comments were written after the first
round. Final md5s (LF) of the other files: `Models/MigrationModels.cs` `d2869e806b45911013764a7f4d65638a`;
tests `MigrationReportedTimeTests.cs` `94df854cf0c3a1d4dcda900181ddb964`, `RecipientLookupReportedTimeTests.cs`
`88d605cb2d096e5e48a3e70e3a1c05be`, `MigrationCsvTests.cs` `f7bf865be663e9592743eb738998726a`,
`ProgressRegistry.cs` `c1bd3ee9fda8e4acd2013ae026ae69d7`.

**Pins.** `ClickGateRegistry`: `Migration.razor` is 4377 lines before and after, every edit is in place and no
registered line or snippet changed; `ExpectedLineCount` untouched. `RecipientLookup.razor` is `"tier 4, not
approved"` with no line-keyed entries, so its added lines move nothing. The `ClickGate*` tests pass.
`ProgressRegistry`: Recipient Lookup's new lifecycle method has its `Exempt` entry, and the existing pins are
unchanged. `MigrationStatusPageTests` pass unchanged; the Created sort is still the switch default.

## Gates

On the final bytes (the branch at the aud-a23e commit, on top of aud-a23f):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings, the count before
  this slice. The one in a changed file is `Migration.razor`'s existing CS8604 at 4051:56, the line it was on
  before.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5383 passed, 3 skipped (the known
  `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5386 in all, 16 of them new cases;
  4 m 56 s, no hang. The `ClickGate*` and `ProgressRegistry*` tests are among them.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `Models/ReportedTime.cs` - `Order`; comments.
- `Models/MigrationModels.cs` - `CreatedDateTime` nullable; comments.
- `Services/MigrationService.cs` - the MinValue default removed; a comment.
- `Components/Pages/Migration.razor` - in place, 4377 lines: `FormatBatchDate`, the header's Created, the
  mailbox row, the two time sorts, the CSV's Created cell; two comments rewritten in their own lines.
- `Components/Pages/RecipientLookup.razor` - the browser-zone read; `ShowTime`; the two rows; comments.
- `ExchangeAdminWeb.Tests/MigrationReportedTimeTests.cs` - five new tests (9 cases); the instant test's
  expectations.
- `ExchangeAdminWeb.Tests/RecipientLookupReportedTimeTests.cs` - a fake browser; two new tests (7 cases); the
  instant test's expectations.
- `ExchangeAdminWeb.Tests/MigrationCsvTests.cs` - the Created cell's expectation.
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs` - Recipient Lookup's `OnAfterRenderAsync`.
- This record.

## Known gaps

- **Assumption, unverified (no live Exchange call):** which Kind the Exchange Online module gives these
  times. Each Kind is shown for what it states.
- **No browser ran.** `getBrowserTimeZone` was answered by a fake `IJSRuntime`, and the tests call
  `ReadBrowserZoneAsync` directly, as aud-a7e's did; the first-render path itself ran through no renderer.
- **The Migration CSV's date cells change format**, from the server's `MMM dd, yyyy HH:mm` to `yyyy-MM-dd
  HH:mm (UTC, UTC+00:00)`. Anything that parses exported files by the old format needs to know.
- Recipient Lookup's Created row now shows the time as well as the date.
- **Two Migration clocks outside this finding's evidence remain unnamed**: the mailbox report's "Taken
  <time>" (the server's clock, `ToLocalTime`) and the report-export file name's timestamp. Candidate (the
  first).
- `MigrationBatchInfo.ScheduledCompletionUtc` reads a Kind-Unspecified `CompleteAfter` through
  `ToUniversalTime`, as a time in this server's zone - a Kind guess in logic. No page shows the schedule.
  Candidate.
- Version bumps (Migration, Recipient Lookup) are the supervisor's; `ReportedTime.Order` is in the shared
  model.

## Closeout

Fixed on branch; not merged, not pushed.
