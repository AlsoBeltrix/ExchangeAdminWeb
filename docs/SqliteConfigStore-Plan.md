# SQLite Config Store Migration — Plan

Status: Draft (for Michael's review — 2026-06-15)
App version at drafting: 2.3.8 (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`)
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. Implements
decision **2026-06-12 — Runtime config and operational data move to SQLite**
(`.agents/decisions.md`). No implementation until this plan is marked `Approved`.

Evidence base: the touchpoint inventory was produced by a fan-out audit over six
dimensions (C# file I/O, appsettings reads, UI pages, ops scripts, tests, docs) plus a
completeness critic. Every file path below was reported by that audit; load-bearing
claims were re-verified against current source while drafting.

---

## 1. Goal and non-goals

### Goal

Replace the scattered JSON/text config fragments under the app's `config/` directory
with a single SQLite database stored **outside the deploy target**, so that:

- The corrupt-file and partial-write failure classes (2026-06-12 incident) are retired
  structurally, not guarded.
- Deploys can no longer touch runtime config (the DB is not in the publish path), which
  retires the robocopy exclusions, the pre-deploy config snapshot, and the post-deploy
  drift check added during the incident.
- A quick **prod↔dev config copy** becomes a first-class supported operation.
- New modules and new settings **self-register idempotently at startup** (non-destructive
  INSERT-if-missing), relaxing — for seeding only — the 2026-06-12 "no enablement writes
  at startup" direction.

### Non-goals (this plan)

- **Audit and operation-trace logs stay as JSONL files** (`JsonlLogService`,
  `AuditService`, `OperationTraceService`) for now. They are high-volume, append-only,
  written outside the deploy target already, and are not a pain point. Moving them to
  SQLite is a *separate future decision* (see §9 Deferred). Same for EmergencyDisable
  snapshots and HeaderAnalysis temp files.
- **Runtime-editable appsettings keys** (`Email:*`, `ServiceNow:*`, `Audit:*`,
  `ExtendedLog:*`) do **not** move in this plan. They are read by singletons that cache
  at construction; making them DB-backed and live-editable needs a service-lifetime
  refactor that is out of scope here. Flagged as a possible Phase F follow-up.
- **Module *code* packaging / runtime assembly loading** is a separate plan
  (`docs/ModulePackaging-Plan.md`, not yet written). This plan only moves module
  *configuration data*; it deliberately leaves a clean seam (the `module_*` tables and
  startup self-registration) that the packaging plan can build on.
- No change to authorization semantics, the module catalog, fail-closed permission
  design, or credential isolation (Delinea). Those are invariants, not migration targets.

---

## 2. What moves, what stays

### 2a. Moves to SQLite (the in-scope stores)

| Store file (in `config/`) | Owning service | Today's semantics to preserve |
|---|---|---|
| `modules-enabled.json` | `ModuleEnablementService` | on-demand read (no cache); **fail-closed** (corrupt ⇒ all non-system disabled); `IsStoreCorrupt`; atomic write |
| `sectionaccess.json` | `SectionAccessService` | cache-until-save (no TTL); **fail-closed**; `IsFragmentCorrupt`; legacy appsettings fallback |
| `module-config-{ModuleId}.json` (per module) | `ModuleConfigService` | on-demand; **fail-open** (empty dict) on read, but admin **save refuses** when `IsModuleCorrupt`; legacy `module-config.json` one-time migration; `ConfigSaved` event |
| `protected-principals.json` | `ProtectedPrincipalService` | 30 s TTL cache; **fail-closed**; legacy MailboxPermissions `ExcludedUsers` fallback |
| `ad-editable-attributes.json` | `ADAttributeEditorService` | 30 s TTL cache; **null on corrupt** (distinct signal, not empty); hard-denylist validation stays in service |
| `ad-editable-attributes-legend.json` | `ADAttributeEditorService` | 30 s TTL cache; **fail-open** (empty legend OK) |
| `module-admins.json` | `ModuleAdminService` | on-demand; **silent fail-open** (empty); no corruption probe today |
| `extended-log-level.txt` | `ExtendedLogService` | rewritten on **every startup** (LoadLevel→SetLevel); silent-catch on write failure |

The four distinct cache patterns (on-demand / cache-until-save / 30 s TTL / on-demand)
and the four distinct fail-modes (fail-closed all-disabled / fail-closed empty / fail-open
empty / null-on-corrupt) above are **load-bearing**. The repository layer must reproduce
each service's behavior exactly; this is the single biggest correctness risk in the
migration (see §6 Risks).

### 2b. Stays in `appsettings.json` — bootstrap-only (cannot move)

These are read before any DB connection could open, or are environment identity:

- `Security:AllowedGroups`, `Security:AdminGroups` (authorization policy bootstrap)
- `Application:PathBase` (per-environment URL prefix; promote patches this)
- `Serilog:*` (logging framework init)
- `Delinea:SecretServerUrl`, `Delinea:CredentialTarget` (vault bootstrap)
- **NEW:** the SQLite DB path/connection string itself (see §3b)

### 2c. Stays in `appsettings.json` — dual-source legacy fallbacks (transitional)

Today these appsettings keys act as a **fallback** when a `config/` fragment or module
config is absent. They must keep working during the transition and be **removed only after**
the corresponding store is DB-backed and the importer has run everywhere:

- `Security:SectionAccess` ← fallback for `sectionaccess.json`
- `Security:ExcludedUsers` ← fallback for MailboxPermissions module config (read by
  `PermissionValidator` and `ProtectedPrincipalService`)
- `Security:ProtectedPrincipalDirectoryReadSecretId` ← fallback for ProtectedPrincipals
  module config
- `ExchangeOnline:AppId|Organization|CertificateSubject` ← fallback for
  `module-config-ExchangeOnline.json` (read by `ExoConnectionPool`, `ModuleEnablementService`)
- `Migration:*` (HybridEndpoint, CloudTargetDeliveryDomain, OnPremTargetDeliveryDomain,
  ExcludedADGroups, OnPremTargetDatabases, CloudQuotaGB) ← fallback for Migration module
  config (`MigrationService`, `MigrationTargetDatabaseSelector` — note the latter parses
  both array and CSV forms; preserve)
- `OnPremExchange:ServerUri` ← read by ~six services; **no module-config alternative
  today**, so this one likely **stays in appsettings** even post-migration (treat as
  bootstrap-ish, not a fragment).

### 2d. Out of scope / stays as files

- JSONL audit + trace logs; EmergencyDisable snapshots; HeaderAnalysis temp `.msg` files.
- `deployment-manifest.json` — **critic finding:** written by deploy scripts, **never read
  by C#**. It is a deploy artifact in the publish path, not runtime config. Leave it alone.
- `Email:*`, `ServiceNow:*`, `Audit:*`, `ExtendedLog:*` appsettings keys (Phase F, §9).

---

## 3. Target design

### 3a. Data-access approach (decision point — recommendation)

The repo has **no** existing data-access stack (verified: no EF Core, no
`Microsoft.Data.Sqlite`, no Dapper). Options:

- **(Recommended) `Microsoft.Data.Sqlite` + a thin hand-written repository per store,
  plus a tiny versioned migration runner keyed on `PRAGMA user_version`.** The data is
  almost entirely key/value and small list tables; there is no ORM-shaped domain model to
  gain from. This keeps the dependency surface minimal (one package), the SQL explicit and
  auditable (matches the Constitution's bias toward inspectable behavior), and schema
  evolution is a numbered list of idempotent `CREATE`/`ALTER` steps we control.
- (Alternative) **EF Core 10 + `Microsoft.EntityFrameworkCore.Sqlite` with EF migrations.**
  Nicer if the model grows (e.g. if module *packaging* later wants richer schema), gives
  `dotnet ef` migration tooling. Cost: heavier dependency, migration files are generated
  artifacts that need their own review discipline, and LINQ hides the exact SQL.

**Recommendation: start with `Microsoft.Data.Sqlite` + thin repos.** Revisit EF only if the
module-packaging plan needs relational depth. Either way, enable **WAL mode** and a busy
timeout; the app is single-writer (one pool) with a few concurrent admin readers, which is
SQLite's ideal envelope.

### 3b. Database location and identity

- **Outside the deploy target**, per the decision. Proposed:
  `<Audit:LogRoot>\ExchangeAdminWeb\data\exchangeadmin.db` (i.e. under `E:\WWWOutput\...`,
  the same volume already used for logs/backups and already ACL-granted to the pool
  identity). One DB per environment — dev and prod each have their own, exactly as they
  have separate `config/` today.
- A **new bootstrap key** `ConfigStore:DatabasePath` (or a connection string) in
  `appsettings.json`, defaulted from `Audit:LogRoot` when absent so existing installs need
  no manual edit.
- Install/deploy scripts grant the pool identity `(M)` on the `data\` directory (replacing
  the retired `config\` grant). The directory, being outside the publish path, is
  structurally immune to robocopy.

### 3c. Schema sketch (illustrative — finalized at implementation)

```
PRAGMA user_version  -- migration version, drives the runner

module_enablement(module_id TEXT PRIMARY KEY, enabled INTEGER NOT NULL, updated_at TEXT)
module_config(module_id TEXT, config_key TEXT, config_value TEXT,
              PRIMARY KEY(module_id, config_key), updated_at TEXT)
section_access(policy_alias TEXT, group_value TEXT,           -- row-per-group
              PRIMARY KEY(policy_alias, group_value))
module_admins(module_id TEXT, admin_group TEXT,              -- row-per-group
              PRIMARY KEY(module_id, admin_group))
protected_principal(kind TEXT, value TEXT,                   -- kind ∈ user|group|ou|sam_pattern
              PRIMARY KEY(kind, value))
editable_attribute(name TEXT PRIMARY KEY, label TEXT, type TEXT, choices_json TEXT,
              required INTEGER, allow_clear INTEGER, max_length INTEGER, pattern TEXT, level INTEGER)
attribute_legend(attribute_name TEXT, choice_value TEXT, description TEXT, note TEXT, source TEXT,
              PRIMARY KEY(attribute_name, choice_value))
app_setting(key TEXT PRIMARY KEY, value TEXT)               -- single-row settings e.g. extended_log_level
schema_meta(key TEXT PRIMARY KEY, value TEXT)               -- importer-done markers, provenance
```

Row-per-group for `section_access`/`module_admins` (vs. a JSON blob column) is preferred so
a corrupt single value can never take down the whole alias — directly addressing the
incident's "one bad fragment fails the whole store" property. **Corruption now means a
failed transaction or a constraint violation, not an unparseable file**, so the
`IsStoreCorrupt`/`IsFragmentCorrupt`/`IsModuleCorrupt` probes become DB-integrity checks
(can the table be opened and read?) rather than JSON parse attempts. The admin-page
refuse-to-save guards (incident fix #3) **stay**, retargeted to the new probes.

### 3d. Startup self-registration (the relaxation)

On startup, after the migration runner ensures the schema, a seeding step performs
**non-destructive** `INSERT ... ON CONFLICT DO NOTHING` for every catalog module missing a
`module_enablement` row (seeded to the descriptor's `EnabledByDefault`) and any new
settings with defaults. This is the behavior the owner asked for and it explicitly **does
not** overwrite existing rows — the destructive startup write that caused the incident
(flipping ExchangeOnline to false) remains forbidden. **If approved, this plan updates
decision 2026-06-12 in `.agents/decisions.md`** to record that non-destructive seeding is
permitted while destructive startup writes stay banned, and updates the relevant
`ModuleEnablementService` tests (the current "startup writes nothing" tests become "startup
performs no *destructive* write; seeding inserts only missing rows").

---

## 4. Migration / cutover strategy

**One-time import, then DB is authoritative. No long-lived file↔DB dual-sourcing** (the
critic's strongest warning: a half-migrated state where some reads hit files and some hit
the DB produces inconsistent runtime state).

1. **First startup with the feature present**, for each in-scope store: if the DB table is
   empty *and* a legacy `config/` file exists, import the file's contents into the table
   inside one transaction, then record an importer-done marker in `schema_meta` and **rename
   the legacy file aside** (`*.imported-<timestamp>`) so it can never be re-read or
   re-imported. This preserves dev's hand-recovered post-incident state and prod's current
   state without operator action.
2. The legacy `module-config.json → per-module` migration that `ModuleConfigService` does
   today folds into this importer (read legacy, insert rows, archive).
3. The **appsettings legacy fallbacks** (§2c) remain readable during and after import; they
   are only consulted when both the DB row and the (now archived) file are absent. They are
   retired in a later cleanup once every environment is confirmed imported.
4. **Rollback**: because the importer archives rather than deletes, reverting the app build
   restores file-based behavior (the archived files can be renamed back). The plan keeps a
   documented manual rollback recipe until the first prod cycle proves the importer.

This makes the cutover safe on the owner's machine: deploy the build, it imports on first
run, the app is DB-backed, and the old files sit archived next to the DB as a safety net.

---

## 5. Phased work breakdown

Each phase is independently committable; tests land with the code that needs them (repo
rule). Phases A–C are the core; D–E make it operable; F is optional/future.

### Phase A — Infrastructure
- Add `Microsoft.Data.Sqlite`; connection factory (WAL, busy timeout) registered in DI.
- Versioned migration runner (`PRAGMA user_version`) that creates the schema idempotently.
- DB path bootstrap key + default-from-LogRoot; create `data\` dir; **no** behavior change
  to any service yet.
- **AC:** fresh app start creates the DB and schema; second start is a no-op; unit tests
  for the migration runner (v0→vN idempotent, re-runnable).
- **Version:** base app bump (shared infrastructure).

### Phase B — Repositories + service cutover (the bulk)
Per store, in this suggested order (lowest blast radius first):
1. `extended-log-level.txt` → `app_setting` (also kills the every-startup file rewrite that
   forced the deploy drift-check exclusion — see the standing `ExtendedLogService` cleanup
   note in `.agents/state.md`).
2. `module-admins.json` → `module_admins` (simplest; silent fail-open).
3. `module-config-*` → `module_config` (+ fold legacy `module-config.json` import; keep
   `ConfigSaved` event and `IsModuleCorrupt` semantics).
4. `modules-enabled.json` → `module_enablement` (fail-closed; `IsStoreCorrupt`).
5. `sectionaccess.json` → `section_access` (fail-closed; cache-until-save; keep appsettings
   fallback; `IsFragmentCorrupt`).
6. `protected-principals.json` → `protected_principal` (30 s TTL; fail-closed; keep
   MailboxPermissions/appsettings fallback).
7. `ad-editable-attributes(.legend).json` → `editable_attribute` / `attribute_legend`
   (30 s TTL; **null-on-corrupt** for allowlist, fail-open for legend; denylist validation
   stays in the service).
- Each store: repository + service rewrite + its importer step + rewritten service tests
  proving the *preserved* cache/fail-mode semantics (revert-the-fix check per repo rule).
- **AC per store:** behavior parity with the file version, including the exact fail-mode and
  cache TTL; admin-page corrupt-guard still refuses to save when the DB probe trips.
- **Version:** base app bump; touched modules get their `Version` bumps per the two-rule
  versioning invariant.

### Phase C — Startup self-registration + decision update
- Idempotent seed of missing `module_enablement` rows and default settings (§3d).
- Update `.agents/decisions.md` (relaxation) and the affected startup tests.
- **AC:** adding a new module to the catalog and starting the app creates its row at
  `EnabledByDefault` and writes nothing else; existing rows are untouched; a corrupt/locked
  DB still fails closed.

### Phase D — Ops scripts
- `deploy.ps1`: **retire** robocopy `/XD config`, the pre-deploy config snapshot, the
  post-deploy drift check, and the `config\` ACL grant (replace with a `data\` grant);
  **keep** appsettings backup; **rewrite** config-reconciliation warnings to query the DB
  (e.g. "section_access empty for fail-closed alias X"); **add** a lightweight post-deploy
  DB health check (DB opens, schema at expected version, tables readable).
- `tools/promote-dev-to-prod.ps1`: **rewrite** the dev-wins fragment merge into a
  table-level dev-wins DB merge; **keep** appsettings `PathBase` patching and the
  backup/rollback (extend the snapshot to include a `VACUUM INTO` copy of the prod DB).
- `tools/Install-ExchangeAdminWeb.ps1`: **rewrite** JSON seeding into DB seeding (or rely on
  the app's startup self-registration and seed only what must exist pre-first-run);
  **retire** the `config\` exclusion/ACL, **add** `data\` ACL.
- `tools/deploy-pipeline.ps1`: structure unchanged; `-PlanOnly` now narrates DB operations.
- **NEW tool: `tools/copy-config.ps1`** — the owner's prod↔dev request. Two directions:
  - `-Promote` (dev→prod): table-level **dev-wins** merge (the promote semantics, but on
    the DB), atomic, backup-first.
  - `-Refresh` (prod→dev): full copy of prod's config DB into dev (for reproducing prod
    state in dev), backup-first, never touches appsettings/PathBase.
  - Implemented via `VACUUM INTO` snapshots + an attach-and-merge script; `-PlanOnly`
    supported per the ops-script invariant.
- **AC:** Pester coverage for each script's new invariants; `-PlanOnly` paths print, don't
  act; a dev→prod promote followed by prod read shows dev's values; refresh is
  non-destructive to prod.

### Phase E — Tests + docs sweep
- **Rewrite** service tests against SQLite (`ModuleEnablementServiceTests`,
  `SectionAccessServiceTests`, `ModuleConfigServiceTests`, `ProtectedPrincipalServiceTests`,
  `ADAttributeEditorServiceTests`, `GroupAuthorizationHandlerTests` helpers, plus the
  per-module service tests that call `SaveModuleConfig`:
  `MigrationTargetDatabaseSelectorTests`, `LicensingUpdatesServiceTests`,
  `TestAccountPoolServiceTests`, `EmergencyDisableServiceTests`).
- **Keep unchanged** (storage-agnostic, in-memory contracts): `ModuleCatalogTests`,
  `PageAuthorizationRecheckTests`, `ConferenceRoomConfigPreflightTests`.
- **Rewrite** `tests/ps/DeployInvariants.Tests.ps1`: drop the now-obsolete file-based
  invariants (robocopy `/XD config`, config backup-before-mirror, drift check) and add the
  DB-based ones (DB outside deploy target, health check present, copy-config dev-wins).
  Keep the appsettings non-rewrite and dev-default/fresh-install-consent invariants.
- **Docs:** update `ProjectConstitution.md` (the "deploys never overwrite runtime config"
  invariant now reads "runtime config lives in the out-of-target DB"; config-promotion
  dev-wins now describes the DB merge; backup expectations; the no-startup-writes rule
  amended for non-destructive seeding), `AGENTS.md` (Architectural Invariants 2 & 3),
  `AdminModuleSpec.md` + `AdminModuleDeveloperGuide.md` (module config & section access are
  DB-backed; new-module authors get a row, not a file), `.agents/state.md`,
  `.agents/decisions.md`, and the relevant `README.md` config/deploy sections.
- **AC:** full local verification green at the new version (xUnit + Pester); the
  revert-the-fix proof for at least the fail-closed enablement and section-access tests.

### Phase F — (Optional / future) runtime-editable appsettings
Move `Email:*`, `ServiceNow:*`, `ExtendedLog:*`, `Audit:*` (and possibly the
`Security:PreventSelfGrant` flag) into `app_setting`, refactoring `EmailService` and
`ServiceNowService` off construction-time caching so edits take effect without a restart.
Separate scope; only if the owner wants live-editable app settings. Audit/trace JSONL
relocation is its own deferred decision (§9).

---

## 6. Risks and mitigations

1. **Behavior drift in cache/fail-mode semantics.** Four cache patterns and four fail-modes
   must survive verbatim. *Mitigation:* per-store parity tests written before the cutover;
   each test names the exact mode it pins; revert-the-fix check on the fail-closed ones.
2. **Partial migration / dual-source inconsistency** (critic's top warning). *Mitigation:*
   import-once-then-archive; never read a file and the DB for the same store; an
   importer-done marker in `schema_meta` is the single source of truth for "this store is
   DB-backed now."
3. **Losing dev's hand-recovered post-incident state during import.** *Mitigation:* importer
   reads existing files first and archives (not deletes); documented manual rollback;
   prove on dev before prod.
4. **SQLite file locking under IIS app-pool recycles / overlapped shutdown.** *Mitigation:*
   WAL + busy timeout; the single-writer model; health check on startup; the DB lives on
   the same already-proven `E:` volume as logs.
5. **`MigrationTargetDatabaseSelector` static parse chain** (module config → appsettings
   array → appsettings CSV). *Mitigation:* preserve the full fallback chain; add a test for
   each form before refactor.
6. **Backup/restore story changes shape.** ConfigBackups currently holds appsettings + a
   `config/` snapshot. *Mitigation:* keep appsettings backups; replace the `config/`
   snapshot with a `VACUUM INTO` DB snapshot in deploy and copy-config; document restore.
7. **Module packaging plan coupling.** *Mitigation:* the `module_*` tables and startup
   seeding are designed as the seam the packaging plan will use; we don't pre-build it, but
   we don't paint it into a corner either.

---

## 7. What gets retired (incident hardening made obsolete-by-design)

Per the decision's requirement to enumerate this explicitly. Once Phases B–D land and the
importer has run everywhere:

- robocopy `/XD config` exclusion in `deploy.ps1`, `promote-dev-to-prod.ps1`,
  `Install-ExchangeAdminWeb.ps1` (incident fix history / commit 0021502 regression anchor).
- `config/`-directory backup before deploy (incident fix #4) — **kept for appsettings only**.
- pre-deploy config snapshot + post-deploy drift check (incident fix #5), including the
  `extended-log-level.txt` drift exclusion.
- `config\` folder ACL grant (replaced by `data\` grant).
- The corresponding Pester invariants in `DeployInvariants.Tests.ps1`.

**Explicitly kept:** the admin-page refuse-to-save-on-corrupt guards (incident fix #3,
retargeted to DB probes); deploy.ps1 dev-default + fresh-install-consent (incident fix #6);
appsettings non-rewrite + appsettings backups; the no-*destructive*-startup-write rule.

---

## 8. Acceptance criteria (whole work-stream)

- All in-scope `config/` stores are DB-backed; on a clean upgrade the importer moves
  existing state into the DB and archives the files, with no operator action and no data
  loss (proven on dev first).
- Every preserved cache pattern and fail-mode has a passing parity test; fail-closed paths
  pass the revert-the-fix proof.
- `tools/copy-config.ps1` supports dev→prod (dev-wins merge) and prod→dev (full refresh),
  both with `-PlanOnly` and backup-first.
- Deploy no longer references `config/` for runtime state; the retired invariants are gone
  and replaced by a DB health check; Pester reflects the new invariants.
- Startup self-registers missing module rows non-destructively and writes nothing else;
  decision 2026-06-12 is updated to record the relaxation.
- Docs (Constitution, AGENTS, module spec/guide, state, decisions, README) match the new
  reality; no doc still claims runtime config lives in files.
- Full local verification green (xUnit + Pester) at the bumped app version.

---

## 9. Deferred decisions (need an owner call, not blocking Phase A–E)

- **Audit + operation-trace JSONL → SQLite?** Big upside (queryable audit, config *change
  history* — this incident's forensics would have been a `SELECT`), but high volume and a
  different write pattern (append + rotate). Recommend a separate plan after the config
  migration proves the approach.
- **Phase F** (runtime-editable appsettings, singleton refactor): do it or leave Email/
  ServiceNow/Audit in appsettings?
- **`OnPremExchange:ServerUri`**: confirm it stays in appsettings (no module-config home
  today; read by ~six services). Recommended: stays.
- **Data-access library**: confirm `Microsoft.Data.Sqlite` + thin repos vs EF Core (§3a).

---

## 10. Review log

- 2026-06-15, round 1 (Draft): Plan created from the six-dimension touchpoint audit + critic.
  Decision 2026-06-12 (SQLite over SQL Express) is the parent. Open questions consolidated
  in §9 for Michael. No implementation until Status flips to Approved. Recommendations
  carried into the plan: Microsoft.Data.Sqlite + thin repos; DB under `E:\WWWOutput\...\data`;
  import-once-then-archive cutover; audit JSONL and runtime-editable appsettings deferred.
