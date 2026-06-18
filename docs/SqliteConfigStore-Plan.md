# SQLite Config Store Migration — Plan

Status: In progress (owner go 2026-06-18; implementation started at app 2.3.11)
App version at drafting: 2.3.8 (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`)
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. Implements
decision **2026-06-12 — Runtime config and operational data move to SQLite**
(`.agents/decisions.md`).

The three Phase-A-blocking open questions in §9 are **resolved** (owner decisions
2026-06-18, recorded in `.agents/decisions.md`):
- **DB location (§3b): Option A** — SQLite file lives in `config/`, one DB per environment;
  no shared dev/prod DB. §7 retirement list resolves to the Option-A column.
- **Data-access (§3a): `Microsoft.Data.Sqlite` + thin repositories.** No Entity Framework.
- **Cache invalidation (§5B.2): the `schema_meta` change-token**, not accept-the-staleness.

Implementation cadence (owner direction 2026-06-18): phases are implemented autonomously,
one commit per phase; each commit is reviewed by `codex review --commit <sha>` and findings
fixed before moving on, rather than per-phase human sign-off.

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
SQLite's ideal envelope. **Critical lifetime constraint (see §5B.1): do not register a
single shared `SqliteConnection`/`DbContext` as a Singleton** — config consumers are a mix
of Singleton, Scoped, and a HostedService, so use a connection **factory** (open-per-op) or
scoped connection behind the `IConfigStore` abstraction (§5A).

### 3b. Database location and identity

**CORRECTION (owner, 2026-06-15): the DB must NOT live under `Audit:LogRoot`.** That tree
holds log files and is **regularly pruned** to keep it from growing without bound — it is
not a home for vital configuration. An earlier draft proposed it; that was wrong. Vital
config must live somewhere **persistent (never pruned), surviving deploys, ACL'd to the
pool identity**. Two viable homes:

- **Option A — keep it where config already lives: `<PublishPath>\config\exchangeadmin.db`.**
  That directory is already the persistent, non-pruned home for exactly this data, already
  ACL-granted, and already protected from deploys by the existing robocopy `/XD config`
  exclusion. The DB is just one more file in it. Smallest change. **Cost:** we *keep* the
  `/XD config` exclusion rather than retiring it (it already works), so §7's "retire the
  robocopy config exclusion" item is withdrawn under this option.
- **Option B — a dedicated data dir outside the publish path**, e.g.
  `C:\ProgramData\ExchangeAdminWeb\<env>\exchangeadmin.db`. Fully decouples config from the
  deploy target; needs a new ACL grant and a new managed path, but lets the `config\`
  directory (and its robocopy exclusion) eventually go away entirely.

**Recommendation: Option A** — the data already lives safely in `config\`; the DB should
too. No relocation, no new path, no new appsettings knob. The path is **derived** from
`PublishPath`/content root, not hardcoded and not a new setting. One DB per environment
(dev and prod each have their own, exactly as they have separate `config\` today). **Open
question for Michael in §9.**

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
- Add `Microsoft.Data.Sqlite`; a connection **factory** (WAL, busy timeout) — **not** a
  shared Singleton connection (§5B.1).
- Introduce the internal **`IConfigStore`** abstraction (§5A): open connection, run in a
  transaction, typed get/set. `ModuleConfigService` and the six cross-cutting services will
  all sit on it so connection/transaction/collation handling lives in **one** place.
- Versioned migration runner (`PRAGMA user_version`) that creates the schema idempotently,
  with `COLLATE NOCASE` on every text key column (§5B.3).
- DB path **derived** from the content root / `PublishPath` (Option A, §3b) — no new
  appsettings key, not hardcoded; create the dir if missing; **no** behavior change to any
  service yet.
- Decide the cache-invalidation model (§5B.2): recommend a `schema_meta` change-token that
  readers check, replacing per-instance TTL drift.
- **AC:** fresh app start creates the DB and schema; second start is a no-op; mixed-case key
  round-trips (NOCASE) pass; unit tests for the migration runner (v0→vN idempotent,
  re-runnable) and for the factory under concurrent scoped + hosted access.
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

> **Phase B promotion debt (running list — must all be resolved here).** Each Phase B store
> cutover moves a config file's data into the DB *before* the promote/deploy scripts learn to
> carry the DB. During Phase B this is an accepted, bounded gap (the file-copy in
> `promote-dev-to-prod.ps1` safely *skips* a now-missing source — `Copy-FileChecked` warns,
> does not throw — so nothing breaks; the setting simply does not promote until rewired here).
> Stores cut over so far whose promotion/deploy handling Phase D must replace with DB-aware
> logic:
> - **extended-log-level.txt** (B.1): `promote-dev-to-prod.ps1` line ~502 still copies the
>   (now archived) file; replace with the `app_setting` row merge. Also retire the
>   `deploy.ps1` `extended-log-level.txt` drift exclusion (the every-startup file rewrite is
>   gone).
> - **module-admins.json** (B.2): not in the promote fragment list; no debt.
> - **module-config-*.json** (B.3): `promote-dev-to-prod.ps1` `Merge-JsonConfig` loop over
>   `module-config-*.json` (and the older single `module-config.json`) now merges files that
>   no longer exist post-cutover; replace with a `module_config` table merge (dev-wins per
>   module). After cutover those files are archived, so the current loop silently no-ops them.
> - **modules-enabled.json** (B.4): in the `$jsonConfigFiles` merge list; post-cutover the
>   file is gone so the merge no-ops. Replace with a `module_enablement` table merge. Note the
>   importer leaves an *unparseable* legacy file in place (does not archive it), matching the
>   "don't silently discard corrupt config" rule.
> - **sectionaccess.json** (B.5): in the `$jsonConfigFiles` merge list; post-cutover the file
>   is gone so the merge no-ops. Replace with a `section_access` table merge (dev-wins),
>   preserving the presence-marker distinction (configured-empty must promote as deny-all, not
>   as "unconfigured"). Unparseable legacy file left in place + fail-closed, like B.4.
> - **protected-principals.json** (B.6): in the `$jsonConfigFiles` merge list; post-cutover the
>   file is gone so the merge no-ops. Replace with a `protected_principal` table merge (dev-wins,
>   all four kinds), preserving the presence marker (configured-empty vs never). Unparseable
>   legacy file left in place + fail-closed, like B.4/B.5.

**Correction (owner, 2026-06-15): no new copy tool.** An earlier draft invented
`tools/copy-config.ps1` parallel to machinery that already exists. The deploy pipeline
already does install and dev→prod copy; the migration only swaps files→DB underneath it.
The single genuinely new capability is the **prod→dev** direction ("vice-versa"), which is
a **flag on the existing promote script**, not a new file.

- `deploy.ps1`: **keep** appsettings backup; **rewrite** config-reconciliation warnings to
  query the DB (e.g. "section_access empty for fail-closed alias X"); **add** a lightweight
  post-deploy DB health check (DB opens, schema at expected version, `PRAGMA integrity_check`
  passes). Under DB Option A (§3b) the `config\` robocopy exclusion, ACL grant, pre-deploy
  config snapshot, and post-deploy drift check **stay** (config/ still exists, now holding
  the DB) — the snapshot/backup just covers the DB file via online backup, not a raw copy.
  Under Option B those would be retired and replaced with a `data\` ACL. (Which set of
  retirements applies depends on the §3b decision — that is why §7 is now conditional.)
- `tools/promote-dev-to-prod.ps1`: **rewrite** the dev-wins fragment merge into a
  table-level dev-wins DB merge (dev→prod, the existing direction); **keep** appsettings
  `PathBase` patching and the backup/rollback (extend the pre-promotion snapshot to a
  `VACUUM INTO` consistent copy of prod's DB, integrity-checked). **Add a `-Refresh`
  (prod→dev) switch** — the owner's "vice-versa": copy prod's config DB into dev to
  reproduce prod state, backup-first, never touches appsettings/PathBase. Promotion must
  **not** run against a live DB — preserve the existing pool-stop (§5B.4).
- `tools/Install-ExchangeAdminWeb.ps1`: **rewrite** JSON seeding into DB seeding (or rely on
  the app's startup self-registration and seed only what must exist pre-first-run). Config
  ACL/exclusion follows the §3b decision.
- `tools/deploy-pipeline.ps1`: structure unchanged; `-PlanOnly` now narrates DB operations.
- **Operator repair path (§5B.7):** ship documented `sqlite3` recipes (or a tiny CLI) so an
  operator can inspect/repair the DB in an incident — the JSON world's one real virtue was
  "open it in Notepad," and that must not be silently lost.
- **AC:** Pester coverage for each script's new invariants; `-PlanOnly` paths print, don't
  act; a dev→prod promote followed by prod read shows dev's values; `-Refresh` is
  non-destructive to prod; the post-deploy DB health check fails loud on a bad/locked DB.

### Phase E — Tests + docs sweep
- **Rewrite** service tests against SQLite (`ModuleEnablementServiceTests`,
  `SectionAccessServiceTests`, `ModuleConfigServiceTests`, `ProtectedPrincipalServiceTests`,
  `ADAttributeEditorServiceTests`, `GroupAuthorizationHandlerTests` helpers, plus the
  per-module service tests that call `SaveModuleConfig`:
  `MigrationTargetDatabaseSelectorTests`, `LicensingUpdatesServiceTests`,
  `EmergencyDisableServiceTests`). (`TestAccountPoolServiceTests` is **not** in this list —
  that module and its tests were removed in app 2.3.10; see `.agents/decisions.md`.)
- **Keep unchanged** (storage-agnostic, in-memory contracts): `ModuleCatalogTests`,
  `PageAuthorizationRecheckTests`, `ConferenceRoomConfigPreflightTests`.
- **Rewrite** `tests/ps/DeployInvariants.Tests.ps1`: per the §3b/§7 decision, retarget the
  config-protection invariants to the DB (DB present + integrity-checked under Option A; or
  the larger retirement under Option B) and add the promote `-Refresh` (prod→dev)
  non-destructive invariant. Keep the appsettings non-rewrite and
  dev-default/fresh-install-consent invariants.
- **Docs (config-swap-specific edits):** update `ProjectConstitution.md` (the "deploys never
  overwrite runtime config" invariant now reads "runtime config lives in the SQLite store";
  config-promotion dev-wins now describes the DB merge; backup expectations; the
  no-startup-writes rule amended for non-destructive seeding), `AGENTS.md` (Architectural
  Invariants 2 & 3), `AdminModuleSpec.md` version header + the module-config/section-access
  sections (DB-backed; a new module gets a row, not a file), `.agents/state.md`,
  `.agents/decisions.md`, and the relevant `README.md` config/deploy sections.
- **AC:** full local verification green at the new version (xUnit + Pester); the
  revert-the-fix proof for at least the fail-closed enablement and section-access tests.

### Phase E2 — Full module developer guide audit & rewrite (after the config swap lands)
The config migration is **not** the only thing that has drifted away from
`docs/AdminModuleDeveloperGuide.md` (and the `AdminModuleSpec.md` it leans on). Patching
only the config bits would leave a guide that is wrong in other places — and the owner is
about to **farm out new-module development** against it, so it must stand alone for a
developer with no chat context. Do a **full audit and rewrite once the config swap is
complete** (so the guide describes the final DB-backed world, not a moving target), not a
piecemeal patch.

Scope of the audit (verify every claim against current code, not memory — this is a
`drift` pass per AGENTS.md, settled by reading files):
- **FailClosed permissions** — the guide predates the `MainPermission`/`GranularPermissions`
  `FailClosed` model (commits through `f7df81a`); confirm it documents that mutating-module
  permissions are fail-closed and what that means for a new module's section access.
- **Enablement semantics** — no startup writes (2.3.7), DependsOn cascade, EnabledByDefault,
  system modules; and post-swap, startup self-registration (a new module's row is seeded).
- **Config & section access are DB-backed** — new authors get a row via `ModuleConfigService`
  / the new `IConfigStore`, not a `module-config-*.json` they hand-author; update or retire
  the `config/module-config-ConferenceRooms.example.json` sample referenced by the guide.
- **Module descriptor surface** — `ModuleCatalog` fields actually in use today (Version,
  IsConfigOnly, IsSystemModule, DependsOn, ConfigFields, policy aliases, icon CSS), checked
  against the live catalog.
- **The two-rule versioning model** (base app vs per-module `Version`) and when each fires.
- **Authorization wiring** — the page `[Authorize(Policy=...)]` / dynamic-alias pattern and
  the `PageAuthorizationRecheckTests` contract a new page must satisfy.
- **Testing expectations** — new Services require xUnit coverage; what a module's test
  baseline looks like now.
- **Packaging/validation** — `tools/validate-module-package.ps1` exists and gates contributed
  modules; the guide should point at it. (Note: this overlaps the separate
  `docs/ModulePackaging-Plan.md` work — coordinate so the guide and that plan agree on the
  authoring→validate→install flow rather than contradicting each other.)
- Cross-check the `AdminModuleSpec.md` version header against the csproj version and fix
  drift (AGENTS.md authority-order item 6).
- **AC:** a developer outside this codebase can build a new module end-to-end (descriptor →
  page → auth → config row → tests → validate) from the guide alone, and every command/
  path/field in it resolves against the then-current repo. Tracked separately as the
  queued "module developer guide review" item in `.agents/state.md`; this plan records that
  it must become a **full rewrite gated on the config swap**, not just a config touch-up.

### Phase F — (Optional / future) runtime-editable appsettings
Move `Email:*`, `ServiceNow:*`, `ExtendedLog:*`, `Audit:*` (and possibly the
`Security:PreventSelfGrant` flag) into `app_setting`, refactoring `EmailService` and
`ServiceNowService` off construction-time caching so edits take effect without a restart.
Separate scope; only if the owner wants live-editable app settings. Audit/trace JSONL
relocation is its own deferred decision (§9).

---

## 5A. Consumer architecture — how config is read/written today (call graph)

The original touchpoint audit produced a flat *file* inventory but did not map how
consumers relate to the stores. That structure is what determines how large the migration
actually is, so it is recorded here from a call-site grep (verified, not inferred).

**Per-module config is already funnelled through one shared service.** Every feature
module reads/writes its config exclusively via `ModuleConfigService`
(`GetValue(moduleId,key)`, `GetModuleConfig(moduleId)`, `SaveModuleConfig(moduleId,dict)`):
ConferenceRooms, Comms10k, Migration, MfaReset, M365GroupManagement, NamedLocations,
GroupManagement, LicensingUpdates, DhcpAuthorization, ExoConnectionPool,
ModuleCredentialService, PermissionValidator, MigrationTargetDatabaseSelector, etc. **None
of them touch a file directly.** Swapping `ModuleConfigService`'s internals moves them all
at once with zero per-module work.

**The six cross-cutting stores each hand-roll their own I/O** (duplicated
temp-file+`File.Replace`+`JsonSerializer`, not a shared helper): `ModuleEnablementService`,
`SectionAccessService`, `ProtectedPrincipalService`, `ADAttributeEditorService`,
`ModuleAdminService`, `ExtendedLogService`.

**Implication:** the migration is **1 shared swap + 6 dedicated swaps, not 23**. It also
means this is the right moment to put the six behind a single small store/connection
abstraction so reads/writes happen in *one* place rather than seven — the same duplication
is precisely why the incident's atomic-write/corrupt-file bug had to be fixed in multiple
services instead of one. **Design note for Phase A:** introduce one internal
`IConfigStore` (open connection, transaction, typed get/set) that `ModuleConfigService` and
the six services all sit on; do not let each repository re-implement connection/transaction
handling.

## 5B. Non-obvious hazards (the dimension the file audit missed)

These are behavior/relationship hazards a file listing cannot surface. Each is grounded in
current code and must be addressed by the implementation.

1. **Service lifetime vs. a shared connection.** DI registrations are **mixed**: most config
   services are `Singleton` (`ModuleConfigService`, `ModuleEnablementService`,
   `SectionAccessService`, `ProtectedPrincipalService`, `ModuleAdminService`,
   `ExtendedLogService`) while several consumers that do config I/O are **`Scoped`** — e.g.
   `ADAttributeEditorService` (Scoped; writes its own allowlist store) and the Scoped
   feature services that read module config per request (`GroupManagementService`,
   `MigrationService`, etc.). *Hazard:* a SQLite connection or `DbContext` is **not safe to
   register as a single shared Singleton across Singleton + Scoped consumers.** *Mitigation:* use a connection **factory**
   (open-per-operation, short-lived) or a scoped connection — never one long-lived shared
   `SqliteConnection`. This is an explicit Phase A design constraint, not an afterthought.
   (Note: the app's former *only* `AddHostedService` — `TestAccountPoolCleanupWorker`, which
   read config from a background timer — was removed in app 2.3.10 with the TestAccountPool
   module. That removed the background-thread variant of this hazard; the Singleton-vs-Scoped
   constraint above still stands on its own and the factory model handles both, so this
   section needs no rework.)

2. **In-process cache invalidation is per-instance and will silently drift.**
   `ProtectedPrincipalService` and `ADAttributeEditorService` cache with a 30 s TTL and
   invalidate **their own** in-memory field on save. Today that is fine because each is one
   object writing through itself. With a shared DB, a write through a **different** path
   (the new prod→dev refresh tool, a future second writer, or a manual DB edit) will not
   invalidate these caches — stale config can persist up to 30 s, and `SectionAccessService`
   caches **until save with no TTL at all**, so an out-of-band write is invisible until a
   pool restart. *Mitigation:* the plan must state the invalidation model explicitly —
   either keep caches and accept the 30 s/restart staleness window (document it), or add a
   cheap DB change-token (e.g. a `schema_meta` version bumped on every write that readers
   check). Recommend the change-token; it also makes the corrupt-store probes cheap.

3. **Case-insensitive keys must become a DB collation, or matches break.** Module IDs and
   config keys are compared with `StringComparer.OrdinalIgnoreCase` throughout
   (`ModuleConfigService`, `ModuleEnablementService`; 140 `OrdinalIgnoreCase` uses across the
   service layer). SQLite text PRIMARY KEYs are **case-sensitive by default** (`BINARY`
   collation). *Hazard:* `"exchangeonline"` and `"ExchangeOnline"` would become two rows,
   silently breaking enablement/cascade lookups that today treat them as one. *Mitigation:*
   declare `COLLATE NOCASE` on every text key column that backs an ID/alias/key, and add a
   test that inserts mixed-case and reads back via the other casing.

4. **Concurrent-writer model changes shape.** Today each service serializes its own writes
   with an in-process `_writeLock`/`lock`. That lock is **per-store and in-process only**.
   With one DB file and a write tool (`promote`/`refresh`) potentially running **while the
   app is up**, the locking story is now cross-process. *Mitigation:* WAL + busy-timeout
   covers app-internal concurrency; the ops tools must take the app offline or use a proper
   transaction, and the plan must say which. (The current promote stops the pool; preserve
   that — do not promote into a live DB.)

5. **Cross-store atomicity that doesn't exist today, now possible — and now an obligation.**
   A few operations write two stores in sequence (e.g. AdminSettings saves enablement *and*
   protected principals; ExchangeOnline config save drains the EXO pool *after* writing).
   Today a crash between them leaves a half-applied state silently. SQLite gives us real
   transactions; the plan should identify which multi-store saves should become **single
   transactions** rather than blindly porting them as two writes. (Don't over-apply — the
   EXO pool drain is a side-effect, not a DB write, and must stay outside the transaction.)

6. **Migration importer fidelity edge cases.** The JSON→row import must preserve quirks the
   file format tolerated: empty-string vs. absent key (some services treat `""` and missing
   differently — e.g. `MaskIfSecret` shows `(empty)`), `null` arrays normalized to empty,
   the `MigrationTargetDatabaseSelector` value that may be a JSON **array or a CSV string**,
   and module-config values that are secrets (must not be logged during import). *Mitigation:*
   the importer is covered by round-trip tests (file → import → read via the new repo equals
   the old file read) per in-scope store, including the empty/null/CSV cases.

7. **External and human writers exist outside the app.** `Install-ExchangeAdminWeb.ps1`
   seeds config files and `promote-dev-to-prod.ps1` merges them; operators have also
   hand-edited JSON during incidents (and the dev recovery copied prod files in by hand).
   After migration, **none of that works** — you cannot hand-edit a row with a text editor,
   and the install/promote scripts must speak SQLite. *Mitigation:* (a) ship a tiny
   read/write CLI or documented `sqlite3` recipes so an operator can inspect/repair the DB
   in an incident (the file format's one genuine virtue was "open it in Notepad"); (b)
   ensure the install/promote rewrites land **before** any environment is DB-only, or an
   operator is stranded.

8. **`appsettings.json.sample` and the ConferenceRooms `.example.json` are documentation
   surfaces.** `appsettings.json.sample` (tracked) documents the config shape for installers;
   `config/module-config-ConferenceRooms.example.json` shows module-config shape. After
   migration these still describe *bootstrap* appsettings (fine) but the module-config
   example becomes misleading (there is no file to copy). *Mitigation:* update/retire the
   `.example.json` and the sample's config-fragment guidance in the Phase E docs sweep
   (the audit's docs dimension did not flag these two sample files specifically).

9. **Backup/restore is now a single point of failure.** Today 20 files mean a corrupt one
   loses one store; the deploy backs up the whole `config/` dir. One DB file means one
   corruption (or one bad `VACUUM`) can lose **everything**. *Mitigation:* the deploy DB
   snapshot must use the online backup API / `VACUUM INTO` (consistent copy, not a raw file
   copy of a live WAL DB), be integrity-checked (`PRAGMA integrity_check`) after creation,
   and retained like the appsettings backups. This raises the bar above the file world, not
   just matches it.

These nine are now first-class plan content; items 1–4 are **Phase A design constraints**
(they shape the connection/cache/collation model before any store moves), 5–6 are **Phase B
per-store obligations**, and 7–9 are **Phase D/E** (ops + docs).

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
   WAL + busy timeout; the single-writer model; startup health check; the DB lives in the
   persistent, non-pruned `config\` dir (§3b Option A), not the pruned log tree.
5. **`MigrationTargetDatabaseSelector` static parse chain** (module config → appsettings
   array → appsettings CSV). *Mitigation:* preserve the full fallback chain; add a test for
   each form before refactor.
6. **Backup/restore story changes shape — now a single point of failure (§5B.9).**
   ConfigBackups currently holds appsettings + a `config/` snapshot. *Mitigation:* keep
   appsettings backups; back up the DB via online backup / `VACUUM INTO` (consistent, not a
   raw copy of a live WAL DB), `PRAGMA integrity_check` after, in both deploy and the
   promote/`-Refresh` paths; document restore.
7. **Module packaging plan coupling.** *Mitigation:* the `module_*` tables and startup
   seeding are designed as the seam the packaging plan will use; we don't pre-build it, but
   we don't paint it into a corner either.

---

## 7. What gets retired — **conditional on the §3b location decision**

The earlier draft assumed the DB would live outside `config\` and therefore retired the
whole `config\` deploy-protection apparatus. With **Option A (DB stays in `config\`,
recommended)** most of that protection is still doing real work — it now guards the DB file
— so the retirement list shrinks. The honest accounting:

**Under Option A (recommended):**
- **Kept** (still protecting `config\`, which now holds the DB): robocopy `/XD config`
  exclusion; the `config\` ACL grant; the pre-deploy snapshot and post-deploy drift check
  (retargeted to verify the DB came through intact — via integrity check, not file diff).
- **Changed:** the `extended-log-level.txt` drift exclusion goes away (that file moves into
  the DB / `app_setting`, so the every-startup rewrite that forced the exclusion is gone —
  this also resolves the standing `ExtendedLogService` cleanup note).
- **Genuinely retired:** the per-fragment corrupt-JSON *parse* guards inside the services
  (replaced by DB integrity/transaction semantics); the JSON-merge logic in promote
  (replaced by table-level merge).

**Under Option B (DB outside the publish path):** additionally retire the `/XD config`
exclusion, the `config\` ACL (replaced by a `data\` grant), and the config-dir snapshot —
the full list the earlier draft claimed. This is the only scenario where that larger
retirement is correct.

**Explicitly kept either way:** the admin-page refuse-to-save-on-corrupt guards (incident
fix #3, retargeted to DB probes); deploy.ps1 dev-default + fresh-install-consent (fix #6);
appsettings non-rewrite + appsettings backups; the no-*destructive*-startup-write rule.

(The earlier draft's flat "retire it all" list was wrong for Option A — it assumed a
location that §3b now rejects. This conditional list supersedes it.)

---

## 8. Acceptance criteria (whole work-stream)

- All in-scope `config/` stores are DB-backed; on a clean upgrade the importer moves
  existing state into the DB and archives the files, with no operator action and no data
  loss (proven on dev first).
- Every preserved cache pattern and fail-mode has a passing parity test; fail-closed paths
  pass the revert-the-fix proof.
- `promote-dev-to-prod.ps1` does dev→prod (dev-wins, existing direction) and the new
  `-Refresh` prod→dev (non-destructive to prod), both with `-PlanOnly` and backup-first; no
  new copy tool is introduced.
- Deploy verifies the DB came through intact (integrity check) rather than diffing config
  files; Pester reflects the new invariants per the §3b decision.
- Startup self-registers missing module rows non-destructively and writes nothing else;
  decision 2026-06-12 is updated to record the relaxation.
- Docs (Constitution, AGENTS, module spec/guide, state, decisions, README) match the new
  reality; no doc still claims runtime config lives in files.
- Full local verification green (xUnit + Pester) at the bumped app version.

---

## 9. Open questions for Michael

**Blocking Phase A (must decide before infrastructure work):**

- **DB location (§3b):** Option A (DB in `config\`, recommended — smallest change, keeps the
  proven deploy protection) vs Option B (dedicated dir outside the publish path). This
  decision drives §7's retirement list and the install/deploy ACL changes.
- **Data-access library (§3a):** `Microsoft.Data.Sqlite` + thin repos (recommended) vs EF
  Core. Affects the `IConfigStore` shape and the migration-runner mechanism.
- **Cache-invalidation model (§5B.2):** accept the documented 30 s/restart staleness window,
  or add the `schema_meta` change-token (recommended). Affects the read path of every store.

**Not blocking Phase A–E (can decide later):**

- **Audit + operation-trace JSONL → SQLite?** Big upside (queryable audit, config *change
  history* — this incident's forensics would have been a `SELECT`), but high volume and a
  different write pattern (append + rotate). Recommend a separate plan after the config
  migration proves the approach.
- **Phase F** (runtime-editable appsettings, singleton refactor): do it or leave Email/
  ServiceNow/Audit in appsettings?
- **`OnPremExchange:ServerUri`**: confirm it stays in appsettings (no module-config home
  today; read by ~six services). Recommended: stays.

---

## 10. Review log

- 2026-06-15, round 1 (Draft): Plan created from the six-dimension touchpoint audit + critic.
  Decision 2026-06-12 (SQLite over SQL Express) is the parent. Recommendations carried in:
  Microsoft.Data.Sqlite + thin repos; import-once-then-archive cutover; audit JSONL and
  runtime-editable appsettings deferred.
- 2026-06-15, round 2 (Draft, owner review): three corrections from Michael, plus the
  dimension the round-1 audit missed.
  (1) **DB location:** round 1 put the DB under `Audit:LogRoot` — wrong, that tree is pruned
  and is not a home for vital config. §3b rewritten: DB lives in the persistent `config\`
  dir (Option A, recommended) or a dedicated dir (Option B); path is derived, not a new
  appsettings key, not hardcoded.
  (2) **No new copy tool:** round 1 invented `copy-config.ps1` parallel to the existing
  pipeline. Removed. Install and dev→prod copy stay in `Install-ExchangeAdminWeb.ps1` /
  `promote-dev-to-prod.ps1` (files→DB underneath); the only new capability, prod→dev, is a
  `-Refresh` flag on the promote script.
  (3) **§7 retirement list was wrong for Option A** (it assumed the rejected location);
  now conditional on §3b.
  (4) **Missing dimension — consumer call graph (§5A) + non-obvious hazards (§5B):** the
  round-1 audit produced a flat file list and never mapped how consumers relate. Added from
  call-site greps: per-module config funnels through one shared `ModuleConfigService` (1
  swap moves ~17 modules) while six cross-cutting services hand-roll duplicate I/O (→ one
  `IConfigStore` abstraction). Nine non-obvious hazards now first-class, the load-bearing
  ones being mixed DI lifetimes vs a shared connection (§5B.1), per-instance cache drift
  under a shared DB (§5B.2), and `OrdinalIgnoreCase` keys needing `COLLATE NOCASE` (§5B.3).
  §9 now separates the three **Phase-A-blocking** decisions (location, library,
  cache model) from the deferrable ones. Status remains Draft.
- 2026-06-15, round 3 (Draft, owner request): added **Phase E2 — full module developer
  guide audit & rewrite**, gated on the config swap completing. Owner note: the config
  change is not the only drift affecting new modules (FailClosed permissions, enablement
  semantics, descriptor surface, two-rule versioning, auth wiring, validate-module-package),
  so the guide gets a full rewrite once the DB world is final rather than a config-only
  patch. Cross-referenced with the queued guide-review item in `.agents/state.md` and the
  separate module packaging plan. Status remains Draft.
- 2026-06-15, round 4 (Draft): removed forward references to the **TestAccountPool** module,
  which is being deleted before this work starts (owner direction; queued in
  `.agents/state.md`). Dropped it from the §5A per-module list and the §5B.1 lifetime
  example, and from the Phase E test-rewrite list (its tests go with the module). Net effect
  on the plan: §5B.1 is *smaller* — removing the app's only `AddHostedService` eliminates
  the background-thread variant of the connection-lifetime hazard; the Singleton-vs-Scoped
  constraint and the connection-factory mitigation are unchanged. Historical docs
  (Incident-*, ProdReadiness*) intentionally keep their TestAccountPool references — they
  record history; only this forward-looking plan was scrubbed. Status remains Draft.
