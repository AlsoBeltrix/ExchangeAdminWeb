# Bulk Job Runner — durable server-side batches decoupled from the browser connection

Status: Approved (owner, 2026-07-02) — not yet started

Authorization model: **option (a) locked** (owner, 2026-07-02) — submission-time
`AuthorizeAsync` + captured role-claim snapshot re-checked per row via a shared pure
group-checker. Option (b) (live per-row SAM→groups re-resolution) is not being built.
Scope: base-app facility + ConferenceRooms as first caller
App version: bump `<VersionPrefix>` (shared/app-wide change) + ConferenceRooms module `Version`
Owner: Michael

## Problem

Bulk apply in ConferenceRooms (Room Finder / Room Type CSV) runs as a single server-side
loop **inside the Blazor Server circuit** (`ConferenceRooms.razor:743–768`, `:988–1010`):
one row at a time, several sequential Exchange/AD calls each, `StateHasChanged()` after
each row. The whole job's lifetime is chained to that one browser connection. Anything
that drops the connection — IIS/proxy idle cutoff, network blip, laptop sleep, tab close —
kills the loop and loses the remaining rows. Operators work around it by splitting CSVs to
~<40 rooms. This must work for **any** batch size (1000+ rooms) with no arbitrary timeout.

Raising any timeout is rejected by the owner: no fixed number works for arbitrary N. The
fix is to move the batch off the connection.

## What is explicitly IN and OUT of scope

IN:
- The batch runs **server-side, independent of the submitting browser connection**. Closing
  the tab does not stop or lose the job.
- Live UI is **unchanged for the watcher**: rows still stream in as they process.
- On return / refresh / another operator, the Conference Rooms page shows running + queued
  jobs (who, ticket, progress). Job state is visible to **all** operators, not only the
  submitter.
- A second submission while one runs is **queued** and starts automatically when the head
  finishes.
- Completion admin notification (email) fires from the **job**, regardless of tab state.
- Deploy pipeline **warns** if any job is Running/Queued before a recycle.

OUT (owner direction 2026-07-01):
- **No resume-after-recycle.** The `.db` exists to survive a dropped *browser connection*,
  not an app-pool recycle. A job interrupted by a restart is reported **Interrupted**, never
  silently resumed and never silently stuck. (See Anti-brittleness.)
- No arbitrary/global timeout knob.
- No change to per-row Exchange/AD logic (the 2026-07-01 on-prem room-list fallback and all
  existing Set-Place / Set-ADUser / Set-Type behavior are untouched — the runner calls the
  same `ConferenceRoomService` methods).

## Why this cannot live entirely in the ConferenceRooms module

Evidence from the code (all base-app, in `Program.cs`):
- `ConferenceRoomService` is `AddScoped` (`Program.cs:95`) — one instance per browser
  circuit, destroyed when the connection dies. A job cannot survive inside it.
- Audit/actor/ticket/IP context is assembled in the Razor page from the authenticated
  connection (`ConferenceRooms.razor:526–537`, `:753`) and dies with it.
- Operation-trace context is `AsyncLocal` (`OperationTraceService.cs:8`) — ambient to the
  async flow. **Correction (codex review 2026-07-02):** `AsyncLocal` *does* flow across
  `Task.Run` unless execution-context flow is suppressed, and `BeginOperation` parents any new
  scope to the current context (`OperationTraceService.cs:31`). So the risk is the opposite of
  what an earlier draft said: a job pump could accidentally *inherit a stale parent* trace
  context. The job must therefore start each row from a **clean root** trace context (open its
  own root `BeginOperation`, not nest under whatever ambient context the pump inherited). See
  Audit fidelity.

To survive a closed tab, job state must live app-wide. This app has exactly two app-wide
homes and both are base-app singletons registered in `Program.cs`: an in-memory singleton,
or SQLite. Therefore the **job tracking is a new base-app service**; only the per-row
*processing* stays in the module.

### DI lifetime (verified — codex review 2026-07-02)

`ConferenceRoomService` is **`AddScoped`** (`Program.cs:95`). A singleton job service therefore
**cannot inject or hold** it, and must never capture the page's scoped instance (that re-ties
the job to the dying circuit scope). The runner must take **`IServiceScopeFactory`** and, per
job, `CreateScope()` → resolve a fresh `ConferenceRoomService` (and any other scoped deps) for
the lifetime of that job, disposing the scope at terminal state. The runner depends on the
module work through an **interface seam** (working name `IBulkRoomProcessor`, implemented by
`ConferenceRoomService`) so the job service is unit-testable with a substitute and carries no
compile-time dependency on the concrete module service.

