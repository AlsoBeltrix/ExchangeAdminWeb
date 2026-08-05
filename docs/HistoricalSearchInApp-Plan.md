# Historical Search In-App Delivery Plan

Status: **SUPERSEDED 2026-08-05, never implemented.** Its central premise is false and its central
mechanism does not work. Kept as the record of both, so neither is rediscovered.

**Superseded by:** widening the realtime path to the full 90-day window
(`Components/Pages/MessageTrace.razor`, `MessageTrace` module `1.4.0`). No slice of this plan was
built.

**Premise falsified.** The plan assumed Exchange Online's realtime trace covers only 10 days and
that anything older must go through the asynchronous `Start-HistoricalSearch` pipeline. The 10-day
figure came from the deprecated `Get-MessageTrace`. The cmdlet this app actually calls,
`Get-MessageTraceV2`, serves the **full 90-day retention window synchronously** - measured against
this tenant 2026-08-05: rows returned at 9, 11, 20, 45, 89 and 90 days back; refused at 91 with
"Invalid StartDate value. The StartDate can't be older than 90 days from today." Every window the
page was deferring to an emailed report could be answered in-app immediately.

**Mechanism falsified.** Even for genuinely older data the design could not have worked.
`Get-HistoricalSearch` does not return report content: it returns `FileUrl`, a link to
`admin.protection.outlook.com`. Fetching that URL with the app's certificate identity follows a 302
to `nam02b.admin.protection.outlook.com` and then to `login.microsoftonline.com`, returning a 42 KB
**HTML sign-in page** - the portal requires an interactive user session. No cmdlet returns the
bytes; only `Get`/`Start`/`Stop-HistoricalSearch` exist. The app therefore cannot retrieve those
reports at all, which is precisely the cloud-admin-account barrier this plan set out to remove.

**Two further live facts worth keeping.** `Status = "Done"` does NOT imply a report exists - a
zero-row search is Done with an empty `FileUrl` and `ReportStatusDescription = "Complete - No
results found"`, so a poller keying on Done alone would have dereferenced nothing. And
`Get-HistoricalSearch` accepts only `JobId`; there is no `ResultSize` parameter.

**What survives from this work.** The runner defects the three review rounds found are real and
independent of this feature - the job runner still has no early-stop mechanism, its registry still
keys on module id alone so a second processor for a module silently overwrites the first, and jobs
still run strictly one at a time. None of them is fixed. They matter to whoever next adds a job
type; the analysis in this document stands.

Everything below is the plan as reviewed, unchanged, and describes work that was NOT done.

---

Status when drafted: Draft - awaiting owner approval. D1-D5 open.

Reviewed by codex (gpt-5.5-dzs @ xhigh) 2026-08-05, three rounds: **15 findings total, all verified
against code, all admitted and all incorporated.** Round 3 confirmed the round-2 corrections landed
correctly and raised four further defects, which are incorporated but not themselves re-reviewed.
See `## Review history` at the end.

## Problem, measured

Message Analysis has two query paths, split at 10 days
(`Components/Pages/MessageTrace.razor:601`, `IsHistoricalRange => (endDate - startDate).TotalDays > 9`):

- **Realtime** (<=10 days): `Get-MessageTraceV2` / `Get-MessageTrackingLog`, results render in-app,
  and the operator can select messages for a per-hop detail export delivered through the
  Downloadable Reports page.
- **Historical** (>10 days): `RunHistoricalSearch` (`:758`) calls
  `MessageTraceService.StartHistoricalSearchAsync` (`Services/MessageTraceService.cs:46`), which
  invokes `Start-HistoricalSearch` with `-NotifyAddress <operator>`. **Microsoft** generates the
  report and mails it to that address. The app keeps only a `JobId`
  (`HistoricalSearchResponse.JobId`, set at `:70`) and never sees the results.

The historical path therefore delivers nothing in-app. Two consequences, both confirmed against
this deployment rather than assumed:

1. **Operators without a cloud admin account cannot use the result.** The report arrives as a mail
   from Microsoft to the notify address. That is the only copy. Owner statement 2026-08-05: users
   of this app "don't always have cloud admin accounts."
2. **The app's own audit trail stops at submission.** `Audit.LogLookupAction(..., "HistoricalSearch", ...)`
   (`:791`) records that a search was started. What was returned, and who read it, is invisible to
   this app - unlike every realtime query, whose results and downloads are audited.

