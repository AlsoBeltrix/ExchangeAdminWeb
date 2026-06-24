# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change. Resolved work lives in the plan/decision/incident docs, not here.

## Now

- App version `2.3.20` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **`master` at `cf837e8`, level with `origin/master`, working tree clean.** (Only
  untracked files are stray `*-command-messagecatchupcommand-message.txt` scratch files.)
- **ACTIVE WORK STREAM — SQLite config store** (`docs/SqliteConfigStore-Plan.md`, Status:
  *In progress*). Migrates all `config/` JSON fragments into a per-environment SQLite
  store at `config/exchangeadmin.db`. Phase progress (detail in the plan, commit log in
  git):
  - **Phase A** infra (`Services/Storage/`, connection factory, migrator) — ✅
  - **Phase B** all 7 stores cut over (app settings, module admins, module config,
    modules-enabled, section access, protected principals, editable attributes); schema at
    `user_version 5` — ✅, **dev-validated on a real box 2026-06-18 (app 2.3.19)**
  - **Phase C** startup non-destructive seeding (`SeedMissingModules`) — ✅
  - **Phase D** ops scripts (verified online backup + post-deploy integrity check;
    promotion = wholesale verified DB copy; `-Refresh` prod→dev; install seeds + repair
    guide `docs/ConfigDbOperations.md`; shared `tools/SqliteConfigBackup.psm1`) — ✅
  - **Phase E** (tests + docs sweep) and **Phase E2** (full module developer-guide
    rewrite) — **NOT STARTED.**

- **NEXT ACTION — start Phase E** (plan §369 / §394):
  1. Rewrite service tests against SQLite (list in plan §370–376); keep the
     storage-agnostic ones (§377); retarget `tests/ps/DeployInvariants.Tests.ps1` to the
     DB (§379).
  2. Config-swap doc edits: `ProjectConstitution.md` (deploys-never-overwrite-config
     invariant → "config lives in SQLite store"; promotion = DB copy; backup expectations;
     no-startup-write rule amended for non-destructive seeding), `AGENTS.md` Architectural
     Invariants 2 & 3, `AdminModuleSpec.md` version header + DB-backed config/section-access
     sections, relevant `README.md` sections.
  3. Then **Phase E2** (gated: owner direction is the authoritative module-guide rewrite
     happens *after* the swap lands). Includes validator enforcement of the module-version
     display (see Queued work). Finally flip the plan to **Implemented**.

## Blockers

- None blocking Phase E.
- **Deferred (owner direction 2026-06-18):** prod deploy of the SQLite-era build is held
  until the work queue clears — do not push to prod until then. Sub-TODO that gates CR-1
  in prod: configure the ConferenceRooms AD `DelineaSecretId` in the deployed instance.
- **Deployed versions are not repo-verifiable** — confirm prod/dev on the boxes before any
  deploy. Plan baseline intent was prod + dev both on `2.3.11` pre-cutover; dev last
  validated at `2.3.19`.

## Verification

- Code: `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx`. Add `dotnet format ExchangeAdminWeb.csproj
  --verify-no-changes --no-restore` and `git diff --check HEAD` where practical.
  (Always target the `.slnx`; bare `dotnet test` runs zero tests.)
- PowerShell: `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps`.
  Deploy-host dependency for the ops scripts: `sqlite3.exe` on PATH.
- When a change ships with a new test, prove it non-vacuous (revert the fix, see the test
  fail, restore). Full policy + manual-check list: `.agents/repo-map.json`, `AGENTS.md`.

## Findings (environment / CI — still live)