## Backend: separate operational SQLite database (owner-confirmed)

- **Own `.db` file, NOT `config/exchangeadmin.db`.** Rationale (owner 2026-07-02): the config
  DB is promoted dev→prod, backed up before every deploy, and holds stable configuration.
  Job state is environment-local (**must never promote** — a dev job must never appear in
  prod), high-churn, and prunable. Mixing the two reintroduces the "many concerns in one
  store" coupling the SQLite migration exists to kill (`.agents/decisions.md` 2026-06-12).
- Location: `config/` directory alongside the config DB but a distinct file (working name
  `config/exchangeadmin-jobs.db`), so it inherits the same `/XD config` deploy exclusion and
  ACL, and is **not** raw-copied over on deploy. It is NOT part of the config
  backup/promote path (`tools/SqliteConfigBackup.psm1`, `tools/promote-dev-to-prod.ps1`
  stay pointed only at the config DB).
- Engine choice justified: durable across a connection drop, no service to run,
  single-writer (all Exchange work already serializes through the one `ExoConnectionPool`),
  trivial write load. Consistent with the 2026-06-12 "operational state → SQLite" decision.
- Access via a thin hand-written repository (`Microsoft.Data.Sqlite`, no EF), mirroring the
  existing `Services/Storage/*Repository.cs` pattern.

### Durability scope — persist little, on purpose

Owner is not worried about recycle-loss, only the UI-timeout. So we persist the **minimum**
to (a) survive a dropped connection and (b) report honestly after a recycle:
- **Job row**: id, module, action (Finder/Type), submitter (SAM), ip, ticket, status,
  created/started/finished timestamps, total-row-count, done-count, success-count,
  heartbeat timestamp.
- **Job input payload**: the parsed CSV rows for the job (serialized), so a job that is
  merely *Queued* (never started, nothing in memory yet) is fully self-describing on disk.
  Required to even represent a queued job durably. (See restart policy below — we do not
  resume, but the payload must exist for a queued job to be a real, inspectable record and
  for the row-level input to be auditable.)
- **Per-row result row**: job id, row index, target email, status (Success/Failed/Partial),
  message. (Needed so the returning/other operator sees the same result table today's page
  shows, and so the completion email summary is accurate.)
- Live per-row *step* detail (the little grey step lines) may stay in memory for the watching
  circuit; only the row-level outcome is persisted. This keeps the durable footprint small.

### Restart policy — no resume, both Running AND Queued go Interrupted (corrected)

Owner direction: no resume-after-recycle. On startup, **every job in a non-terminal state
(`Running` OR `Queued`) is flipped to `Interrupted`** — full stop. We do **not** promote a
queued job across a restart. (An earlier draft said startup "promotes the queue"; that was
wrong — it would require resume semantics the owner rejected, and a mid-recycle in-memory
CSV would be gone even though the payload is on disk. Queue promotion happens only *within a
live process*, never across a restart.) The persisted input payload exists so an Interrupted
queued job is a truthful record the operator can see and re-submit — not so it auto-runs.

## Runner shape — self-pumping singleton, NOT a hosted background timer

The 2026-06-17 decision removed this app's only `AddHostedService` (a background timer that
mutated AD **unattended** under a synthetic actor with no ticket) and called it an
architectural oddity. This plan must not reintroduce that shape. Distinction:
- **This work is user-initiated**, carries a real operator + ticket + IP, and is fully
  audited per row. It is not unattended automation.
- The runner is a **singleton job service** that processes one job at a time on a background
  `Task`, started **in response to a submission** (and to promote the queue), not a
  clock-driven `IHostedService` that wakes on its own. There is no timer mutating anything on
  a schedule. On app start it does exactly one thing: reconcile orphaned jobs (below).

(Decision to record if approved: this narrows — does not overturn — the 2026-06-17 posture.
It is a user-initiated, ticketed, audited, cancellable job runner, not unattended
automation.)

### Startup hook must be explicitly invoked (verified — codex review 2026-07-02)

