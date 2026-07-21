# Repo-Specific Guidance
<!-- Extends AGENTS.md; never overrides it. Rules and pointers only — state
     lives in .agents/state.md. -->

## Mission Detail

ExchangeAdminWeb is an ASP.NET Core 10 Blazor Server app (`net10.0-windows`) for
Exchange Online / Microsoft Graph / on-prem Active Directory administration. It
uses a descriptor-based module architecture: each admin capability is a module
registered in `Modules/ModuleCatalog.cs`. The current app version lives in
`<VersionPrefix>` in `ExchangeAdminWeb.csproj`; the live module count and current
work are tracked in `.agents/state.md`, not here.

## Authority Order

Repo-specific reading order, layered under the AGENTS.md Source of Truth section:

1. Human request.
2. `docs/ProjectConstitution.md` — **highest engineering authority.**
   Non-negotiable rules for the whole app (authorization, credential isolation,
   audit, notifications, versioning, the never-do list). It outranks module plans,
   this file's specifics, and chat summaries. When anything here conflicts with the
   Constitution, the Constitution wins — read it first.
3. `AGENTS.md` — process and behavioral contract for agents.
4. This file (`.agents/repo-guidance.md`) — repo-specific rules; extends AGENTS.md,
   never overrides it.
5. `.agents/state.md` — current active work and blockers.
6. `.agents/decisions.md` — durable decisions and supersessions.
7. `docs/AdminModuleSpec.md` — module contract, binding for `Modules/` and
   `Components/Pages/`. Check its version header against the csproj version; flag
   drift, do not silently trust it.
8. `docs/AdminModuleDeveloperGuide.md` — how to build a module.
9. `docs/*-Plan.md` — work-stream plans. Check the `Status:` header; only `Approved`
   or `In progress` plans represent current intent. `Implemented`/`Superseded` are
   history.
10. Current code, tests, and CI as evidence for behavior. `README.md` is the full
    behavior reference — large; read only the relevant section.

When sources disagree, report the drift and fix the lower-authority source or ask
which should win. Do not silently choose whichever source is convenient.

## Commands

- Build: `dotnet build ExchangeAdminWeb.slnx -c Release`
- Test: `dotnet test ExchangeAdminWeb.slnx` (xUnit v3 + NSubstitute, in
  `ExchangeAdminWeb.Tests/`). Always target the solution: bare `dotnet test` from the
  repo root resolves only the web csproj and silently runs zero tests.
- Non-Windows dev machines: append `-p:EnableWindowsTargeting=true` to build/test
  (the app targets `net10.0-windows`).