`Get-HistoricalSearch` is not called anywhere in the repo. Verified 2026-08-05:
`git grep "Get-HistoricalSearch"` returns no match in any `.cs`, `.razor`, or `.ps1` file.

## Goal

The operator who submits a historical search gets the results **inside the app**, on the existing
Downloadable Reports page, without needing a mailbox that receives Microsoft's mail and without a
cloud admin account.

## Constraint that shapes the whole design: the runner is single-lane

`BulkJobService` runs **exactly one job at a time, FIFO** (`Services/Jobs/BulkJobService.cs:284`
pump loop, `:301` awaits one `RunJobAsync`; `docs/BulkJobRunner-Plan.md:159-165`). That rule was
approved for a specific measured reason: all Exchange/AD work funnels through one
`ExoConnectionPool` with **5 fixed slots** (`Services/ExoConnectionPool.cs:73`), and two large
batches in parallel fight for slots and invite EXO throttling.

**A historical-search poll job does not fit that reason.** Its EXO cost is one
`Start-HistoricalSearch` write plus one cheap `Get-HistoricalSearch` read per poll interval;
between polls it holds no slot and no connection. Under today's rule such a job would occupy the
single lane for **hours**, blocking every Conference Rooms batch and every detail export behind it.

**Rejected: a single global `MaxConcurrentJobs`.** It was the first design and it is wrong. A
global worker count of 3 does not just admit one sleeping poller - it admits three
`ConferenceRoomBulkProcessor` or `MessageTraceDetailJobProcessor` jobs
(`Services/Jobs/ConferenceRoomBulkProcessor.cs:105`,
`Services/Jobs/MessageTraceDetailJobProcessor.cs:85`), which is exactly the slot contention the
single-lane rule exists to prevent. The knob would be a loaded gun aimed at the reason for the
rule.

**Adopted: lanes by declared job weight.** The processor declares what its rows cost, and the
runner enforces a limit per class:

- `JobWeight.Heavy` - rows perform sustained EXO/AD work. **One at a time, unchanged.** Both
  existing processors are Heavy, so today's behaviour is preserved exactly.
- `JobWeight.Light` - rows are a cheap status check plus a wait, holding no pooled connection
  between rows. Runs alongside the heavy lane, with its own small cap.

This satisfies the throttling rationale rather than trading it away: the number of jobs doing
sustained Exchange work stays at one, forever, with no setting that can change it.

## Design

### Part 1 - lanes (slices 1-2)

**Weight is REGISTRATION METADATA, not an interface member.** A defaulted interface member
(`JobWeight Weight => JobWeight.Heavy;`) was the first design and it does not work: interface
members are instance members, and the registry stores only a `Type`
(`Services/Jobs/BulkJobProcessorRegistry.cs:13`), so the pump cannot read a weight from a `Type`
without constructing the processor - which means opening a DI scope and building scoped module
services just to decide whether a job may be claimed. Instead the registration carries it:

    BulkJobProcessorRegistration(string ModuleId, string? JobType, Type Processor, JobWeight Weight)

with an omitted weight defaulting to `Heavy`. Every existing registration therefore stays Heavy
with no edit, and a future processor whose author does not think about weight is Heavy too - the
safe direction.

