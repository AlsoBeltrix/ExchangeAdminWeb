# aud-a23d: Migration and recipient-lookup timestamps are read with `as DateTime?`, so another type silently drops the value

**Severity**: LOW - no wrong write. `Services/MigrationService.cs` reads `CreationDateTime`,
`StartDateTime`, `CompletionDateTime`, `CompleteAfter` and `LastSyncTime`, and
`Services/RecipientLookupService.cs` reads `WhenCreated` and `LastLogonTime`, each with
`as DateTime?` from the PowerShell object. If the Exchange Online or AD module hands back a
`DateTimeOffset`, a string or a wrapped PSObject, the cast yields null and the page shows no value,
as if none were set, with no sign anything was dropped - the aud-a7h shape in two other modules.
**Status**: Fixed on branch; closed on guard proof; review pending
**Branch**: `worktree-agent-a569fa37de1b7388c` (forked at `f1460447`)
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

Built as the Plan.

- `Models/ReportedTime.cs` (new): `ReportedTime` moved out of `Models/LookupModels.cs` with its behaviour
  unchanged (`git diff --color-moved` against the aud-a7i commit shows the move); its comments now name no
  one cmdlet; `Of(DateTime)` and `SortGroup(ReportedTime?)` added.
- `Models/MigrationModels.cs`: `CreatedDateTime` is a `ReportedTime` defaulting to `Of(DateTime.MinValue)`;
  `StartDateTime`, `CompletedDateTime`, `CompleteAfter` and `MigrationUserInfo.LastSyncDateTime` are
  `ReportedTime?`; `AutoComplete` unchanged in text; `ScheduledCompletionUtc` reads `CompleteAfter?.Time`;
  `CompleteAfterUnreadable` new.
- `Services/MigrationService.cs`: the five reads through `ReportedTime.Read`; an absent `CreationDateTime`
  still `MinValue`.
- `Components/Pages/Migration.razor`, in place - 4377 lines before and after: the header (833-840) through
  `FormatBatchDate`, Started/Completed shown when reported; the mailbox row (1300) through `Show`; the
  LastSync sort (2494-2498, its comment rewritten in its two lines) and the Created sort (4164) keyed on
  `SortGroup` first; the CSV (4194-4196, its summary comment rewritten in its eight lines) and
  `FormatBatchDate` (4206) on `ReportedTime`. Line 1049, "Batch not started.", is unchanged in text.
- `Models/LookupModels.cs`: `RecipientInfoResult.WhenCreated`/`LastLogonTime` are `ReportedTime?`.
  `Services/RecipientLookupService.cs`: both reads through `ReportedTime.Read`.
  `Components/Pages/RecipientLookup.razor`: both rows through `Show`, "N/A" when absent; a two-line Razor
  comment above them (tier 4, no line-keyed entries).

| Reported as | Migration: header, mailbox row, CSV | Recipient Lookup |
| --- | --- | --- |
| a `DateTimeOffset` (+05:30), a `PSObject` wrapping it, a `PSObject` wrapping the Kind-Utc `DateTime` | as the Kind-Utc instant does: the server-local `MMM dd, yyyy HH:mm` (the row `MMM dd HH:mm`) | as the Kind-Utc instant does, as read: `2026-10-01`, `2026-10-30 13:00` |
| a Kind-Local `DateTime` (Migration) / Kind-Utc (Recipient Lookup) - what the old read accepted | as before (the control) | as before (the control) |
| text in a culture's format, an `Int32`, a deserialized-shaped object | the marker with its type and text, never blank, never "-", never an empty cell | the marker with its type and text, never "N/A" |
| nothing | as before: Created `MinValue`; no Started/Completed; "-"; empty cells | "N/A", as before |

| Decision driven by a timestamp | An unreadable value now | Before (`as DateTime?`) |
| --- | --- | --- |
| "Batch not started." (pending mailboxes, Injection, no start) | not said; the header shows `Started: <marker>` | said - the start read as none |
| LastSync sort | after every time and before none, both directions | among the "none"s, in input order |
| Created sort (the default) | after every time, both directions | `MinValue`: first ascending |
| `AutoComplete` | true - a CompleteAfter was reported | false |
| `ScheduledCompletionUtc` | null, and `CompleteAfterUnreadable` true - null is not "not scheduled" | null, nothing said |

