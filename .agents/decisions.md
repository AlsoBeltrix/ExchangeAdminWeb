# Agent Decisions

Durable repo decisions. Not a chat log. Each entry should make sense without
conversation history and should name superseded guidance when relevant.

## Decisions

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

Consequences (to be finalized at plan approval):
- New modules and new app settings self-register idempotently at startup
  (INSERT-if-missing with defaults). This RELAXES the 2026-06-12 owner direction
  "the app must never write enablement state at startup" for NON-DESTRUCTIVE seeding
  only; destructive startup writes remain forbidden. The original direction stands
  unmodified until the plan is approved and this entry is updated.
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
