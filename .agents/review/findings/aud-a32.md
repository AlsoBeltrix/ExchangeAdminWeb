# aud-a32: a cancelled export is emailed as ready and then refused on the page

**Severity**: MEDIUM - the operator is told their export is waiting, follows the link, and is
told it was never produced. Nothing is lost; the contradiction is the defect.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 32 of the 2026-10-16 audit, verified against the code. Two halves that disagree.

`Services/Jobs/BulkJobService.FinishAndNotify` fires the completion hook on **every** terminal
transition, not only `Completed` - it persists the terminal state, then calls
`NotifyCompletedInScope` for whatever that state was.

So for a cancelled message-trace job, `MessageTraceDetailJobProcessor.OnJobCompletedAsync`
runs in full: it builds the CSV - deliberately including the rows the job never reached,
marked "Not processed (job did not reach this message)" so the export reflects the whole
selection - saves it, and sends `SendMessageTraceResultAsync`, the "your export is ready"
mail with a download link.

`Services/MessageTraceExportListing.ClassifyState` then said:

```csharp
if (job.Status != BulkJobStatus.Completed)
    return MessageTraceExportState.NotProduced;
```

The file exists, the operator has been told it exists, and the page refuses it as never
produced.

## Predicted observable failure

An operator cancels a long trace, having got what they needed from the first few hundred
rows. They receive a mail saying the export is ready. They open the exports page and it says
the export was never produced. The file is on disk the whole time.

## Approach

`Cancelled` joins `Completed` as a status that can have produced an export, and the existing
`SaveFailed` / `TryResolve` logic then decides Failed, Available or Expired as before.

**`Interrupted` is deliberately NOT included, and the asymmetry is the point.** Two of its
three sources do go through `FinishAndNotify` - a missing processor, and a throw out of the
runner - but the startup orphan sweep calls `InterruptAllNonTerminal` directly on the
repository, with no hook. Treating it as produced would report "Expired" for a file that
never existed, which is precisely the collapse this method's own remarks warn against
(openreview F1: a disk-full fault must not masquerade as ordinary retention).

That leaves the two notified Interrupted paths understating - a produced export shown as never
produced. Same class, smaller, and recorded below rather than fixed by guessing.

## Files changed

- `Services/MessageTraceExportListing.cs`
- `ExchangeAdminWeb.Tests/MessageTraceExportListingTests.cs`

## Guard proof

`ACancelledJobsPartialExport_IsServed_NotDeniedAsNeverProduced` seeds a cancelled job, writes
the export file the processor would have written, and asserts the listing serves it.

**An existing test asserted the defect** and had to change:
`JobThatNeverCompleted_IsNotProduced` covered `Cancelled` and `Interrupted` together. It is
now `JobThatNeverProducedAnExport_IsNotProduced` and covers `Interrupted` alone, with a
comment saying why that one stays. Queued and Running were tried and removed - those jobs
never reach the listing at all, so including them tested nothing.

| Mutation | Result |
| --- | --- |
| restore `!= BulkJobStatus.Completed` | FAIL - `ACancelledJobsPartialExport_...`, and only it |

Source restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4343 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. `MessageTrace` 1.5.7 -> 1.5.8.

## Known gaps

- **The two notified `Interrupted` paths still understate.** Distinguishing them from the
  startup sweep needs the job record to carry whether the completion hook ran, which is a
  change to the job model rather than to this classifier.
- The "ready" mail does not say the export is partial when the job was cancelled. The file
  itself marks every unreached row, so the information is in the download - but the mail
  reads the same for a complete and a cancelled export. Not fixed here.
- Nothing ran against a real cancelled job end to end; the test writes the file the processor
  would have written rather than letting the processor write it.

## Closeout

Fixed directly on `master`. Independent review pending.