A DI singleton is **not constructed until first resolved**, so orphan reconciliation will
NOT run just by registering the service. `Program.cs` already resolves specific singletons at
startup for exactly this reason (config migrator / enablement seeding, `Program.cs` seeding
block). The plan adds a concrete startup step, in that same block and ordering: after the
config-store migrator, **explicitly resolve the job service and call its `InitializeAsync()`**
which (1) migrates/creates the jobs `.db`, (2) reconciles orphaned non-terminal jobs to
`Interrupted`, **before the app serves requests**. This is a one-shot startup call, not a
timer/hosted worker — consistent with the 2026-06-17 posture.

## Concurrency: single active job, FIFO queue

All Exchange/AD funnels through the singleton `ExoConnectionPool` with a few fixed slots.
Two big batches in parallel would fight for slots and invite EXO throttling. So:
- Exactly **one job Running** at a time; further submissions are **Queued** (FIFO).
- When the head finishes/fails/cancels/interrupts, the next Queued job is promoted.
- The whole queue is visible to all operators (positions, who, ticket).

## Anti-brittleness (owner's explicit requirement — load-bearing)

Every state a job can reach must be clearable by a human, and nothing can sit "Running"
when nothing is actually running:

1. **Orphan reconciliation on startup.** Anything the DB marks non-terminal (`Running` OR
   `Queued`) when the app boots is orphaned (no in-memory worker or live queue survived the
   recycle). At startup the job service flips **both `Running` and `Queued` → Interrupted**
   before accepting new work. It does **not** auto-promote or auto-start anything across a
   restart (no resume — owner direction). A job can never claim to run when it isn't. This is
   the single rule that kills "stuck silently forever." (Queue promotion happens only within a
   live process — see Concurrency — never at startup.)
2. **Always cancellable, independently.** Any operator can cancel the Running job OR remove
   any Queued job from the UI, at any time. Cancelling the head does not trap the queue;
   removing a queued job does not touch the head. This kills "a stuck job prevents fixing
   it."
3. **Stale-heartbeat detection — with an honest limit (corrected, codex review 2026-07-02).**
   The Running job stamps a heartbeat as it completes each row. If the heartbeat goes stale
   beyond a threshold (a wedged/hung Exchange call), the UI surfaces the job as **Stalled**.
   **Important truth:** the Exchange/AD calls are synchronous `PowerShell.Invoke()` with no
   cancellation-token path (`ExchangeServiceBase.cs:47`, `:190`), so a row hung *mid-call*
   cannot be aborted in place. Therefore:
   - "Cancel" reliably stops the job **before the next row** (cooperative
     `CancellationToken` checked between rows) — this covers the common case (a slow-but-alive
     job the operator wants to stop).
   - A genuinely **wedged in-flight call** is surfaced as Stalled but **its worker cannot be
     force-killed**; it clears on the next app recycle, at which point orphan reconciliation
     marks it Interrupted. The plan states this limitation openly rather than pretending
     cancel unblocks a hung syscall. The EXO pool's own `OperationTimeout` (15s per call,
     `ExchangeServiceBase.cs:331`) bounds most hangs anyway, so true unbounded wedges are rare.
   - The queue is **not** advanced while a Stalled job's worker may still be alive (advancing
     would risk two workers hitting the pool at once). In-process advancement happens only on
     real terminal state. Across a recycle nothing is advanced at all — startup marks the whole
     queue Interrupted (no resume). This is a deliberate safety choice over the "always advance"
     convenience.
4. **Bounded, visible queue.** No hidden backlog; positions shown.

Cancellation semantics: cancel stops *before the next row*; the in-flight row is allowed to
finish (its Exchange/AD write is not interruptible mid-call — same non-atomic residual the
module already documents). Cancelled jobs are reported with done/remaining counts.

## Audit / notification fidelity off-connection (must not regress)

Today each row does `OpTrace.BeginOperation(...)` + `Audit.LogConferenceRoomAction(...)` with
the page's `currentUser`/`clientIpAddress`/`ticket`, and the bulk completion calls
`Email.SendAdminNotificationAsync(...)` (`ConferenceRooms.razor:753–778`, `:1099`).

