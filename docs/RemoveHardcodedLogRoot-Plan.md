# Remove hardcoded E:\WWWOutput log-root fallback (fail-fast on unset Audit:LogRoot)

Status: Implemented
Owner: Michael Coelho
Created: 2026-07-21
Implemented: 2026-07-21 (commits fa40485 helper+guard, b14fce6 services, docs slice)

## Problem

Three shipped services default the audit/operational log root to a hardcoded, ADI-specific
local path when `Audit:LogRoot` is unset:

- `Services/ExtendedLogService.cs` (constructor): `config["Audit:LogRoot"] ?? @"E:\WWWOutput"`
- `Services/JsonlLogService.cs` (constructor): same
- `Services/EmergencyDisableService.cs` `PersistSnapshot()` (runtime method):
  `_config["Audit:LogRoot"] ?? @"E:\WWWOutput"`

`appsettings*.json` is untracked runtime config (Architectural Invariant #3), so the
hardcoded fallback IS the shipped default. Any environment that fails to set `Audit:LogRoot`
silently writes audit/operational logs to `E:\WWWOutput\ExchangeAdminWeb` -- a local path
baked into the product. It also broke CI once the format gate was fixed: tests that build a
real logging service without setting `Audit:LogRoot` hit `Directory.CreateDirectory(@"E:\...")`,
which exists on the ADI dev box but not the CI runner.

## Decision (owner, 2026-07-21)

No baked-in default. `Audit:LogRoot` MUST be configured by each environment, on a path
OUTSIDE the deploy folder (the app deploys to `D:\inetpub\<app name>`, which is
`ContentRootPath`; logs must not live under it, nor under `wwwroot`). If `Audit:LogRoot` is
unset or blank at startup, the app FAILS TO START with a clear error -- matching the app's
fail-closed audit posture (Constitution Audit section). Silently losing or misplacing audit
logs is worse than a deploy that stops and says why.

Rejected alternatives:
- Default to `Path.Combine(ContentRootPath, "logs")` -- rejected: that is inside the deploy
  folder (`D:\inetpub\<app name>\logs`), where builds land; wrong home for operational logs.
- Start with a warning + temp/ProgramData fallback -- rejected: audit logs could land
  somewhere unexpected until noticed.

## Change

### 1. Startup validation (Program.cs, fail-fast block ~lines 170-197)

Add a check inside the existing post-`builder.Build()` fail-fast block (the same block that
migrates the config store), BEFORE `app.Run()` and before any logging service is resolved:

- Read `builder.Configuration["Audit:LogRoot"]` (the check uses `builder.Configuration`;
  place it so it runs unconditionally at startup).
- If null/whitespace: throw a clear exception (e.g.
  `throw new InvalidOperationException("Audit:LogRoot is not configured. Set it to an absolute path outside the deploy folder (e.g. in appsettings). The app will not start without it so audit logs are never silently misplaced.")`).
- This throw propagates out of `Main` and aborts startup (consistent with the existing
  config-store fail-fast). Log via `Log.Fatal` before throwing if it improves the operator
  message, matching the surrounding Serilog usage.

Rationale for a central check rather than per-constructor throws: `ExtendedLogService` and
`JsonlLogService` are DI singletons (lazy -- a constructor throw only fails startup if
resolved during boot), and `EmergencyDisableService` is scoped with its path use in a runtime
method (`PersistSnapshot`), never at startup. A single boot-time guard covers all three
deterministically.

### 2. Remove the hardcoded fallback in all three services

Replace `config["Audit:LogRoot"] ?? @"E:\WWWOutput"` with a read that assumes the value is
present (startup guarantees it). Options, pick the simplest that reads cleanly:
- `config["Audit:LogRoot"]!` (non-null-forgiving) with a short comment pointing at the
  Program.cs guard, OR
- a tiny shared helper `RequireLogRoot(IConfiguration)` that throws
  `InvalidOperationException` if unset (defense in depth: also correct if a service is ever
  constructed outside the app host without the guard). Prefer the helper -- it keeps the three
  call sites honest and gives tests a single documented contract.

Apply to `ExtendedLogService.cs`, `JsonlLogService.cs`, and `EmergencyDisableService.cs`. No
occurrence of `E:\WWWOutput` remains in shipped code after this change (verify with a grep).

### 3. Test impact

All test classes that build these services already set `["Audit:LogRoot"] = _tempDir` (this
was just added to `ConferenceRoomProtectionGateTests` in commit b978362). Confirm every test
that constructs `ExtendedLogService`/`JsonlLogService`/`EmergencyDisableService` sets it; the
grep in Verification catches any that do not. If the helper (option 2b) throws on unset, a
missing test config now fails loudly and locally rather than only on CI -- desirable.

### 4. Deployment docs / config

The deploy scripts and any environment-setup docs must state that `Audit:LogRoot` is
REQUIRED and name the expected path convention (absolute, outside the deploy folder). Check
`tools/Install-ExchangeAdminWeb.ps1`, `deploy.ps1`, and any config template/readme that
enumerates required settings; add `Audit:LogRoot` to the required list where such a list
exists. Do NOT commit an appsettings with a real path (untracked by Invariant #3).

## Out of scope

- Changing log format, rotation, or content.
- Changing where any specific environment actually points its logs (an ops task, not code).
- Reworking the logging services beyond removing the fallback and reading the required value.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (all green; the three ConferenceRoomProtectionGate
  tests and every other logging-service test pass)
- `git grep -n 'WWWOutput' -- '*.cs'` returns nothing in shipped code (tests included).
- Non-vacuity for the startup guard: temporarily remove `Audit:LogRoot` from a test/dev
  config (or unit-test the helper) and confirm the app/helper throws the documented error;
  restore.
- ASCII lint (`tools/Test-AsciiOnly.ps1`) stays green -- the new strings are ASCII.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean.

## Commit slicing (one concern per commit)

1. Add the shared required-log-root helper + startup guard in Program.cs (with its
   non-vacuity proof).
2. Remove the hardcoded fallback in the three services, routing through the helper.
3. Docs: mark `Audit:LogRoot` required in deploy/setup docs. Update `.agents/state.md`.

(Slices 1-2 may merge if they are naturally one change; keep docs separate.)