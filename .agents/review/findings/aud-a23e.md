# aud-a23e: Migration and Recipient Lookup show times with no zone, an absent creation time shows year 0001, and sorts ignore Kind

**Severity**: LOW - display and ordering, no wrong write. (1) Migration renders readable times in
the server's zone (`ToLocalTime`) with no zone named, and Recipient Lookup prints them as read, so a
`Local` and a `Utc` value of the same instant show different clocks and the operator cannot tell
which zone a time is in - the aud-a7f ambiguity in two more modules. (2) An absent
`CreationDateTime` still shows "Jan 01, 0001 00:00" in the batch header and CSV and sorts first
ascending (`ReportedTime.Of(DateTime.MinValue)` preserved the old default). (3) Readable times are
compared ignoring their Kind, so a list mixing kinds could sort wrongly (hypothetical; the live
module's kind is unverified).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-a92cf96af45d6d9c6` (forked at `9ef0a225`)
**Commit**: -
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

Pending.