- Format check: `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
  (this is what CI enforces; targeting the `.slnx` matches the gate)
- PowerShell lint: `Invoke-ScriptAnalyzer -Path . -Recurse`
- PowerShell tests: `Invoke-Pester tests/ps`
- Dev deploy: `./deploy.ps1` (ADI-specific). Generic install:
  `tools/Install-ExchangeAdminWeb.ps1`.
- Deploy-host dependency: `sqlite3.exe` must be on PATH (`winget install SQLite.SQLite`).
  The deploy/promote scripts use it to make a verified online backup of
  `config/exchangeadmin.db` before each deploy and fail fast if it is missing (see
  `tools/SqliteConfigBackup.psm1`). The app bundles its own SQLite engine; this is a
  host dependency for the ops scripts only.

## Reading Order

`AGENTS.md` → this file → `docs/ProjectConstitution.md` (highest engineering
authority) → `.agents/state.md` (current work) → `.agents/decisions.md` →
`.agents/repo-map.json`. Then the module contract docs (`docs/AdminModuleSpec.md`,
`docs/AdminModuleDeveloperGuide.md`) and any `Approved`/`In progress`
`docs/*-Plan.md` relevant to the task.

## Verification

- Code changes: `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx`. Add `dotnet format ExchangeAdminWeb.slnx
  --verify-no-changes --no-restore` and `git diff --check HEAD` where practical.
  Always target the `.slnx`; bare `dotnet test` runs zero tests.
- PowerShell changes: `Invoke-ScriptAnalyzer -Path . -Recurse` and
  `Invoke-Pester tests/ps`.
- New or rewritten Services require corresponding tests in `ExchangeAdminWeb.Tests/`
  before the work stream is "done". New `.ps1` logic requires Pester coverage in
  `tests/ps/`.
- When a change ships a new test, prove it non-vacuous: revert the fix, confirm the
  test fails, restore it, confirm everything passes.
- CI (`.github/workflows/ci.yml`) triggers on push to `master` and on every PR, with
  a `build-test` job (build, format check, `dotnet test` on the solution, test-result
  upload) and a `powershell` job (PSScriptAnalyzer + Pester) — both on
  `windows-latest`. CI has been observed failing on real test failures, so it is a
  trustworthy gate. Still run local verification before claiming completion; treat CI
  as an additional gate, not a replacement.
- For behavior automation does not cover (operator workflows, dev deployment, Delinea
  secret creation, section access groups), run the relevant manual check or state
  clearly that it was not run.

## Remotes & Sync

- `github` — https://github.com/AlsoBeltrix/ExchangeAdminWeb.git
- `origin` — https://ashbexutil1/gitea/mcoelho/ExchangeAdminWeb.git (LAN gitea)

Main branch: `master`. Push policy is in `.agents/push-policy.md`.

## Versioning

Two independent rules (see `docs/ProjectConstitution.md` §Deployment And
Versioning). Shared/app-wide changes bump the base app version (`<VersionPrefix>` +
`AssemblyVersion` + `FileVersion` in `ExchangeAdminWeb.csproj`; the sidebar reads it
via `BuildInfo`). Module-scoped behavior changes bump that module's `Version` in
`Modules/ModuleCatalog.cs`. A change touching both layers gets both bumps; each rule
fires on its own. **Exception:** adding a new module does not bump the base app version --
only the new module's own version is set (Constitution "Deployment And Versioning";
`.agents/decisions.md` 2026-07-21).

## Architectural Invariants

1. `tools/Install-ExchangeAdminWeb.ps1` is environment-neutral and standalone. Never
   couple it to `deploy.ps1` or ADI-specific configuration.
2. Config promotion is dev-wins (`tools/promote-dev-to-prod.ps1`).
3. Deploys never overwrite runtime config: `appsettings*.json`, `config/`, `logs/`
   are excluded from robocopy mirroring. Preserve these exclusions in any deploy
   change. Runtime operational config lives in `config/exchangeadmin.db` (SQLite); it
   is backed up via verified online backup before each deploy, not by raw file copy.
4. Every ops-script step must support `-PlanOnly` (via `Invoke-PlanOrAction` /
   `Write-Plan`).
5. PowerShell error model: `$ErrorActionPreference = "Stop"`; failures go through
   `Write-Fail` (throw). Native exe results must be converted to throws by checking
   `$LASTEXITCODE` (robocopy: ≥8 is failure; 0–7 are success variants).
6. PowerShell scripts read by Windows PowerShell 5.1 (e.g. `deploy.ps1`, and any
   module it imports) must be pure ASCII unless saved with a BOM: 5.1 reads BOM-less
   files as ANSI and mangles non-ASCII characters into parse errors. PS7 is unaffected,
   so such bugs stay latent until a 5.1 run.

## Known Failure Classes — check every diff against these

1. **Side-effect ordering vs try/catch/finally** — state writes (manifests, markers)
   must be unreachable on failure paths. Trace the actual exception flow; do not
   pattern-match.
2. **Success aggregation** — loops over N items must aggregate per-item failures,
   never report blanket success.
3. **Fail-closed authorization** — authorization/enablement stores must deny when a
   read or write fails (never fall through to a permissive default), and every
   mutating module must route its write target through the protected-principal check
   before writing.
4. **Stale references** — never trust remembered file contents or doc claims. Re-read
   files before editing; verify doc statements against current code.

## Earned Practices

- Plan first for code: write or update a `docs/<Feature>-Plan.md`, get approval, then
  implement. Implementation must not exceed plan scope; scope changes go back through
  the plan. The Constitution lists which change types require a written plan and which
  do not. The `plan` and `new-module` operators automate the drafting.
- Disputes about repo behavior are settled by reading the file, not by argument. Any
  control-flow claim must quote exact lines and name the error mechanism (throw vs
  Write-Error vs native exit code vs swallowed catch).
- Never conclude a branch is merged from ancestry alone: `git branch --merged` can lie
  after an `-s ours` or octopus merge. Verify content actually arrived
  (`git diff <branch> <main>`) before deleting anything or treating work as landed.
- Address exactly one finding or fix per commit and commit each before starting the
  next; batch sweeps spanning many findings happen only on the owner's explicit
  request.
