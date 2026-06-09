# ExchangeAdminWeb — agent guide

ASP.NET Core 10 Blazor Server app (`net10.0-windows`) for Exchange Online / Graph /
on-prem AD administration. 21 modules built on a descriptor-based module architecture
(`Modules/ModuleCatalog.cs`). Current version: `<VersionPrefix>` in ExchangeAdminWeb.csproj.

## Commands
- Build: `dotnet build -c Release`
- Test: `dotnet test` (xUnit v3 + NSubstitute, in `ExchangeAdminWeb.Tests/`)
- PowerShell lint: `Invoke-ScriptAnalyzer -Path . -Recurse`
- Dev deploy: `./deploy.ps1` (ADI-specific). Generic install: `tools/Install-ExchangeAdminWeb.ps1`.

## Authoritative docs — read BEFORE touching the related area
- `docs/ProjectConstitution.md` — **highest authority.** Non-negotiable engineering rules
  for the whole app; outranks module plans, this file's specifics, and chat summaries. When
  any guidance here conflicts with the Constitution, the Constitution wins — read it first.
- `docs/AdminModuleSpec.md` — module contract. Binding for `Modules/`, `Components/Pages/`.
  NOTE: check its version header against the csproj version; flag drift, don't silently trust it.
- `docs/AdminModuleDeveloperGuide.md` — how to build a module.
- `docs/*-Plan.md` — work-stream plans. Check the `Status:` header; only `Approved` or
  `In progress` plans represent current intent. `Implemented`/`Superseded` are history.
- `README.md` — full behavior reference. It is large; read only the relevant section.

## Architectural invariants
1. `tools/Install-ExchangeAdminWeb.ps1` is environment-neutral and standalone.
   Never couple it to `deploy.ps1` or ADI-specific configuration.
2. Config promotion is dev-wins (`tools/promote-dev-to-prod.ps1`).
3. Deploys never overwrite runtime config: `appsettings*.json`, `config/`, `logs/` are
   excluded from robocopy mirroring. Preserve these exclusions in any deploy change.
4. Every ops-script step must support `-PlanOnly` (via `Invoke-PlanOrAction` / `Write-Plan`).
5. PowerShell error model: `$ErrorActionPreference = "Stop"`; failures go through
   `Write-Fail` (throw). Native exe results must be converted to throws by checking
   `$LASTEXITCODE` (robocopy: ≥8 is failure; 0–7 are success variants).
6. Versioning (two independent rules — see `docs/ProjectConstitution.md` §Deployment And
   Versioning): shared/app-wide changes bump the base app version (`<VersionPrefix>` +
   `AssemblyVersion` + `FileVersion` in `ExchangeAdminWeb.csproj`; the sidebar reads it via
   `BuildInfo`). Module-scoped behavior changes bump that module's `Version` in
   `Modules/ModuleCatalog.cs`. A change touching both layers gets both bumps; each rule
   fires on its own, not because of a special "both" rule.

## Known failure classes — check every diff against these
1. **Side-effect ordering vs try/catch/finally** — state writes (manifests, markers) must
   be unreachable on failure paths. Trace the actual exception flow; do not pattern-match.
2. **Success aggregation** — loops over N items must aggregate per-item failures, never
   report blanket success.
3. **Stale references** — never trust remembered file contents or doc claims. Re-read
   files before editing; verify doc statements against current code.

## Working rules
- Plan first: write or update a `docs/<Feature>-Plan.md`, get approval, then implement.
  Implementation must not exceed plan scope; scope changes go back through the plan.
- Disputes about repo behavior are settled by reading the file, not by argument. Any
  control-flow claim must quote exact lines and name the error mechanism
  (throw vs Write-Error vs native exit code vs swallowed catch).
- When compacting, always preserve: the list of modified files, the active plan file
  path, and the test commands.
- After any compaction, re-read a file before editing it.
- New or rewritten Services require corresponding tests in `ExchangeAdminWeb.Tests/`
  before the work stream is "done". New `.ps1` logic requires Pester coverage in `tests/ps/`.
