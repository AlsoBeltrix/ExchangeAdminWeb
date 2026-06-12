# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.7` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
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
- Incident fix #1 implemented (2.3.7): `ModuleEnablementService` startup migration
  reduced to read-only `WarnIfExchangeOnlineUnset`; enablement is written only by
  `SaveEnablement`; startup-no-write tests added. Fix #2 shipped earlier (round 7) as
  hardening; its suspected causal role is refuted. Fixes #3-#8 await owner scheduling.
- **Deploy hold lifted for dev** (Michael accepted the round-8 findings; round 9).
  **Prod is frozen for the weekend of 2026-06-13** — no prod deploys until Michael runs
  them post-freeze with the §Deploy-notes alias check below (prod's fragment already
  covers the six aliases). Incident fixes #3-#7 approved for implementation against dev.
- Work stream: `docs/ProdReadiness-Plan.md` (Approved) — phases 1-3 complete and CI-green
  (now 408/408 xUnit local, 24/24 Pester). Task 20 (manual UI verification) is
  unblocked once Michael confirms. Phase 4 waits behind incident-fix scheduling.
  Findings register: `docs/ProdReadinessReview-2026-06-12.md`.

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

- **Module developer guide review**: drift-check `docs/AdminModuleDeveloperGuide.md`
  (and the `docs/AdminModuleSpec.md` version header) against current code before the
  week of 2026-06-15 — Michael will farm out new-module development and the guide must
  stand alone. Recent invalidation risks: FailClosed permissions, enablement semantics
  (no startup writes as of 2.3.7), fragment-based section access.
- **Module packaging/import**: a way to package modules and import them cleanly into
  the main app, preferably through the UI; if recompile is unavoidable that trade-off
  goes back to Michael. Needs a `docs/ModulePackaging-Plan.md` and approval before any
  implementation (modules are currently compiled in: descriptor in
  `Modules/ModuleCatalog.cs` + razor pages, so runtime import implies assembly loading
  vs a source-drop + rebuild pipeline — open architecture question for the plan).

## Blockers

- Prod freeze (weekend of 2026-06-13): no prod deploys until Michael runs them.
- Task 20 (manual UI verification) needs ~15 minutes of Michael's time in dev after
  the incident-fix batch lands.

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
