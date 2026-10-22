# aud-a23d: Migration and recipient-lookup timestamps are read with `as DateTime?`, so another type silently drops the value

**Severity**: LOW - no wrong write. `Services/MigrationService.cs` reads `CreationDateTime`,
`StartDateTime`, `CompletionDateTime`, `CompleteAfter` and `LastSyncTime`, and
`Services/RecipientLookupService.cs` reads `WhenCreated` and `LastLogonTime`, each with
`as DateTime?` from the PowerShell object. If the Exchange Online or AD module hands back a
`DateTimeOffset`, a string or a wrapped PSObject, the cast yields null and the page shows no value,
as if none were set, with no sign anything was dropped - the aud-a7h shape in two other modules.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38k/a7h agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `0187257e`: `git grep -n "as DateTime?" Services/MigrationService.cs Services/RecipientLookupService.cs`.
aud-a7h introduced `ReportedTime.Read(object?)` in `Models/LookupModels.cs` as the one reader for a
reported time (PSObject unwrapped; `DateTime` as is; `DateTimeOffset` as its instant; ISO 8601 text;
anything else named as unreadable).

## Approach (to be written by the implementing agent before code)

- Read each of the seven timestamps through `ReportedTime.Read` (or a shared sibling if it needs
  to live outside the Out of Office models), and render an unreadable value as the named marker, not
  as blank. Where a timestamp drives logic (a migration batch's completion status, a sort), an
  unreadable value must not be treated as "none" silently: refuse or flag it, per the no-guess rule.
  Label the live modules' actual types as an unverified assumption.
- Tests: for each read, a `DateTimeOffset` and a wrapped value render as the same instant; an
  unreadable type renders the marker and is not treated as absent in any logic that depends on it.
  `ClickGateRegistry`: the Migration page has line-keyed entries - do not add or remove lines in
  `Components/Pages/Migration.razor`; keep the change in the services and models. Mutation at the
  call site: `as DateTime?` again on one Migration read and one lookup read.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Module-scoped: Migration and Recipient Lookup (their version bumps are the supervisor's;
`Modules/ModuleCatalog.cs` and `ExchangeAdminWeb.csproj` are not edited here). Lands after aud-a7i and
reuses its marker. No new JS interop, no new handler or lifecycle method. Written before the code; line
numbers are at `f1460447`.

1. `ReportedTime` moves from `Models/LookupModels.cs` (148-222) to its own `Models/ReportedTime.cs`:
   three modules now read through it, so it leaves the Out of Office models, and its comments name no
   one cmdlet. `Read` stays the one reader. Two additions:
   - `Of(DateTime)` - a time already held as a `DateTime` (a default, a test's value), not something a
     cmdlet reported;
   - `SortGroup(ReportedTime?)` - 0 a time, 1 one this app cannot read, 2 none reported: the first key
     of any sort by a reported time, so an unreadable value is neither the earliest time nor "none".
2. `Models/MigrationModels.cs`: `MigrationBatchInfo.CreatedDateTime` (59) becomes `ReportedTime`,
   defaulting to `Of(DateTime.MinValue)` - what an absent value read as before, unchanged;
   `StartDateTime`/`CompletedDateTime` (60-61), `CompleteAfter` (96) and
   `MigrationUserInfo.LastSyncDateTime` (117) become `ReportedTime?`.
   - `AutoComplete` (82), unchanged in text: an unreadable `CompleteAfter` was still reported, so the
     batch is set to finalise - true, where the old read made it false.
   - `ScheduledCompletionUtc` (103-106) reads `CompleteAfter?.Time`, so it is null for an unreadable
     value; a new `CompleteAfterUnreadable` says so, and then null does not mean "not scheduled". No
     page shows a batch's schedule today, so the flag is the result's statement of it.
3. `Services/MigrationService.cs`: the five reads (639-641, 649, 713) through `ReportedTime.Read`; an
   absent `CreationDateTime` is still `DateTime.MinValue` (`?? ReportedTime.Of(DateTime.MinValue)`).
4. `Components/Pages/Migration.razor`, edited in place - its line-keyed `ClickGateRegistry` entries and
   `ExpectedLineCount` (4377) mean no line is added or removed:
   - 833-840, the open batch's header: each time through `FormatBatchDate`; Started and Completed shown
     when reported (`is { } started`), an unreadable one as the marker;
   - 1049, "Batch not started.": unchanged text; `StartDateTime: null` now means nothing was reported,
     so an unreadable start no longer reads as "not started" - the header shows the marker instead;
   - 1300, the mailbox row: `LastSyncDateTime?.Show(...) ?? "-"`;
   - 2494-2498, the LastSync sort: `SortGroup`, then the time; its two comment lines rewritten in place;
   - 4164, the Created sort (the default): `SortGroup`, then the time;
   - 4194-4196 and 4206, the CSV: `FormatBatchDate(ReportedTime)` through `Show`; an unreadable time is
     the marker in its cell, an absent Started or Completed still an empty one.
5. Recipient Lookup: `RecipientInfoResult.WhenCreated`/`LastLogonTime` (`Models/LookupModels.cs`
   127-128) become `ReportedTime?`; `Services/RecipientLookupService.cs` (34, 68) reads both through
   `ReportedTime.Read`; `Components/Pages/RecipientLookup.razor` (73-74) shows each through `Show`, "N/A"
   when absent as before (tier 4, no line-keyed entries).
6. A readable value renders exactly as before - Migration in the server's zone (`ToLocalTime`), Recipient
   Lookup as read. A `DateTimeOffset` is read as its instant (Kind Utc, aud-a7h), so it renders as the
   Kind-Utc `DateTime` of the same instant does; a wrapped value as what it wraps.
7. **Assumption, unverified (no live Exchange call):** the types `Get-MigrationBatch`,
   `Get-MigrationUser`, `Get-Recipient` and `Get-MailboxStatistics` hand these seven properties back as
   through the Exchange Online module. The old reads assumed `DateTime`; the fix depends on no answer.

**Tests.**

- New `ExchangeAdminWeb.Tests/MigrationReportedTimeTests.cs`: the real `MigrationService` over a stub
  runspace whose `Get-MigrationBatch`/`Get-MigrationUser` report what each test sets (the
  `MigrationMailboxLoadTests` idiom), and the page's real render tree and CSV.
  - Each of the five reads handed over as a `DateTimeOffset` at +05:30, a `PSObject` wrapping it, a
    `PSObject` wrapping the Kind-Utc `DateTime`, and (control) a Kind-Local `DateTime`: the header, the
    mailbox row and the CSV show what the Kind-Utc instant shows; `ScheduledCompletionUtc` is that
    instant.
  - Each read handed over as text in a culture's format, an `Int32` and a deserialized-shaped object:
    the marker with its type and text in the header, the row and the CSV, never blank; and the logic -
    an unreadable start of a batch with pending mailboxes in Injection does not say "Batch not
    started."; the LastSync sort puts it after every time and before "none" in both directions (the
    input puts it after a "none", so read as none it would stay there); the Created sort puts it after
    every time in both directions (read as MinValue it would lead ascending); an unreadable
    `CompleteAfter` is `AutoComplete`, `CompleteAfterUnreadable`, with `ScheduledCompletionUtc` null.
- New `ExchangeAdminWeb.Tests/RecipientLookupReportedTimeTests.cs`: the real `RecipientLookupService`
  over a stub runspace (`Get-Recipient`, `Get-Mailbox`, `Get-MailboxStatistics`) and the page's render
  tree with its result: both reads in the same forms render as the Kind-Utc instant (literal, invariant
  culture); unreadable ones as the marker with their text; absent ones "N/A".
- `MigrationCsvTests`, `MigrationMailboxLoadTests`, `MigrationScheduleTests`: their `DateTime` values
  become `ReportedTime`s; expectations unchanged.

**Mutations at the call site** (the aud-a7i procedure; `Migration*`, `RecipientLookup*` and `ClickGate`
tests): D1 `as DateTime?` again on the `StartDateTime` read; D2 `as DateTime?` again on the
`LastLogonTime` read.

**Gates**: Release build; full suite with `--blame-hang`; `dotnet format --verify-no-changes`;
`git diff --check HEAD`; ASCII lint after `git add -A`; `ClickGate*` and `ProgressRegistry*` passing
(Migration's `ExpectedLineCount` unchanged). Comments written after the mutation round.

## Guard proof

Pending.
