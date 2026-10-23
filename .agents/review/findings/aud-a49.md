# aud-a49: Admin Event Log - an unreadable trace file is not shown, unparseable lines are dropped without a count, and the extended log's Dispose can leave a full queue unwritten and uncounted

**Severity**: LOW - diagnostics and audit visibility. (1) `AdminEventLog.razor` `LoadEvents`' trace-file
catch only logs, so with diagnostics on the page looks complete when a trace file could not be read (the
aud-a46 part-2 defect, for the trace stream). (2) `ParseAuditLine` and its siblings drop an unparseable line
silently with no count; the cut line a dead writer leaves (which the aud-a28z tail repair keeps) disappears
without a trace in the UI. (3) `ExtendedLogService.Dispose` waits 5 s for the consumer; at about 10 ms per
append a full 1,024-entry queue needs about 11 s, so shutdown can leave hundreds of entries unwritten and not
counted in `DroppedEntryCount`.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
