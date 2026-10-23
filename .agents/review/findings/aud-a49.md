# aud-a49: Admin Event Log - an unreadable trace file is not shown, unparseable lines are dropped without a count, and the extended log's Dispose can leave a full queue unwritten and uncounted

**Severity**: LOW - diagnostics and audit visibility. (1) `AdminEventLog.razor` `LoadEvents`' trace-file
catch only logs, so with diagnostics on the page looks complete when a trace file could not be read (the
aud-a46 part-2 defect, for the trace stream). (2) `ParseAuditLine` and its siblings drop an unparseable line
silently with no count; the cut line a dead writer leaves (which the aud-a28z tail repair keeps) disappears
without a trace in the UI. (3) `ExtendedLogService.Dispose` waits 5 s for the consumer; at about 10 ms per
append a full 1,024-entry queue needs about 11 s, so shutdown can leave hundreds of entries unwritten and not
counted in `DroppedEntryCount`.
**Status**: Fixed on the branch, not merged; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-ac1038e1d7ece4ae5` (from `132d6e97`)
**Commit**: `6372c392` (fix, tests, README, record); comments `c2d6215d`; plan `5ed8a5a8`
**Reviewer**: implementer (surfaced by the aud-a47/a45/a46 agent, 2026-10-22, candidates 2, 3 and 4 of its hand-back)

## Evidence

At `2a15e9ed`: `Components/Pages/AdminEventLog.razor` around 646 (the trace-file catch) and 732
(`ParseAuditLine`; the siblings for trace and extended lines); `Services/ExtendedLogService.cs` around 262
(`Dispose`'s 5 s wait); the aud-a46 `ExtendedLogRead(Entries, Unreadable)` shape and the diagnostics warning.

## Approach (to be written by the implementing agent before code)

- The trace read returns the aud-a46 typed shape (entries, unreadable files); the page's diagnostics warning
  names unreadable trace files beside the extended ones.
- Every parser counts the lines it drops per stream; the page shows one muted line "N lines in the audit/
  trace/extended log could not be parsed and are not shown" when the count is above zero.
- `Dispose` drains the queue with a bound proportional to its length (or marks the remainder as dropped and
  counts them before returning); either way the count is honest and the wait is bounded.
- Tests through `EventLogDiagnosticsTests` and `ExtendedLogServiceTests`; mutations at the call site: the
  trace catch back to log-only; one parser's count dropped; the fixed 5 s wait again with a full queue.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Admitted from the aud-a47/a45/a46 hand-back (`24eab6c9`). One commit
`fix(event-log,extended-log): ... (aud-a49)`, third of three on branch `worktree-agent-ac1038e1d7ece4ae5` (from
`132d6e97`), after aud-a51 and aud-a48. Two layers change: the extended log, which is shared, so the base app version is
due; and the Admin Event Log's behaviour, so its module version is due. The supervisor owns both bumps.

Evidence, re-read at `132d6e97`:
1. `Components/Pages/AdminEventLog.razor` `LoadEvents` (539-730):
   - The trace files' read (617-648): its catch (644-647) only calls `Logger.LogError` (646). A trace file that cannot be
     read is shown nowhere, and with diagnostics on the page looks complete.
   - The extended read's `Unreadable` is gathered (651-652) into `unreadableDiagnostics` (580). After the date loop that
     list becomes `diagnosticsWarning`, worded for the extended log only (670-679).
   - `ProgressRegistry`'s entry for `LoadEvents` pins `ReadLinesShared(` at both its occurrences (the audit read at 598 and
     the trace read at 631), so the trace read stays inline in `LoadEvents`.
   - `AuditFirstMergeTests` pins `diagnostics.Enqueue(entry);` twice and `buffer.Enqueue(entry);` once.
2. The parsers:
   - `ParseAuditLine` (732-794), `ParseTraceLine` (796-854) and `ParseExtendedLine` (856-912) each return null from their
     catch (790-793, 850-853, 908-911) for a line that does not parse, and also return null for a line they filter: one
     outside the range (744, 808, 868), or an audit file's record of another event type (749).
   - Their three callers (601, 634, 656) skip a null either way. A dropped line is therefore counted nowhere, including
     the cut line a dead writer leaves, which the aud-a28z tail repair keeps.
   - `ParseAuditLine`'s inner `catch { }` round `UndoRegistry.FindHandler` (765) drops nothing.
3. `Services/ExtendedLogService.cs` `Dispose` (257-269) closes the queue and waits `TimeSpan.FromSeconds(5)` (262) for the
   consumer, then returns whatever is left.
   - The queue holds up to 1,024 entries (13), and aud-a46 measured 10.98 ms per append on this host, so a full queue
     needs about 11 s.
   - Entries still queued when the wait ends are neither written nor counted in `DroppedEntryCount`.
   - The queue is created with `SingleReader = true` (61).

**Rule.** Whatever the page could not show, it says. A trace file it could not read is named beside the extended ones,
and the lines each log held that could not be parsed are counted. The extended log's shutdown waits as long as its queue
needs, within a bound, and counts what it still could not write before it returns.

1. `Components/Pages/AdminEventLog.razor`:
   - The trace catch adds "<file name>: <reason>" to the list the extended read's `Unreadable` fills. That is the aud-a46
     typed shape, kept inline for the pins in evidence 1. Its log line stays. The lines read before the failure stay as
     well: they are whole lines.
   - The warning covers both streams: "Some of the diagnostic logs could not be read, so diagnostic entries may be missing:
     ...".
   - Three new counters, `unparsedAuditLines`, `unparsedTraceLines` and `unparsedExtendedLines`, are reset at the top of
     `LoadEvents` with the other fields.
     - Each parser's outer catch increments its stream's counter before it returns null.
     - Only the catch does: a filtered line is not a drop.
   - One muted line above the table, beside the other notices, so the empty-result branch shows it too: "N lines in the
     audit log, M lines in the trace log and K lines in the extended diagnostic log could not be parsed and are not shown."
     - It names only the streams above zero.
     - It says "line" for one, and "is" when the total is one.
   - NotYetConverted in `ClickGateRegistry`, so no line key moves.
2. `Services/ExtendedLogService.cs` `Dispose`:
   - After closing the queue, it waits for the consumer at most `max(DisposeFloor, queued x DisposeAllowancePerEntry)`.
     - The floor is 5 s, today's wait.
     - The allowance is 25 ms per entry still queued when `Dispose` begins, about twice the measured append. A full queue
       gets 25.6 s.
   - If the consumer has not finished by then, the entries still queued are taken out, counted in `DroppedEntryCount`, and
     reported once through `ReportSafely`, all before `Dispose` returns.
   - The entry the consumer is writing at that moment stays its own: written, or counted by its catch.
   - The queue's `SingleReader` becomes false, since `Dispose` now reads from it too.
   - New test seams `internal TimeSpan DisposeFloor` and `internal TimeSpan DisposeAllowancePerEntry`.
3. `README.md`:
   - The Admin Event Log section: unparseable lines are counted per log.
   - The extended diagnostics paragraph: an unreadable trace file is named too, and shutdown waits in proportion to the
     queue and counts what it could not write.
4. Comments after the mutation round, checked code-identical.

Tests:
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`, driving the page's own `LoadEvents` and render tree as aud-a46's
  do:
  - `ATraceFileThatCannotBeRead_IsShownAsUnreadable_NotAsNoDiagnostics`.
  - `LinesThatCannotBeParsed_AreCountedPerLog_AndShownInOneMutedLine`:
    - With diagnostics on, a cut line in each of the three logs: all three are named.
    - With diagnostics off: the audit log only.
    - With every line whole: no such line.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`:
  - `Dispose_GivesTheQueueTimeInProportionToItsLength`. The consumer is held until 100 entries are queued, then writes
    each in about 5 ms, with a 100 ms floor and a 100 ms allowance. All 101 entries are written and none is counted.
  - `Dispose_CountsWhatItCouldNotWriteInItsBound_BeforeItReturns`.
    - The consumer is held inside its first write while 50 entries wait, with a zero floor and a 1 ms allowance.
    - When `Dispose` returns, 50 are counted and one report is made.
    - After release, the held entry is written; written plus counted is 51.

Mutations at the call site (md5 before and after, the named failing test, restored, touched, rebuilt, md5 matched, green):
- M1 the trace catch back to log-only.
- M2 one parser's count dropped (`ParseTraceLine`'s increment).
- M3 the fixed 5 s wait again (`Dispose`'s old body).
- Extra E1 the bound without its per-entry term (the floor only).

`ProgressRegistryTests` and `ClickGate*` are run alone as well.

Files:
- `Components/Pages/AdminEventLog.razor`
- `Services/ExtendedLogService.cs`
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs`
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`
- `README.md`
- this record

## Fix

- `Components/Pages/AdminEventLog.razor` (1,645 lines -> 1,673; NotYetConverted in `ClickGateRegistry`, so no key moves):
  - `LoadEvents`' trace catch adds "<file name>: <exception message>" to `unreadableDiagnostics` before its log line.
  - The warning reads "Some of the diagnostic logs could not be read, so diagnostic entries may be missing: ...".
  - New fields `unparsedAuditLines`, `unparsedTraceLines` and `unparsedExtendedLines`, reset at the top of `LoadEvents`.
    Each parser's outer catch increments its own before `return null`.
  - New `UnparsedLinesText`, from new static `UnparsedLinesNotice(audit, trace, extended)`. It is null when all three
    are zero, and otherwise names the logs above zero: "N line(s) in the audit log, ... and ... could not be parsed and
    is/are not shown."
  - The notice is rendered as `text-muted small` after the dropped-entry line, above the table.
  - The pins hold: `ReadLinesShared(` twice in `LoadEvents`, `diagnostics.Enqueue(entry);` twice, `buffer.Enqueue(entry);`
    once.
- `Services/ExtendedLogService.cs`:
  - `Dispose` closes the queue, reads its `Count`, and waits `max(DisposeFloor, DisposeAllowancePerEntry x Count)`.
  - If the consumer has not finished, it takes every entry still queued (`TryRead`) and adds them to `_droppedEntries`.
    If there were any, it reports once through `ReportSafely`: "The extended log was disposed with {Count} entries it had
    not written within {Milliseconds} ms; they are dropped and counted".
  - A faulted consumer is reported as before, and the queue is then drained and counted the same way.
  - New `internal DisposeFloor` (5 s) and `internal DisposeAllowancePerEntry` (25 ms).
  - The queue's `SingleReader` is false.
- `README.md`:
  - The Admin Event Log section says unparseable lines are counted per log.
  - The extended diagnostics paragraph says an unreadable trace file is named too, and that shutdown waits in proportion
    to the queue and counts what it could not write.

Tests:
- `ExchangeAdminWeb.Tests/EventLogDiagnosticsTests.cs` +2:
  - `ATraceFileThatCannotBeRead_IsShownAsUnreadable_NotAsNoDiagnostics`
  - `LinesThatCannotBeParsed_AreCountedPerLog_AndShownInOneMutedLine`, in four stages:
    - every line whole: no notice;
    - one cut audit line, diagnostics off: "1 line in the audit log could not be parsed and is not shown.";
    - two audit, one trace and one extended, diagnostics on: all three named, "are";
    - the same, diagnostics off: the audit log only.
  - Helpers `AuditRecord`, `TraceRecord`, `AppendCutLine`, `AuditFile` and `TraceFile`.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs` +2:
  - `Dispose_GivesTheQueueTimeInProportionToItsLength`: 101 written, none counted.
  - `Dispose_CountsWhatItCouldNotWriteInItsBound_BeforeItReturns`: 50 counted and one warning before `Dispose` returned;
    then the held entry written, and the count still 50.

Caller-visible changes, by design:
- With **Show diagnostics** on, a trace file the page cannot read is named in the warning with the extended ones. The
  warning now says "the diagnostic logs".
- A line the page cannot parse is still not shown, and the page now says so: one muted line naming each log with such
  lines and how many. The audit log is counted with diagnostics off too, since it is always read.
- `ExtendedLogService.Dispose` waits up to `max(5 s, 25 ms x the entries queued)`, 25.6 s for a full queue, instead of a
  fixed 5 s. What it still could not write is counted in `DroppedEntryCount` and reported once, before it returns.

## Guard proof

Reproduction: each failure below is the defect as the record states it.
- M1 is the trace catch as at `132d6e97`, log-only: the page says nothing of a trace file it could not read.
- M2 drops one parser's count: that log's unparseable line is not counted, as none was before.
- M3 is `Dispose` as at `132d6e97`, the fixed 5 s wait: 0 counted where 50 entries were left unwritten.

Same driver as aud-a51's (session scratchpad `mutate/mutate.ps1`, spec `mutate/round-a49.json`, log
`mutate/a49-mutations.log`). The filter (511 tests) is aud-a46's - the extended log's, the event log's, the writer's and
the page registries' - plus the two source pins on `LoadEvents`:
`FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~EventLog|FullyQualifiedName~JsonlWriterLockTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditFirstMergeTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate`

Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
bytes and the filter: 511/511 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the trace catch back to log-only | `AdminEventLog.razor`, `FDA7E9CFCDE3845C999320A414F0AAB9` | `0A49FE3F82244FF2474F074FDD9978F2` | 1 of 511: `ATraceFileThatCannotBeRead_IsShownAsUnreadable_...` - "could not be read" not on the page |
| M2 one parser's count dropped (`ParseTraceLine`'s increment) | `AdminEventLog.razor`, `FDA7E9CFCDE3845C999320A414F0AAB9` | `2FBAEEB0ECD89C31EA819A8C28DFA589` | 1 of 511: `LinesThatCannotBeParsed_AreCountedPerLog_...` - the three-log line not on the page |
| M3 the fixed 5 s wait again (`Dispose`'s old body) | `ExtendedLogService.cs`, `0CC372ADB3FE3F15C31888D3BAE5E5B1` | `54D206218A45F9C4851A63636F1B9432` | 1 of 511: `Dispose_CountsWhatItCouldNotWriteInItsBound_...` - 0 counted where 50 |
| extra E1 the bound without its per-entry term (the floor only) | `ExtendedLogService.cs`, `0CC372ADB3FE3F15C31888D3BAE5E5B1` | `C3B14CB727EA16F70F764FF028247711` | 1 of 511: `Dispose_GivesTheQueueTimeInProportionToItsLength` - 96 counted where 0 |

Unchanged through the round:
- `EventLogDiagnosticsTests.cs` `C993E9D52A5FE26F9E19039C5483436C`
- `ExtendedLogServiceTests.cs` `6ABDE3BB60C67814F03730864D233D00`

`ProgressRegistryTests` and `ClickGate*` are in the filter, green under every mutant and on the restored bytes. They are
run alone again at the branch tip. The code comments are written after all three findings' rounds, in their own commit, so
the MD5s above are this commit's bytes.

Not run, by design: the page in a browser. The notices are read from the compiled page's render tree, with `LoadEvents`
driven by reflection, as aud-a46's are. An IIS recycle with a full queue was not run either; the bound and the count are
driven through the seams.

## Known gaps

- The unparsed count is the page's own, per load. It counts lines in the files it read: for the extended log, only the
  newest 2,000 lines `GetEntries` hands it, and the trace and extended logs only with diagnostics on.
- A line counted as unparseable is still not shown. The page says how many, not which. The file holds it, and it is
  readable there.
- `Dispose`'s bound can now reach 25.6 s, for a full queue. A host that ends the process sooner - the IIS module's own
  shutdown limit, for one - still loses what is left, uncounted and unreported, as before, but only past that point. The
  count lives in memory either way; the report is the one trace that outlives the process.
- The trace catch's log line, like the audit catch's, still calls the page's logger unguarded (aud-a28q's class, in the
  page). It is listed as a candidate in the hand-back.

