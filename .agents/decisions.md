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
