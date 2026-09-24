# Migration -- Redesign The Status Interface (queue item 14)

Status: **Draft, awaiting owner approval.** No code written. The layout was settled by the
owner over many rounds against a working mockup (`.agents/mockups/migration-v3.html`), and four
codex openreviews have run against it. **Every question the reviews raised is closed and no
question is outstanding.** What remains is the owner approving the plan itself.

## Why a plan at all

`docs/ProjectConstitution.md` "Planning Rules" requires one for **destructive or
high-blast-radius workflows**. This restructures the surface that carries Complete, Stop,
Resume and Delete for whole batches and for individual mailboxes, and `Migration` is a
converted page in `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`, so the gating contract
moves with it. That is the trigger, not the visual work.

## The requirement

`queue.txt` item 14, verbatim:

> 14. Precedes 12 & 13. Design a better, safer, and easier to use interface for managing
> migrations. multi-pane or tabs or something that makes it less dense and easier to use
> without UI-induced error. no burying controls off the screen, no hiding selected items
> off the page.

**Scope.** Items 12 and 13 were always in scope; an earlier draft of this plan said otherwise,
which was my error and not an owner ruling (owner, 2026-09-24). Mailbox checkboxes (R7),
per-mailbox bulk actions (R12), Schedule as a first-class control (R13) and Export Reports
(R31) are all here. Item 14 "preceding" 12 and 13 means the layout lands first, in S1-S6,
before their behaviour is wired in S7 and after.

The `CompleteAfter` service semantics item 12 needs (trap 1 below) are S8, and they are in:
nothing ships until all three items are done, so Schedule lands working rather than disabled.

## What is wrong with the page today

`Components/Pages/Migration.razor`, 2025 lines, Migration Status tab:

1. One wide batch table. Clicking a batch injects a **second, nested user table inside the
   row** (`:654-:790`), pushing everything below it down the page.
2. The bulk action bar (`:460-:487`) sits above the table, so it scrolls out of sight while
   the operator ticks rows below it.
3. The per-row ticket confirmation (`:646-:653`, `:769-:777`) renders at the row's own
   position, which may be off screen.
4. The user table has no sort and no filter; the batch table has sort only.
5. Nothing is addressable. Which batch is expanded lives in `expandedBatch`, so browser
   Back, refresh and a circuit reconnect all lose it.

## Requirements

Every line below is an owner ruling from the 2026-09-24 design session, most of them made by
rejecting something I had built. They are requirements, not preferences.

**The mockup `.agents/mockups/migration-v3.html` is the reference for layout and interaction
only, and this list wins wherever they differ.** Two places they do: the mockup routes on
`#/batch/<array index>`, while R22 and S1 require a query parameter keyed by batch **name** --
an array index is not an identity and would point at a different batch after any refresh that
reorders the list. And the mockup was built while ticking and opening were separate acts, so
older parts of it may still behave that way; R9 governs.

### Structure

R1. Two panes. Batch list on the left, detail on the right. The pane structure itself was
accepted early and is not in question.

R2. **The left pane is about batches. The right pane is about the selection.** With one
batch ticked the right pane is **entirely and only** about that batch's mailboxes -- no
batch-scoped control appears in it at all. With more than one ticked it lists exactly the
ticked batches.

R3. **Batch operations live in the batch pane**, whether one batch is ticked or eight
hundred. They are never in the right pane.

R4. **The multi-batch view has its own pager**, labelled so it is unmistakable that it pages
the selection ("1-40 of 847 selected batches"), not the catalogue.

R5. **Nothing is pinned into the batch list.** No ticked-rows section at the top, no dividers.
The left list is a plain paged list. The selection is visible because it has its own pane, not
because rows are held back from the filter.

R5a. **Ticked mailboxes ARE pinned, above an "OTHER MAILBOXES" divider** (owner ruling
2026-09-24). R5 applies to the batch list only, where the right pane shows the selection
instead. Mailboxes have no equivalent second surface, so pinning is how R11 is met for them.

