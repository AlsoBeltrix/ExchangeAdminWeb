# aud-a20: diagnostics evicted audit records from the event log

**Severity**: HIGH - audit retention. Turning diagnostics on silently removed audit records
from what an administrator sees, and the page reported the truncation as ordinary.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 20 of the 2026-10-16 audit, verified against the code. The plan marks it
"**Audit retention - Constitution-shaped**", and that is the right framing.

`Components/Pages/AdminEventLog.razor` filled **one** `Queue<MergedLogEntry>` from three
sources, in this order: audit records, then trace files, then extended diagnostic files. Each
source trimmed with the same two lines:

```csharp
buffer.Enqueue(entry);
if (buffer.Count > MaxTotalEvents)
    buffer.Dequeue();
```

`Queue.Dequeue` removes the oldest **enqueued** item, not the oldest by time. Audit went in
first, so once the 2000-entry cap was reached **every diagnostic line evicted an audit
record**, regardless of either one's timestamp.

The sort by timestamp happened afterwards, at the end, on whatever had survived - so the
page presented a clean reverse-chronological list with audit records missing from the middle
of it, and the warning said only "Showing last 2000 of N events in range."

**Diagnostics are off by default**, so this bites exactly when someone turns them on to
investigate something - the moment they most need the audit trail intact.

## Predicted observable failure

An administrator investigating an incident enables diagnostics and loads a busy day. Trace
and extended-log lines outnumber audit records heavily. The audit rows they came to read are
gone from the list, with nothing distinguishing that from "it did not happen".

## Approach

Audit records and diagnostics go into separate buffers. At merge time every audit record is
kept, and diagnostics fill only the room left over, newest first.

The rule is extracted to `Services/AuditFirstMerge.Merge` rather than left inline, for one
reason: the page's entry type is a private nested record, so an inline rule could only ever
be source-scanned. Generic over the key so a test can use plain tuples.

**Audit can still exceed the cap on its own**, and that is deliberate - it is a real
truncation and the existing warning covers it. What must not happen, and now cannot, is an
audit record being dropped to make room for a trace line.

## Files changed

- `Services/AuditFirstMerge.cs` (new)
- `Components/Pages/AdminEventLog.razor`
- `ExchangeAdminWeb.Tests/AuditFirstMergeTests.cs`

## Guard proof

Behaviour, five cases: diagnostics never evict audit under a tight cap; the diagnostics that
survive are the newest rather than the first read; output is newest-first; audit over the cap
is truncated **without** a diagnostic taking the freed place; and no diagnostics is just the
audit.

The fourth matters most. "Audit is never dropped" would be the wrong rule and easy to write
by accident; the right one is "audit is never dropped **for a diagnostic**", and the test
says which.

One source scan, because the page method is long and unharnessed: the page separates the two
sources and hands them to the merge in the right order - one `buffer.Enqueue`, two
`diagnostics.Enqueue`.

| Mutation | Result |
| --- | --- |
| merge everything and `Take(cap)` by timestamp | FAIL - `DiagnosticsNeverEvictAnAuditRecord` and `AuditAloneCanStillExceedTheCap_...` |

That mutation is the plausible wrong fix - "just sort by time first" - and it still loses
audit records to newer diagnostics. Source restored byte-identical (md5 verified), touched,
rebuilt.

Gate: Release 0 errors; suite 4374 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. `AdminEventLog` 1.2.4 -> 1.3.0.

## Known gaps

- The page's own wiring is a scan; nothing drives `LoadEvents` end to end.
- The 2000 cap itself is unchanged. A day with more than 2000 audit records still truncates,
  and the warning does not distinguish "audit truncated" from "diagnostics truncated" - an
  administrator cannot tell from the message which they lost.
- Nothing here changes what is written to disk; this is about what the page shows.

## Closeout

Fixed directly on `master`. Independent review pending.

## Independent review, 2026-10-20 - LOW finding, fixed

**Fallback harness** (`gpt-5.5` via Portkey) - see `aud-a21-a22` for the caveat. The separate
buffers were confirmed to work as claimed.

**The truncation warning no longer described what the page did.** It still said "Showing last
2000 of N events in range", which was true of the single FIFO and is not true of "every audit
record that fit, plus the newest diagnostics after them". An operator deciding whether they
are looking at a complete picture could not tell which kind had gone missing.

The warning now names it: audit dropped at the limit, or diagnostics that did not fit. That
needed per-source counters, because `totalParsed` covers all three and the audit queue
self-caps, so neither could reveal how much audit had been discarded.

This record's own phrase "audit is kept in full" was also too strong and the reviewer said
so: audit is never dropped **for a diagnostic**, but it is still capped at 2000 on its own.
The Approach section already said that; the summary line did not.

Guards: `FitDiagnostics_ReportsExactlyWhatFitsAfterTheAudit`,
`FitDiagnostics_KeepsNothingWhenAuditAlreadyFillsTheCap`, and a scan that the old wording is
gone.
