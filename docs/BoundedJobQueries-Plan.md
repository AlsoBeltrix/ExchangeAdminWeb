# Bounded Job Queries - Plan

Status: **DRAFT - awaiting owner go.** Nothing below is authorised yet.

One of three plans addressing production memory growth. The others are
`docs/CircuitLifetimeLeak-Plan.md` and `docs/DownloadMemoryRetention-Plan.md`.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** One database query fetches every row of a bulk job with no limit, and the
result is kept for as long as the operator's browser tab stays open. A ten-thousand-row job
means ten thousand rows held in memory per viewer. This bounds it, and adds a check so the
next query written here cannot forget.

**What it gets you.** On its own, modest - this is bounded by how many tabs are open, so it
does not run away. Its real value is as a multiplier: it is how one leaked session from the
other plan turns into hundreds of megabytes instead of a few. Fixing the leak without this
leaves the leak cheaper but still expensive.

**What it costs.** The smallest of the three by a wide margin. One query, the paging the
pages need to use it, and a test.

**Biggest risk.** Paging a list that currently arrives whole changes what the operator sees.
If someone is scrolling a long job looking for one failed row, a naive page size makes that
worse, not better. The fix has to keep "find the failures" easy, which probably means
filtering by status rather than only paging.

**One thing worth knowing.** Every other query in this same file already takes a limit - this
one is the only exception. That makes it an oversight rather than a design choice, which is
why this plan is short and why it is worth doing even if it is not the main memory story.

**What approving authorises:** the query bound, the page changes to use it, and the test. It
does NOT authorise redesigning the bulk-job detail view.

---

## The defect

`Services/Jobs/BulkJobRepository.cs:484-490`:

```csharp
public IReadOnlyList<BulkJobRow> GetRows(string jobId)
{
    ...
    "SELECT job_id, row_index, target, status, message, recorded_at " +
    "FROM bulk_job_row WHERE job_id = $job ORDER BY row_index ASC;"
```

No `LIMIT`. Verified. **Every sibling query in the same file is bounded** - `:358`, `:380`,
`:409`, `:475` all take `$limit`. This one is the exception, which is what marks it as an
omission.

**The result is held in page fields, not locals**, so it lives as long as the circuit:

- `Components/Pages/AdminBulkJobs.razor:147` - `detailsRows`
- `Components/Pages/ConferenceRooms.razor:709`, `:710`, `:791`

**Three callers, and one is worse than the others.** `AdminBulkJobs.ToggleDetails:208` and
`ConferenceRooms.ToggleJobDetails:794` are operator-initiated. `AdminBulkJobs.RefreshJobs:177`
re-runs it on **every refresh while a row is open**, so a large job is re-read and re-held
repeatedly rather than once.

**Interaction with the leak.** A `ConferenceRooms` circuit leaked by
`docs/CircuitLifetimeLeak-Plan.md`'s defect holds its `detailsRows` forever. The two plans
multiply; neither alone describes the cost.

## Scope

**S1 - bound the query.** `GetRows` takes a limit, following the shape of its four bounded
siblings rather than inventing one.

**S2 - the pages page it.** Both callers use the bounded form. `RefreshJobs` must not re-read
the full set on a timer.

**S3 - keep "find the failure" easy.** The reason an operator opens job details on a large
job is almost always to find what failed. Paging alone makes that harder. A status filter at
the query level - failures first, or failures only - is in scope because it is what makes the
bound acceptable rather than merely smaller. Exact shape is an implementation decision; the
requirement is that finding failed rows in a 10,000-row job does not get worse.

**S4 - the guard.** A test asserting every row-returning query in `BulkJobRepository` is
bounded. Written against the rule so the next unbounded query fails, not against this one
method.

## Out of scope

- Redesigning the bulk-job detail view.
- The other two plans.
- `MessageTraceDetailJobProcessor._fetched` (`Services/Jobs/MessageTraceDetailJobProcessor.cs:50`),
  an uncapped `ConcurrentDictionary` holding every fetched detail for a job's run. It is
  scoped to the job (`Program.cs:259`) and released when the job ends, so it grows under load
  but does not leak. Named so it is a decision, not an oversight.

## A survey correction this plan records

`MessageTrace.ExportCsv` was flagged as an unbounded in-memory CSV in the progress survey and
in `docs/ProgressCoverage-Plan.md`. **That is wrong.** It exports `response.Results`, capped
at `MessageTraceResponse.MaxResults = 1000` (`Models/LookupModels.cs:93`), enforced in five
places in `Services/MessageTraceService.cs` (`:53`, `:238`, `:481`, `:612`, `:682`). The
`ProgressRegistry` entry's defect text should be corrected when that operation is next
touched; it is a progress gap, not a memory one.

## Verification

- Standard gates: build, full suite, format, `git diff --check`.
- `ConferenceRooms.razor` is ClickGate line-pinned (`ExpectedLineCount = 1576`) - re-anchor
  per `.agents/playbooks/clickgate-reanchor.md` if line counts move.
- Module version bumps for the affected modules.
- Non-vacuous proof: remove the limit, confirm the S4 guard names `GetRows`; restore, `touch`.
- Codex review per slice.

## Manual acceptance

Open a job with several thousand rows. Confirm the page loads promptly, paging works, and a
failed row is still findable without scrolling the whole set - that last one is the
acceptance criterion that matters, because it is the thing paging could break.

## Owner gate

Go or no-go. One question inside it: **is failures-only an acceptable default** for the
detail view of a large job, or must the full row list stay the default view?
