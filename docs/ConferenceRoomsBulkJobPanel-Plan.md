# Conference Rooms Bulk Jobs Panel Plan

Status: **Implemented 2026-08-04** (all six slices; see Progress). No open owner gates -- D1 was
withdrawn and replaced by the owner's rulings below. **The 10 manual checks have NOT been run:**
they need a dev deploy, and the panel is markup, so they are the only evidence it renders.

Reported 2026-08-04: the Conference Rooms page shows a permanent "Bulk Jobs" row with no date, no
way to remove it, and no way to see what it was.

## Owner rulings (2026-08-04)

- **D1 SUPERSEDED. Jobs move OFF the main UI into their own tab.** Owner: *"move the jobs to
  another tab. out of the main UI, because it's going to push the actual module down further and
  further."* This is a better fix than any of D1's three retirement rules: the original complaint
  was a permanent fixture consuming the top of the page, and a panel that is not on the working
  surface cannot push the module down however many jobs it holds. The dismiss-hides-it-for-
  everyone objection that made D1 hard to rule disappears with it.
- **D2. A per-job Remove button**, in addition to the tab move.
- **D3. Retention stays at 30 days.** The owner first asked for 90; that was raised as a conflict
  rather than implemented, because `PruneFinishedBefore` DELETES terminal rows at
  `BulkJobs:RetentionDays` (default 30) and the store is shared with Message Analysis, whose
  reports page pins its own 30-day expiry to the same table. A 90-day panel over a 30-day store
  would show nothing between day 31 and day 90 -- a number that reads as a promise and is not one.
  Given the options (display-only 90 over a 30-day store, or raise the store to 90) the owner
  ruled **"30 days is fine"**. No retention change; the panel shows exactly what is retained.

## Evidence

The reported row was identified against the live dev jobs database
(`D:\inetpub\ExchangeAdminWebDev\config\exchangeadmin-jobs.db`, read-only), not inferred:

    id         ee8c424b643d44cfbbd072232ad48fcf
    module_id  MessageTrace
    job_type   MessageTrace_DetailExport
    status     Completed
    submitted  ANALOG\mcoelho  2026-07-29T20:38:54Z
    finished   2026-07-29T20:38:55Z
    ticket     (empty)
    rows       1/1  success 1  partial 0  failed 0

It is the only row in `bulk_job` on that instance. Every visible field matches the screenshot,
including the empty Ticket cell. **The row rendered as "Room Type (bulk)" on the Conference Rooms
page is a Message Analysis export job.**

## Defects

### F1 -- the panel is not scoped to the module (root cause)

`Components/Pages/ConferenceRooms.razor:603-604`:

    activeJobs = BulkJobs.GetActiveJobs();
    recentJobs = BulkJobs.GetRecentJobs();

Both are unfiltered across every module. `BulkJobRepository.GetActive` (`:307`) selects on
`status IN (queued, running)` with no `module_id` predicate; `GetRecentFinished` (`:319`) selects
on the three terminal statuses with no `module_id` predicate and `LIMIT $limit`
(`BulkJobs:RecentJobLimit`, default 25).

Three consequences, in descending severity:

- **A running job belonging to another module is offered a Cancel button** on this page
  (`:72-74`). Cancel is not module-checked anywhere: `BulkJobService.CancelJob` takes an id. A
  Conference Rooms operator can today cancel a Message Analysis export.
- **Cross-module disclosure.** Submitter, ticket, and row counts of another module's work are
  shown to anyone holding only the `ConferenceRooms` policy. The Message Analysis *payload*
  descriptor is not rendered here, so no trace data leaks -- but who ran what, and under which
  ticket, does cross a section-access boundary.
- **Every circuit on this page re-renders for every job event app-wide.** `OnJobChanged`
  (`:617`) is subscribed to `BulkJobService.JobChanged`, which fires per row of any job in any
  module, and the handler unconditionally calls `RefreshJobs()` -- four SQLite reads per event.

