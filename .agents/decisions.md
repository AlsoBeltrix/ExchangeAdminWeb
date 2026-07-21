# Agent Decisions

Durable repo decisions. Not a chat log. Each entry should make sense without
conversation history and should name superseded guidance when relevant.

## Decisions

### 2026-07-21 - All source is pure ASCII (no non-ASCII characters anywhere)

Status: Active

New and edited source in this repo is pure ASCII — no em-dashes, smart quotes, or other
non-ASCII characters, in any file type (`.cs`, `.razor`, `.ps1`/`.psm1`, `.md`, JSON, config).
Use `--` or `-` for dashes, straight quotes, plain `...` for ellipsis. This generalizes the
existing PowerShell-only rule (`.agents/repo-guidance.md` Architectural Invariants #6 / Known
Failure Class #6, which required ASCII for 5.1-read scripts) to the whole codebase.

Reason:
Non-ASCII carries no benefit and real, already-realized cost. A BOM-less em-dash in
`tools/JobStateWarning.psm1` broke a Windows PowerShell 5.1 deploy this session (5.1 reads it as
ANSI and mangles it into parse errors). Beyond scripts: audit strings flow into logs and the
SQLite audit DB where encoding can drift or render wrong; em-dash-vs-hyphen is invisible in review
and survives or dies silently through copy-paste, terminals, and tooling. `.cs`/`.razor` compile
fine under Roslyn so the hazard is latent there, not absent — "won't break today" is not a reason
to keep a zero-benefit risk. Existing non-ASCII is left in place for now (changing audit-log text
is a behavior change with test impact); a deliberate cleanup is tracked in `.agents/state.md`.

### 2026-07-21 - ConferenceRooms protected-principal check is one guarded-execution enforcement point

Status: Active

The ConferenceRooms module enforces the protected-principal gate through a single
`ConferenceRoomProtectionGate.GuardThenRunAsync(identity, onDenied, onAllowed)` helper
(`Services/ConferenceRoomProtectionGate.cs`), not per-path inline copies. Every room-mutating
write — single-room Finder, single-room Type, and each bulk row — reaches its write only inside
the gate's `onAllowed` delegate, so the check runs exactly once per write and no path can write
without passing it. The write's trace scope opens inside `onAllowed`, so the protection decision is
fully made before any side effect (fail-closed; Known Failure Class #1). Denial auditing stays with
each caller so per-path action labels are preserved (Finder `ConferenceRooms_SetMetadata`, Type
`ConferenceRooms_SetType`, bulk `_Bulk`-suffixed with captured job actor/ip/ticket).

This supersedes the three prior near-duplicate inline checks (page Finder had none — the gap;
page Type and the bulk processor each had their own copy). Closes the last known protected-principal
gap (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`, Implemented; finding pp-finder-1).
The guard is deliberately ConferenceRooms-scoped, not added to the shared `ProtectedPrincipalService`
— keeping it module-local made this a module-version bump (`ConferenceRooms` 2.2.0 → 2.3.0) with no
app-version bump, per the two-rule versioning policy.

Reason:
Duplicated authorization checks drift — the single-room Finder path was the copy that silently
lacked the gate. A guarded-execution helper makes the write unreachable except through the gate, so
the invariant is structural rather than maintained by discipline across call sites. Subordinate to
the 2026-06-29 protected-principal decision and `docs/ProjectConstitution.md` §Protected Principals.

### 2026-07-02 - Bulk operations run as durable, user-initiated, ticketed, audited server-side jobs

Status: Active

The Bulk Job Runner (`docs/BulkJobRunner-Plan.md`, Implemented) is built. ConferenceRooms bulk
apply (Room Finder / Room Type CSV) no longer runs an inline loop inside the Blazor circuit; it is
submitted as a durable server-side job that survives the submitting browser closing. Several
durable decisions this locks in:

1. **A durable job runner narrows — does not overturn — the 2026-06-17 no-background-worker
   posture.** `BulkJobService` is a self-pumping singleton, not an `IHostedService`/timer. The
   2026-06-17 decision removed the app's only hosted service because it mutated AD *unattended*
   under a synthetic actor with no ticket. This runner is different in kind: every job is
   user-initiated, carries a real submitter + IP + ServiceNow ticket, is fully audited per row,
   and is always cancellable. It does nothing on a schedule — on startup it runs exactly one
   one-shot reconciliation (via an explicit `InitializeAsync()` call in Program.cs, because a DI
   singleton is not constructed until resolved), then only acts when an operator submits.

2. **Job state lives in a SEPARATE operational SQLite database, `config/exchangeadmin-jobs.db`,
   never the config DB.** Rationale: job state is environment-local (a dev job must never appear
   in prod), high-churn, and prunable; the config DB is promoted dev→prod and backed up before
   every deploy. Mixing them would reintroduce the "many concerns in one store" coupling the
   2026-06-12 SQLite migration exists to kill. The jobs DB inherits the `config/` deploy
   exclusion + ACL but is **excluded from the config backup/promote path** — it is never promoted
   and never restored. Consistent with the 2026-06-12 "operational state → SQLite" decision.

3. **No resume across restart.** On startup every non-terminal job (Running OR Queued) is flipped
   to `Interrupted` — full stop. An interrupted job is a truthful record an operator can inspect
   and re-submit, never something that silently resumes or sits stuck "Running". Queue promotion
   happens only within a live process. This, plus always-cancellable jobs and a display-only
   "Stalled" classification for a stale heartbeat, is the load-bearing anti-brittleness rule: no
   job state a human cannot clear, and nothing claims to be running when it isn't. Known honest
   limit: an in-flight PowerShell cmdlet cannot be aborted mid-call (no cancellation-token path),
   so cancel stops a job *before its next row*; a genuinely wedged call clears on the next recycle
   via orphan reconciliation.

4. **Off-circuit authorization = option (a): capture the authorization DECISION at submit,
   re-check per row.** The app has no SAM→groups lookup — authorization is entirely
   `ClaimsPrincipal`-based (role claims + `IsInRole` on the live Windows principal), which a job
   worker thread does not have. At submit (on the circuit) the job records which of the section's
   allowed groups the submitter actually satisfied (via claims OR `IsInRole`), and the runner
   re-checks that captured decision against the section's *current* allowed set per row, fail
   closed. This authorizes the submission and re-checks the snapshot; it does **not** detect
   mid-job group-membership revocation — parity with today's one-check-per-loop model, not a
   regression. The group-match logic is extracted into a shared pure `GroupMembershipChecker` used
   by both the live `GroupAuthorizationHandler` and the job re-check so they cannot diverge.
   (Option (b), live per-row SAM→groups re-resolution, was not built.)

5. **Protected-principal gate enforced in the job, per row, on BOTH Room Finder AND Room Type
   paths — no carve-out.** This closes **GAP 3** (see below): the Room Finder bulk path previously
   had no protected-principal check at all. Fail closed on Unavailable/Ambiguous/CheckFailed/
   exception, audited as a denial, reported in the row result. Applies the 2026-06-29 "protected
   principals are off-limits to every mutating module — no carve-outs" decision to this surface.

6. **Deploy scripts warn (not block) on active jobs before recycle.** `tools/JobStateWarning.psm1`
   is called by every script that stops the app pool (`deploy.ps1`, `tools/promote-dev-to-prod.ps1`;
   `deploy-pipeline.ps1` is covered transitively as it delegates to both). It lists Running/Queued
   jobs and proceeds — a wedged job must never block the recycle that clears it.

Generalization (owner, 2026-07-02): the runner is a thin general `BulkJobService` with
ConferenceRooms as the first caller behind an `IBulkJobProcessor` seam, so other bulk modules
(Migration, Licensing) can reuse it later. The job service, store, queue and lifecycle are
module-agnostic.

### 2026-06-30 - Notifications enforcement sweep: three rule-1 gaps fixed; rule-2 read-alerting classified non-applicable and deferred

Status: Active

The 2026-06-29 "Notifications are mandatory" decision was docs-only; older modules predated it
and were never retrofitted. A read-only audit (2026-06-30) of all 20 non-system modules found
rule 1 (admin notification on every mutating action) mostly already honoured, with three silent
gaps, and rule 2 (alert on security-sensitive reads) effectively unimplemented. Owner direction
(2026-06-30) resolved scope as follows.

**Rule 1 — three gaps fixed (admins notified):**
- `MfaReset` (`1.0.3`→`1.0.4`, commit `bd68d10`): admin notification on every real
  `MfaReset_Execute` attempt (reset, protected block, fail-closed outcome, exception); skips the
  trivial ticket-invalid / auth-denied pre-gates and the read-only ListMethods path. Page change,
  fail-safe.
- `ConferenceRooms` (`2.0.11`→`2.0.12`, commit `6e83ef9`): admin notification on all four write
  paths (single Finder, single Type per apply; bulk Finder, bulk Type one summary per CSV apply
  with counts — LicensingUpdates bulk precedent, not per row). Page change, fail-safe.
- `AccountLockoutRemediation` (`1.0.0`→`1.0.1`, commit `14c6219`): one summary admin notification
  per **executed** logoff (both public paths), gated on `result.Executed` so dry-runs stay silent.
  Placed at the public-method boundary, not the per-row `AuditLogoff` sites (those sit past the
  credential gate — untestable and would email per session). Service change + 3 non-vacuous tests.
  `EmailService`'s two `SendAdminNotificationAsync` overloads were made `virtual` (no behaviour/
  signature change) to give tests a seam to observe firing; the repo had none.

**Rule 3 (notify affected user):** none of the three gap modules are user-permission grants, so
admins-only. **Open, gated on testing:** `AccountLockoutRemediation` user-notification (telling a
logged-off user) is deferred until the module is actually exercised — nobody uses it yet and it is
not validated. Revisit after real testing; record a follow-up decision then. Do not build it now.

**Rule 2 (alert on security-sensitive reads): classified non-applicable for this app, alerting
deferred.** Candidate reads (DelegationReport, MessageTrace, EventLog viewer, RecipientLookup,
AccountLockout discovery) all already audit, and the app exposes only data already visible in AD /
the address book. Owner: these are not genuinely sensitive reads, so audit logging is sufficient
and per-read admin alerting is **not** wanted (it would bury the change-notifications that matter
under message-trace / event-log-open volume). **Never** notify users for these. The lift is small
but the value is negative, so read-alerting is deferred indefinitely, not scheduled. The
Constitution §Notifications rule-2 wording was narrowed (this commit) so its old examples
("audit lookups", "protected-object inspection") no longer contradict this classification.

Plan: `docs/NotificationsEnforcementSweep-Plan.md` (Status: Implemented). App version unchanged
throughout (no functional `EmailService` change); each gap module took a **patch** bump because
this is conformance to already-mandatory behaviour, not new capability (owner, 2026-06-30).

Builds on / enforces the 2026-06-29 "Notifications are mandatory" decision (below), which remains
the canonical statement of the three rules in `docs/ProjectConstitution.md` §Notifications.

### 2026-06-30 - Migration eligibility check: protected status is a separate axis, suppresses single-user create

Status: Active

Decision (owner direction 2026-06-30):
The Migration module's **Check Eligibility** step must flag protected principals, treating
protected status as an axis **orthogonal** to the Exchange/AD eligibility verdict — it does
not change Eligible/Ineligible:

- Protected **and** eligible in Ex/AD → still shown **Eligible**, flagged as a protected
  principal that must be escalated outside this tool.
- Protected **and** ineligible in Ex/AD → still shown **Ineligible**, with the protected/
  escalate flag plus the real ineligibility reason(s).

Create-button behavior differs by entry type (explicit owner direction):

- **Single-user entry:** a protected principal is treated, for the **Create Migration Batch**
  button, exactly like an ineligible user — the Create card/button does not appear.
- **Group / bulk (CSV) entry:** no change to the create flow — surfaced "as already decided."
  The 2026-06-30 GAP 2 batch gate already filters protected targets out at creation and
  reports them; the eligibility table simply shows the protected flag at check time.

Fail-closed: when protection cannot be verified (Unavailable / Ambiguous / CheckFailed), the
target is flagged protected (escalate), consistent with the GAP 2 gate's posture.

Mechanism: reuses the existing in-service protection check (`CheckProtectedAsync`) via a new
`ApplyProtectionFlagAsync` seam called from `CheckMigrationEligibilityAsync`; no new protection
logic. The check is a read — no new denial audit row or admin alert is raised at check time
(the GAP 2 gate already does that at create time); the existing eligibility-check audit detail
and admin notification record protected status.

Builds on the 2026-06-30 GAP 2 decision (below): that decision governs *batch creation*; this
one moves protected *visibility* earlier, to the eligibility check, and adds single-user create
suppression.

Implemented: module `Migration` `1.2.0` → `1.3.0` (app version unchanged); commits `acf877d`
(model+service+tests), `2fb842c` (UI+audit), + docs/version slice. Plan:
`docs/MigrationEligibilityProtectedFlag-Plan.md` (Status: Implemented). 4 new unit tests,
proven non-vacuous; 593/593 green.

### 2026-06-30 - Migration batches: filter protected principals out, never silently, never block the whole batch

Status: Active

Decision (owner direction 2026-06-30):
When a migration batch (`Migration` module, `CreateMigrationBatchAsync`, both ToCloud and
ToOnPrem) contains a protected principal among its targets, the protected target(s) are
**filtered out** and the batch is created for the remaining (non-protected) targets. Two
hard constraints from the owner:

1. **It must never fail silently** — every exclusion is reported back to the operator
   clearly and directly (a distinct, always-visible warning block in the UI naming each
   excluded principal and the reason), audited as its own denial row, and included in the
   admin notification body.
2. **One protected target must never block the whole batch** — the rest are still migrated.

Degenerate case: if **every** target is protected (including the single-target path),
nothing is created and the operator is told plainly why, with the escalate-outside-this-tool
message.

This closes **GAP 2** from the 2026-06-29 protected-principal sweep (`.agents/state.md`).
It applies the 2026-06-29 "protected principals are off-limits to every mutating module"
decision to the migration-batch surface, and chooses the *filter-and-report* enforcement
shape (not refuse-whole-batch, not silent-drop) per explicit owner direction.

Protection check scope: reuses the existing on-prem-AD check (`ProtectedPrincipalService`
`ResolveWithStatusAsync` + `CheckAsync`), fail-closed on Unavailable/Ambiguous/exception.
Same accepted, documented cloud-only limitation as GroupManagement / M365GroupManagement:
a cloud-only target AD cannot resolve returns `NotFound` and is treated as not protected.
This is most relevant on the ToOnPrem (move-back) path, where targets are cloud mailboxes.

Implemented: module `Migration` `1.1.3` → `1.2.0` (app version unchanged); commits
`0b855ac` (service+tests), `5d72978` (UI+audit+notification), + this docs/version slice.
Plan: `docs/MigrationProtectedPrincipalGate-Plan.md` (Status: Implemented).

### 2026-06-29 - Module distribution end state: UI-driven .zip upload that installs/updates a module

Status: Active (long-term direction; nothing to implement now, no plan yet)

Decision (owner direction 2026-06-29):
The end state for module distribution is that the **main app can load a module from the UI as
a `.zip` upload** — an administrator uploads a packaged module through the web UI and the app
installs or updates it, with **no full app rebuild-and-redeploy** for that module. Whether the
package carries a **precompiled** module assembly or **source compiled at runtime** is left
**open** — the owner is explicitly not deciding that yet. Interim steps toward this are at the
agent's discretion; this entry records the destination, not the route.

This is **long-term thinking. Nothing is to be built now and no plan is approved.** It is
recorded so the requirement is durable repo memory rather than living only in chat.

Why this is now recorded (triggering context):
A one-line BlockedSenders UI fix (module 1.0.1 → 1.0.2) could not reach prod on its own — prod
still runs BlockedSenders 1.0.0 — because today a "module" is not an installable unit: it is C#
+ Razor compiled into the single `ExchangeAdminWeb.dll`, its services hand-wired in `Program.cs`
(~54 registrations), its policies generated from the compiled `ModuleCatalog` at startup. There
is no seam where a module plugs in, so any module change requires rebuilding and shipping the
whole app. That friction is the motivation for this direction.

Refines / updates: the **2026-06-18 "Module packaging direction"** decision (same file), which
set `.zip` package + `tools/validate-module-package.ps1` validator as near-term scope and
**deferred runtime upload / assembly loading** as "the hardest and riskiest version … solves a
problem the owner does not currently have." That deferral still holds for *now* (no
implementation), but the owner has now confirmed UI-driven upload **is** the intended end state,
not merely a someday-nice-to-have. The 2026-06-18 entry's near-term scope (rebuild-to-install,
documented package + validator) is the sensible first leg; this entry sets the further
destination it builds toward.

Assessed terrain (agent analysis 2026-06-29 — guidance for the future plan, not owner decisions):
- The hard prerequisite is a **module contract / self-registration seam**: a module declares its
  own services, policies, routes, catalog descriptor, and components, and the app *discovers*
  modules instead of hand-wiring them in `Program.cs`. This refactor is valuable and low-risk
  regardless of precompiled-vs-runtime, and is step 1.
- **Precompiled-vs-runtime only forks at the install step.** The contract refactor, splitting
  each module into its own assembly, and the package format are shared groundwork either way.
  Runtime compilation means accepting and compiling arbitrary code in a privileged Exchange/AD
  tool (an ACE surface) — agent lean is precompiled, but the owner has deferred the call.
- **"From the UI" almost certainly still means a quick self-restart to apply**, not true
  zero-restart live loading: Blazor Server fixes its module/route/DI set once at startup.
  Zero-restart live swap fights the framework hardest and is the riskiest variant; treat it as
  "probably never," not the target.
- Plausible interim staging (each step independently useful, single deploy until step 4):
  (1) module contract / self-registration; (2) each module builds as its own DLL loaded from a
  folder at startup → install/update = drop a DLL + restart, no full rebuild; (3) `.zip` package
  + validator (the 2026-06-18 near-term scope); (4) UI upload + self-restart to apply.

Canonical location / next step when actioned:
This entry is the durable requirement. The implementation scope still belongs in a
`docs/ModulePackaging-Plan.md` that must be written and approved before any code. The
precompiled-vs-runtime decision is to be made when that plan reaches the install/loader stage,
and recorded then. See also `.agents/state.md` "Queued work → Module packaging/import" and the
OPEN versioning-rule blocker (new modules should not bump the base app version), which is the
same end state viewed from the versioning angle.

### 2026-06-29 - M365 group member/owner changes: admin notification only, no affected-user notification

Status: Active

Decision: Adding or removing a member or owner of an M365 group sends an **admin
notification only**. No notification is sent to the affected user (the member/owner being
added or removed).

This **refines** the 2026-06-29 "Notifications are mandatory" decision, which requires
that a permission/access change also notify the affected user. Group member/owner changes
are excluded from that affected-user requirement: per owner, M365 group membership is
typically not tied to permissions, and even when it is, user-facing emails would only
drive tickets. Admin notification and full audit still apply to every change.

Scope: `M365GroupManagement` module member/owner add/remove only. The broader
affected-user notification rule stands unchanged for genuine permission grants/changes in
other modules.

Also recorded here: **GAP 1 from the 2026-06-29 protected-principal sweep is closed for the
principal-write surface.** `M365GroupManagementService` member/owner add/remove now routes
the target identity through an in-service protected-principal gate (`CheckAsync`, fail
closed on Unavailable/Ambiguous/CheckFailed) before any Graph write, mirroring
`GroupManagementService`. Group create/update/delete remain ungated by design (owner:
member/owner only, no protected-*group* gating). Known limitation: the gate resolves
against on-prem AD, so a cloud-only account AD cannot resolve returns NotFound and is
treated as not protected — accepted risk, consistent with on-prem Group Management.

Implemented: module `M365GroupManagement` 1.0.3 → 1.1.0 (app version unchanged); commits
`211c6eb` (service+tests), `03c443a` (UI). Plan:
`docs/M365MemberOwnerManagement-Plan.md` (Status: Implemented).

### 2026-06-29 - Protected principals are off-limits to every mutating module — no carve-outs

Status: Active

Decision:
No module in this tool may perform a mutating operation whose **target** is a protected
principal. This is absolute and covers every change type — account state, permissions, group
membership, directory attributes, password/session state, anything — across every module that
writes, including Emergency Disable, AD Attribute Editor, Group Management, and the planned
M365 member/owner management. If the object being changed is a protected principal (directly,
or transitively via a protected group), the operation must refuse, fail closed, and audit the
denial. There is no group-management exception and no "routine change" exception.

This explicitly resolves the previously-open question (recorded in `.agents/state.md`) of
whether routine group add/remove should be exempt from protected-principal gating. The answer
is **no exemption**: the guard binds to the *target* of the write, uniformly.

Audience rationale (why this is non-negotiable):
This tool's users are L2 helpdesk personnel who are deliberately NOT trusted with direct admin
access. The tool exists to hand them a limited, heavily-logged, notified subset of admin
actions to save L3/L4 from grunt work. The protected-principal guard is what stops an L2
operator from acting on a high-value identity (e.g. adding the CEO to a group that changes his
O365 licensing) in response to an unvetted ticket. Operations against protected principals must
escalate to a real admin outside this tool, not be processable within it.

Scope / mechanism note:
This is a confirmation and scope-clarification of existing intent, not a new rule. The guard is
the existing protected-principal check; this decision forbids treating any mutating module as
out of its scope. The narrow, documented compensation-cleanup bypass allowed by
`docs/ProjectConstitution.md` §Protected Principals (line: "Never bypass ... unless the bypass
is narrowly scoped, documented, and required for compensation cleanup") is unchanged — that is
the only permitted exception and it is not a helpdesk-facing operation.

Canonical location:
`docs/ProjectConstitution.md` §Protected Principals remains the authority. This entry settles
the open scoping question and removes the ambiguity that let group membership be read as a
possible exception.

Reason:
Owner direction 2026-06-29. Settles the open blocker that was gating M365 member/owner
management design.

Supersedes:
The open question in `.agents/state.md` ("should protected-principal checks gate routine group
add/remove?"). Resolved: yes, they gate it, with no carve-out.

### 2026-06-29 - Notifications are mandatory for changes, security reads, and permission changes

Status: Active

Decision:
Notification is no longer discretionary. Three rules now bind every module:

1. **Every mutating action** (any create, write, delete, or change to a user, mailbox,
   group, identity state, access state, password, token/session state, or directory
   attribute) must send an administrator notification.
2. **Every security-sensitive read** (any module whose purpose is to surface
   security-relevant data — e.g. lockout/sign-in/audit lookups, protected-object
   inspection) must send an administrator alert.
3. **Any change to a user's permissions or access** must additionally notify the
   affected user, not only administrators.

Notification is in addition to the mandatory audit event, never a substitute. Notification
failure must not change or mask the backend operation result (same fail-safe rule as audit).

All notification goes through the existing shared `Services/EmailService.cs`
(`SendAdminNotificationAsync` incl. the generic `details`-dictionary overload for arbitrary
module data; `SendUserNotificationAsync` for the affected user). Modules must NOT build a
bespoke mailer, SMTP client, or message template; if a new shape is needed, add an overload
(with a test) to `EmailService` rather than notifying from the module.

Canonical location:
The rule lives in exactly one place — `docs/ProjectConstitution.md` §Auditing And Tracing →
Notifications. `docs/AdminModuleDeveloperGuide.md` §Notifications and `docs/AdminModuleSpec.md`
(audit/trace requirements + new-module checklist) point to it and name the methods; they do
not restate the policy.

Reason:
Owner direction 2026-06-29. Privileged directory/Exchange/Graph changes and security lookups
must be visible to administrators in real time (not only in the audit log), and users must
learn when their own access changes.

Supersedes:
The prior discretionary guidance in `docs/AdminModuleDeveloperGuide.md` §Notifications
("use `EmailService` only when the workflow warrants", "avoid alert fatigue — routine reads
usually should not email anyone", "security-response modules *may* notify"). That text is
removed, not retained, so the deprecated rule does not linger to confuse a future reader.

### 2026-06-26 - SQLite native-lib advisory CVE-2025-6965: tracked, no action available yet

Status: Active (re-check periodically)

Decision:
The build emits `NU1903` for `SQLitePCLRaw.lib.e_sqlite3` 2.1.11
(CVE-2025-6965 / GHSA-2m69-gcr7-jv3q, High). This is **accepted and tracked, not fixed**,
because there is no patched package to move to: 2.1.11 is the latest published version of
the native lib, and the advisory lists patched version "None" as of 2026-06-26. The flaw is
an upstream SQLite engine bug (aggregate-function handling, fixed in SQLite 3.50.2) not yet
rolled into a released `e_sqlite3` build. It reaches this app only transitively via
`Microsoft.Data.Sqlite` 10.0.7.

Practical risk here is low: exploitation needs attacker-controlled SQL containing malicious
aggregate expressions, and the SqliteConfigStore is a single-writer, app-controlled config DB
with hand-written queries — no untrusted SQL is executed. Severity is High in the abstract;
exposure for this usage is not.

Action when a fix ships: bump `Microsoft.Data.Sqlite` (and/or pin a patched
`SQLitePCLRaw.lib.e_sqlite3`) to a version carrying SQLite ≥ 3.50.2, rebuild, run the full
suite incl. config-store tests, then drop this note. Do NOT suppress `NU1903` in the
meantime — keep the advisory visible.

Reason:
Owner direction 2026-06-26 (document & track) after research confirmed no patched package
exists. Recorded so the recurring build warning is a known, assessed item rather than noise,
and so a future session does not waste effort attempting a non-existent version bump.

### 2026-06-18 - SQLite config store: three design decisions resolved; module packaging direction set

Status: Active

These resolve the three Phase-A-blocking open questions in `docs/SqliteConfigStore-Plan.md`
§9 and set the scope direction for the (not-yet-written) module packaging plan. The
SQLite plan stays **Draft** — these decisions unblock it but the owner has not yet given a
go/no-go to execute the migration. When the plan is next revised, fold these in and record
them in its review log.

1. **DB location (SqliteConfigStore-Plan §3b): Option A — the SQLite file lives in the
   existing `config/` directory, one DB per environment (dev and prod each have their own).**
   A single DB shared between dev and prod was considered and rejected: it would make dev
   config changes instantly live in prod (removing the test-then-promote safety net), cannot
   hold per-environment values (security groups, connection targets, `PathBase`), and a
   network-shared single-file SQLite DB reintroduces exactly the file-locking/corruption
   failure class the migration exists to kill. The "seamless config sync" goal is met instead
   by the existing dev→prod promote plus the planned prod→dev `-Refresh` flag, not by a shared
   file. This keeps the `config/` deploy protections (robocopy `/XD config`, ACL, snapshot)
   doing real work; §7's conditional retirement list resolves to the Option-A column.

2. **Data-access library (§3a): `Microsoft.Data.Sqlite` + thin hand-written repositories.
   No Entity Framework.** The config data is key/value pairs and short lists with no
   relational structure, so EF's ORM advantages buy nothing while adding a heavy dependency,
   generated migration artifacts needing their own review, and behavior-hiding "magic" that
   works against the Constitution's inspectable-behavior bias. Revisit only if module
   packaging later makes the data model genuinely relational.

3. **Cache-invalidation model (§5B.2): add a cheap DB change-token (the recommended
   `schema_meta` counter), not the accept-the-staleness option.** Without it, an out-of-band
   write (the prod→dev `-Refresh` tool, a manual DB edit) leaves the running app serving stale
   cached config for up to 30 s — or, for section access, until an app restart — because the
   writing path is no longer the same instance that holds the cache. The change-token lets
   readers detect a change and refresh immediately, and also makes the corrupt-store probes
   cheap.

4. **Module packaging direction: modules are distributed as `.zip` packages with a
   validation tool, but installation still requires a back-end rebuild. Runtime upload /
   assembly loading is explicitly deferred.** Runtime `.zip`-upload-no-rebuild is the hardest
   and riskiest version of the feature (Blazor pages/routes are compiled ahead of time, and it
   means loading arbitrary code into a privileged Exchange/AD admin tool), and it solves a
   problem the owner does not currently have — the owner is the only module author today, and
   it was always a "nice to have for other deployments." "Modules are compiled extensions
   installed by an administrator" is a defensible enterprise posture (cf. SharePoint `.wsp`,
   Dynamics plugins, much of the Jenkins/Jira/Grafana ecosystem). Scope when the plan is
   written: documented `.zip` package structure + `tools/validate-module-package.ps1` as the
   gate; defer runtime upload until a real second deployment needs it. This sets the scope for
   the future `docs/ModulePackaging-Plan.md`; that plan is still required before any
   implementation.

Reason:
Owner decisions 2026-06-18 after a plain-English walkthrough of the four open questions.

### 2026-06-18 - Conference Rooms: cloud-only room lists, and partial-write is reported

Status: Active

Decision (two related owner decisions, 2026-06-18):

1. **Room lists are created in the cloud with no organizational unit.** The Conference Rooms
   module creates room lists via Exchange Online `New-DistributionGroup` and must NOT pass
   `-OrganizationalUnit`. Exchange Online does not understand on-prem AD OUs, so passing one
   (the legacy `RoomListOU` value, an on-prem OU path) made every room-list creation fail with
   "organizational unit not found." The room list is consequently a cloud-only object — it is
   NOT created on-prem and synced up like the company's other distribution lists. The owner
   accepts this divergence given on-prem Exchange is slated for decommission (next year). The
   `RoomListOU` config field was removed entirely.

2. **Partial Room Finder applies are reported and audited as partial, not as plain failures.**
   A Room Finder apply performs several non-transactional writes across EXO and on-prem AD
   (`Set-Place`, then `Set-ADUser` for City/State/Country, then timezone, then room-list
   membership). If an early step commits and a later one fails, the room is left
   half-configured. The result must surface this explicitly (`RoomOperationResult.Partial`, a
   "PARTIAL" UI badge, and the partial detail in the audit record) rather than reporting a bare
   failure that implies nothing changed. Re-running a row is safe (every step is idempotent).
   The inherent residual — a genuine write failure after the pre-mutation preflight passes —
   cannot be eliminated (two systems, no distributed transaction); it is made visible instead.

Reason:
Both came out of a live owner test (2026-06-18) of Room Finder apply plus a follow-up review.
This also corrects an earlier `docs/CommitReview-2026-06-17.md` note that described the
partial-write residual as "accepted" when no such decision existed.

Scope guard:
This does NOT change how other DLs are managed, and does not endorse cloud-first creation for
anything beyond Conference Rooms room lists.

### 2026-06-17 - TestAccountPool module removed

Status: Active

Decision:
The TestAccountPool module is removed from the application as of app version `2.3.10`.
Deleted: `Services/TestAccountPoolService.cs`, `Services/TestAccountPoolCleanupWorker.cs`,
`Components/Pages/TestAccountPool.razor`, `ExchangeAdminWeb.Tests/TestAccountPoolServiceTests.cs`,
the catalog descriptor in `Modules/ModuleCatalog.cs`, both `Program.cs` registrations, the
orphaned `EmailService.SendTestAccountPasswordAsync` helper, and the config seeds/docs in
`tools/Install-ExchangeAdminWeb.ps1`, `appsettings.json.sample`, and `README.md`.
`ModuleCatalogTests` counts updated (modules 21→20, configurable aliases 28→27).

Reason:
Owner direction (2026-06-15): the module was never activated in any environment and is not
wanted. It was also the application's only `AddHostedService` — a background timer
(`TestAccountPoolCleanupWorker`) that mutated AD unattended under a synthetic
`"System"/"BackgroundWorker"` actor with no ticket — which was the architectural oddity that
prompted removal. Removing it also retires the background-thread variant of the
connection-lifetime hazard tracked in `docs/SqliteConfigStore-Plan.md`.

Notes:
Historical audit-log entries (`TestAccountPool_*`) are intentionally NOT scrubbed. Historical
docs (`docs/Incident-*`, `docs/ProdReadiness*`) keep their references as history. This entry is
the authority for the removal; `docs/FutureModules-Plan.md` and `docs/SqliteConfigStore-Plan.md`
point here.

### 2026-06-17 - Credentials live in the deployment's PAM solution, not hardcoded to Delinea

Status: Active

Decision:
The Constitution's Credential Isolation rule is generalized: every password or privileged
credential (now explicitly including SMTP and ServiceNow service passwords, not only
directory/Exchange/Graph secrets) must come from the deployment's PAM/secret-management
solution and must never sit as plaintext in `appsettings.json` or other config files.
Delinea Secret Server remains the only backend implemented today, and the existing field
names (`DelineaSecretId`, `GraphDelineaSecretId`) stay, but code and docs must not treat
Secret Server as the only *possible* backend. A future deployment may add another (e.g.
CyberArk) or a Windows-protected/encrypted store.

Scope guard:
Do NOT build a new PAM integration (CyberArk etc.) speculatively. Do keep the
credential-resolution seam generic enough that adding a backend later does not require
touching every module. This is a principle/wording change, not an implementation task.

Reason:
Owner direction 2026-06-17. The current deployment configures neither an SMTP nor a
ServiceNow password, so there is no live plaintext exposure today; this records the
intended rule so future deployments and future developers do not hardcode Delinea or
park secrets in config files. Resolves the ProdReadiness `[creds]` medium findings as a
posture decision rather than a code change.

Supersedes:
The Constitution's prior absolute "must come from Delinea Secret Server" phrasing in
§Credential Isolation, which is now the "only backend implemented today" rather than the
only permitted backend.

### 2026-06-10 - Adopt the standard `.agents/` governance layout

Status: Active

Decision:
This repo now uses `AGENTS.md` as the canonical agent guidance, with current state in
`.agents/state.md`, durable decisions in `.agents/decisions.md`, and machine-readable
maps in `.agents/repo-map.json` and `.agents/artifact-manifest.json`. `CLAUDE.md` is a
thin pointer shim that includes `AGENTS.md` so the Claude Code harness keeps working.

Reason:
Establishes one canonical guidance location and one discoverable current-state entry
point, reducing drift between harness-specific files and durable repo memory.

Supersedes:
The standalone `CLAUDE.md` agent guide. Its content moved into `AGENTS.md`; the engineering
rulebook `docs/ProjectConstitution.md` was left in place and remains the highest
engineering authority.

### 2026-06-12 - Runtime config and operational data move to SQLite

Status: Active (direction approved by Michael; implementation gated on approval of
`docs/SqliteConfigStore-Plan.md`, drafted 2026-06-15, Status: Draft)

Decision:
The scattered JSON fragments under `config/` (and runtime-editable operational state
generally) will move to a SQLite database stored outside the deploy target. SQL
Express was considered and rejected: no ADI policy mandates a managed SQL instance for
this app, the app is single-writer/single-box by design, and SQLite removes ops
surface (no service, file-copy backups, `VACUUM INTO` snapshots for the planned
prod<->dev config copy tool) where Express adds it.

Consequences:
- New modules and new app settings self-register idempotently at startup
  (INSERT-if-missing with defaults). This RELAXES the 2026-06-12 owner direction
  "the app must never write enablement state at startup" for NON-DESTRUCTIVE seeding
  only; destructive startup writes remain forbidden.
  **IN EFFECT as of SQLite Phase C (app 2.3.20, 2026-06-18):** `ModuleEnablementService.
  SeedMissingModules()` runs at startup (Program.cs, after the migrator) and does
  `INSERT ... ON CONFLICT DO NOTHING` for catalog modules with no row, at their
  `EnabledByDefault`. It reads-first and only opens a write transaction when something is
  actually missing (a no-op seed bumps no change token). It NEVER modifies an existing row
  (the 2026-06-12 incident regression — ExchangeOnline flipped to false — is guarded by a
  test that fails if seeding becomes destructive), and it no-ops on a corrupt/unreadable
  store. The original "no *destructive* startup write" direction stands unmodified.
- Much of the 2026-06-12 incident hardening (config/ backup snapshots, post-deploy
  drift check, robocopy config exclusions, corrupt-JSON guards) becomes
  obsolete-by-design; the plan must enumerate what is retired vs kept.

Reason:
Repeated config-file incidents (see `docs/Incident-2026-06-12-DevConfigLoss.md`) all
stem from many loose files shared by the app, deploys, and humans. Transactional
single-file storage retires the corrupt-file and partial-state failure classes
structurally.

### 2026-06-10 - `docs/ProjectConstitution.md` remains the highest engineering authority

Status: Active

Decision:
The Constitution is not migrated or restated in `AGENTS.md`. `AGENTS.md` points to it and
defers to it on all whole-app engineering rules (authorization, credential isolation,
auditing, protected principals, module system, deployment and versioning, the never-do
list).

Reason:
Avoids duplicating a competing copy of the engineering rules. One canonical source per
truth.