**Assumption, unverified (no live Exchange call):** the types `Get-MigrationBatch` (`CreationDateTime`,
`StartDateTime`, `CompletionDateTime`, `CompleteAfter`), `Get-MigrationUser` (`LastSyncTime`),
`Get-Recipient` (`WhenCreated`) and `Get-MailboxStatistics` (`LastLogonTime`) hand back through the
Exchange Online module. The old reads assumed `DateTime`; the fix depends on no answer. Checked on this
host instead: a value placed in the stubs reaches the services as itself - a `PSObject` stays one (the
wrapped cases fail under `as DateTime?`), a Kind-Local or Kind-Utc `DateTime` stays bare (the controls pass
under it).

| Test | Pins |
| --- | --- |
| `MigrationReportedTimeTests.EachTimeReportedInAnotherForm_IsShownAsTheSameInstant` (4 forms) | **the finding**, all five Migration reads at once through the real service and page: header Created/Started/Completed, the mailbox row's last sync, the three CSV cells, `AutoComplete` and `ScheduledCompletionUtc` (a future instant); each field a different instant |
| `MigrationReportedTimeTests.EachTimeThisAppCannotRead_IsShownAsTheMarker_WithItsText_NeverAsBlank` (3 forms) | the marker after `Created:`, `Started:`, `Completed:`, and in the row - four on the page - and three in the CSV |
| `MigrationReportedTimeTests.AStartThisAppCannotRead_IsNotABatchThatHasNotStarted` (3 forms) | **logic**: pending, Injection, an unreadable start: no "Batch not started"; `Started: <marker>` |
| `MigrationReportedTimeTests.TheLastSyncSort_PutsATimeThisAppCannotReadAfterEveryTime_AndBeforeNone` (both directions) | **logic**: the page's own `SortMailboxes`; the input puts the unreadable mailbox after the one with none |
| `MigrationReportedTimeTests.TheCreatedSort_PutsATimeThisAppCannotReadAfterEveryTime_InBothDirections` (both directions) | **logic**: the page's own `SortBatches`, its default (Created) branch |
| `MigrationReportedTimeTests.ACompleteAfterThisAppCannotRead_IsAutoCompleting_AndSaysItsScheduleIsUnknown` (3 forms) | **logic**: `AutoComplete`, `CompleteAfterUnreadable`, `ScheduledCompletionUtc` null |
| `MigrationReportedTimeTests.NoCompleteAfter_IsNeitherAutoCompletingNorUnknown` | none is still none |
| `RecipientLookupReportedTimeTests.BothTimesReportedInAnotherForm_AreShownAsTheSameInstant` (4 forms) | **the finding**, both lookup reads through the real service and page; literal, invariant culture; the creation time late in the UTC day, so a kept +05:30 clock would show the next day |
| `RecipientLookupReportedTimeTests.BothTimesThisAppCannotRead_AreShownAsTheMarker_WithTheirText_NeverAsNA` (2 forms) | the marker on both rows |
| `RecipientLookupReportedTimeTests.BothTimesNotReported_AreShownAsNA_AsBefore` | "N/A" when nothing was reported |
| `MigrationCsvTests`, `MigrationMailboxLoadTests`, `MigrationScheduleTests` (changed) | their `DateTime` values wrapped (`ReportedTime.Of`, `ReportedTime.Read`); every expectation unchanged and passing |

