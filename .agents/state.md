# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.5` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`). A base-version
  bump to 2.3.6 is owed at the end of the current remediation batch (shared-infra
  changes landed; see plan §5 versioning note).
- **Active work stream:** `docs/ProdReadiness-Plan.md` (Status: Approved) — prod-release
  remediation of the 2026-06-12 multi-agent review. Findings register:
  `docs/ProdReadinessReview-2026-06-12.md`.
- **Phase 1 (verification) complete, Phase 2 (security) complete** — 11 local commits
  ahead of `origin/master`, awaiting owner push (this machine's GitHub auth is the
  owner's personal account `roethlar`; the repo needs the corporate account):
  - Plan + findings register.
  - `ExchangeAdminWeb.slnx` added — bare `dotnet test` previously ran ZERO tests (no
    solution file); CI and AGENTS.md commands now target the solution.
  - Cross-platform fix for the audit write-failure test (Z:\ was Windows-only).
  - `tests/ps/DeployInvariants.Tests.ps1` (18 static Pester tests) + CI hardening
    (Pester fails on missing dir, -SkipPublisherCheck, dotnet format step).
  - FailClosed flipped on legacy mutating permissions (MailboxPermissions,
    CalendarPermissions, Migration×3, OutOfOffice); read-only allowlist pinned by test.
  - Pre-write authorization re-checks: MailboxPermissions + CalendarPermissions
    cloud/bulk paths, AdminEventLog ExecuteUndo.
  - ProtectedPrincipalService fails closed on corrupt MailboxPermissions config.
  - ConferenceRooms CEO/restricted permission-removal failures now surface (2.0.5).
  - GroupAuthorizationHandler direct tests (7 scenarios).
- **Next: Phase 3** (deploy pipeline truth, audit gaps, ClientInfo IP, UI
  responsiveness) then Phase 4 (docs sweep, backlog). Tasks in plan §7.

## Findings

- CI exists at `.github/workflows/ci.yml` but has NEVER been observed running with a
  nonzero test count. First push must confirm ≥392 tests execute and that a failing
  test fails the run (AC1). Until then treat verification as local-only.
- On macOS, a missing Windows COM DLL can nondeterministically drop xUnit test
  collections (total count varies, e.g. 301 vs 392); windows-latest CI is unaffected.
  Trust failure lists, not totals, on local macOS runs.
- Local macOS commands need `-p:EnableWindowsTargeting=true` and (for Pester)
  `pwsh` installed as a dotnet global tool with `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.

## Blockers

- Push to `origin/master` requires the owner's corporate GitHub credentials (gh on
  this machine is logged in as `roethlar`, which got 403; no SSH key registered).

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