- The pump runs one **heavy worker** (today's loop, behaviour unchanged) plus N **light workers**.
  Light capacity is `BulkJobs:MaxLightConcurrency`, clamped 1..5, default 2.
- **The runner contract must change, not just the claim.** Today `RunJobAsync` receives a Queued
  job and calls `TryStart` (`BulkJobService.cs:353`), which compare-and-swaps from Queued
  (`BulkJobRepository.cs:64`). If a claim step marks the job Running first, `TryStart` returns
  false and `RunJobAsync` returns **without finishing the job - leaving it stuck Running forever**,
  which is the precise failure the runner's anti-brittleness rules exist to prevent. So slice 2
  must restructure: `ClaimNext(weight)` returns an already-owned Running job, and `RunJobAsync`
  accepts a claimed job and no longer calls `TryStart`. The row-count stamp moves into the claim,
  or claim-count-start stays inside one service method. Either way the "who owns this job" boundary
  must be one place.
- **`_pumpRunning` must become per-lane state.** It is a single bool that starts one task and is
  cleared by one loop (`:260-273`, `:284-312`). One heavy worker plus N light workers cannot share
  it: the first loop to find no work would clear the flag while other workers are still running,
  and a subsequent `Enqueue` would start a duplicate. Replace with per-lane worker/supervisor state
  tracking how many workers are live in each lane, with the same fault-safety property the current
  `finally` provides - a faulting worker must never leave its lane permanently wedged.

- **Weight-filtered claiming must not strand unregistered jobs.** Today the pump dequeues first and
  resolves the processor afterwards, marking a job with no processor `Interrupted`
  (`BulkJobService.cs:290`, `:329-336`). Once claiming filters by weight, a job whose type has no
  registration has **no lane that will claim it** and would sit `Queued` forever - invisible,
  uncancellable by the normal path, and cleared only by a restart. Unregistered jobs must remain
  claimable: the heavy lane claims them and routes them straight to the existing
  no-processor `Interrupted` path. Test: an unknown job type on a typed module becomes Interrupted
  with a reason, never stuck Queued.

**Already concurrency-safe, verified, do not "fix":** `_running` is a `ConcurrentDictionary`
(`:42`); `CancelJob` uses a compare-and-swap then signals the token (`:141-164`); `Enqueue`
inserts then kicks the pump (`:125-133`). Startup orphan reconciliation and the no-resume rule are
unchanged.

### Part 2 - processor dispatch by job type (slice 3)

`BulkJobProcessorRegistry` maps **module id -> one processor type**
(`Services/Jobs/BulkJobProcessorRegistry.cs:13-19`), and the last registration for a key wins
**silently**. `Program.cs:71` registers `MessageTrace -> MessageTraceDetailJobProcessor`.
Registering a second `MessageTrace` processor would overwrite the existing one and break the detail
export with no error at all.

Key the registry by **(ModuleId, JobType)**, with a module-level fallback that is **only legal for
a module that has no type-specific registrations at all**.

That restriction is load-bearing. An unrestricted fallback means that once `MessageTrace` has both
a detail and a historical processor, an unregistered or mistyped MessageTrace job type falls back
to `MessageTraceDetailJobProcessor` - which does not validate `job.JobType` and simply deserializes
the payload and counts rows (`Services/Jobs/MessageTraceDetailJobProcessor.cs:70-74`). The
mismatched payload then fails inside `CountRows`, and the runner marks the job **Completed** with
"Job could not start" (`BulkJobService.cs:342-350`). A misrouted job would therefore report as a
completed job that produced nothing, instead of failing fast as unregistered.

So:
- existing single-processor modules keep working through the fallback and need no change;
- a module with any type-specific registration gets **no** fallback: an unknown job type there is
  unregistered and is handled by the existing no-processor path, which marks it Interrupted with an
  explicit reason (`:329-336`);
- a duplicate registration for the same key **throws at startup** rather than silently overwriting.
  A silent overwrite of an authorization-adjacent processor is precisely the failure class this
  repo's guidance calls out.
- **Both** dispatch sites must resolve by (ModuleId, JobType): the run path
  (`ResolveProcessor`, `:466-472`) and the completion-notification path. Changing only one would
  send a job's completion hook to a different processor than ran it.

### Part 3 - the poller (slices 4-6)

New job type `MessageTrace_HistoricalSearch` on module `MessageTrace`, `Weight = Light`.

**Submission must not report success without a JobId.** `StartHistoricalSearchAsync` sets
`Success = true` unconditionally after the invoke and reads `JobId` from the first result
(`Services/MessageTraceService.cs:68-71`); if the result is absent or the property missing,
`Success` is still true with a null id. The page treats `Success` alone as sufficient
(`Components/Pages/MessageTrace.razor:782`). **The poller cannot work without that id** - it would
enqueue a job that can never find its search, which then burns its whole budget and reports
budget-exhausted for what was actually a failed submission. Make a non-blank `JobId` part of
success: otherwise report and audit a submission failure and do not enqueue. Test the
success-with-null-id case explicitly; it is currently reachable.

**Expiry must be measured on the same clock as retention.** `ExpiresAtUtc` is computed from
**submission** time (`Services/MessageTraceExportStore.cs:114`) and that value is what the email
and the reports page state, while the pruner deletes on the file's **last-write** time (`:181`).
For a detail export those are minutes apart and the discrepancy is invisible. A historical report
can be written **hours or a day** after submission, so a report would be advertised as expiring
well before the pruner actually removes it - or, worse, advertised as already expired while the
file is still on disk, which is the exact "Expired for a file that exists" defect the 2026-08-04
retention work fixed. For historical reports the availability window must be stated from the write
time. Needs a delayed-completion test.

**Materialization must be audited.** The detail export audits its save and its failure
(`Services/Jobs/MessageTraceDetailJobProcessor.cs:206`), and downloads audit separately
(`Components/Pages/MessageTraceReports.razor:172`), but the historical path today audits only
submission (`Components/Pages/MessageTrace.razor:791`). Since one motivation for this whole plan is
that the app's audit trail currently stops at submission, the poller must audit its terminal
outcome - fetched-and-saved, search failed, budget exhausted, save failed - using the job's
captured actor, IP and ticket, not an ambient circuit that no longer exists.

**A processor must be able to end a job early, and today it cannot.** `ExecuteRowsAsync` loops to
`total` unless cancelled (`BulkJobService.cs:391`), and `BulkJobRowOutcome`
(`Services/Jobs/IBulkJobProcessor.cs:54-63`) carries no way to say "stop". A poller that finishes
on poll 3 of its budget would otherwise either run the remaining polls pointlessly or fake
completion by recording hundreds of short-circuit rows - noise in `bulk_job_row` and one spurious
`RaiseChanged` event per row
(`:425-434` records a row and raises an event **per row, unconditionally**).

Add `StopAfterThisRow` (bool) plus a terminal reason to `BulkJobRowOutcome`. The runner honours it
by breaking the loop and finishing normally. This is a small, general runner capability - a
`Get-HistoricalSearch` that reports Done has genuinely finished the work, and no job type should
have to burn its remaining budget to say so.

**Early stop breaks progress reporting unless the total is corrected.** `TryStart(job.Id, total,
...)` stamps `total_rows` before the first row runs (`BulkJobService.cs:353`), and `total_rows` is
a persisted column surfaced in every job view (`BulkJobRepository.cs:469`). A poller that finishes
on poll 3 of a 360 budget would report **3 of 360 done** - permanently rendering a successful job
as 1% complete, which is worse than no progress bar because it reads as a stuck job. When a row
stops the loop early, the runner must rewrite `total_rows` to the number actually processed, so
done and total agree. That correction belongs in the same slice as `StopAfterThisRow`, and needs a
test asserting a stopped job reports 100%, not a fraction.

Aggregation is unaffected in kind (Known Failure Class #2): per-row Success/Partial/Failed counts
still accumulate exactly as today, and an early stop never converts a failed row into a success -
the stopping row records its own real status first.

**Heartbeat vs poll interval.** Heartbeat is stamped only after `ProcessRowAsync` returns
(`:425-433`), and `DisplayStatus` renders Running as **"Stalled"** when
`now - heartbeat > StaleHeartbeatMinutes`, default **5** (`:63`, `:243-249`). A 5-minute poll
interval makes a healthy poller flap to Stalled. Therefore:

- the poll wait is `Task.Delay(interval, cancellationToken)` **inside** the row, so cancel is
  honoured in milliseconds rather than after a full interval;
- the interval must be strictly less than the stale-heartbeat threshold, **asserted at
  construction** and logged if the configuration violates it. Two independent numbers that must
  hold a relationship will drift unless something enforces it.

**Row semantics:**

| Poll outcome | Row status | StopAfterThisRow | Job terminal state |
|---|---|---|---|
| NotStarted / InProgress | Success | no | (continues) |
| Done, report fetched and written | Success | yes | Completed, report Available |
| Done, report fetched, write FAILED | Failed | yes | Completed, save-failed marker |
| Microsoft reports Failed / error | Failed | yes | Completed, search-failed marker |
| Budget exhausted, still running | Partial | (loop ends) | Completed, budget-exhausted marker |

**The reports page must be able to say which of these happened.** Today
`MessageTraceExportState` has four values (`Services/MessageTraceExportListing.cs:12-25`) and
`ClassifyState` (`:258-278`) returns `Expired` for any Completed job with no file on disk. Under
this plan a search that Microsoft failed, and a poll that ran out of budget, would both render as
**Expired** - telling the operator their report aged out when it never existed. That is the exact
conflation openreview F1 already corrected once for save-failure. Add `SearchFailed` and
`PollBudgetExhausted`, classified from the job's terminal marker, with tests proving neither
renders as `Expired` or as save-`Failed`.

**How those markers must be written, and why not the obvious way.** The existing save-failed marker
is written through the additive `BulkJobService.AppendJobMessage`, deliberately NOT through the
terminal transition - `MessageTraceExportListing.cs:94-99` records the reason: the runner persists
the terminal state by compare-and-swap from a non-terminal status **before**
`OnJobCompletedAsync` runs, so a `TryFinish` issued from the completion hook matches no row and is
silently lost. The new markers are written from the same place and are subject to the same rule.
Writing them via the terminal transition would compile, run, and produce nothing - a silent failure
that renders as `Expired`, which is the exact defect these states exist to prevent.

**Export paths must become type-aware.** `FileNameFor` and `PathFor` hardcode the
`MessageTraceDetail_` prefix (`Services/MessageTraceExportStore.cs:63-67`, `:75-95`) and the
listing resolves a file from job id and timestamp alone (`MessageTraceExportListing.cs:271`). A
historical report written as `MessageTraceHistorical_...` would be classified **missing** by the
existing resolver. So: pass an explicit export kind into `FileNameFor`, `PathFor` and `TryResolve`.

The pruner's `ExportFilePattern` (`:131-133`) becomes an anchored alternation of the exact allowed
prefixes - not a loosened wildcard. **The export directory sits inside the audit log root**
(`:127-129`), so a sloppy pattern deletes audit data. The existing non-recursive and
audit-survival tests must stay and must be extended to the new prefix in the same commit that
writes the first file with it.

**Authorization.** Download stays behind the MessageTrace module policy with its ticket prompt
(`docs/MessageTraceDownloadLink-Plan.md` D2). A historical report is the same class of data as a
detail export, so it inherits that gate unchanged. No new authorization surface.

## Owner decisions - open

**D1. Light-lane capacity default.** `BulkJobs:MaxLightConcurrency`, clamped 1..5. Recommend **2**:
enough that two operators can each have a search polling, small enough that nothing approaches the
pool's 5 slots. The heavy lane stays hard-coded at 1 and is deliberately NOT configurable.

**D2. Does the notify address stay?** Today `Start-HistoricalSearch` always passes
`-NotifyAddress`, so Microsoft mails the operator regardless. Recommend **keep it** - it costs
nothing and it is the fallback when a poll budget is exhausted or a recycle interrupts the job.

**D3. Poll interval and budget.** Must satisfy interval < stale-heartbeat (default 5 min).
Recommend **interval 4 minutes, budget 360 (24h)**, both config keys. Note the consequence
honestly: 360 polls means up to 360 rows in `bulk_job_row` for one job, because the runner records
a row per row unconditionally and this plan does not change that. If the owner considers that
unacceptable, the alternative is a runner change to record rows conditionally, which is broader
scope and would need its own decision.

**D4. Interrupted poller after an app-pool recycle.** The runner has no resume by design
(`docs/BulkJobRunner-Plan.md:172-178`, owner direction); startup flips non-terminal jobs to
Interrupted. For a poller that discards recoverable work - the search still exists at Microsoft and
is still fetchable by JobId. Options: (a) keep no-resume, operator re-submits; (b) allow this one
job type to be re-queued at startup because a status check is idempotent. Recommend **(a)** here
and treat (b) as separate scope - changing the resume rule touches every job type.

**D5. Where does the light-lane setting live?** `BulkJobService` reads `BulkJobs:*` from
`IConfiguration` in its constructor (`:63-65`), so an appsettings key needs an app-pool recycle to
take effect. Admin Settings persists app-level values through `AppSettingRepository` (the pattern
`ExtendedLogService` uses). Options: (a) appsettings only - simplest, matches the other
`BulkJobs:*` keys, requires a recycle; (b) `app_setting` row, editable in Admin Settings, read
per-use so it takes effect live. Owner asked for Admin Settings, which implies (b); recommend
**(b)** with clamping applied on both save and read, so a value written directly to the DB cannot
bypass the ceiling.

## Unknowns to resolve during slice 4, not now

- The exact shape of what `Get-HistoricalSearch` returns and how report content is retrieved from
  it (a property on the record, or a separate download URL). Confirm against live EXO before
  writing the fetch. Do NOT infer it from documentation: `.agents/state.md` records this exact trap
  - `Properties["DisplayName"]` where the cmdlet returns `Name` compiles, passes every unit test,
  and yields an empty string at runtime.
- Whether the returned report is already CSV in Microsoft's summary format. If so it is written
  through unchanged.

## Slices

Each slice is one commit, verified before the next starts.

1. **`JobWeight` as registration metadata + light-lane setting.** Registration record carries the
   weight, omitted defaults to Heavy; config read with clamp to 1..5 and a log on clamp. No pump
   change yet. Tests: clamp at 0/1/2/5/99; both existing registrations resolve as Heavy.
2. **Multi-lane pump: claim contract + per-lane worker state.** `ClaimNext(weight)` returns an
   owned Running job; `RunJobAsync` accepts a claimed job and no longer calls `TryStart`;
   `_pumpRunning` becomes per-lane worker state. One heavy worker plus N light workers over the
   FIFO queue. **Existing `BulkJobServiceTests` must pass UNMODIFIED** - that is the proof heavy
   behaviour is unchanged. New tests: a light job runs while a heavy job runs; two heavy jobs never
   run together whatever the setting; light capacity respected; a job is claimed by exactly one
   worker under contention; cancel affects only its own job; a faulting worker cannot wedge its
   lane; no job is ever left Running after its worker returns.
3. **Registry keyed by (ModuleId, JobType); fallback only for modules with no typed
   registrations.** Duplicate key throws at startup; both the run and completion dispatch sites
   resolve by the pair. Existing registrations keep working through the fallback, proven by the
   existing tests passing unmodified. Test: an unknown job type on a module that HAS typed
   registrations is treated as unregistered (Interrupted with a reason), never routed to a sibling
   processor.
4. **`StopAfterThisRow` + total-rows correction + `Get-HistoricalSearch` seam.** Runner honours
   early stop and rewrites `total_rows` to the processed count so a stopped job reports 100% rather
   than a fraction. Confirm the live cmdlet shape first, then add
   `MessageTraceService.GetHistoricalSearchAsync` returning a normalized status plus report
   content, behind an interface so the processor is testable without EXO - same seam shape as
   `Authorization/ISectionAccessDirectoryCommands.cs`. Tests: a stopped job reports done == total;
   per-row counts still aggregate correctly across an early stop; no existing job type changes
   behaviour (the flag defaults false).
5. **Type-aware export paths + new report states.** Export kind threaded through `FileNameFor` /
   `PathFor` / `TryResolve`; pruner pattern becomes an anchored alternation; `SearchFailed` and
   `PollBudgetExhausted` added with tests proving they never render as `Expired`. Audit-survival
   tests extended to the new prefix.
6. **`MessageTraceHistoricalJobProcessor` + page wiring.** Poll-as-rows per the table above,
   cancellable delay, interval-vs-heartbeat assertion. Submission requires a non-blank `JobId`
   before enqueueing. Every terminal outcome audited from the job's captured actor/IP/ticket.
   Availability window stated from write time, not submission time. Historical submissions enqueue
   the job; the reports page lists both artifact types with a distinguishing column
   (`BulkJobRepository.GetFinishedByModule:402` already provides the query); the trace page tells
   the operator where the report will appear. Tests against a fake: in-progress then done; search
   failed; budget exhausted; cancel mid-delay; write failure reported as save-failed and not as
   Expired; submission with a null JobId does not enqueue; a report completed a day after
   submission advertises an availability window that matches when the pruner will actually remove
   it.

## Non-goals

- Changing the 10-day realtime/historical split. It is Microsoft's retention boundary, not ours.
- A global `MaxConcurrentJobs`. Explicitly rejected above; the heavy lane stays at one.
- Resuming interrupted jobs of any type (D4(b)).
- Changing how the runner records rows (D3's consequence).
- Any change to the realtime detail export's CSV format. Separate defect, separate fix, so each
  stays revertible alone.
- Scheduled execution of any kind. Owner ruling 2026-08-04: "there are and will be no scheduled
  tasks." This is operator-submitted job work, the same posture `BulkJobService` already documents
  (`:8-13`).

## Verification

Per `.agents/repo-guidance.md`: `dotnet build ExchangeAdminWeb.slnx -c Release`, then
`dotnet test ExchangeAdminWeb.slnx`, plus `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore` and `git diff --check HEAD`. Every new test proven non-vacuous by
reverting its guard.

Manual checks (need live EXO and a real >10-day search):

1. Submit a historical search; confirm it polls **without blocking** a concurrently submitted
   Conference Rooms batch - the whole point of Part 1.
2. Submit two heavy batches; confirm they still run one at a time whatever the light setting is.
3. When Microsoft finishes, confirm the report appears on Downloadable Reports and downloads with a
   ticket prompt.
4. Confirm the downloaded file matches what Microsoft mailed to the notify address.
5. Cancel a polling job mid-delay; confirm it stops within seconds, not a full interval.
6. Confirm a healthy polling job never displays as "Stalled".
7. Recycle the app pool with a poll job running; confirm it reports Interrupted, not Running.
8. Confirm the retention sweep deletes an aged historical report and leaves audit `.jsonl` files
   untouched.

## Review history

**Round 1 - codex gpt-5.5-dzs @ xhigh, 2026-08-05. 7 findings, all verified against code, all
admitted and incorporated:**

1. Global `MaxConcurrentJobs` reopens the exact throttling risk the single-lane rule prevents ->
   replaced with weight-based lanes; heavy stays at 1 and is not configurable.
2. A second `MessageTrace` processor cannot be selected and would silently overwrite the existing
   one -> registry keyed by (ModuleId, JobType), duplicate throws.
3. **The runner has no early-stop mechanism at all** - the plan's "remaining rows short-circuit"
   was not implementable. Missed entirely in round 1 drafting -> `StopAfterThisRow` added as an
   explicit runner capability.
4. New failure causes would render as `Expired` -> `SearchFailed` and `PollBudgetExhausted` states
   added with tests.
5. 5-minute poll interval collides with the 5-minute stale-heartbeat default -> interval < threshold
   asserted at construction.
6. Prefix change incomplete without type-aware path resolution -> export kind threaded through the
   store; pruner becomes an anchored alternation.
7. The Admin Settings value has no storage decision -> raised as D5.

**Round 2 - same reviewer, 2026-08-05. 4 findings, all verified, all admitted and incorporated:**

1. **"Atomic claim" alone was not enough** - `RunJobAsync` calls `TryStart`, which CAS's from
   Queued (`BulkJobRepository.cs:64`), so a pre-claimed Running job would fail that swap and the
   method would return **leaving the job stuck Running**. The runner contract itself must change
   (`ClaimNext` returns an owned job), and `_pumpRunning` must become per-lane state - one bool
   cannot serve one heavy plus N light workers.
2. **Weight could not be read from a `Type`.** A defaulted interface member is an instance member;
   the registry holds only `Type`. This was a plain C# error in the round-1 revision. Weight is now
   registration metadata.
3. **The module-level fallback could misroute.** Once MessageTrace has two processors, an unknown
   job type would fall back to the detail processor, which does not validate `JobType` and would
   report "Job could not start" as **Completed** rather than failing as unregistered. Fallback is
   now legal only for modules with no typed registrations, and both dispatch sites resolve by the
   pair.
4. **`StopAfterThisRow` misreports progress** - `total_rows` is stamped up front, so a stop at poll
   3 of 360 renders as `3 / 360` in the admin UI (`AdminBulkJobs.razor:109`). Caught independently
   while the review was running and already folded in: the runner rewrites `total_rows` on early
   stop.

Round 2 confirmed the export-path and report-state half of the plan is accounted for; the
outstanding work was all runner/registry semantics.

**Round 3 - same reviewer, 2026-08-05. Confirmed all five round-2 corrections are incorporated
correctly. 4 further findings, all verified, all admitted and incorporated:**

1. **Weight-filtered claiming would strand unregistered jobs forever.** With no lane willing to
   claim an unknown job type, it sits Queued indefinitely instead of reaching the existing
   Interrupted path. The heavy lane now claims unregistered jobs so they still terminate.
2. **Historical submission can report success with a null JobId**
   (`MessageTraceService.cs:68-71`), which would enqueue a poller that can never find its search.
   A non-blank id is now part of success.
3. **Expiry is computed from submission time while pruning uses write time.** Harmless for a
   detail export written seconds later; wrong for a historical report written a day later. The
   availability window is now stated from write time.
4. **No audit of report materialization.** The historical path audits only submission - the very
   gap this plan cites as a motivation. Terminal outcomes are now audited from the job's captured
   context.

Three review rounds completed (the agreed limit). The reviewer's round-3 verdict was "not quite
safe to implement" **against the pre-round-3 text**; the four findings behind that verdict are
incorporated above and have not been re-reviewed. A fourth round would be the way to confirm them.