Because trace context flows unpredictably across `Task.Run` (see Correction above), the job
must not rely on ambient context either way. The job must:
- **Capture** submitter SAM, IP, ticket, action at submit time and store them on the job row.
- **Open a clean root** trace scope on the worker per row: `OpTrace.BeginOperation("Conference
  Rooms", "SetMetadata_Bulk"/"SetType_Bulk", <submitter>, <ip>, target, <ticket>)`, ensuring
  it is a root (not nested under a stale inherited parent). If needed, `OperationTraceService`
  gains an explicit "start root operation" entry point so the job never parents to leaked
  context.
- **Audit per row** via `Audit.LogConferenceRoomAction(<submitter>, <ip>, ...)` exactly as
  today, using the captured values.
- **Send the completion admin notification from the job** at terminal state
  (Completed/Interrupted/Cancelled), so a closed tab still yields the email. This MOVES the
  existing `NotifyRoomAdminAsync` call out of the page loop into the job. Notification
  failure must not change the job result (existing fail-safe rule).

Net: same audit rows, same actor, same email — just emitted by the job, not the circuit.

## Protected principals — MUST move into the job (corrected; pre-existing drift found)

**Codex review 2026-07-02 disproved the "already gated" assumption, and found a pre-existing
Constitution violation.** Verified against source:
- The protected-principal check is **page-local** (`ConferenceRooms.razor:1059
  CheckProtectedPrincipalAsync`, via `ProtectedPrincipalService.ResolveWithStatusAsync` +
  `CheckAsync`), and is only wired into the **Room Type** paths (single `:818–819`, bulk
  `:982–983`).
- The **Room Finder** paths (single `ApplyFinder` `:564`, bulk `ApplyFinderCsv` `:739`) call
  `ReauthorizeAsync()` but do **NOT** call the protected-principal check.
- `ConferenceRoomService` contains **no** protected-principal code at all (grep: zero matches).

So two things are true and must be fixed by this work, not assumed away:
1. **Finder is already ungated** — a standing violation of the 2026-06-29 "no carve-outs"
   decision and the Constitution §Protected Principals. `.agents/state.md`'s sweep entry that
   listed ConferenceRooms as gated is **inaccurate for the Finder path** and must be corrected
   (drift note below).
2. If the loop moves off-page, the page-local checks (both PP and reauth) are left behind
   entirely unless we relocate them.

**Decision (owner, 2026-07-02): keep the check, add it to Finder so both paths match — no
carve-out.** Rationale, recorded so it isn't re-litigated: a conference room is a non-person
mailbox and, in practice, will essentially never match the protected lists (Users/Groups are
people-oriented; rooms live in room OUs, not executive OUs). The realistic protective value is
near zero. BUT the gate matches on **OU** and **SAM-name wildcard** too, which are blind to
object type — a broad protected-OU or a wildcard like `svc*`/`admin*` *could* trip a room —
and the check doubles as the app's fail-closed-on-error posture. Given the 2026-06-29
"no carve-outs" rule, the owner chose uniformity over a room-mailbox exception: cheaper to run
a near-always-pass check than to carve a hole in the Constitution.

**Requirement:** the protected-principal gate must be enforced **inside the job, per row,
before every mutating write, on BOTH Finder and Type paths**, fail-closed on
Unavailable/Ambiguous/exception, audited as a denial, and reported in the row result +
completion summary. Cleanest home is the `IBulkRoomProcessor` seam (or a shared pre-write
guard the runner calls), so it cannot be bypassed by any caller. This also *fixes* the
pre-existing Finder gap as a side effect — call that out explicitly and test it (revert →
protected Finder target is NOT blocked → restore → it is).

## Authorization off-circuit — per-row, background-safe (Constitution requirement)

The Constitution requires reauthorization immediately before every write. Today that is
`ReauthorizeAsync()` (`ConferenceRooms.razor:1047`), called once per bulk loop from the live
circuit using the circuit's `AuthenticationStateProvider` — which **does not exist** once the
tab closes and the job runs on a worker thread. Capturing `currentUser` is **not** sufficient
(it proves who submitted, not that they are still authorized at write time).

**Constraint (verified — codex review 2026-07-02): there is no SAM→groups lookup in this app.**
Authorization is entirely `ClaimsPrincipal`-based: `GroupAuthorizationHandler` checks
`user.IsInRole(...)` and `ClaimTypes.Role` claims carried on the live Windows-authenticated
principal (`GroupAuthorizationHandler.cs:90–93`), and `ReauthorizeAsync` calls
`AuthorizationService.AuthorizeAsync(authState.User, "ConferenceRooms")`
(`ConferenceRooms.razor:1049–1050`). The group membership only exists as claims on the live
principal; nothing resolves "which groups is SAM X in" from scratch. So a worker thread cannot
re-run the *exact* existing check without the live principal.

