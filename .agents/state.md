# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.6` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Active work stream:** `docs/ProdReadiness-Plan.md` (Status: Approved) — prod-release
  remediation of the 2026-06-12 multi-agent review. Findings register:
  `docs/ProdReadinessReview-2026-06-12.md`.
- **Phases 1–3 complete and pushed; CI green** (405/405 xUnit on windows-latest,
  23/23 Pester, format + PSScriptAnalyzer). Highlights: real test execution via
  `ExchangeAdminWeb.slnx` (bare `dotnet test` previously ran ZERO tests); Pester
  invariant suite in `tests/ps/`; FailClosed backport to legacy mutating modules;
  pre-write auth re-checks (MailboxPermissions/CalendarPermissions cloud+bulk,
  AdminEventLog undo); protected-principal corrupt-config fail-closed; CEO-room
  permission-removal surfacing; deploy.ps1 Write-Fail throws + staging cleanup in
  finally; deploy-pipeline failure masking fixed + -PlanOnly prod dry run;
  ADAttributeEditor/Migration failure audits; Graph status surfacing (MFA blanket
  success fixed); per-circuit audit IP via ClientInfoCircuitHandler; UI freeze fixes
  (fire-and-forget handlers, EXO connect off dispatcher, autocomplete contention);
  ConferenceRooms on-prem path retired (on-prem Exchange decommissioning, plan Q1).
- **Open:** plan task 20 — owner manually verifies in dev that buttons respond on
  first click (AC13) after deploying this batch. Then Phase 4 (docs drift sweep,
  register backlog incl. the hardcoded `E:\WWWOutput` Audit:LogRoot default).

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

- None. (Pushes from this machine work since the owner fixed GitHub auth.)

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