R5b. **The pinned block is itself paged, or R5a, R8 and R20 cannot all hold.**
Select-all has no cap (R8), every ticked mailbox is pinned (R5a), and no view may render the
full set (R20) -- so ticking all 2000 mailboxes would pin 2000 rows and reproduce the Defender
failure exactly. The mockup does this today: it concatenates every ticked mailbox onto the
current page. The resolution keeps all three rules: the pinned block has its own pager
("1-40 of 2000 ticked"), the way the batch selection got one in R4, so the selection always has
a visible home of bounded size. **Owner ruling 2026-09-24: adopted as written.** The alternative -- capping select-all for
mailboxes -- is already refused by R8.

R6. **Actions sit at the top of the thing they act on. Never a footer.** That is what every
other list UI does and what operators expect.

### Selection

R7. Checkboxes on batches **and** on mailboxes.

R8. **Select-all means all. No cap.** "Select up to the cap" is not select-all.

R9. **There is one concept: selection.** Highlighted, ticked, shown in the right pane and
acted on are always the same set. Clicking a row selects only that row; the checkbox adds or
removes. There is no separate "open" state. Owner ruling 2026-09-24, replacing an earlier
design where ticking and opening were different acts -- "tick vs select vs left vs right is
confusing and annoying". From the multi-batch list, **Open** on a row narrows the selection to
that batch and the pane becomes its mailboxes.

R10. "Untick" is only said where checkboxes exist. The right pane says "Clear selection" and
"Remove from selection".

R11. No control that acts on a selection may be off screen while that selection exists, and
no selected item may be hidden by a filter or a page change.

### Actions

R12. **Both action bars are identical** in look and behaviour: outline buttons, the picked
one highlighted, a per-row outcome written against every affected row, a ticket required, and
Confirm carrying the eligible count. Ineligible rows are listed as skipped and nothing is
sent for them.

R13. Scheduled completion (item 12) is reachable without hunting, and per-mailbox actions
(item 13) have a home. Neither is buried inside a single dialog on one status.

### Legibility

R14. **One line per batch row.** Fixed columns so counts and badges align on every row.

R15. **Nothing clipped** -- not the counts, and not the trailing identifier in a batch name.
Columns are sized for real magnitudes: four-digit counts, three-digit failure counts.

R16. **A column header row** naming Batch / Synced / Failed / Status, rendered inside the
scrolling box so the scrollbar cannot shift it out of alignment with the rows.

R17. **No unlabelled values.** A bare coloured number with no header is not acceptable.

R18. **One fact in one place.** A batch's state is not restated as a badge and a sentence and
a bar and a summary strip. The page must be scannable at a glance.

R19. **Ambiguity is fixed by structure, not by labels.** Adding a caption to explain what a
control acts on is not a fix; putting it where its scope is obvious is.

### Scale and behaviour

R20. Both lists page. The page must stay usable at 2000 batches and 2000 mailboxes per batch,
which means no view may render the full set -- Blazor Server pushes a render diff per row over
the circuit and that is how the Defender page died.

R21. Filter and sort apply over the whole set, not the rendered page, on **both** lists. The
mailbox list has sorting today; it must not lose it.

R22. **Browser Back works**, and refresh and a circuit reconnect keep your place, because the
open batch is addressable rather than held in component state.

### Every control has somewhere to put its output

R23 is the rule the first draft of this list was missing, and it was caught by the owner
asking where the Report button displays. R1-R22 were written from design corrections, not
from an audit of the page's own controls, so a button with no destination survived. The
remaining rules come from auditing `Components/Pages/Migration.razor` for everything that
produces output.

R23. **A control that produces output has a defined place for it.** A button with nowhere to
render its result is a defect, not a detail to settle later.

R24. **The per-mailbox report is a modal dialog with an Export button.** Owner ruling
2026-09-24, after a drill-down, a panel under the table, a third pane and a new tab were each
rejected. It must not replace the mailbox table, be hidden by it, or hide it, and the drawer
under the row that production uses today (`:777-:790`) is out. The dialog carries the
mailbox's status and its row action in its header, so the operator can act on what the report
says. Export writes the whole report to a file rather than making anyone select tens of
thousands of lines out of a scroll box. Only the Close button dismisses it.