**Against the unfixed code: not runnable as a whole.** The model's types changed (`DateTime` to
`ReportedTime`), so the new and adjusted tests cannot compile against the old model. Instead each of the
seven reads and each of the four decisions was put back to its old behaviour at its own call site - the
table below - and each such revert fails the tests that pin it.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the
solution rebuilt (Release) and the 462 `Migration*`/`RecipientLookup*` tests run, the file restored from its
saved bytes, touched (a restored copy keeps its old mtime and MSBuild would skip the rebuild), md5
re-confirmed equal, rebuilt, and the tests re-run green. First round on the pre-comment bytes (LF:
`Services/MigrationService.cs` `e286edb850bbd95e50b49cb63c897a9a`, `Services/RecipientLookupService.cs`
`8af7609589b6f679eb24ee21941c11b5`, `Components/Pages/Migration.razor` `d6e0aec237112053dbd36dceb9e8e638`,
`Models/MigrationModels.cs` `9ffd42bab2744cc1851e5383f8e8f619`); re-run on the final bytes with the same
results - the md5s below are the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| D1 (the Approach's Migration read): `StartDateTime` read `as DateTime?` again | service `1655c98fad571dc64cfb2f1b2e735118` | `303d2ccf91267a4be03374a5e7743e4e` | 9: other forms (3: `DateTimeOffset`, both `PSObject`s), the marker (3), not-started (3) |
| D2 (the Approach's lookup read): `LastLogonTime` read `as DateTime?` again | `RecipientLookupService.cs` `6724c835f55419f31f552457cfcb998a` | `d8acb607a3751995046988dace628f83` | 5: other forms (3), the marker (2) |
| D1b: `CreationDateTime` read `as DateTime?` | service | `05722016b6ce30895341cb83094124c8` | 8: other forms (3), the marker (3), the Created sort (2) |
| D1c: `CompletionDateTime` read `as DateTime?` | service | `6d1ec9dacce783e9249bed547da74656` | 6: other forms (3), the marker (3) |
| D1d: `CompleteAfter` read `as DateTime?` | service | `80521b9584f84302d86017172756a323` | 6: other forms (3), the unreadable CompleteAfter (3) |
| D1e: `LastSyncTime` read `as DateTime?` | service | `cdd6f1f727c1d37cefde53f83f158573` | 8: other forms (3), the marker (3), the LastSync sort (2) |
| D2b: `WhenCreated` read `as DateTime?` | `RecipientLookupService.cs` | `bbe89d8780457e01cf5066c9ae73977e` | 5: other forms (3), the marker (2) |
| D3: the LastSync sort takes an unreadable time for none (`OrderBy(u => u.LastSyncDateTime?.Time == null)`, ascending) | page `56c64c16da3bf136ddcc1bcbb51af9cb` | `b947ce36141df3c042f4006933335f05` | 1: the LastSync sort, ascending |
| D4: the Created sort reads an unreadable time as `MinValue` (`OrderBy(b => b.CreatedDateTime.Time ?? DateTime.MinValue)`, ascending) | page | `4eceb0c07107998a15c2d4036043b395` | 1: the Created sort, ascending |
| D5: `AutoComplete => CompleteAfter?.Time != null` | `MigrationModels.cs` `0a8ef2fada834e8e7ce8b447662c4590` | `5043a98ec83c68d352c22559b3a0b70e` | 3: the unreadable CompleteAfter |
| D6: "not started" also for an unreadable start (`StartDateTime: null or { Time: null }`) | page | `1cef94fba32330a2fd525b3ce4ffa36b` | 3: not-started |

The controls (a Kind-Local or Kind-Utc `DateTime`) passed under every read mutation, as they must: the old
read accepted them. D3 and D4 change one direction each; the other direction of each sort is pinned by the
read mutations D1e and D1b.

Comments were written after the first round. Final md5s (LF) of the other files: `Models/ReportedTime.cs`
`c11e5641e6746deba780e1326cd43388`, `Models/LookupModels.cs` `f1a42ca96c63a57dcbb648296200da72`,
`Components/Pages/RecipientLookup.razor` `446da0377e3c7e9b786fdfe72f0d5385`; tests
`MigrationReportedTimeTests.cs` `316f293e197e87155a4ca7298e17316e` (one initializer re-flowed for
`dotnet format` after the round; no assertion changed), `RecipientLookupReportedTimeTests.cs`
`1b750444ccde44271f8d8fd538442c2b`, `MigrationCsvTests.cs` `15fe4c67a519f797f65e999c39a27a50`,
`MigrationMailboxLoadTests.cs` `86bb86597570d2923fa44affe48b4ecb`, `MigrationScheduleTests.cs`
`73ae56cdcc092fea033f713bcd604511`.

**Pins.** `ClickGateRegistry`: `Migration.razor` is 4377 lines before and after and no line-keyed entry's
line or snippet changed (every edit is in place, the two comment rewrites included);
`ExpectedLineCount` untouched - the `ClickGate*` tests pass. `RecipientLookup.razor` is `"tier 4, not
approved"` with no line-keyed entries. `ProgressRegistry`: no handler or lifecycle method was added;
`FormatBatchDate` is a static render helper; its tests pass. The `MigrationStatusPageTests` sort scans
(`EveryColumnTheSortOffersIsAColumnYouCanSee`, the mailbox twin) pass unchanged.

## Gates

On the final bytes (the branch at the aud-a23d commit, on top of aud-a7i):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings (the count
  aud-a7f/a7g/a7h recorded). The one in a changed file is `Migration.razor`'s existing CS8604 at
  4051:56, the line it was on before - the page's line count did not move.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5319 passed, 3 skipped (the
  known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5322 in all, 25 of them new
  cases; 5 m 25 s, no hang. Run on the final bytes, after the one test-file re-flow; the run before it
  gave the same totals. The `ClickGate*` and `ProgressRegistry*` tests are among them.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `Models/ReportedTime.cs` - new: `ReportedTime`, moved; `Of`, `SortGroup`; comments.
- `Models/LookupModels.cs` - `ReportedTime` moved out; `RecipientInfoResult.WhenCreated`/`LastLogonTime`.
- `Models/MigrationModels.cs` - the five times' types; `ScheduledCompletionUtc`; `CompleteAfterUnreadable`;
  comments.
- `Services/MigrationService.cs` - the five reads; a comment.
- `Services/RecipientLookupService.cs` - the two reads; a comment.
- `Components/Pages/Migration.razor` - in place, 4377 lines: header, row, two sorts, CSV,
  `FormatBatchDate`, two comments rewritten in their own lines.
- `Components/Pages/RecipientLookup.razor` - the two rows; a two-line comment.
- `ExchangeAdminWeb.Tests/MigrationReportedTimeTests.cs` - new, 7 tests (18 cases).
- `ExchangeAdminWeb.Tests/RecipientLookupReportedTimeTests.cs` - new, 3 tests (7 cases).
- `ExchangeAdminWeb.Tests/MigrationCsvTests.cs`, `MigrationMailboxLoadTests.cs`, `MigrationScheduleTests.cs` -
  `DateTime` values wrapped.
- This record.

## Known gaps

- **Assumption, unverified (no live Exchange call):** the types the live module reports (above).
- **No circuit or browser ran**: the pages' compiled render trees were read directly, as in
  `MigrationMailboxLoadTests` and the Out of Office harness.
- **Readable times keep their pre-existing rendering, with no zone named**: Migration shows them in the
  server's zone (`ToLocalTime`); Recipient Lookup prints them as read, so a Kind-Local and a Kind-Utc value
  of the same instant print different clocks. The aud-a7f class, in two other modules. Candidate.
- **An absent `CreationDateTime` still reads as `DateTime.MinValue`**: "Jan 01, 0001 00:00" in the header
  and the CSV, first in an ascending Created sort. Pre-existing and kept on purpose (this finding is about
  unreadable values). Candidate.
- **Within the readable group, times are ordered as read** - `DateTime` comparison ignores Kind, as before;
  a list that mixed Kinds could misorder by the server's offset. Hypothetical while the module reports one
  type.
- **A batch whose row throws while being read is dropped from the list** with only a logged warning
  (`GetMigrationBatchesAsync`'s per-batch catch; e.g. a count `Convert.ToInt32` cannot read). Pre-existing.
  Candidate.
- Version bumps (Migration, Recipient Lookup; whether the move of `ReportedTime` into a shared file counts
  as a shared-infrastructure change for the base version) are the supervisor's.

## Closeout

Fixed on branch; not merged, not pushed.