This makes off-circuit authorization a real design decision, not a mechanical port. Options,
to be settled before implementation (recorded as a decision):

- **(a) Capture the authorization decision + principal snapshot at submit time.** At submit
  (on the circuit, principal present) we run the existing `AuthorizeAsync("ConferenceRooms")`
  and also snapshot the actor's relevant role claims onto the job. The job re-evaluates
  section access against those captured claims per row using the same allowed/admin group set
  (`SectionAccessService.GetGroupsForSection` + the same comparison logic, refactored into a
  pure checker shared with `GroupAuthorizationHandler`). This is honest about what it is: it
  authorizes the *submission*, and re-checks against a snapshot — it does **not** detect a
  mid-job group-membership revocation (which the current one-check-per-loop model also does
  not detect today). Lowest risk, matches current behavior, no new AD lookup.
- **(b) Build a real server-side SAM→groups resolver** (e.g. `WindowsIdentity`/AD group
  expansion for the captured SAM) and re-authorize freshly per row. Detects revocation, but is
  net-new security-sensitive code and a new AD dependency — larger surface, more to get wrong.

**Recommendation: (a).** It preserves exactly today's authorization strength (submission-time
check, snapshot for the run), fails closed if the snapshot lacks access, and avoids inventing
a new privileged lookup. Whichever is chosen is recorded as an explicit durable decision
because it touches the Constitution's reauth-before-write rule. Refactor the group-comparison
into a shared pure function so page and job cannot diverge.

## UI changes (ConferenceRooms.razor)

- **Submit** hands the parsed CSV rows + ticket to the job service and returns immediately
  with a job id; it no longer runs the loop inline.
- **Live view**: the page subscribes to the running job and renders rows as they complete —
  same table/streaming feel as today for the watcher.
- **On load**: if a job for this module is Running/Queued, show it (progress, who, ticket)
  and the results-so-far; show recent finished jobs with their pass/fail summary.
- **Controls**: cancel running, remove queued. Second submit while busy → job is queued, UI
  shows its position.
- Ticket handling and the existing "pending apply / not applied yet" clarity stay.

## Deploy pipeline check (owner-requested)

Before recycling an app pool, query the jobs `.db` for Running/Queued jobs on that
environment and **warn** (loud, listing them) that a recycle will interrupt them.

**Coverage must be at the actual recycle sites (corrected, codex review 2026-07-02).**
`deploy.ps1` stops app pools directly on the dev/update path (`deploy.ps1:474`, `:658`), so a
plain `-Dev` deploy would skip a warning that lived only in `deploy-pipeline.ps1`. Put the
check in a **reusable helper module** (e.g. `tools/JobStateWarning.psm1`) and call it from
**every script that recycles a pool**: `deploy.ps1`, `deploy-pipeline.ps1`, and
`tools/promote-dev-to-prod.ps1`. Honor `-PlanOnly` (report, don't act) and the
`Write-Fail`/`$LASTEXITCODE` error model. **Warn — do not hard-block** — so a stuck job can
never prevent a deploy that fixes it (directly serves the anti-brittleness goal). Pester
coverage in `tests/ps/` for the helper and for each call site's warn/PlanOnly behavior.

## Testing

- **Job service (xUnit + NSubstitute):** submit→run→complete happy path; FIFO promotion
  within a live process; queue a second job; cancel running (stops before next row);
  remove queued; **orphan reconciliation flips both Running AND Queued → Interrupted on
  startup** (no cross-restart promotion); stale-heartbeat classification; per-row failure
  aggregation (one bad row does not fail the job; counts correct); completion notification
  fires once at terminal state; audit called per row with captured actor/ticket/ip.
- **Protected-principal gate (both paths):** a protected target is blocked, audited as denial,
  reported — on **Finder AND Type** rows. Explicitly prove the pre-existing Finder gap is
  fixed (revert guard → protected Finder target processes → restore → it is blocked).
- **Off-circuit authorization (option (a) semantics):** submission with no section access is
  rejected up front; a job whose captured claim-snapshot lacks access fails closed on every
  row with an audited denial; an authorized snapshot's rows proceed. (Mid-job live revocation
  is explicitly NOT asserted — option (a) checks the snapshot, matching today's one-check
  model. If option (b) is chosen instead, add a live-revocation test then.)
