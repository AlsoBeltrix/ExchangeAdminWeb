# aud-a65: A sustained rotation failure grows the active JSONL file without bound after one warning

**Severity**: MEDIUM - audit visibility under a persistent fault. Since aud-a56 a row is never dropped for
a rotation failure: it is appended to the full active file and one warning is given per failure streak.
With rotation failing for days (a SIEM tailer holding the file without delete sharing; a folder refusing
delete) there is no ceiling and no second warning until a rotation succeeds, so the active audit or trace
file grows until the log volume fills, after which writes fail as ordinary file-write failures and
visibility degrades anyway.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a62/a63 agent's scope; fixed on branch, closed on guard proof; not merged
**Branch**: `worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`)
**Commit**: `bdf013cf` (fix, tests, README, record); record and plan `3c306bcc`
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-16, 2026-10-23)

## Evidence

At `d2f6f2ec`: `Services/JsonlFileAppender.cs` around line 85 (the `rotationFailed` callback and the
append past the limit); `Services/JsonlLogService.cs` around 159 (the one-warning streak);
`Services/ExtendedLogService.cs` (the same shape).

## Approach (to be written by the implementing agent before code)

- Keep "never drop the row". Add an escalation: the rotation-failure warning repeats on a schedule while
  the streak lasts (every N MB of growth past the limit, or every M minutes, whichever first), naming the
  current size and the amount over the limit; the streak's state (since when, how far over) is exposed on
  the Admin Event Log diagnostics area beside the dropped-entry count (aud-a46/a49).
- Preferably a bounded overflow pattern: when the active file is more than K times its limit and
  rotation still fails, the writer opens `<active>.overflow-<n>.jsonl` (a new file needs no delete right
  and no move) and appends there; the readers include overflow files for the date; the overflow files are
  rotated into the normal chain when rotation succeeds again. Record which of the two (escalation only, or
  escalation plus overflow) was built and why.
- Tests: a persistent rotation failure with growth past the limit re-warns and (if built) spills to an
  overflow file the reader includes; mutation at the call site: the re-warn schedule removed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"), within the 2026-10-23 stopping rule (MEDIUM). Raised by codex
attr-batch-16 on aud-a56 and folded into this agent's scope by the supervisor on 2026-10-23; the record text is master
`97b89ae4`'s, checked out into this branch. One commit `fix(audit-log,event-log): ... (aud-a65)`, the third fix on branch
`worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`), after aud-a62 and aud-a63. Two layers change:
- The JSONL writer and the extended log are shared, so the base app version is due.
- The Admin Event Log's diagnostics area is that module's, so its module version is due.

The supervisor owns both bumps.

Evidence, re-read on this branch with aud-a62 in place (the line numbers are those of the aud-a62 fix):
1. `Services/JsonlFileAppender.cs` `Append` (63-117):
   - `rotateIfFull()` runs in a try (85-92) whose catch hands the exception to `rotationFailed` and goes on. The line is
     then appended to the full file.
   - The callbacks run under the file's lock, before the line, and must not throw: a throw from `rotationFailed` would
     leave `Append` and drop the row.
2. `Services/JsonlLogService.cs`:
   - `_rotationFailureReported` (20), a `HashSet<LogStream>` read under `_lock`.
   - `AppendLine` (148-174): `rotateIfFull` removes the stream once `RotateIfFull` returns (154-158), and
     `rotationFailed` reports only when it adds the stream (159-167).
   - So a streak is reported once, at its first failed write, and never again while it lasts, however long it lasts and
     however large the file grows. The warning says so: "this is not reported again until a write finds the file rotated
     or under its limit" (164).
3. `Services/ExtendedLogService.cs`: the same, with a `bool` the one consumer touches (42; `AppendLine` 367-398).
4. `Components/Pages/AdminEventLog.razor`:
   - The diagnostics area (198-211) shows the diagnostic files that could not be read, this instance's extended-log drop
     count (`DroppedEntriesText`, diagnostics on), and the lines that could not be parsed (diagnostics on or off).
   - `LoadEvents` reads the drop count in memory (686-697). Nothing shows a rotation that keeps failing.
