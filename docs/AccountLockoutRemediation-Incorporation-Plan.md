# AccountLockoutRemediation — Incorporation Plan

Status: Draft (awaiting owner approval)
Owner: Michael Coelho
Created: 2026-06-26
Tracking: `.agents/state.md` "Validated, ready to incorporate" / Next-up #1

## What this is

The AccountLockoutRemediation module was built as an isolated package (GPT-authored,
following `docs/AdminModuleDeveloperGuide.md`) and staged at
`_not_for_github/example_scripts/AccountLockoutRemediation/`. It passed static validation
2026-06-26 (`tools/validate-module-package.ps1` → 0 errors / 0 warnings; all host
dependency signatures re-confirmed against current code in this session). This plan covers
**incorporating** the already-built, already-validated package into the host app — it is a
mechanical splice plus a test-coverage addition, **not** a from-scratch module build, so
`/new-module-command` does not apply.

## What the module does

Helps operators (1) discover account-lockout *source* machines from Security 4740 events on
the PDC/named DCs, (2) log selected accounts off the implicated machines, and (3) sweep a
scoped OU/computer list and log accounts off everywhere via WinRM. Built around the existing
`Clear-LockoutSessions.ps1` / `Force-AdminLogoff.ps1` workflows.

Security posture (verified by reading the source this session): disabled by default; main
`Access` + granular `Logoff` permissions both `FailClosed: true`; execution gated on a
ticket number **and** typed "LOG OFF"; every target re-resolved through
`ProtectedPrincipalService` with an immutable-GUID re-check immediately before logoff;
audit + operation-trace on all paths (lookup, dry-run, execute, denied, protected-block,
partial, failure); credentials module-scoped via its own `DelineaSecretId` (no reuse);
dry-run is the default; per-machine failures aggregated (no blanket success).

## Host-fit verified this session

- Catalog descriptor (`integration/ModuleCatalog.snippet.cs`) uses the current record
  shapes: `ModuleConfigField(... Required, DefaultValue)`, `ModulePermission(Name,
  PolicyAlias, FailClosed)`. SortOrder **780** slots cleanly between MfaReset (750) and
  NamedLocations (790); same `Identity & Access` category.
- Service calls match host signatures exactly: `ModuleCredentialService.GetCredentialsAsync
  (moduleId, purpose)`; `ProtectedPrincipalService.ResolveWithStatusAsync` →
  `(principal, status)`, `CheckAsync`, `ResolutionStatus`, `ProtectedPrincipalResult`
  (IsProtected/CheckFailed/Reason), `ResolvedDirectoryPrincipal`
  (ObjectGuid/DistinguishedName/SamAccountName); `OperationTraceService.BeginOperation` /
  `Step` / `OperationScope.Complete`; `AuditService.LogModuleAction` / `LogLookupAction`;
  `ModuleConfigService.IsModuleCorrupt` / `GetValue`; `ClientInfoService.IpAddress` /
  `GetIpForUser`.
- Page (`AccountLockoutRemediation.razor`) follows host conventions: `@rendermode
  InteractiveServer`, `[Authorize(Policy=...)]`, `<ModuleVersion />`, ClientInfo wiring,
  `access-denied` redirect, ticket + typed-confirmation gating on execute buttons.
- DI line (`integration/Program.snippet.cs`):
  `builder.Services.AddScoped<AccountLockoutRemediationService>();`

## Decisions (owner, 2026-06-26 — both resolved)

1. **Models folder convention — KEEP the subfolder.** Models stay at
   `Models/AccountLockoutRemediation/AccountLockoutModels.cs` (namespace
   `ExchangeAdminWeb.Models.AccountLockoutRemediation`), not flattened to the host's legacy
   flat `Models/` convention. Owner: subfolders are the better organization going forward.

2. **Test coverage gap — ADD the tests.** The package ships only two descriptor tests; the
   service has real testable logic with no coverage (throttle clamp, `MaxSweepTargets` cap,
   credential/corrupt/authz failure paths, the protected-principal guard, success
   aggregation). Per AGENTS.md ("new Services require corresponding tests"), add unit tests
   for the host-substitutable logic. PowerShell/WinRM/AD I/O stays manual-validation-only.
   Owner: no deadline — do it right. See "Add service tests" below.

## Incorporation steps

### 1. Move files into the host tree
- `src/Components/Pages/AccountLockoutRemediation.razor` → `Components/Pages/`
- `src/Services/AccountLockoutRemediationService.cs` → `Services/`
- `src/Models/AccountLockoutRemediation/AccountLockoutModels.cs` → `Models/AccountLockoutRemediation/`
  (or flattened to `Models/AccountLockoutRemediationModels.cs` if decision 1 = flatten)