- **Scoped-dependency resolution:** job runs via `IServiceScopeFactory` scope, not a captured
  circuit instance (verified by the seam being an interface with a substitute).
- **Repository (xUnit):** round-trip job + row results; status transitions; prune.
- Keep the per-row Exchange/AD work behind the existing `ConferenceRoomService` seam so the
  runner is tested with a substituted service (no live EXO).
- **Pester (`tests/ps/`):** the `JobStateWarning.psm1` helper classifies Running/Queued vs
  none; **and each recycle call site** (`deploy.ps1`, `deploy-pipeline.ps1`,
  `promote-dev-to-prod.ps1`) invokes it and warns, with `-PlanOnly` clean (report, no action).
- Prove any new test non-vacuous (revert the guard, see it fail, restore).

## Manual validation (dev)

1. Submit 60+ rooms (over today's ~40 ceiling), **close the tab mid-run** → job continues
   server-side → completion email arrives → reopen page shows it Completed with full
   results.
2. Second operator submits during a run → sees running job + own job Queued → own job starts
   automatically when first finishes.
3. Cancel a running job → stops before next row, reports done/remaining, queue advances.
4. Recycle the app pool mid-job (simulate deploy) → on restart job shows **Interrupted**
   (not Running, not resumed) → the recycle script (whichever of `deploy.ps1` /
   `deploy-pipeline.ps1` / `promote-dev-to-prod.ps1` was used) warned beforehand.
5. Cloud-only + on-prem-synced room lists both still process correctly inside a job (ties to
   2026-07-01 fallback).

## Versioning

- App `<VersionPrefix>` + `AssemblyVersion` + `FileVersion` bump (shared base-app facility).
- ConferenceRooms module `Version` bump (behavior change: bulk apply is now a job).
- Both fire per the two-rule versioning invariant (AGENTS.md #6).

## Open design points to resolve during the review loop (not owner decisions)

- Exact jobs schema (table/column names, status enum values, index on status).
- Generalization: **DECIDED (owner, 2026-07-02) — build a thin general `BulkJobService`**
  with ConferenceRooms as the first caller, so other bulk modules (Migration, Licensing) can
  reuse it later. The module-specific per-row work stays behind the `IBulkRoomProcessor` seam;
  the job service, store, queue, and lifecycle are module-agnostic.
- Heartbeat staleness threshold and prune retention window (concrete numbers).
- Whether a redirected/second browser watching the same running job needs live push or just
  poll-on-navigation (Blazor Server can do either; poll is simpler and enough).

## Decisions to record if approved (`.agents/decisions.md`)

- Bulk operations run as durable, user-initiated, ticketed, audited, cancellable server-side
  jobs in a separate operational SQLite `.db`; this narrows (does not overturn) the
  2026-06-17 no-background-worker posture (unattended ≠ user-initiated).
- Job state DB is environment-local and never promoted; config promote/backup path unchanged.
- **Off-circuit authorization model**: submission-time `AuthorizeAsync` + captured role-claim
  snapshot re-checked per row via a shared pure group-checker (recommended option (a)), since
  the app has no SAM→groups lookup and jobs have no live principal. Record the chosen model
  explicitly since it touches the Constitution's reauth-before-write rule.
- **Stalled-job limitation**: a wedged in-flight `PowerShell.Invoke()` cannot be force-aborted;
  it clears on recycle via orphan reconciliation. Recorded so the residual is a known,
  accepted item, not a surprise.

## Drift to fix alongside this work (`.agents/state.md`)

The 2026-06-29 protected-principal sweep entry lists ConferenceRooms as gated. Verified false
for the **Room Finder** path (single `:564`, bulk `:739` have no PP check;
`ConferenceRoomService` has none). Correct the sweep entry: ConferenceRooms Type paths were
gated, Finder paths were **not**. This plan's PP-in-the-job requirement closes the real gap;
the state.md correction records the true prior state so history isn't wrong. (This is a `drift`
finding surfaced by the 2026-07-02 codex review.)