R24a. **A fetched report is kept, and re-fetching is a deliberate act.**
`Get-MigrationUserStatistics` can take **twenty minutes or more** on a long or problematic
migration, so closing and reopening the dialog must never run it again. The dialog shows the
copy already held and states when it was taken; "Fetch again" is its own button and says what
it costs. This is a correctness requirement about operator time, not a caching optimisation.

R24c. **Reports are stored on disk, not in the circuit, and there is exactly one copy that
every consumer reads.** Today `userReport` is a `string?` field on the component
(`Components/Pages/Migration.razor:878`) -- one report, held in the operator's circuit memory.
That is fine for one and wrong for many: reports run to several megabytes, so a cache of fifty
would pin hundreds of megabytes per operator for the life of the circuit. A report is a
point-in-time snapshot, so it is written once with its fetch time, and the modal and the export
zip both read those same bytes. Two artifacts labelled "the report" for one mailbox that do not
match is the failure this prevents (owner, 2026-09-24).

R24f. **The store's contract, stated before implementation.** `MessageTraceExportStore` is the
precedent and it is careful about exactly the things a migration report store can get wrong.
  - **Path.** Under the instance's own runtime directory, alongside the other per-instance
    runtime files, never in the publish folder and never promoted between dev and prod.
  - **Keying.** A report is identified by batch name plus mailbox address, both of which are
    operator-supplied text. Neither may reach the filesystem as written: the on-disk name is a
    hash or a sanitised token, the real identity lives inside the file or in an index, and any
    path that resolves outside the store directory is refused rather than corrected.
  - **Ownership and download.** A stored report is readable only through the module's own
    authorised route, subject to the same section-access check as the page that produced it.
    Downloads are audited as a read of migration data.
  - **Purge hooks.** Every read, write and export touches the sweep, and the application start
    path calls it once. There is no other trigger, because there is no scheduler.

R24d. **Retention: invalidation first, twelve hours as the outer bound, purged lazily.** There
is no scheduler in this app, so the store sweeps entries older than **12 hours** whenever it is
touched, and again at application start -- otherwise a store nobody opens keeps last week's
reports (owner ruling, 2026-09-24). Generation invalidation (R24b) still deletes immediately,
because a report whose batch was reloaded or recreated is misleading rather than merely old.
A failed delete during a sweep is logged and skipped and must not fail the read it interrupted:
**fail-soft on purging, fail-closed on serving.** Never render a report that cannot be shown to
be current.

R24e. **Dismissing the modal and invalidating the report are two different operations.**
Today they are one: the Close button calls `CloseUserReport()`
(`Components/Pages/Migration.razor:777`), which bumps `reportGeneration` and throws the text
away. Reusing that helper for Close would make R24a impossible -- every close would guarantee
a re-fetch. The implementation needs `DismissReportModal` (hides the dialog, touches nothing)
separate from `InvalidateStoredReports` (bumps the generation, deletes the stored copies),
called only by the events in R24b.

R24b. **The kept report is invalidated by the same events that close it today, or R24a
regresses a fixed bug.** Queue item 3 was exactly this: a report open while the batch was
removed elsewhere and recreated under the same name kept showing the previous migration's
report. The fix is `CloseUserReport()` and the `reportGeneration` counter
(`Components/Pages/Migration.razor:1830-1836`), called by every site that reloads or discards
`batchUsers`, with the generation bump also invalidating a fetch still in flight. The cache
must hang off the same generation: any refresh or replacement of the mailbox rows discards it.
A report kept across a batch being recreated is worse than a slow one.

R25. **Action results are reported per row, and never as a blanket banner.** Today one
`batchActionResult` alert (`:434-:443`) speaks for an operation over N batches. A loop over N
items must report per-item outcomes -- the same rule the outcome preview follows before the
action, applied after it. The result appears in the pane that owns the action.

