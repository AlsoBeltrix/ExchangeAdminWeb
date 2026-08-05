# Admin Bulk Jobs + Export Retention Plan

Status: **Implemented 2026-08-04** (all three slices). No owner gate is open. Approved by owner
directive: *"fix scheduled jobs, add the admin bulk jobs management."*
**On dev as of `2.5.3` (22:24). Checks 2 and 3 PASS on real data; 1 and 4-9 remain unrun.**

Observed after the dev restart, which is what triggers the sweep:

- **Check 2 PASS** -- both real exports (6 days old, inside the 30-day window) survive. This is the
  case that matters most day to day: the sweep must do nothing far more often than it deletes.
- **Check 3 PASS** -- all 87 `.jsonl` audit logs in the parent directory are intact. That is the
  deletion the anchored filename pattern exists to prevent, now confirmed against a real audit
  tree rather than a temp fixture.
- **Check 1 not yet exercised** -- no export on either instance has reached 30 days, so the
  deleting path has not run in production conditions. Only the guards have been proven live.

## Progress

| Slice | Commit | Note |
|---|---|---|
| 1 `PruneExpired` + startup wiring | this commit | 11 tests; 3 guard probes each fail the right one |
| 2 `AdminBulkJobs` page + catalog | `24a248a` | `FailClosed: true`; policy generated from the descriptor, no DI change |
| 3 Docs + versions | this commit | README, state. **No app bump** -- Constitution: a new module does not bump the base version |

**Nothing to install.** Retention runs in the app at startup; there is no scheduled task on any
host and none is wanted (owner, 2026-08-04).

Two things, related by a common cause: work that runs outside a page has nowhere to be seen and
nothing to tend it.

## Part A -- export retention was documented but never performed

**Measured on this host 2026-08-04, not inferred.** `schtasks /query` returns **266 tasks; none
belongs to this application.** The only near-match is a Windows built-in
(`\Microsoft\Windows\NetTrace\GatherNetworkInfo`).

The app is built around that task existing. `Services/MessageTraceExportStore.cs:13-24` states it
as fact:

> Retention note: this app NEVER deletes export files. A scheduled task on the host removes files
> older than `RetentionDays` from the audit log root.

`README.md:44` repeats it to operators. `docs/MessageTraceDownloadLink-Plan.md` D1 records it as an
owner ruling ("cleanup handled out of process, scheduled task"), and openreview finding F4 rejected
a configurable retention key specifically to avoid a second retention truth that could disagree
with the task. **The one source of truth was never created.**

Current exposure is small and that is luck, not design: `E:\WWWOutput\ExchangeAdminWeb\MessageTraceExports`
holds **2 files, 0.6 KB each, 6 days old** -- inside the 30-day window, so nothing has yet outlived
its promise. The defect is that nothing ever will remove them.

Consequences, in order:

- **The app tells operators something untrue.** The Downloadable Reports page prints an "Available
  Until" date and the completion email states an expiry. Both are computed from
  `ExpiresAtUtc` = submitted + 30 days. Past that date the page will show **Expired** while the
  file is still on disk and still downloadable in principle -- the status is wrong in the direction
  that matters, because the row claims data is gone when it is not.
- **Message trace exports accumulate forever.** These contain message metadata (sender, recipient,
  subject) for arbitrary mailboxes. An unbounded retention of that is a data-retention exposure,
  not a disk-space one; the files are tiny.
- **The reasoning behind F4 was void.** The constant was pinned to a policy nothing enforced.
  Moving retention in-process restores it: the pruner, the email and the page now read one number.

### Design (Part A)

**Owner ruling 2026-08-04: "there are and will be no scheduled tasks."** Retention runs
**in-process**, at startup.

*(Superseded first attempt, recorded so it is not revived: this originally shipped as two
PowerShell scripts -- `Remove-MessageTraceExports.ps1` plus an installer registering a daily task.
That reproduced the missing-external-dependency shape rather than removing it: an install step
someone must remember, on every host, forever, whose absence is invisible. The owner ruled it out.
Both scripts and their Pester file were deleted in the same commit that added the in-process
version.)*

