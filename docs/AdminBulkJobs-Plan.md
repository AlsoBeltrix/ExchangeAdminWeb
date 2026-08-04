# Admin Bulk Jobs + Export Retention Plan

Status: **Approved 2026-08-04** by owner directive: *"fix scheduled jobs, add the admin bulk jobs
management."* That wording sets the scope; no owner gate is open.

Two things, related by a common cause: work that runs outside a page has nowhere to be seen and
nothing to tend it.

## Part A -- the export retention task does not exist

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
- **The reasoning behind F4 is void.** The constant was pinned to a policy nothing enforces.

### Design (Part A)

`tools/Install-MessageTraceExportRetention.ps1` -- a standalone, environment-neutral script that
registers a daily scheduled task, plus `tools/Remove-MessageTraceExports.ps1` doing the deletion.

Two scripts rather than one because they answer to different rules: the remover is the logic that
needs Pester coverage (`.agents/repo-guidance.md`, Verification: "New `.ps1` logic requires Pester
coverage"), and the installer performs a privileged host mutation that cannot be unit tested.
Splitting them means the deletion logic is testable without registering anything.

`Remove-MessageTraceExports.ps1`:

- `-LogRoot` (required) and `-RetentionDays` (default 30) -- **no built-in default for the root**.
  A cleanup script that guesses its own target is the failure mode where a wrong guess deletes the
  wrong tree.
- Deletes only files under `<LogRoot>\ExchangeAdminWeb\MessageTraceExports` matching the exact
  export filename pattern `MessageTraceDetail_<32 hex>_<yyyyMMdd-HHmmss>.csv`. **A pattern, not a
  wildcard**: the directory is inside the audit log root, and a `*.csv` sweep there is one config
  mistake away from deleting audit data.
- `-WhatIf` via `SupportsShouldProcess`, matching the repo's `-PlanOnly` norm for ops scripts.
- Refuses a missing directory quietly (exit 0, nothing to do) but a missing **LogRoot** loudly --
  the first is the state before the first export, the second means it is pointed somewhere wrong.
- Writes a one-line summary so the task history shows what it did.

`Install-MessageTraceExportRetention.ps1`: registers a daily task running the remover as SYSTEM,
`-LogRoot` supplied at install time. Environment-neutral per architectural invariant 1 (the same
rule that keeps `Install-ExchangeAdminWeb.ps1` free of ADI specifics). It is **not** wired into
`deploy.ps1`: deploys must not perform privileged host registration as a side effect, and the task
is per-host, not per-deploy.

**The constant stays 30 and stays a constant.** This work makes the existing promise true rather
than making it configurable -- F4's reasoning is reinstated, not overturned.

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

1. **`Remove-MessageTraceExports.ps1` + Pester.** Deletion logic, pattern matching, `-WhatIf`,
   missing-directory and missing-root behaviour. Tests build a temp tree with a mix of matching
   exports, non-matching files, and an out-of-window matching file, and assert only the intended
   ones go.
2. **`Install-MessageTraceExportRetention.ps1` + PSScriptAnalyzer.** Registers the daily task. Not
   Pester-testable (registers a host object); the remover holds the logic.
3. **`AdminBulkJobs` page + catalog entry.** `FailClosed: true`.
4. **Docs + versions.** README retention section corrected to say the task must be installed and
   how; `.agents/state.md`; app version bump.

## Verification

Per `.agents/repo-guidance.md`: build, `dotnet test`, format, ASCII lint, `git diff --check HEAD`,
plus `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` for the new scripts.

**Known pre-existing failure, do not chase:** `tools/Test-CoverageFloor.ps1` reports 64.7% against
a 65.06 floor, already failing before this work, traced to `0e35e7b` growing the 0%-covered
`Services/SectionAccessGroupDirectory.cs`. Confirm the ratio does not move down; do not lower the
floor.

### Manual checks

1. `Remove-MessageTraceExports.ps1 -WhatIf` against the real export directory names the two
   existing files as *not* eligible (both 6 days old, window is 30) and deletes nothing.
2. After installing, `schtasks /query /tn <name>` shows the task Ready, and a manual run exits 0.
3. **The retention promise is now true:** an export older than 30 days is removed, and its
   Downloadable Reports row reads Expired -- status and disk agreeing, which is the defect.
4. `/admin-bulk-jobs` lists jobs from **both** modules with a Module column.
5. A non-admin cannot reach `/admin-bulk-jobs` (fail-closed authorization).
6. A **running** Message Analysis export is visible there -- the gap this closes.
7. Cancel on a foreign running job works from this page and is audited; the Conference Rooms page
   still offers no control over it.
8. Remove on a terminal job deletes it and is audited.
9. Admin page still reachable by `ANALOG\ExchangeWebAdmins` (the `sidf-1` lockout check).

## Non-goals

- Making export retention configurable. The constant is descriptive of the task's window; two
  knobs is the F4 defect.
- Deleting audit logs or any other file under the log root. Exports only, by exact pattern.
- Wiring task installation into `deploy.ps1`.
- Module-checking `BulkJobService.CancelJob` (see above).
- A scheduler inside the app. `BulkJobService` "does nothing on a schedule, only in response to an
  enqueue" (`Services/Jobs/BulkJobService.cs:10`) and that stays true;
  `docs/FutureModules-Plan.md:308` already rules that scheduled cleanup belongs outside the app
  pool.