R26. **Searching for a person is not the same control as filtering batches.** The page today
has `SearchUser` (`:400`), which finds a mailbox across all batches and highlights it
(`highlightedUser`, `:691`). The mockup's box only filters batch names. Both must exist, and
finding a person must take the operator to the batch that holds them.

R27. **Loading is shown in the pane that is loading**, not as a page-wide freeze. Today
`IsBusy` (`:903-:905`) is one flag over eight operations and disables everything.

R28. **Empty states are distinct and say which one applies**: no batches at all, no batch
matching the filter, no mailbox matching the filter. "Nothing matches" for all three hides
whether the filter or the data is the problem.

R29. **Export says what it exports.** `Download CSV` (`:417`) is ambiguous once a selection
exists: all batches, the selection, or the open batch's mailboxes. It must name its scope.

R30. **Refresh is per pane.** Refreshing the batch list and refreshing one batch's mailboxes
are different acts and both exist today (`:424`, `:628`); neither may silently do the other.

R31. **Export Reports pulls a report for every ticked mailbox and delivers a zip, one text
file per report.** It sits in the ticked-mailbox action bar beside Complete, Pause, Resume and
Remove. Owner request 2026-09-24; one combined file was explicitly rejected.

R31a. **It cannot run on the circuit.** Each report is a `Get-MigrationUserStatistics` that
can take twenty minutes or more, so fifty ticked mailboxes is potentially a whole day. It runs
as a background job through the existing bulk-job machinery
(`docs/BulkJobRunner-Plan.md`), not in a request handler, and the operator is told the
expected cost before it starts rather than watching a page that appears to do nothing.

R31b. **Per-mailbox outcomes, and a partial result is still delivered.** R25 applies: three of
six succeeding means a zip of those three and a named list of the three that failed, never a
blanket failure and never a silent gap. One file per report is what makes a partial result
unambiguous.

R31c. **It reuses the kept reports.** Anything already fetched under R24a is packaged from the
copy held, not pulled again.

R31d. **Read-only, so no ticket and no protected-principal gate.** Fetching a report changes
nothing. It is still audited as a read of migration data, and the zip is named for the batch
and the time it was taken.
  - **Permission: `MigrationCheck`, the module's main permission** -- the same one that already
    lets an operator read the batch list, and the same level today's single `Report` button
    runs at, since that button sits outside every `canManage` check
    (`Components/Pages/Migration.razor:460`, `:518`, `:541`). **Not `MigrationManage`**:
    reading diagnostics is not managing a batch, and requiring the mutating permission to read
    a report would be a quiet privilege escalation of the page's read surface.
  - **Explicitly exempt from R12's ticket flow.** It is the one control in the mailbox action
    bar that changes nothing, so it does not stage, does not take a ticket and does not show a
    per-row eligibility preview -- every mailbox is eligible to be read. That exemption is
    stated here precisely because it sits beside four mutating actions that all require one.

R31f. **Delivery: the zip is assembled on demand from the stored reports.** The bulk-job
runner persists per-row outcomes, not files, so the job's own record is the list of which
mailboxes were fetched and which failed. The completed job exposes a download that builds the
zip at click time from the R24c store. Consequences that must be handled rather than
discovered: a report that expired under R24d between the job finishing and the download being
clicked is **named as missing in the zip's manifest rather than silently omitted**; a download
with nothing left to package says so instead of delivering an empty archive; and the download
itself is audited as a read of migration data, separately from the fetches that produced it.

R31e. **The export writes nothing new: it zips the stored reports.** Each mailbox's report is
fetched into the R24c store as the job walks the selection, and the zip is assembled from those
same files. The zip is the transient artifact; the reports it contains are not deleted when it
is delivered, because the modal still serves them and a report re-fetched later would no longer
match the zip the operator already has (owner, 2026-09-24). Their lifetime is R24d's, not the
download's. `MessageTraceExportStore` and `Components/Pages/MessageTraceReports.razor` are the
precedent for how an artifact is written, served and audited in this repo; the bulk-job
framework persists row outcomes and not files, so the job cannot simply hand a file back.