`MessageTraceExportStore.PruneExpired(nowUtc, logger)`, called once from `Program.cs` in the same
startup block that already prunes old bulk-job RECORDS. The records and the files they describe now
expire on the same schedule by the same mechanism, which is the property the external task never
had.

It lives on `MessageTraceExportStore` because that type already owns the directory, the filename
convention and the expiry date. Retention as a separate service would be a second place that knows
where exports live -- exactly the drift `MessageTraceExportStore` exists to prevent.

Rules, each with a test:

- **Anchored filename pattern, never a wildcard.** The export directory is INSIDE the audit log
  root; a `*.csv` sweep there is one configuration mistake from deleting audit data. Only files
  matching `MessageTraceDetail_<32 hex>_<yyyyMMdd-HHmmss>.csv` are considered.
- **Non-recursive.** A subdirectory under the export directory is not this sweep's business.
- **Exclusive cutoff.** A file exactly at the window survives; deleting a day early would make the
  reports page say Available for a file already gone.
- **Never throws.** Retention is housekeeping and must not be able to stop the app booting. A
  missing directory returns 0, an unresolvable log root returns 0, and a per-file failure is
  counted and logged rather than aborting the sweep -- one locked file must not strand the rest.
- **Never creates the directory.**
- `nowUtc` is injected so the cutoff is testable without waiting 30 days.

**The constant stays 30 and stays a constant.** F4's reasoning is not overturned but strengthened:
one number is now read by the pruner, the notification email and the reports page alike, so a
second knob could only create disagreement.

