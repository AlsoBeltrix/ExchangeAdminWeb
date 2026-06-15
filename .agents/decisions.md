# Agent Decisions

Durable repo decisions. Not a chat log. Each entry should make sense without
conversation history and should name superseded guidance when relevant.

## Decisions

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