## Slices

Each slice is its own commit with its own verification. S1 and S2 carry no behaviour change.

**Slices are commits, not releases.** Owner ruling 2026-09-24: nothing ships until items 12,
13 and 14 are all done. So no slice is deployed on its own, no intermediate state has to be
coherent for an operator, and no half-wired control ever reaches anyone. Slicing here buys
reviewability and bisectable history, not incremental delivery -- which also means a slice may
leave a control non-functional if the slice that wires it is still to come.

**S1 -- Addressable state.** Drive the open batch from a **query parameter on the existing
route** (`/migration?batch=<name>`), not from `expandedBatch`, and not from a new path
segment. A path segment is ruled out on evidence: `Components/Shared/ModuleVersion.razor:23`
and `Components/Layout/UsageTracker.razor:53` both resolve the current module through
`Catalog.GetByRoute(route)`, which matches the catalog's exact `Route = "migration"`
(`Modules/ModuleCatalog.cs:202`), so `/migration/batch/x` would stop resolving and would
silently break the version badge and usage telemetry. A query parameter also avoids
inventing encoding rules for operator-supplied batch names. Selection state moves to fields
that survive a re-render. No layout change. Module version bump only -- nothing shared
changes.

**S2 -- Split the panes, and page the batch list.** Replace the nested-row expansion with the
two-pane layout: batch list left, mailbox table right, each with its own scroll region, sticky
header and pinned footer. **This slice owns the batch catalogue paging** -- page state, page
size, the pager, and resetting to the first page whenever the filter or sort changes so the
operator is never left on page 9 of a 3-page result. R20 is otherwise only assigned for
mailboxes (S5) and for the selection pane (S3), which would leave the list this page opens on
unpaged. Same data, same actions, same gating, no new operations. Module version bump.

**S3 -- Selection model.** Checkboxes on both lists, select-all over the whole filter with no
cap, the batch operation bar at the top of the left pane, and the multi-batch selection view
with its own pager in the right pane. Remove the old bulk bar at `:460`. Module version bump.
Satisfies R2-R12.

**S4 -- Outcome preview.** Per-row eligibility annotation and the eligible count on Confirm,
replacing the current blanket staging. This is where Known Failure Class 2 (success
aggregation) is addressed in the UI: the operator sees per-row outcomes before committing,
and the result banner must report per-row outcomes after. Module version bump.

**S5 -- Filter, sort and paging on the mailbox table.** Currently absent entirely. Module
version bump. Satisfies R21.

**S6 -- Output destinations.** The mailbox report as a modal with Export and a kept copy
instead of an injected row, per-row action results replacing the single banner, person-search distinct from
batch filtering, per-pane loading and refresh, distinct empty states, and a named export
scope. Module version bump. Satisfies R23-R30.

**S7 -- Export Reports.** The ticked-mailbox bulk report export: a background job through the
existing bulk-job machinery, per-mailbox progress and outcomes, reuse of reports already held,
and a zip of one text file per report with a named list of any that failed. Module version
bump. Satisfies R31. This is the only slice that adds a new operation rather than relocating
an existing one, and it can ship after the rest.

**S8 -- Scheduled completion semantics.** `CompleteAfter` currently means
"complete now": `Services/MigrationService.cs:490` and `:698` pass a past timestamp and `:596`
reads the property as an auto-complete boolean. A real future schedule needs those three sites
to separate "complete immediately" from "complete at T", with the boolean reading replaced.
This is service behaviour on a mutating path, not layout, and it is the work that makes the
Schedule button in S3 real. Module version bump; tests must cover a past time, a future time
and no time at all.

## Traps this must not walk into

1. **`CompleteAfter` already exists and lies.** `Services/MigrationService.cs:490` passes it
   to `Set-MigrationBatch` and `:698` to `Set-MigrationUser`, both with a time in the
   **past** (`AddHours(-1)`, `UtcNow`) to mean "complete now", and `:596` reads
   `CompleteAfter != null` as an auto-complete boolean. Item 12 needs a **future** time, so
   "complete now" and "complete at T" stop being the same value and `:596`'s reading stops
   being safe. This plan does not implement item 12; it must not make that worse, and S2
   must not move those call sites.