- CI is real: a failing test fails the run. Trust it.
- On local macOS, a missing Windows COM DLL can nondeterministically drop xUnit
  collections (totals vary) — trust the failure *list*, not the total. `windows-latest`
  CI is unaffected. macOS builds need `-p:EnableWindowsTargeting=true`; Pester needs
  `pwsh` + `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- `deploy.ps1` still lacks a native `-PlanOnly` (deferred with owner visibility;
  `deploy-pipeline -PlanOnly` covers the prod dry-run requirement).

## Known issues (pre-existing, NOT SQLite-caused)

- **MFA Reset stranded legacy config key.** The Graph Delinea secret was renamed
  `DelineaSecretId` → `GraphDelineaSecretId` in the catalog. The `ModuleConfig` page only
  renders the new key, but `MfaResetService` reads `GraphDelineaSecretId ?? DelineaSecretId`.
  Environments configured before the rename hold the value under the OLD key, so the page
  shows blank while the service still works via fallback. Confirmed blank in prod
  (pre-SQLite) and dev → pre-existing. The SQLite import copies every key verbatim, so it
  neither fixes nor worsens it. Proper fix (deferred, not requested): one-time key rename
  then retire the service-side fallback.

## Queued work (forward-looking — no other doc home)

These have no plan doc yet; do not start without the noted plan/approval.

- **Module developer guide — full audit & rewrite.** Gated on the SQLite swap (= Phase E2
  above). Guide + `AdminModuleSpec.md` version header have drifted on FailClosed
  permissions, enablement semantics (no startup writes since 2.3.7), descriptor surface,
  two-rule versioning, auth wiring, `validate-module-package.ps1`. Must stand alone for a
  developer with no chat context. Coordinate with the module-packaging plan.
- **Validator enforcement of the module-version display.** The app-wide version display is
  shipped (app 2.3.11, `Components/Shared/ModuleVersion.razor` on all 20 module pages;
  rule recorded in `AdminModuleSpec.md` + `AdminModuleDeveloperGuide.md` as **enforced**).
  REMAINING: `tools/validate-module-package.ps1` must check a module page renders its
  descriptor `Version` (via `<ModuleVersion />`), with Pester coverage. Until it ships, the
  "validator enforces this" claim is forward-looking. Bundle with the guide work.
- **Module packaging/import.** Direction set 2026-06-18 (`.agents/decisions.md`): `.zip`
  package + validator, rebuild-to-install, runtime upload deferred. Needs
  `docs/ModulePackaging-Plan.md` written + approved before any implementation.
- **GM-1 (bug): GroupManagement search too fuzzy.** Exact group name (e.g. "IAM") returns
  dozens of fuzzy matches. Tighten on-prem AD group search so exact/near-exact ranks first.
  Search path not yet code-located. Confirm desired ranking with owner before implementing.
- **GM-2 (bug): M365 group management finds no groups at all.** Root cause unknown (Graph
  query / auth-scope / result mapping). Investigate before any fix; treat as a real defect.
- **GM-3 (new module, needs own plan — DECIDE LATER): self-service group management.**
  Owner direction 2026-06-17, plan separately (`docs/SelfServiceGroupManagement-Plan.md`),
  nothing built until approved. Key requirements: likely a separate module; do NOT preload
  the user's manageable groups (explicit "show groups I manage" button with a slow-load
  warning); search restricted to groups the user manages with GM-1 fixes applied; reject
  any modification to non-managed groups at the service/authorization layer (UI hiding is
  not security). Open: how "manages" is determined, on-prem vs M365 vs both, making the
  lookup tolerable. Depends on GM-1/GM-2 being understood first.

## Recently completed (pointers only — full detail in the named docs)

- **SQLite Phases A–D** — `docs/SqliteConfigStore-Plan.md` + git log (`e8b155c`..`cf837e8`).
- **ProdReadiness work stream** — COMPLETE, plan Status **Implemented**
  (`docs/ProdReadiness-Plan.md` §10 round 17, `a5ab6aa`); all AC1–AC16 met.
- **2026-06-12 dev config-loss incident** — Remediated
  (`docs/Incident-2026-06-12-DevConfigLoss.md`); real cause `f7df81a` FailClosed on a dev
  box lacking `sectionaccess.json`. Much of the deploy-hardening it produced is
  obsoleted-by-design by the SQLite store.
- **TestAccountPool module removed** (app 2.3.10), **Conference Rooms** RoomListOU removal
  + partial-apply reporting (`8d4f0d6`, module 2.0.10) — decisions in `.agents/decisions.md`.

## Active Sources

- `AGENTS.md`
- `docs/ProjectConstitution.md` (highest engineering authority)
- `docs/SqliteConfigStore-Plan.md` (active work stream)
- `.agents/decisions.md`
- `.agents/repo-map.json`

## Unrecorded Repo Memory

- None known. Engineering rules: `docs/ProjectConstitution.md`; module contract:
  `docs/AdminModuleSpec.md`; work-stream history: `docs/*-Plan.md`.
