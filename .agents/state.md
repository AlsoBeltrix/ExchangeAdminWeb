# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.6` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **ACTIVE INCIDENT — read `docs/Incident-2026-06-12-DevConfigLoss.md` FIRST.** The
  2026-06-12 task-20 dev deploy triggered loss of dev's section-access/enablement state.
  Dev config recovery was performed on 2026-06-12: current dev files were backed up to
  `E:\WWWOutput\ExchangeAdminWeb\ConfigBackups\dev-recovery-20260612144642`,
  dev `appsettings.json` was restored from `appsettings.20260612124749.bak`,
  prod `modules-enabled.json` and `sectionaccess.json` were copied to dev, JSON was
  validated, and `web.config` was touched because direct IIS recycle required an
  elevated admin token. The incident doc contains root cause, the previous session's
  errors, open diagnostics, and required fixes.
- **DEPLOYS PARTIALLY ON HOLD**: deploy appsettings reconciliation no longer rewrites
  upgrade `appsettings.json`, but the startup enablement write removal is
  still owner-directed and not implemented.
- Work stream: `docs/ProdReadiness-Plan.md` (Approved) — phases 1-3 complete and CI-green
  (405/405 xUnit windows-latest, 24/24 Pester). Task 20 blocked on incident recovery.
  Phase 4 waits behind incident fixes. Findings register:
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

## Blockers

- Startup enablement writes still need to be removed before trusting another deploy
  against dev or prod.
- Remaining incident fixes need owner approval/scheduling before implementation (plan
  review log round 6).

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