2. **`ClickGateRegistry.Pages` lists Migration as converted.** Every control this redesign
   moves or adds has to satisfy that suite, and the suite reads source text -- the sweep
   already found that a CSS class literally named `disabled` on Migration's tab anchor was
   satisfying a gating assertion (`.agents/state.md`, queue 9). Re-run the harness against
   each slice and check the assertions still bite.
3. **Render volume.** Blazor Server pushes a render diff per row over the circuit; the
   Defender page died this way. Paging is what keeps this safe, so no slice may render the
   full set -- there is no exception, including a select-all of every batch (R20).
4. **Protected-principal gate, ticket, audit and notification** obligations are unchanged
   and apply to every action the new layout exposes, including per-mailbox actions.

## Open questions

**Q1 is closed, 2026-09-24. No cap.** The question only existed because ticked rows were
pinned into the left list, so a select-all had to render every one of them. The owner
rejected a cap outright -- "that's not Select ALL, it's Select up to the cap" -- and the
pinned design with it. With the selection living in its own paged pane, select-all renders
40 rows there and 40 in the left list whatever the selection size, so trap 3 does not fire
and nothing needs limiting.

**Q2 is closed, 2026-09-24, and was largely overtaken.** It asked whether losing "complete
from the expanded view" needed a transition. Under R9 a click both selects and shows the
batch, so reaching its actions is one click in the batch pane rather than a different act
from opening it. No transition is needed and nothing blocks on this.

**Q3 is closed, 2026-09-24: nothing ships until 12, 13 and 14 are all done** (owner). So S8 is
in and Schedule works; there was never a disabled-button option, because there is no release
in between for a disabled button to appear in. See "Slices are commits, not releases" below.

**Q4 is closed, 2026-09-24: yes, ticked mailboxes are pinned** (owner). R5a resolved.

## Tests each slice must bring

Named here so they are not decided while implementing. Every one of these must be shown to
bite: revert the behaviour, watch the test fail, restore it.

1. **Routing (S1).** The batch in the URL is keyed by name, not by a list index, and a reorder
   or refresh of the batch list does not change which batch the URL resolves to.
   `Catalog.GetByRoute("migration")` still resolves with the query parameter present, so the
   version badge and usage telemetry keep working.
2. **No full-list rendering (S2, S3, S5).** With a large batch set and a large mailbox set,
   the rendered row count stays at the page size -- including with every batch ticked and every
   mailbox ticked, which is the case R5b exists for and the one a naive pinned implementation
   fails. This is the Defender hazard and the only test that catches a regression of it.
3. **Selection identity (S3).** Selection survives paging and filtering, and is held by batch
   name so it cannot silently retarget. Ticking, highlighting and the right pane always agree
   -- the R9 single-concept rule, asserted rather than assumed.
4. **Skipped rows are not sent (S4).** An operation over a mixed selection issues calls only
   for the eligible rows, and the result names every row with its own outcome.
5. **Stale report invalidation (S6).** The kept report is discarded when the mailbox rows are
   reloaded or replaced, including a batch removed and recreated under the same name. This is
   a regression test for queue item 3, which R24a would otherwise undo.
6. **Export store (S7).** The zip contains one entry per succeeded mailbox and none for the
   failed ones, a partial result is still downloadable, and the download is audited.
7. **Retention (S6, S7).** A report older than 12 hours is gone after the store is next
   touched and after an application restart; a report whose batch was reloaded or recreated is
   gone immediately; a delete that fails mid-sweep does not fail the read it interrupted; and
   the bytes the modal renders are the same bytes the zip contained.

## Acceptance

Beyond the gates: with the app running, no control that acts on a selection may be off
screen while that selection exists, and no ticked row may be hidden by a filter or a page
change. Those two are the item-14 clauses and are checked by hand on dev.