The repository already has the filtered read for the terminal case:
`GetFinishedByType(moduleId, jobType, limit)` (`:339`), added for the Message Analysis reports
page, whose doc comment states precisely the reason this page needs it -- an unfiltered
cross-module window lets a busy module push a module's own jobs out of view. There is **no**
module-scoped equivalent for the active case.

### F2 -- unknown job kinds are mislabelled, not rejected

`Components/Pages/ConferenceRooms.razor:640-643`:

    private static string JobKindLabel(BulkJob job)
        => job.JobType == ConferenceRoomJobPayload.FinderJobType
            ? "Room Finder (bulk)"
            : "Room Type (bulk)";

A two-way ternary over an open string. Anything that is not `SetMetadata_Bulk` renders as Room
Type. This is what turned the export job into a plausible-looking Conference Rooms row; had it
rendered the raw `MessageTrace_DetailExport`, the F1 leak would have been obvious on sight.

F1 makes unknown kinds unreachable, but the fallback must still be correct: a future
Conference Rooms job type would otherwise inherit the same silent mislabel.

### F3 -- no timestamp

The table has Kind, Submitted by, Ticket, Status, Progress, Success, Partial, Failed (`:51-52`).
There is no submitted or finished time in any row, so a row from six days ago is
indistinguishable from one from six seconds ago. `BulkJob` carries `SubmittedAtUtc`,
`StartedAtUtc`, `FinishedAtUtc` and `HeartbeatAtUtc` already (`Services/Jobs/BulkJobModels.cs:86-91`);
none reaches the markup. `Components/Pages/MessageTraceReports.razor:77` is the pattern to copy
(`.ToLocalTime().ToString("yyyy-MM-dd HH:mm")`).

### F4 -- finished jobs never leave the panel

The only thing that removes a terminal row is `PruneFinishedBefore` at startup
(`BulkJobService.cs:85`), with `BulkJobs:RetentionDays` defaulting to **30**. Neither
`BulkJobs:RecentJobLimit` nor `BulkJobs:RetentionDays` is written by
`tools/Install-ExchangeAdminWeb.ps1` or present in `appsettings.json.sample`, so both defaults are
live on both instances. A completed job is therefore a fixture of the page for up to a month,
and only an app-pool recycle clears it.

Retention is correct as a *data* rule -- the record is the audit trail and must survive. The
defect is that the panel treats "retained" and "worth showing at the top of the page" as the same
question.

### F5 -- no way to see what a row was

The per-row results card (`:271`, `:452`) renders only for `finderJob` / `typeJob`, which
`SelectedJobOfKind` (`:647-649`) resolves by matching `JobType`, and only inside the matching tab.
A row in the summary table that is not the selected job of its kind has no route to its rows at
all -- `BulkJobs.GetRows(id)` is never called for it. There is also no surface for
`BulkJob.Message`, which is where the interrupt reason and the completion notes live
(`BulkJobModels.cs:104`).

## Design

### Scope: module-filtered reads

Add the missing module-scoped active read and use module-scoped reads on both sides.

`Services/Jobs/BulkJobRepository.cs`:

    /// <summary>Non-terminal jobs (Queued + Running) for one module, oldest submission first.</summary>
    public IReadOnlyList<BulkJob> GetActiveByModule(string moduleId)

Same shape as `GetActive`, plus `module_id = $module`. `module_id` is `COLLATE NOCASE`
(`JobStoreMigrator.cs:29`), so the comparison matches the store's own collation without extra
handling.

`Services/Jobs/BulkJobService.cs`:

    public IReadOnlyList<BulkJob> GetActiveJobsByModule(string moduleId)
    public IReadOnlyList<BulkJob> GetRecentJobsByModule(string moduleId, int limit)

`GetRecentJobsByModule` needs a repository read filtered by `module_id` **without** a `job_type`
predicate -- `GetFinishedByType` is per (module, type) and this panel spans both Conference Rooms
kinds. Add `GetFinishedByModule(moduleId, limit)` alongside it rather than widening
`GetFinishedByType`, whose narrow contract is documented and relied on by the reports page.

`GetActiveJobs()` and `GetRecentJobs()` stay: they are the honest cross-module reads and removing
them is out of scope. They simply have no caller in a module page after this work.