- `tests/AccountLockoutRemediationTests.cs` → `ExchangeAdminWeb.Tests/`
- `docs/AccountLockoutRemediation.md` (module user doc) → `docs/`

### 2. Splice the catalog descriptor
Insert the `integration/ModuleCatalog.snippet.cs` descriptor into `Modules/ModuleCatalog.cs`
`RegisterAll()`, positioned by SortOrder (between MfaReset and NamedLocations).

### 3. Register DI
Add the `integration/Program.snippet.cs` line to `Program.cs` alongside the other
`AddScoped<…Service>()` registrations.

### 4. Update catalog count tests (these WILL fail otherwise)
`ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`:
- `Catalog_HasExpectedModuleCount`: **20 → 21** (and the inline comment 19→20 operational).
- Configurable policy alias count (currently `Assert.Equal(27, …)`): the new module adds
  **2** configurable aliases (`AccountLockoutRemediation` main + `AccountLockoutRemediationLogoff`
  granular) → **27 → 29**. *(Confirm the delta at implementation by reading the assertion;
  system/config-only modules are excluded from this list, and this module is neither.)*

### 5. Add service tests (decision 2)
New tests in `ExchangeAdminWeb.Tests/` for the host-substitutable logic. NSubstitute the
host services (`ModuleConfigService`, `ProtectedPrincipalService`, `AuditService`,
`OperationTraceService`, `IAuthorizationService`, `ModuleCredentialService`) and assert:
- throttle clamp (≤0 → default; >256 → 256; in-range passthrough);
- `MaxSweepTargets` cap rejects an over-limit sweep before any WinRM;
- credential-missing / module-corrupt / unauthorized each return the documented failure
  result with no mutation;
- the protected-principal guard blocks a protected/ambiguous/GUID-mismatch target and
  audits the block;
- success aggregation: a mix of hit + failure rows reports the right counts and does not
  claim blanket success.
Each new test proven non-vacuous (revert the guard, see it fail).
*(If the service's PowerShell methods aren't cleanly separable from the testable logic,
note which branches remain manual-only rather than contorting the code.)*

### 6. Docs
- README.md: add an `### Account Lockout Remediation (/account-lockout-remediation)` feature
  section and bump the module count in the intro line (21 → 22 total / "20 operational"
  wording — match whatever the README currently says after the count change).
- `docs/AdminModuleSpec.md` / developer-guide module lists if they enumerate modules
  (check; update only if they do).

### 7. Version bumps
- **App version** (`<VersionPrefix>` + `AssemblyVersion` + `FileVersion` in
  `ExchangeAdminWeb.csproj`): bump (a new module is an app-wide change). Current 2.3.26 →
  **2.3.27**.
- **Module version**: the descriptor ships at `Version = "1.0.0"` — correct for a new
  module; no bump needed.

### 8. Clean up staging
After the module builds/tests green in the host tree, remove the staged copy under
`_not_for_github/example_scripts/AccountLockoutRemediation/` (or leave it — it's gitignored;
confirm with owner). *(One decision; trivial.)*

## Commit slicing (one item per commit, per Git Safety)

1. Move source files + catalog splice + DI + count-test updates (the module compiles and the
   catalog tests pass) — the module is present but only descriptor-tested.
2. Service tests (decision 2).
3. Docs (README + spec) + app version bump → mark this plan Implemented.

(Slices 1–2 may merge if the count-test fix and service tests are naturally one change; keep
docs/version as their own slice.)

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (full suite green incl. new service tests, proven
  non-vacuous)
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`
- `git diff --check HEAD`
- `tools/validate-module-package.ps1` is a package-shape gate; once incorporated, the host
  build/test is the authority.
- **Manual (post dev deploy — NOT automatable, run the package's own Manual Validation
  steps):** live AD 4740 event read on the PDC; WinRM reachability to a target; `quser.exe`
  / `logoff.exe` parsing; a real dry-run then a real ticketed logoff against a test machine;
  confirm a protected-principal target is blocked. State clearly that these are deferred to
  the dev deploy.

## Out of scope

- No runtime module upload / dynamic loading (per `.agents/decisions.md` 2026-06-18).
- No new PAM backend.
- No ticket-system validation/writeback.
- No remediation of service/scheduled-task/IIS-pool/saved-credential lockout sources (the
  module targets interactive sessions only).

## Risks

- The bulk of the module's real behavior (AD/WinRM/PowerShell) is unverifiable until a dev
  deploy; static validation + unit tests cover structure and host-substitutable logic only.
- The remote PowerShell session-parsing script (`quser`/`logoff`) is the highest-risk
  surface and can only be proven against real machines — flagged for manual validation.
