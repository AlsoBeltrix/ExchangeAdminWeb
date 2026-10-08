# prog-9: The progress yield opened a null window on MessageTrace's CSV export

**Severity**: MEDIUM - introduced by this work stream, not pre-existing
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## Evidence

`Components/Pages/MessageTrace.razor:959` checks `response.Results`; the progress code added
in `2c6625e` awaits `Task.Yield()` at `:966`; the CSV loop dereferences `response.Results`
again at `:971`.

`ExportCsv` does not set `isLoading`, and the Run Trace button is disabled only by
`isLoading` (`:297`). `RunTrace` clears `response` at `:787` before awaiting the backend
search.

Triggering condition: an operator starts Export CSV and a trace action is processed while
the export is yielded for the progress repaint.

Predicted observable failure: `RunTrace` sets `response` to null before the export resumes,
and the export throws on the re-read.

**This is a defect the progress work created.** Before `2c6625e` the handler was synchronous
end to end - there was no suspension point between the guard and the loop, so no window.
Adding the yield is correct and necessary (a synchronous handler cannot paint), but it turns
a guarded read into an unguarded one.

## The same class, found independently and already fixed elsewhere

The agent on S5 batch 2 found this exact hazard on its own pages and closed it before
committing: `BitLockerRecovery.SearchAsync` clears `results` and rewrites `searchTicket` on
its first synchronous lines, so its export could have handed over an empty file under the
wrong ticket. Both it and `BlockedSenders` now snapshot before the yield.

It also characterised when the hazard does NOT arise: on pages where the handler's busy flag
is part of `IsBusy` and is raised ABOVE the yield, the page is shut for the whole window.
Migration and the three converted pages need no snapshot for that reason.

`MessageTrace.ExportCsv` is the one instance that predates that insight and did not get it.

## Approach

Pending. Snapshot `response.Results` into a local before the yield, matching
`BitLockerRecovery`/`BlockedSenders`. Then probe it: null `response` across the yield and
confirm the export still writes the snapshot rather than throwing.

## Known gaps

**Worth a sweep, not a one-off.** Every handler this work stream made `async` with a yield
needs the same question asked: is anything it re-reads after the yield mutable by another
handler that is not gated while it runs? The batch-2 agent asked it on its own pages; nobody
asked it retroactively on batch 1's.

## Reviewer comments

Raised by codex reviewing `6a561df..c15680a` (S5 batch 1), which otherwise checked out: the
`UsageTracker` test was re-anchored and not weakened, `ExecuteUndo` correctly completes
before its self-reporting refresh, `LoadEvents`' first `Complete` does sit below all three
named calls, `CsvEscape` is within the operation-scale rule, and the `Unknown` sizing choices
match a live-only progress UI.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard.
No build run; no-build filtered tests passed 38 and 66; `git diff --check` clean.
Raw output: `.agents/review/prog-s5a.result.json`.

## Closeout

Pending.