**Do not** filter inside the page after an unfiltered read. The predicate belongs in SQL: the
`LIMIT` is applied by the database, so filtering afterwards would still let 25 foreign jobs
consume the whole window and show an empty panel.

Event filtering: `OnJobChanged(string jobId)` receives only an id. Rather than resolve the job on
every event to learn its module (a read per event, which is what the filter is meant to avoid),
refresh unconditionally but from the module-scoped reads -- the wasted work is then four scoped
queries, and the rendered output cannot change for a foreign job. Widening the `JobChanged`
signature is a shared-runner change affecting Message Analysis and is deliberately not in scope.

### Kind labels: a testable seam

`.razor` files have no test harness in this repo (the reason `MessageTraceExportListing` and
`AdminPageDirtyState` exist). Move the label out of the markup:

`Services/Jobs/ConferenceRoomJobPayload.cs`:

    /// <summary>Operator-facing label for a Conference Rooms job type. An unrecognised type
    /// returns the raw type rather than being folded into a known kind.</summary>
    public static string KindLabel(string jobType) => jobType switch
    {
        FinderJobType => "Room Finder (bulk)",
        TypeJobType   => "Room Type (bulk)",
        _             => jobType
    };

The page's `JobKindLabel` becomes a one-line delegation.

### Timestamps

Two columns, both `ToLocalTime()`:

- **Submitted** -- `SubmittedAtUtc`, on every row.
- **Finished** -- `FinishedAtUtc` on terminal rows; for active rows show the heartbeat age
  instead (that is the signal behind the existing Stalled classification and is more useful than
  a null cell).

Format `yyyy-MM-dd HH:mm` with `class="text-nowrap"`, matching
`Components/Pages/MessageTraceReports.razor:77`. Times are rendered on the server in the
**server's** local zone, as everywhere else in this app; that is a pre-existing app-wide property
and not this plan's scope to change.

### Placement: a third tab (D1)

The panel moves from above the tab strip into a third tab alongside Room Finder and Room Type.
The card at `:43-96` is deleted from its current position; `activeTab` gains a `"jobs"` value.

The tab label carries a count of active jobs only (`Jobs (2)`), not the total -- a permanent
`Jobs (25)` badge would reproduce, in miniature, the always-present clutter this change removes.
No count when nothing is running.

**The submit paths must not silently lose their feedback.** The existing per-kind results cards
(`:271`, `:452`) live inside the Finder and Type tabs and stream the job the operator just
submitted; they stay exactly where they are. The Jobs tab is the history and cross-cutting view,
not a replacement for the live view on the tab you submitted from.

### Removal (D2)

A hard delete of the job record and its rows, not a hidden flag:

`BulkJobRepository.Delete(string jobId)` -- `DELETE FROM bulk_job WHERE id = $id` inside a
transaction; `bulk_job_row` cascades exactly as it does for `PruneFinishedBefore` (same table,
same foreign key, and that path is already proven by
`PruneFinishedBefore_DeletesOldTerminalJobsAndCascadesRows_KeepsRecentAndActive`).

Three constraints, all load-bearing:

- **Terminal jobs only.** Deleting a Queued or Running row would leave the runner holding a
  cancellation token for a job that no longer exists, and the next heartbeat write would fail
  against a missing row. The repository method itself refuses a non-terminal job and returns
  false -- enforced in SQL (`AND status IN (...)`), not by the caller, so no future caller can
  bypass it.
- **Module-scoped at the service.** `DeleteJob(moduleId, jobId)` verifies the job belongs to the
  named module before deleting. Without it the page would hand an arbitrary id to a delete, which
  is the same cross-module hole as F1's Cancel button, in a more destructive form.
- **Audited.** Removal is a user-facing mutation of a durable record, so it writes an audit event
  per `docs/ProjectConstitution.md` (Auditing And Tracing). The audit log is a separate store from
  the jobs database, so what the operator cleared, and who cleared it, survives the deletion. This
  is what makes a hard delete acceptable rather than data loss.

Because the panel is shared across operators, one operator's Remove removes the row for everyone.
That is the accepted consequence of a shared server-side panel; the audit event is the record.

