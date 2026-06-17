# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.9` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`) — dev-ahead
  Phase-4 batch (security/correctness backlog). **Prod is still on `2.3.8`**; 2.3.9 has
  not been deployed.
- **INCIDENT DIAGNOSED — see Diagnostic Results in
  `docs/Incident-2026-06-12-DevConfigLoss.md`.** Server diagnostics (2026-06-12 PM)
  proved no config was lost: pre/post-deploy `appsettings.json` are SHA256-identical,
  the startup enablement write never fired (the 12:49 write was the owner's own Save on
  /admin-settings), and the real cause was commit `f7df81a` (legacy permissions made
  FailClosed) landing on a dev box with no `sectionaccess.json` and no legacy
  `Security:SectionAccess` — fail-closed sections bypass the AllowedGroups fallback by
  design. Dev recovery is confirmed healthy from logs (residual denials only for
  disabled LicensingUpdates/TestAccountPool, absent from prod's fragment — add groups
  before enabling them).
- **ALL incident fixes #1-#8 are complete** (incident doc Status: Remediated). #1:
  startup enablement writes removed (2.3.7). #3: corrupt-store probes + admin pages
  refuse to save over corrupt backing state (2.3.8). #4: deploys snapshot the whole
  config/ dir. #5: post-deploy config drift check warns loudly. #6: deploy.ps1 defaults
  target dev, fresh installs need -ConfirmFreshInstall. #7: duplicate import removed.
  #8: closed by diagnostics. Remaining: owner sign-off + task-20 manual verification.
- **2.3.8 was deployed to prod (2026-06-17).** Prod freeze over; the FailClosed change
  and all incident fixes #1-#8 are in prod. The §Deploy-notes alias check applied to that
  deploy. Deploy holds fully lifted. **Dev has since advanced to 2.3.9** (the Phase-4
  batch below), so dev is now ahead of prod again; 2.3.9 is not yet deployed.
- Work stream: `docs/ProdReadiness-Plan.md` (Approved) — phases 1-3 complete and signed
  off. Local verification at 2.3.8: 415/415 xUnit, 30/30 Pester (Phase 4 work is at 2.3.9;
  current suite 422/422 xUnit). **Task 20 (manual UI verification, AC13) PASSED 2026-06-17**
  (Michael verified click-responsiveness in dev). Phase 3 is now fully verified; **Phase 4
  (cleanup backlog, AC15-AC16) is in progress.** Findings register:
  `docs/ProdReadinessReview-2026-06-12.md`.

## Findings

- CI is real now: a failing test fails the run (observed: run 27425132329 failed on
  38 Windows-only harness failures, fixed in aeed8f2). Trust CI.
- On macOS, a missing Windows COM DLL can nondeterministically drop xUnit test
  collections (total count varies); windows-latest CI is unaffected. Trust failure
  lists, not totals, on local macOS runs.
