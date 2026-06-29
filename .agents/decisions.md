# Agent Decisions

Durable repo decisions. Not a chat log. Each entry should make sense without
conversation history and should name superseded guidance when relevant.

## Decisions

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
