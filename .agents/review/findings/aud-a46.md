# aud-a46: Extended log leftovers - the consumer's append has the half-line hazard, a failed read shows "no diagnostics", the dropped-entry count is shown nowhere, and queue overflow is not counted

**Severity**: LOW - diagnostics only. (1) `ExtendedLogService`'s consumer writes each line through a
`StreamWriter` over `FileMode.Append`, so it has the aud-a28z part-3 half-line hazard; the Admin Event
Log's `ParseExtendedLine` drops a merged line. (2) `GetEntries` has a bare catch that returns an empty list
on any read failure, so the event log shows "no diagnostics" instead of "the diagnostics could not be read".
(3) `DroppedEntryCount` (aud-a28z) is exposed but shown on no page. (4) The bounded queue's DropOldest
overflow is not counted (the channel's itemDropped callback would do it).
**Status**: Complete; merged `2a15e9ed`; codex attr-batch-14 raised one HIGH, admitted as aud-a51
**Branch**: `worktree-agent-a42d5cbfa41a7a483` (from `2df48c36`)
**Commit**: `543f5d5f` (fix, tests, README, record); comments `ab666f85`; plan `45a99de1`
**Reviewer**: implementer (surfaced by the aud-a28z/a26u/cdt-41 agent, 2026-10-22, candidates 2, 3 and 4 of its hand-back)

## Evidence

At `36da9789`: `Services/ExtendedLogService.cs` around 261-263 (the append), 216-218 (`GetEntries` bare
catch), `DroppedEntryCount`, the bounded channel's options; `Components/Pages/AdminEventLog.razor`
`ParseExtendedLine` around 824 and the diagnostics section; `Components/Pages/AdminSettings.razor` (where a
health figure belongs - NotYetConverted).

## Approach (to be written by the implementing agent before code)

- The consumer writes one line per unbuffered write with the aud-a28z truncate-back and tail repair (share
  the JSONL writer's helper rather than copy it).
- `GetEntries` returns a typed outcome (entries, or failed with a reason); the event log shows the failure.
- The dropped-entry count (including DropOldest overflow via `itemDropped`) is shown on the Admin Settings
  diagnostics area with one line of text.
  *Amended 2026-10-22 by the implementing agent, on the coordinator's brief:* shown on the **Admin Event Log's
  diagnostics** instead (the view "Show diagnostics" turns on), not on Admin Settings -
  `Components/Pages/AdminSettings.razor` is another agent's file in this round, and the event log is where the
  extended log's entries are read, so the count sits beside them.
- Tests through `ExtendedLogServiceTests` and the pages' reflection drivers; mutations at the call site: the
  buffered append again; the bare catch again; the overflow callback removed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a28z/a26u/cdt-41 hand-back (`6b71adcc`).
One commit `fix(extended-log,event-log): ... (aud-a46)`, third of three on branch `worktree-agent-a42d5cbfa41a7a483`,
after aud-a47 (whose shared appender it uses) and aud-a45. Shared infrastructure (the extended log) and the Admin Event
Log's behaviour, so the base app version and that module's version are due; the supervisor owns both bumps. Evidence
re-read at `2df48c36`:

1. `Services/ExtendedLogService.cs` `ProcessQueueAsync` (253-274) writes each entry through a `StreamWriter` (262) over
   a buffered `FileStream` opened `FileMode.Append` (261): aud-a28z's half-line hazard (no take-back, no tail repair) and
   aud-a47's (no lock against the other instance, which writes the same file under a shared root).
   `Components/Pages/AdminEventLog.razor` `ParseExtendedLine` (824-880) parses each line with `JsonDocument.Parse` and
   drops one that does not parse, so a cut line and the entry appended onto it are lost together.
2. `GetEntries` (182-220) wraps the whole read in a bare `catch { return new(); }` (216-219): one file it cannot open or
   read empties the whole answer, and its one caller, the event log's `LoadEvents` (629-642), shows "no diagnostics".
   The other files' lines go with it.
3. `DroppedEntryCount` (76) is read by no page.
4. The queue is `Channel.CreateBounded` with `DropOldest` (54-59) and no `itemDropped` callback, so an entry the full
   queue discards is not counted.

**Rule.** An extended-log entry is a line of its own or nothing, under the file's lock, like a JSONL record. A read that
cannot read a file says which and why, and keeps what it could read. Every entry that passed the level and was not
written is counted - a failed write, a closed queue, an overflow - and the count is on the page that shows the extended
log.

1. ExtendedLogService:
   - The consumer writes through `JsonlFileAppender.Append` (aud-a47). `GetWritableLogPath` splits into
     `ActivePath(date)` and `RotateIfFull(date, activePath)` (unchanged logic). The folder is created as before, and the
     line's bytes are the ones `StreamWriter.WriteLine` wrote (UTF-8 without a BOM, `Environment.NewLine`).
   - New test seams: `internal WriteLineBytes`, as the JSONL writer's, and `internal LockWait`.
   - The repair and a failed truncation are reported through `ReportSafely` as guarded warnings naming the path. A write
     that fails, or a lock not taken, lands in the consumer's catch, as today: dropped, counted, reported, next entry.
   - The channel is created with `itemDropped: _ => Interlocked.Increment(ref _droppedEntries)` - counted, not logged:
     an overflow is a burst, and one report per discarded entry would be the flood it already is.
2. `GetEntries` returns new `public sealed record ExtendedLogRead(IReadOnlyList<string> Entries,
   IReadOnlyList<string> Unreadable)`:
   - Each file is opened and read in its own try.
   - A file that fails adds "<file name>: <reason>" to `Unreadable`, is reported through a guarded warning naming its
     path, and the read goes on to the next file; the lines already read stay, being whole lines.
   - `maxLines <= 0` is an empty answer, as before.
3. AdminEventLog.razor:
   - `LoadEvents` keeps its `ExtendedLog.GetEntries(` call where it is (`ProgressRegistry`'s closing pin), reads
     `.Entries`, and gathers each date's `Unreadable`.
   - After the date loop, with diagnostics on, it sets two new fields, reset with the others at the top:
     - `diagnosticsWarning`: "Some of the extended diagnostic log could not be read, so diagnostic entries may be
       missing: ...".
     - `extendedLogDropped`: `ExtendedLog.DroppedEntryCount`.
   - Shown above the table with `loadWarning`, so in the empty-result branch too:
     - The warning as `alert alert-warning`.
     - A count above zero as one muted line: "This instance's extended log has dropped N entries since it started: ones
       it could not write, and ones its queue discarded when they came faster than the disk took them." Singular for
       one.
   - NotYetConverted in `ClickGateRegistry`, so no line key moves.
4. Not touched: `Components/Pages/AdminSettings.razor` (another agent's).
5. Comments after the mutation round, checked code-identical.

Tests.

`ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`; the two `GetEntries` tests read `.Entries`:
- `TheConsumer_GivenAnEntryThatFailsHalfWritten_TakesItBack_SoTheFileHoldsExactlyTheEntriesWritten`: the seam writes
  half of one entry and throws. The file parses line by line, as the event log parses it, to exactly the other three,
  and one drop is counted.
- `TheConsumer_GivenATailADeadProcessLeft_StartsTheNextEntryOnItsOwnLine_AndReportsTheRepair`.
- `TheConsumer_TakesTheFilesLock_AndAWaitThatRunsOutIsACountedDrop`:
  - The file's lock is held by a raw handle, with a 300 ms bound.
  - The entry is dropped, counted and reported.
  - Once the handle is released, the next entry is written.
- `GetEntries_WhenAFileCannotBeRead_SaysWhichAndWhy_AndKeepsTheLinesItCouldRead`: a readable rotated file and a held
  active one.
- `AFullQueue_CountsTheEntriesItDiscards`:
  - The consumer is held inside its first write, through the seam.
  - The test then writes 1,024 entries that fill the queue, plus five more.
  - Result: five counted. After release, the file holds the first entry and the newest 1,024.

New `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs` - the page's own `LoadEvents` by reflection over a real JSONL
writer and extended log on a temp root, and its own `BuildRenderTree` into a `RenderTreeBuilder` (the aud-a26u page
tests' reading):
- `AnExtendedLogThatCannotBeRead_IsShownAsUnreadable_NotAsNoDiagnostics`.
- `TheDroppedEntryCount_IsShownWithTheDiagnostics_OnlyWhenThereAreSome`: a count of one, shown with diagnostics on and
  not with them off; and nothing shown at zero.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green):
- M1 the buffered append again (the consumer's `StreamWriter` over `FileMode.Append` in place of the appender).
- M2 the bare catch again (a file that cannot be read empties the answer and names nothing).
- M3 the overflow callback removed.
- Extra E1: the page ignoring the read's `Unreadable`.
- Extra E2: the count's line removed.

`ProgressRegistryTests` and `ClickGate*` are run alone as well.

Files:
- `Services/ExtendedLogService.cs`
- `Components/Pages/AdminEventLog.razor`
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs` (new)
- `README.md` (the extended log under the shared-root note, and the queue's drop now counted)
- this record

## Fix

- `Services/ExtendedLogService.cs`:
  - The queue is created with `itemDropped: _ => Interlocked.Increment(ref _droppedEntries)`.
  - `GetEntries` returns new `public sealed record ExtendedLogRead(IReadOnlyList<string> Entries,
    IReadOnlyList<string> Unreadable)`. Each existing file is opened and read in its own try. A file that fails adds
    "<file name>: <exception message>" to `Unreadable` and is reported through `ReportSafely` as "Could not read the
    extended log file {Path}", and the read goes on; the lines it had already read stay.
  - `ProcessQueueAsync` takes `ActivePath(entry.LogDate)`, creates the folder as before, and appends through new private
    `AppendLine(logPath, entry)`. That is `JsonlFileAppender.Append` (aud-a47's lock, then `RotateIfFull`, then the
    aud-a28z one-line append), with the `WriteLineBytes` seam and two guarded warnings naming the path: a repaired tail,
    and a take-back that failed.
  - A failed write, or a lock not taken, still lands in the consumer's catch: dropped, counted, reported, next.
  - `GetWritableLogPath` is split into `ActivePath` and `RotateIfFull`, unchanged in logic.
  - New `internal WriteLineBytes` and `internal LockWait`.
- `Components/Pages/AdminEventLog.razor` (1,613 lines -> 1,638; NotYetConverted in `ClickGateRegistry`, so no key moves):
  - New fields `diagnosticsWarning` and `extendedLogDropped`, reset with the others at the top of `LoadEvents`, and
    `DroppedEntriesText`.
  - The `ExtendedLog.GetEntries(` call stays where `ProgressRegistry` pins it; its `.Unreadable` is gathered per date and
    its `.Entries` parsed as before.
  - After the date loop, with diagnostics on, the warning is set from the gathered names, and the count from
    `ExtendedLog.DroppedEntryCount`.
  - Both are rendered after `loadWarning`: the warning as `alert alert-warning`, and a count above zero as one muted
    line.
- `README.md`: the shared-root subsection names the extended diagnostic files, and the extended-diagnostics paragraph
  says every drop is counted and shown with the diagnostics.

Tests:
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs` +5:
  - `TheConsumer_GivenAnEntryThatFailsHalfWritten_TakesItBack_SoTheFileHoldsExactlyTheEntriesWritten`
  - `TheConsumer_GivenATailADeadProcessLeft_StartsTheNextEntryOnItsOwnLine_AndReportsTheRepair`
  - `TheConsumer_TakesTheFilesLock_AndAWaitThatRunsOutIsACountedDrop`
  - `GetEntries_WhenAFileCannotBeRead_SaysWhichAndWhy_AndKeepsTheLinesItCouldRead`
  - `AFullQueue_CountsTheEntriesItDiscards`
  - Helpers `ReadAsTheEventLogDoes` and `HoldTodaysLock`. The capturing logger keeps each entry's exception. The two
    `GetEntries` tests read `.Entries`.
- New `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`, 2 cases:
  - `AnExtendedLogThatCannotBeRead_IsShownAsUnreadable_NotAsNoDiagnostics`
  - `TheDroppedEntryCount_IsShownWithTheDiagnostics_OnlyWhenThereAreSome`

Caller-visible changes, by design:
- `ExtendedLogService.GetEntries` returns `ExtendedLogRead`. Its one production caller, the Admin Event Log, reads
  `.Entries` and `.Unreadable`.
- With **Show diagnostics** on, the event log:
  - names each extended log file it could not read: "Some of the extended diagnostic log could not be read, so
    diagnostic entries may be missing: <file>: <reason>". It no longer shows "no diagnostics" for that case.
  - shows "This instance's extended log has dropped N entries since it started: ones it could not write, and ones its
    queue discarded when they came faster than the disk took them." when N is above zero. "entry" is singular for one.
- The extended log writes under the file's lock, as the JSONL writer does since aud-a47. A failed write is taken back,
  and a cut tail is ended before the next entry, with a warning.
- A full queue's discards are counted in `DroppedEntryCount`.
- An entry that writes produces the same bytes as before: UTF-8 without a BOM, `Environment.NewLine`.

## Guard proof

Reproduction: M1 puts the consumer back as it was at `2df48c36` - rotation, then a `StreamWriter` over `FileMode.Append`,
no lock. Its failures are the defect: the next entry appended onto a cut line, which the event log drops, and an entry
written while another writer held the file. M2 is `GetEntries`' bare catch as it was. M3 is the queue as it was.

Same driver (session scratchpad `mutate/round-a46.ps1`, log `mutate/a46-mutations.log`). The filter (484 tests) is the
extended log's, the event log's, the writer's, and the page registries':
`FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~EventLog|FullyQualifiedName~JsonlWriterLockTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate`

The round was run twice:
- The first run's M1 also failed the page's unreadable-file test, and not by its assertion. The test took the file
  exclusively while the old consumer was still closing its handle after the line became readable - a race in the
  fixture. The fixed consumer has the same window, only shorter.
- The fixture now retries its exclusive open for a moment (`HoldExclusively`), and the table is the second run, on this
  commit's bytes.

Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
bytes and the filter: 484/484 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the buffered append again (`RotateIfFull` and the `StreamWriter` over `FileMode.Append` in place of `AppendLine(logPath, entry)`) | `ExtendedLogService.cs`, `C8D9768D05FCE0305EF3018C4C4F5E48` | `60F5DC52CA8D108B8758BAFB60BBB67A` | 4 of 484: `TheConsumer_GivenATailADeadProcessLeft_...` - `["earlier"]`, the next entry appended onto the cut line and dropped with it; `TheConsumer_TakesTheFilesLock_...` - the entry written while another writer held the lock; `TheConsumer_GivenAnEntryThatFailsHalfWritten_...` and `AFullQueue_...` - the append no longer goes through the seam either test drives it by |
| M2 the bare catch again (`catch { return new ExtendedLogRead([], []); }` for the per-file catch) | `ExtendedLogService.cs`, `C8D9768D05FCE0305EF3018C4C4F5E48` | `C2B659735EFF483916FFBCEFDCC0B8C2` | 2 of 484: `GetEntries_WhenAFileCannotBeRead_...` - nothing read, nothing named; `AnExtendedLogThatCannotBeRead_IsShownAsUnreadable_...` - the page says nothing of it |
| M3 the overflow callback removed | `ExtendedLogService.cs`, `C8D9768D05FCE0305EF3018C4C4F5E48` | `964685C22E531B5C8DBE4B869EE9F3AB` | 1 of 484: `AFullQueue_CountsTheEntriesItDiscards` - 0 counted where the queue discarded 5 |
| extra E1 the page ignoring the read's `Unreadable` | `AdminEventLog.razor`, `4C8BDEDE7AE4ACA30EFA78C07ECEA8AD` | `2969C150211C15D01FA9CF048CBABCFB` | 1 of 484: `AnExtendedLogThatCannotBeRead_IsShownAsUnreadable_...` |
| extra E2 the count's line removed | `AdminEventLog.razor`, `4C8BDEDE7AE4ACA30EFA78C07ECEA8AD` | `5A71BC49F3448F551801F7193769C883` | 1 of 484: `TheDroppedEntryCount_IsShownWithTheDiagnostics_...` |

Unchanged through the round: `ExtendedLogServiceTests.cs` `D7CC3D9DB3BD5733CB85391764375406` and
`EventLogDiagnosticsTests.cs` `FD23EB6A43772A77488EB2AD8B99A98A`. The code comments are written after the round, in
their own commit, so the MD5s above are this commit's bytes.

Measured on this host while checking the full-queue test's 11 s: an append costs 10.98 ms with the lock and 10.65 ms
without, the mean of 500 writes each. The time is the per-entry open and close, which the old consumer also paid; the lock adds
about 0.3 ms.

Not run, by design: the page in a browser - the alert and the line are read from the compiled page's render tree, and
`LoadEvents` is driven by reflection.

## Known gaps

- The count is this instance's, since it started. The other instance's drops are on its own page, and a restart resets
  the count.
- `Unreadable` names files the read could not open or finish. A line it read that does not parse is still dropped
  silently by `ParseExtendedLine`, as before. With the take-back and the tail repair, only a line a dead writer cut
  remains such a line.
- The trace files' read failures on the same page (`LoadEvents`' trace catch) are logged and not shown - the same class
  as part 2, for the trace stream. Not this finding's; listed as a candidate in the hand-back.

## Comments after the round

Written after all three rounds, in their own commit (`ab666f85`), and checked as aud-a47's record says. This record's
files:

- `Services/ExtendedLogService.cs` `C8D9768D05FCE0305EF3018C4C4F5E48` -> `BEFAD1D78A7BB95DF9876B004789E3B9`: 295 code
  lines, their MD5 `27794546DA5AE6B9E9AADD7C86BD1719` on both sides. 35 lines were added and 2 changed:
  - the full queue's callback and why it never logs;
  - the read's typed answer and why each file is read on its own;
  - the consumer's one-line append under the lock, and why the folder is created before it;
  - the seams, and `ExtendedLogRead`.
  - The two changed lines are the drop count's field comment and its summary, which now name the queue's discards.
- `Components/Pages/AdminEventLog.razor` `4C8BDEDE7AE4ACA30EFA78C07ECEA8AD` -> `114E2E2CCF39796FC6BD8C12B0E5CCCC`: 1,263
  code lines, MD5 `9E57C56B729582C07A4FCB3A8B8D1697`. 7 lines were added: the two notices and why they sit above the
  table, and the post-loop block. The page grows from 1,638 lines to 1,645; it is NotYetConverted, so no key moves.

## Branch-tip gates

See aud-a47's record (`ab666f85`) for the branch's gates. This commit's seven cases are among the branch's twenty.
`ClickGate*` 316/316 and `ProgressRegistryTests` 35/35 were run alone, at the tip.

## Closeout

Fixed on branch `worktree-agent-a42d5cbfa41a7a483`, not merged, not pushed. Two layers changed, so two bumps are due:
- Shared infrastructure, the extended log: the base app version.
- The Admin Event Log's behaviour, its diagnostics notices: its module version.

Both bumps are the supervisor's, at merge: `Modules/ModuleCatalog.cs` and the csproj version elements were outside this
brief. `Components/Pages/AdminSettings.razor` was not touched. The fix needed no file another agent owns, so nothing is
left for a follow-up under that heading.

## Codex review

Pending.