- Local macOS commands need `-p:EnableWindowsTargeting=true` and (for Pester)
  `pwsh` installed as a dotnet global tool with `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- deploy.ps1 still lacks a native -PlanOnly (deferred with owner visibility; plan
  review log round 5). deploy-pipeline -PlanOnly covers the prod dry-run requirement.

## Queued work (owner-requested 2026-06-12)

- **Config storage rethink — database instead of JSON fragments** (decision DEFERRED to
  week of 2026-06-15; Michael will decide; needs a plan doc before any implementation).
  Motivation: repeated config-file headaches (2026-06-12 incident class). Owner asks:
  (a) evaluate a database (SQLite vs SQL Server) for runtime config; (b) what else
  should move there (candidates: all config/ fragments — enablement, section access,
  module configs, module admins, protected principals, AD attribute allowlist, extended
  log level — plus audit/event log, operation traces, config change history, test
  account pool state; bootstrap settings stay in appsettings.json); (c) a quick
  "copy prod config to dev" / "copy dev config to prod" tool; (d) new modules and new
  app settings self-register idempotently at startup (INSERT-if-missing with defaults).
  Item (d) would RELAX the no-writes-at-startup rule (2026-06-12 owner direction) for
  non-destructive seeding only — if adopted, supersede that direction explicitly in
  `.agents/decisions.md`; until then the current rule stands. Interacts with the module
  packaging item below (module manifest table). Much of this week's deploy hardening
  (config backups, drift check, corrupt-store guards) becomes obsolete-by-design if
  this lands; plan should say what gets retired.
  **PLAN DRAFTED: `docs/SqliteConfigStore-Plan.md` (Status: Draft, awaiting Michael's
  review).** Decided: SQLite over SQL Express (decision 2026-06-12). Built from a
  six-dimension touchpoint audit + critic. Open questions for Michael consolidated in
  the plan's §9 (audit JSONL deferral, Phase F runtime-editable appsettings,
  Microsoft.Data.Sqlite vs EF Core, OnPremExchange:ServerUri stays in appsettings). No
  implementation until the plan flips to Approved.

- **Module developer guide — full audit & rewrite** (not just a drift-check):
  `docs/AdminModuleDeveloperGuide.md` + `docs/AdminModuleSpec.md` version header have
  drifted on more than config — FailClosed permissions, enablement semantics (no startup
  writes as of 2.3.7), descriptor surface, two-rule versioning, auth wiring,
  `validate-module-package.ps1`. Michael will farm out new-module development, so the guide
  must stand alone for a developer with no chat context. **Owner direction (2026-06-15):
  do the full rewrite AFTER the SQLite config swap lands** so it documents the final
  DB-backed world, not a moving target — captured as Phase E2 in
  `docs/SqliteConfigStore-Plan.md`. A pre-swap drift pass is fine if Michael needs to
  hand something off sooner, but the authoritative rewrite is gated on the swap. Coordinate
  with the module packaging plan so guide and plan agree on authoring→validate→install.
- **Module packaging/import**: a way to package modules and import them cleanly into
  the main app, preferably through the UI; if recompile is unavoidable that trade-off
  goes back to Michael. Needs a `docs/ModulePackaging-Plan.md` and approval before any
  implementation (modules are currently compiled in: descriptor in
  `Modules/ModuleCatalog.cs` + razor pages, so runtime import implies assembly loading
  vs a source-drop + rebuild pipeline — open architecture question for the plan).

- **Remove the TestAccountPool module entirely** (owner direction 2026-06-15: never
  activated, no one will miss it). Do this as its own work item next run, **one focused
  change**, build + test green before done. It is also the app's **only** `AddHostedService`
  — a self-running timer that mutates AD unattended (`"System"/"BackgroundWorker"`, no
  ticket/actor), which is the architectural oddity that prompted the removal. Touchpoints
  to delete/prune (mapped 2026-06-15):
  - `Services/TestAccountPoolService.cs`, `Services/TestAccountPoolCleanupWorker.cs`,
    `Components/Pages/TestAccountPool.razor`, `ExchangeAdminWeb.Tests/TestAccountPoolServiceTests.cs`
  - `Program.cs` lines ~84-85: the `AddScoped<TestAccountPoolService>` and
    `AddHostedService<TestAccountPoolCleanupWorker>` registrations.
  - `Modules/ModuleCatalog.cs`: the `Id = "TestAccountPool"` descriptor (~line 385).
  - `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`: any case referencing the module
    (the fail-closed/versioning sweeps enumerate the catalog — confirm they pass after
    removal, no hardcoded count of modules).
  - `tools/Install-ExchangeAdminWeb.ps1`, `appsettings.json.sample`, `README.md`: drop
    TestAccountPool config seeding / docs references.
  - Leave the historical docs alone (`docs/Incident-*`, `docs/ProdReadiness*`) — they are
    history, not current state. **`docs/SqliteConfigStore-Plan.md`**: drop TestAccountPool
    from the §5B.1 example list and Phase B/E module-test lists once removed (or note it as
    removed) — and removing the app's only HostedService **simplifies §5B.1**, so revisit
    that hazard's framing (the scoped consumers `ADAttributeEditorService` remain, so the
    connection-factory constraint still stands, just with a smaller blast radius).
  - Base app version bump (module removal is an app-wide change). Audit log keeps any
    historical `TestAccountPool_*` entries — do not scrub those.

## Blockers

- None. (Prod freeze ended; 2.3.8 shipped to prod 2026-06-17. Task 20 PASSED.)

## Deploy notes (before the FailClosed change reaches prod)

- Confirm each alias — MailboxPermissions, CalendarPermissions, MigrationCheck,
  MigrationCreate, MigrationManage, OutOfOffice — has a non-empty group list in the
  deployed `config/sectionaccess.json`. Owner reports per-module groups are in use,
  so this should be a no-op check.

## Verification

- Code changes: `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx`. Add `dotnet format ExchangeAdminWeb.csproj
  --verify-no-changes --no-restore` and `git diff --check HEAD` where practical.
- PowerShell changes: `Invoke-ScriptAnalyzer -Path . -Recurse` and
  `Invoke-Pester tests/ps`.
- Full policy and the manual-check list live in `.agents/repo-map.json` and `AGENTS.md`.

## Active Sources

- `AGENTS.md`
- `docs/ProjectConstitution.md` (highest engineering authority)
- `docs/ProdReadiness-Plan.md` (active work stream)
- `docs/ProdReadinessReview-2026-06-12.md` (findings register)
- `.agents/repo-map.json`
- `.agents/decisions.md`

## Unrecorded Repo Memory

- None known. Engineering rules live in `docs/ProjectConstitution.md`; module contract
  in `docs/AdminModuleSpec.md`; work-stream history in `docs/*-Plan.md`.