5. The readers of these files, for the overflow question:
   - The Admin Event Log reads `JsonlLog.GetAuditLogPaths` and `GetTraceLogPaths` (602, 636). The extended log reads its
     own `GetLogPathsForDate` (412). Nothing in the app prunes or lists the folder.
   - A reader outside the app, such as a SIEM tailer, knows only the documented names.

**Built: the escalation and the diagnostics line, without the overflow.** Why:
- The overflow bounds each file, not the volume.
  - With a folder that refuses delete, the app can delete nothing. The volume fills at the same rate whether the rows go
    to one file or to many.
  - With a held file, the held file stops growing and the overflow files grow instead. The volume is bounded only if the
    overflow files are rotated into the chain and the oldest deleted. One successful rotation would then move and delete
    several files at once, and the held file, whose rows are older than theirs, would sit outside the chain. File order
    would no longer be row order for a reader that reads in chain order.
- Every reader would have to learn a new family of files: the event log's two path lists, the extended log's own, the
  tests' parsers, and a SIEM tailer configured for the documented names. A reader that misses the overflow files loses
  audit rows without a word, which is the harm this work exists to remove.
- The record's harm is that nobody hears of it: one warning, then silence for days while the file grows. The escalation
  repeats the warning with the size and the overage, and the event log shows the state to whoever opens it.
- Both faults are the operator's to clear: release the hold, or let the account delete. The app cannot clear either.
- The supervisor can still rule for the overflow. It is recorded here as the alternative not built.

**Rule.** A rotation that keeps failing keeps every row (aud-a56), and is reported for as long as it lasts:
- at its first failed write;
- then whenever the file has grown by another whole limit since the last report, or an hour has passed since it,
  whichever comes first.

Each report names the file, its size, how far past its limit it is, since when the rotation has failed, and the reason.
The Admin Event Log shows each streak this instance has.

1. New `Services/RotationFailureStreak.cs`:
   - `public sealed record LogRotationFailure(string Log, string Path, DateTime SinceUtc, long SizeBytes, long LimitBytes)`,
     with `OverBytes`.
   - `internal sealed class RotationFailureStreak`:
     - `State`, a `LogRotationFailure` replaced whole on each failure, so a reader on another thread sees one failure's
       state;
     - `Reports`, how many times it has been reported;
     - `Failed(long? sizeBytes, DateTime nowUtc)`, which records the size and answers whether to report: the first
       failure; then another whole limit of growth since the last report, or `ReportEvery` (one hour) since it.
2. `JsonlLogService`:
   - `_rotationFailureReported` becomes `_rotationFailures`, a `ConcurrentDictionary<LogStream, RotationFailureStreak>`,
     still changed only under `_lock`.
   - `rotateIfFull` removes the stream's streak once `RotateIfFull` returns.
   - `rotationFailed` starts a streak, or a new one for a new file. It reads the file's size (guarded) and reports when
     `Failed` says so:
     - the first report is the aud-a56 warning, with the size and the overage added and its last sentence corrected;
     - a later one is "still cannot be rotated", with the size, the overage, since when, and what clears it.
     - The whole callback is guarded, so no failure in it costs the row.
   - New `public IReadOnlyList<LogRotationFailure> RotationFailures`, the streaks' states, read without `_lock`.
   - New test seam `internal TimeProvider Clock`, as `LockWait` is one.
3. `ExtendedLogService`: the same for its one file.
   - `_rotationFailure`, a `RotationFailureStreak?` only the consumer writes, volatile for the page that reads it.
   - `public LogRotationFailure? RotationFailure`, and `Clock`.
4. `AdminEventLog.razor`:
   - `LoadEvents` reads both, in memory, beside the drop count, with diagnostics on or off.
   - The diagnostics area shows one warning line per streak, after the drop count. It names the log and the file, since
     when, the size and how far past the limit; that every entry is still written to it; and what clears it.
   - It is shown whether diagnostics are on or not, because the file grows until the volume fills. It is this
     instance's, like the drop count.
5. `README.md`, "Rotation": the report repeats while the streak lasts, and the event log shows it.
6. Comments after the mutation round, checked code-identical.