## Comments after the round

Written after all three findings' rounds, in their own commit (`c2d6215d`), and checked as aud-a51's record says. This
record's files:
- `Components/Pages/AdminEventLog.razor` `FDA7E9CFCDE3845C999320A414F0AAB9` -> `3F8DB6623CDA20078CADEF46260DFC05`: 1,289 code
  lines, their MD5 `2C0E7A110340D0E6B68A11C839EDA875` on both sides. 7 lines were added, for:
  - the notice;
  - the counters and what they count;
  - the trace catch;
  - the notice's wording.
  - One more was extended: the post-loop comment now says the warning names the trace files too.
  - The page grows from 1,673 lines to 1,680. It is NotYetConverted, so no key moves.
- `Services/ExtendedLogService.cs` `0CC372ADB3FE3F15C31888D3BAE5E5B1` -> `EB73D9F300CEF8E461637AEEE967DCA6`: 310 code lines,
  their MD5 `8E22FA0363ACD1407A02EC8395282870` on both sides. 10 lines were added:
  - why the queue has a second reader;
  - why `Dispose` waits in proportion and counts what is left;
  - the two seams.

## Branch-tip gates

See aud-a51's record for the branch's gates. `ClickGate*` and `ProgressRegistryTests` were run alone at the tip, as that
record says.

## Closeout

Fixed on branch `worktree-agent-ac1038e1d7ece4ae5`, not merged, not pushed. Two layers changed, so two bumps are due:
- Shared infrastructure, the extended log's `Dispose`: the base app version.
- The Admin Event Log's behaviour - an unreadable trace file named, and the unparsed lines counted: its module version.

Both bumps are the supervisor's, at merge: `Modules/ModuleCatalog.cs` and the csproj version elements were outside this
brief. No file another agent owns was needed, so nothing is left for a follow-up under that heading.

## Codex review

Pending.
