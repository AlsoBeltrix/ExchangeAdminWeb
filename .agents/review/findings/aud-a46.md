# aud-a46: Extended log leftovers - the consumer's append has the half-line hazard, a failed read shows "no diagnostics", the dropped-entry count is shown nowhere, and queue overflow is not counted

**Severity**: LOW - diagnostics only. (1) `ExtendedLogService`'s consumer writes each line through a
`StreamWriter` over `FileMode.Append`, so it has the aud-a28z part-3 half-line hazard; the Admin Event
Log's `ParseExtendedLine` drops a merged line. (2) `GetEntries` has a bare catch that returns an empty list
on any read failure, so the event log shows "no diagnostics" instead of "the diagnostics could not be read".
(3) `DroppedEntryCount` (aud-a28z) is exposed but shown on no page. (4) The bounded queue's DropOldest
overflow is not counted (the channel's itemDropped callback would do it).
**Status**: In progress - plan below, approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a42d5cbfa41a7a483` (from `2df48c36`)
**Commit**: -
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

## Guard proof

Pending.