### Retention (D3)

Unchanged: `PruneFinishedBefore` at startup, `BulkJobs:RetentionDays` default 30. No new time
window in the panel -- the store's retention IS the window, which is the property that makes the
displayed history true rather than a second retention truth that can drift from the first. This
is the same defect class as review finding F4 on `Export:RetentionDays`
(`docs/MessageTraceDownloadLink-Plan.md:69`), and it is deliberately not repeated here.

### Details

A per-row expander on the summary table (a `detailsJobId` field; one expanded row at a time), for
**any** row, active or terminal, in either tab:

- `BulkJob.Message` when non-empty -- the interrupt or cancel reason.
- `BulkJobs.GetRows(job.Id)` in the same three-column table the existing results card uses
  (Target / Status / Message, `RowStatusCss` + `RowStatusLabel`).
- Submitted-by, submitted IP, ticket, and the full timestamps.

This subsumes what the existing per-kind results card does for the selected job. Keep that card:
it is the live-streaming view for the job the operator just submitted, and removing it is a
behaviour change beyond this defect repair.

## Slices

Each slice is one commit. Commit each before starting the next
(`.agents/repo-guidance.md`, Earned Practices).

1. **Module-scoped reads (F1).** `GetActiveByModule` + `GetFinishedByModule` on the repository;
   `GetActiveJobsByModule` + `GetRecentJobsByModule` on the service; `RefreshJobs()` switched to
   them. Tests in `ExchangeAdminWeb.Tests/BulkJobRepositoryTests.cs` and
   `BulkJobServiceTests.cs`: seed jobs in two modules, assert each read returns only its own; and
   assert the limit is applied **after** the module filter (seed `limit + 1` foreign terminal
   jobs plus one own job, assert the own job is returned).
2. **Kind label (F2).** `ConferenceRoomJobPayload.KindLabel` + tests including the unknown-type
   branch; page delegates.
3. **Remove (D2).** `BulkJobRepository.Delete` (terminal-only, enforced in SQL) +
   `BulkJobService.DeleteJob(moduleId, jobId)` (module-scoped) + audit write + the button. Tests:
   deletes a terminal job and cascades its rows; **refuses a Running job**; **refuses a job in
   another module**; returns false for an unknown id.
4. **Jobs tab (D1).** Move the panel markup into a third tab; `activeTab` gains `"jobs"`; active
   count on the label. Markup only -- covered by manual checks 1 and 9.
5. **Timestamps (F3) + details expander (F5).** Submitted + Finished/heartbeat-age columns; a
   `detailsJobId` field and a `GetRows` call. Both are markup in the same new tab, so they land
   together rather than churning the same block twice.
6. **Version bump.** Base app `2.5.0 -> 2.5.1` (the repository/service reads are shared
   infrastructure) **and** ConferenceRooms `2.3.1 -> 2.3.2` in `Modules/ModuleCatalog.cs:403`.
   Both rules fire; each is independent (`docs/ProjectConstitution.md`, Deployment And
   Versioning).

## Progress

All six slices landed 2026-08-04. App `2.5.0 -> 2.5.1`, ConferenceRooms `2.3.1 -> 2.3.2`.

| Slice | Commit | Note |
|---|---|---|
| 1 Module-scoped reads (F1) | `5413c2b` | 5 tests; 4 fail with the predicates removed |
| 2 Kind label (F2) | `5e43daa` | 6 tests; 4 fail with the two-way ternary restored |
| 3 Remove (D2) | `d62dd59` | 10 tests; 3 fail without the terminal guard, 1 without the module check |
| 4-5 Jobs tab, timestamps, details | this commit | markup; manual checks only |
| 6 Versions | this commit | both rules fired |

**Method note.** Slices 4-5 moved a 60-line markup block, which is the operation that silently
deleted two whole tables during the admin redesign while the build stayed green -- absent Razor
markup is not a compile error. The finished file was therefore checked for 19 expected symbols and
for tag balance, and the panel's position asserted to be *after* the tab strip, rather than
trusting the build.

## Verification