Tests:
- `ExchangeAdminWeb.Tests/JsonlWriterRotationTests.cs`, over the real writer at 1024 bytes:
  - `AFullFileThatStaysUnrotatable_IsReportedAgain_EachTimeItGrowsByAnotherLimit`: the file held, rows written until it
    has grown by one more limit since the first report, then by another. Three warnings. Each later one names the file,
    its size and its overage, and they match the file.
  - `AFullFileThatStaysUnrotatable_IsReportedAgain_OnceAnHourHasPassed`, through the clock seam: a row a minute short of
    the hour gives no new warning; one past it gives a second.
  - `TheStreak_IsShownWhileItLasts_AndGoneOnceTheRotationSucceeds`. `RotationFailures` names the stream, the file, since
    when, the size, the limit and the overage. Once the hold is released and a row rotates the file, it is empty.
  - The existing streak tests keep their one-warning assertions: their held rows grow the file by less than a limit,
    inside the hour.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`:
  `TheConsumer_GivenAFullFileItCannotRotate_ReportsItAgainAsItGrows_AndShowsTheStreak`.
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`: `ALogFileThatCannotBeRotated_IsShown_WithHowFarPastItsLimitItIs`,
  with diagnostics on and off.

Mutations at the call site (md5 before and after, the named failing test, restored from the index, touched, rebuilt,
md5 matched, green):
- M1 the re-warn schedule removed: `Failed` reports the first failure only (the record's).
- Extra E1 the size step removed, so only the hour re-reports.
- Extra E2 the hour removed, so only the size step re-reports.
- Extra E3 the streak not shown: `RotationFailures` always empty.
- Extra E4 the page's line not rendered.

Filter:
`FullyQualifiedName~JsonlWriter|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~EventLog|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests`.
`ClickGate*` and `ProgressRegistryTests` are run alone as well.

Files:
- `Services/RotationFailureStreak.cs` (new)
- `Services/JsonlLogService.cs`
- `Services/ExtendedLogService.cs`
- `Components/Pages/AdminEventLog.razor`
- `ExchangeAdminWeb.Tests/JsonlWriterRotationTests.cs`
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`
- `README.md`
- this record

## Fix

- New `Services/RotationFailureStreak.cs`:
  - `public sealed record LogRotationFailure(string Log, string Path, DateTime SinceUtc, long SizeBytes, long LimitBytes)`,
    with `OverBytes` (`Math.Max(0, SizeBytes - LimitBytes)`).
  - `internal sealed class RotationFailureStreak`:
    - `ReportEvery`, one hour; `State`, a volatile `LogRotationFailure` replaced whole; `Reports`.
    - `Failed(long? sizeBytes, DateTime nowUtc)` records the size, then takes `grown` (another whole limit since the last
      report) and `due` (an hour since it). It reports the first failure, and after that when either holds.
    - Static `SizeOf(path)`: the file's length, or null when it cannot be read. It never throws.
- `Services/JsonlLogService.cs`:
  - `_rotationFailureReported`, a `HashSet<LogStream>`, is replaced by `_rotationFailures`, a
    `ConcurrentDictionary<LogStream, RotationFailureStreak>`, changed only under `_lock`.
  - `rotateIfFull` removes the stream's streak once `RotateIfFull` returns.
  - `rotationFailed` is new private `RotationFailed(stream, logPath, ex)`, all of it in a try whose catch takes any
    exception. It reads the clock, starts a streak for a new stream or file, and asks `Failed(SizeOf(logPath), now)`.
    - The first report is aud-a56's warning with "it is {Size} bytes, {Over} over its limit of {Limit}" added. Its last
      sentence now says the warning is repeated each time the file has grown by another limit, or once an hour.
    - A later report: "The JSONL {Stream} file {Path} still cannot be rotated: it is {Size} bytes, {Over} over its limit
      of {Limit}, and its rotation has failed since {Since:o}: {Reason} Every event is still written to it, so it grows
      until the rotation succeeds. Whatever holds it open without delete sharing must let go of it, or this instance's
      account must be allowed to delete in the log folder".
  - New `public IReadOnlyList<LogRotationFailure> RotationFailures`, and the test seam `internal TimeProvider Clock`.
- `Services/ExtendedLogService.cs`: the same for its one file.
  - `_rotationFailure`, a `volatile RotationFailureStreak?`, replaces the `bool`.
  - `public LogRotationFailure? RotationFailure`, `Clock`, and `RotationFailed(logPath, ex)` with the extended file's two
    messages.
- `Components/Pages/AdminEventLog.razor`, 1,698 lines -> 1,722:
  - New `rotationFailures`, reset at the top of `LoadEvents`. It is read after the drop count, from
    `JsonlLog.RotationFailures` and `ExtendedLog.RotationFailure`, with diagnostics on or off.
  - New `RotationFailureText` and `SizeText`. The diagnostics area shows one `alert-warning` line per failure, after the
    drop count, under a Razor comment.
  - NotYetConverted in `ClickGateRegistry`, so no key moves. `ProgressRegistry`'s pins in `LoadEvents` are unchanged: the
    new read is in memory, after `ExtendedLog.GetEntries(`.
- `README.md`, "Rotation": the bullet on a rotation that fails says how the warning is repeated, that the event log shows
  the file, and that nothing bounds it while the rotation fails.

Tests:
- `ExchangeAdminWeb.Tests/JsonlWriterRotationTests.cs`, +3:
  `AFullFileThatStaysUnrotatable_IsReportedAgain_EachTimeItGrowsByAnotherLimit`,
  `AFullFileThatStaysUnrotatable_IsReportedAgain_OnceAnHourHasPassed` and
  `TheStreak_IsShownWhileItLasts_AndGoneOnceTheRotationSucceeds`; a `ManualClock`.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`, +1:
  `TheConsumer_GivenAFullFileItCannotRotate_ReportsItAgainAsItGrows_AndShowsTheStreak`; a `FixedClock`.
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`, +2:
  `ALogFileThatCannotBeRotated_IsShown_WithHowFarPastItsLimitItIs`, with diagnostics on and off.
- Unchanged, and still green: aud-a56's streak tests, whose held rows grow the file by less than a limit, inside the
  hour.

Caller-visible changes, by design:
- A failed rotation's first warning names the file's size and how far over its limit it is, and says it will be repeated.
- While the streak lasts, the warning is repeated with the size, the overage and since when. That happens at the first
  write after the file has grown by another limit since the last report, or after an hour has passed since it, whichever
  comes first.
- The Admin Event Log shows each file this instance cannot rotate, with diagnostics on or off.
- What is written does not change: every row is still appended to the full file (aud-a56).

## Guard proof

Reproduction: M1 puts back aud-a56's schedule, a streak reported at its first failure only. The file held open grew by
more than three limits with one warning, and the hour passed with no second one. That is the defect as the record states
it.

Driver: the aud-a62 round's (session scratchpad `w-a62a63a65/mutate.ps1`), spec `w-a62a63a65/round-a65.ps1`, log
`w-a62a63a65/a65-mutations.log`. M1 to E4 are the plan's. E5 (the extended streak) and E6 (the end of a streak) were
added for the two halves the plan's list did not reach.

The filter (185 tests) covers both writers' suites, every `EventLog` suite, and the audit service and writer:
`FullyQualifiedName~JsonlWriter|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~EventLog|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests`

Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
bytes and the filter: 185/185 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the re-warn schedule removed: `if (Reports > 0)` for `if (Reports > 0 && !grown && !due)` | `RotationFailureStreak.cs`, `860BBA60DD7163BA304B40EEF157D1E4` | `485624964B5E35580FBAB041E41C26E8` | 3 of 185: `AFullFileThatStaysUnrotatable_IsReportedAgain_EachTimeItGrowsByAnotherLimit` - "the third report never came" in 200 rows; `..._OnceAnHourHasPassed` - 1 warning where 2; `TheConsumer_GivenAFullFileItCannotRotate_ReportsItAgainAsItGrows_AndShowsTheStreak` - "1 warning(s) for a file that grew by more than three limits" |
| extra E1 the size step removed (`var grown = false;`) | `RotationFailureStreak.cs`, `860BBA60DD7163BA304B40EEF157D1E4` | `E5F9C5BBE01C3B250317455ED440CAA9` | 2 of 185: `..._EachTimeItGrowsByAnotherLimit` and the extended test, as under M1. The hour test passes: it is the size step's alone |
| extra E2 the hour removed (`var due = false;`) | `RotationFailureStreak.cs`, `860BBA60DD7163BA304B40EEF157D1E4` | `767EA74FD9098923FF628209FB8668B0` | 1 of 185: `..._OnceAnHourHasPassed` - 1 warning where 2 |
| extra E3 the JSONL streaks not shown (`RotationFailures` always empty) | `JsonlLogService.cs`, `04F50809CFEB7D5B54934BB00F9DC334` | `EB6BC7D3078667D07CEFC9750508F9B4` | 3 of 185: `TheStreak_IsShownWhileItLasts_AndGoneOnceTheRotationSucceeds` - empty where one; `ALogFileThatCannotBeRotated_IsShown_WithHowFarPastItsLimitItIs`, diagnostics on and off - "cannot rotate its audit log file ..." not found |
| extra E4 the page's line not rendered (`@foreach` over an empty array) | `AdminEventLog.razor`, `4D77DE32116C04CC3B663652485B58E4` | `93A0836A8F78AD8992C4FCBB355AD473` | 2 of 185: the page test, diagnostics on and off - the same |
| extra E5 the extended streak not shown (`RotationFailure => null`) | `ExtendedLogService.cs`, `42FCC0E4027A1AF0BF8AAD0836BE9DFD` | `291E0976116A54048CCBDD0ABEF47B49` | 1 of 185: the extended test - "Assert.NotNull() Failure" |
| extra E6 a JSONL streak never ended (`_rotationFailures.TryRemove(stream, out _);` removed) | `JsonlLogService.cs`, `04F50809CFEB7D5B54934BB00F9DC334` | `E5CCF2B99EAD03C6EAA080A12C044A88` | 4 of 185: `TheStreak_...` - still shown after the rotation; aud-a56's `AFailureStreak_EndsWhenARotationSucceeds_AndTheNextFailureIsReportedAgain` - 1 warning where 2; the page test, on and off - still shown after the rotation |

Unchanged through the round:
- `JsonlWriterRotationTests.cs` `1D345ED5E667F9295E76B5F663E11E3F`
- `ExtendedLogServiceTests.cs` `8A77D88CF765B09810DA6CBBDB7EAB5B`
- `EventLogDiagnosticsTests.cs` `5D39CF76002F1327F0C981D4883352CE`

Before the round, the new tests (with aud-a56's streak test, 7 in all) ran three times on these bytes, 7/7 each time.
They hold files open and wait on the extended log's consumer, so they were checked for flakes.

`ClickGate*` and `ProgressRegistryTests`, run alone on these bytes before the round (Release): 351/351.

Not run, by design:
- A real SIEM tailer - the held handle is the one a tailer opens, as in aud-a56's tests.
- A folder whose ACL denies delete - aud-a56's tests cover that cause for the write; the schedule is the same for both
  causes.
- An hour of wall-clock time - the clock seam moves it.

## Comments after the round

None were needed. The fix's comments were written before the round and are in the bytes measured above.

## Known gaps

- Nothing bounds the file, or the volume, while its rotation fails. The overflow files the record preferred were not
  built; the Plan says why, and the supervisor can still rule for them.
- The schedule is checked on writes. A file that is no longer written is not reported again, but it is not growing
  either.
- A streak and its schedule are this instance's. On a shared log root the other instance reports and shows its own.
- "Since" is this instance's first failed write in the streak, not the moment the file reached its limit.
- When the file's size cannot be read at a failure, the last size stands, and it is 0 at a streak's first failure.
- The page shows this instance's streaks only, and only as of its last load.

## Branch-tip gates

See aud-a62's record for the branch's gates.

## Closeout

Fixed on branch `worktree-agent-a4c254a086c7d1d55`, not merged, not pushed. Two bumps are due, both the supervisor's at
merge, since `ExchangeAdminWeb.csproj` and `Modules/ModuleCatalog.cs` were outside this brief:
- the base app version, for the JSONL writer and the extended log, which are shared;
- the Admin Event Log's module version, for its diagnostics line.

This record was added to master at `97b89ae4` and to this branch from that commit, so the merge meets an add/add on this
one file. This branch's copy is master's with its Status, Branch and Commit lines filled in and the Plan and everything
after it added; take it.