**Startup-only, no timer.** Consistent with `BulkJobService` ("does nothing on a schedule, only in
response to an enqueue") and the 2026-06-17 no-unattended-worker posture. Consequence worth stating:
on an instance that is never restarted, expired exports persist until the next recycle. Acceptable
-- IIS recycles daily by default, and the alternative is the background timer this app has
deliberately refused everywhere else.

## Part B -- no cross-module view of bulk jobs

`docs/ConferenceRoomsBulkJobPanel-Plan.md` scoped the Conference Rooms panel to its own module,
which was correct and closed a real cross-module cancel exposure. It also left a gap that plan
recorded as a non-goal:

- **A running Message Analysis export is now visible nowhere.** `/message-analysis/reports` lists
  only *terminal* exports (`MessageTraceExportListing.GetExports` -> `GetFinishedByType`). Before
  the scoping fix a running export appeared on the Conference Rooms page -- mislabelled, and with
  a Cancel button an unrelated operator could press. Bad, but visible. Small in practice (the one
  export on dev ran in **one second**), and it is a visibility gap, not a functional one: the job
  completes, notifies and lists as normal.
- **`GetActiveJobs()` / `GetRecentJobs()` have no caller.** The honest cross-module reads survive
  with nothing using them.
- **A stalled or interrupted job in any module has no operator surface at all.**

### Design (Part B)

A new admin page at `/admin-bulk-jobs`, module `AdminBulkJobs`, Category `Administration`,
`SortOrder = 920` (after Event Log).

**Authorization: `FailClosed: true`.** This page shows every module's jobs -- submitters, tickets,
targets and per-row outcomes across Conference Rooms and Message Analysis alike. That aggregation
is exactly what the section-access boundary exists to prevent leaking, so the page that
deliberately crosses it must deny on any failure to evaluate, matching `AdminEventLog`
(`MainPermission = new("Access", "EventLog", FailClosed: true)`).

The page states its retention from `BulkJobService.RetentionDays` (added for this), never a
literal. A hardcoded "30 days" beside the table would be a second retention truth free to drift
from `BulkJobs:RetentionDays` -- the same defect openreview F4 recorded against
`Export:RetentionDays`, and one this plan very nearly reintroduced while arguing against it.

Content: one table over `GetActiveJobs()` + `GetRecentJobs()`, with a **Module** column (the column
the Conference Rooms panel could not have, and whose absence let a MessageTrace row pass as a
Conference Rooms job). Kind renders the raw `JobType` -- this page spans modules, so no module's
label map applies. Reuses the Conference Rooms tab's shape: timestamps, heartbeat age for active
rows, a details expander over `GetRows`, Cancel for active, Remove for terminal.

**Cancel and Remove here are NOT module-scoped, deliberately** -- crossing modules is this page's
purpose, and it is gated `FailClosed` behind an admin policy for exactly that reason. Both audit.

`BulkJobService.DeleteJob(moduleId, jobId)` keeps its module argument; this page passes the job's
own module, which it knows. **No unscoped delete overload is added** -- an id-only delete would be
available to every caller, and the guard would then live in whichever page remembered it.

### The non-goal this plan does NOT adopt

`CancelJob` is still not module-checked at the service. Slice 1 of the Conference Rooms plan
removed the reachable path; this page reintroduces a cross-module cancel **behind an admin policy**,
which is the legitimate case. Hardening the service call remains a separate authorization question
across every caller, unchanged by this work and still recorded.

## Slices

1. **`PruneExpired` + tests, wired into startup.** The sweep, its guards, and the `Program.cs`
   call beside the existing job-record prune.
2. **`AdminBulkJobs` page + catalog entry.** `FailClosed: true`.
3. **Docs + versions.** README retention line rewritten (it promised a task); `.agents/state.md`.
   **No base app version bump.** The Constitution is explicit: *"Adding a new module does not bump
   the base app version; only the new module's own version is set. A new module is not a
   shared-infrastructure change."* `AdminBulkJobs` ships at `1.0.0`; the app stays `2.5.1`.

## Verification

Per `.agents/repo-guidance.md`: build, `dotnet test`, format, ASCII lint, `git diff --check HEAD`.

**Known pre-existing failure, do not chase:** `tools/Test-CoverageFloor.ps1` reports 64.7% against
a 65.06 floor, already failing before this work, traced to `0e35e7b` growing the 0%-covered
`Services/SectionAccessGroupDirectory.cs`. Confirm the ratio does not move down; do not lower the
floor.

**Non-vacuity proven 2026-08-04** on the three guards that matter, each failing the right test:

| Probe | Result |
|---|---|
| Widen the filename pattern to any `.csv` | fails `LeavesANonExportFileAloneHoweverOld` + `LeavesANearMissFilenameAlone` |
| Make the cutoff inclusive (delete a day early) | fails `KeepsAFileExactlyAtTheBoundary` |
| Recurse into subdirectories | fails `DoesNotRecurseIntoSubdirectories` |

All 30 store tests pass restored.

### Manual checks

1. **Retention runs at startup:** with an export older than 30 days on disk, restarting the app
   removes it and logs the count. Nothing to install and no task to check.
2. The two existing 6-day-old exports on dev survive a restart (inside the window).
3. An audit log in the parent directory survives a restart. This is the deletion that must never
   happen; the anchored pattern is what prevents it.
4. `/admin-bulk-jobs` lists jobs from **both** modules with a Module column.
5. A non-admin cannot reach `/admin-bulk-jobs` (fail-closed authorization).
6. A **running** Message Analysis export is visible there -- the gap this closes.
7. Cancel on a foreign running job works from this page and is audited; the Conference Rooms page
   still offers no control over it.
8. Remove on a terminal job deletes it and is audited.
9. Admin page still reachable by `ANALOG\ExchangeWebAdmins` (the `sidf-1` lockout check).

## Non-goals

- Making export retention configurable. One constant is read by the pruner, the email and the
  page; a second knob could only create disagreement, which is the F4 defect.
- Deleting audit logs or any other file under the log root. Exports only, by exact pattern,
  non-recursive.
- **Scheduled tasks of any kind.** Owner, 2026-08-04: *"there are and will be no scheduled
  tasks."* This supersedes `docs/MessageTraceDownloadLink-Plan.md` D1 ("cleanup is handled out of
  this process by a scheduled task") and the same claim in `docs/FutureModules-Plan.md:308`. Both
  are history now; do not implement against them.
- A background timer or hosted worker inside the app. `BulkJobService` "does nothing on a schedule,
  only in response to an enqueue" (`Services/Jobs/BulkJobService.cs:10`) and the 2026-06-17
  no-unattended-worker posture both still hold. Retention is a one-shot startup call, which is the
  same shape as the job-record prune that already runs there -- not a scheduler.
- Module-checking `BulkJobService.CancelJob` (see above).