Per `.agents/repo-guidance.md` (Verification):

    dotnet build ExchangeAdminWeb.slnx -c Release
    dotnet test ExchangeAdminWeb.slnx
    dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
    pwsh tools/Test-AsciiOnly.ps1
    git diff --check HEAD

**Known pre-existing failure, do not chase:** `tools/Test-CoverageFloor.ps1` reports 64.7% against
a 65.06 floor and was already failing before this work, traced to `0e35e7b` growing the 0%-covered
`Services/SectionAccessGroupDirectory.cs` (`docs/ThemeSupport-Plan.md`, Verification). Confirm the
ratio does not move *down*; do not lower the floor
(`.agents/review/coverage-floor.txt`, finding tsr-1).

Non-vacuity is required per slice: revert the fix, confirm the new test fails naming the right
thing, restore, confirm green.

### Manual checks

The panel is markup, so these are the only evidence it renders correctly. None is runnable
off-host; all need a dev deploy.

1. **The reported row is gone.** With the Message Analysis export job still in
   `exchangeadmin-jobs.db`, the Conference Rooms page shows no jobs at all -- and the Jobs tab is
   empty rather than showing it. This is the regression test for the report and must be run first.
2. **The module is at the top of the page.** Room Finder is immediately below the tab strip with
   no panel above it, and stays there with jobs present. This is the reported complaint.
3. Submit a Room Type bulk CSV; the Jobs tab row reads "Room Type (bulk)" with a submitted time,
   and a finished time once complete.
4. Submit a Room Finder bulk CSV; the row reads "Room Finder (bulk)". Both kinds appear together.
5. **The live results card still works.** Submitting from the Room Finder tab still streams
   progress *on that tab* -- the Jobs tab is an addition, not a relocation of the submit feedback.
6. **Cross-module control is gone:** with a Message Analysis export *running*, the Conference
   Rooms Jobs tab offers no Cancel and no Remove for it, and the Message Analysis page is
   unaffected.
7. The Downloadable Reports page (`/message-analysis/reports`) still lists exports -- slice 1 adds
   a read next to `GetFinishedByType` and must not disturb it.
8. Details expands for a completed row and shows its per-row outcomes; expands for an interrupted
   row and shows the interrupt reason from `Message`.
9. **Remove:** a completed job disappears on Remove, stays gone after a reload, and **an audit
   event names who removed it**. A running job offers no Remove button.
10. Admin page still reachable by `ANALOG\ExchangeWebAdmins` (the `sidf-1` lockout check, re-run
    after any change touching shared services).

## Non-goals

- Changing job retention (`BulkJobs:RetentionDays`). Ruled at 30 days (D3). The panel shows what
  the store keeps; it never introduces a second retention truth that could drift from the first.
  Per-job Remove (D2) is an explicit operator action, not a retention rule.
- Per-operator removal state. Remove is shared, like the panel; the audit event is the record.
- A cross-module "all jobs" admin view. It may be the right home for the unfiltered reads, but it
  is new capability, not this defect.
- Module-checking `BulkJobService.CancelJob` itself. F1 removes the reachable path from this page;
  hardening the service call is a separate authorization question that should be decided across
  every caller at once, not bolted on here. **Recorded so it is not lost.**
- Per-operator (rather than shared) dismissal state.
- Timezone handling for rendered timestamps.

## Owner gates

**None open.** D1 as originally posed (auto-hide / dismiss / both) was **withdrawn**: the owner
moved the panel off the working surface entirely, which addresses the complaint the three options
were competing to address. Kept here only so the reasoning is not re-derived -- the shared-dismiss
objection that made the choice hard stops mattering once the panel is not in the way. The rulings
that replaced it are recorded at the head of this plan.

## Open questions

- **OQ-1.** `BulkJobs:RecentJobLimit` and `BulkJobs:RetentionDays` are absent from
  `appsettings.json.sample` and from `tools/Install-ExchangeAdminWeb.ps1`, so both instances run
  the hardcoded defaults (25 / 30 days) with no operator-visible way to see or change them.
  Not a defect this plan creates and not required by any slice; worth its own decision.
