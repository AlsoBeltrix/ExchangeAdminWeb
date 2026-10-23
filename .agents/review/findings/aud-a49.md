# aud-a49: Admin Event Log - an unreadable trace file is not shown, unparseable lines are dropped without a count, and the extended log's Dispose can leave a full queue unwritten and uncounted

**Severity**: LOW - diagnostics and audit visibility. (1) `AdminEventLog.razor` `LoadEvents`' trace-file
catch only logs, so with diagnostics on the page looks complete when a trace file could not be read (the
aud-a46 part-2 defect, for the trace stream). (2) `ParseAuditLine` and its siblings drop an unparseable line
silently with no count; the cut line a dead writer leaves (which the aud-a28z tail repair keeps) disappears
without a trace in the UI. (3) `ExtendedLogService.Dispose` waits 5 s for the consumer; at about 10 ms per
append a full 1,024-entry queue needs about 11 s, so shutdown can leave hundreds of entries unwritten and not
counted in `DroppedEntryCount`.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-ac1038e1d7ece4ae5` (from `132d6e97`)
**Commit**: -
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

## Guard proof

Pending.
