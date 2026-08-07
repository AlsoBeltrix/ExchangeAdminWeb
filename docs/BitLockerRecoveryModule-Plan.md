# BitLocker Recovery module integration plan

Status: **Implemented 2026-08-07.** Approved by the owner the same day ("make the
changes, then begin implementation").

This is the authoritative copy. A reference copy lives beside the module package
at `D:\source\scripts\BitLocker\ExchangeAdminWebModule\docs\`; if the two
disagree, this one wins.

## Scope

Integrate the isolated BitLocker Recovery module package from
`D:\source\scripts\BitLocker\ExchangeAdminWebModule` into the compiled
ExchangeAdminWeb host at `D:\source\ExchangeAdminWeb`.

The module is read-only. It searches the local BitLocker SQLite archive by
default and can optionally search live Active Directory with a module-specific
Delinea credential.

Do not change the BitLocker export/import scripts in this plan. Do not change
production host config, deploy scripts, scheduled tasks, remotes, or pushed
state.

## Current package evidence

Before this plan was written, the isolated package had these local checks:

- `tools\validate-module-package.ps1 -PackagePath ... -TreatWarningsAsErrors`
  passed from the host repo.
- `D:\source\scripts\BitLocker\Invoke-Verification.ps1` passed with 110 Pester
  tests and no PSScriptAnalyzer findings.
- A scratch out-of-tree C# project compiled the module services and
  `BitLockerRecoveryTests.cs` against the existing host DLL and ran 30 tests,
  all passing.

These checks do not replace the real host build and host test suite after
integration.

## Preflight

1. In `D:\source\scripts\BitLocker`, confirm the package working tree is clean:
   `git status --short --branch`.
2. In `D:\source\ExchangeAdminWeb`, confirm host state:
   `git status --short --branch`.
3. If the host tree is dirty, inspect the paths. Continue only if the existing
   changes are unrelated or the owner explicitly says to integrate on top of
   them. As of 2026-08-07 the host tree is clean and level with both remotes at
   `81fd069`, so this branch is not expected to trigger.
4. Re-run the package validator from `D:\source\ExchangeAdminWeb`:
   `.\tools\validate-module-package.ps1 -PackagePath D:\source\scripts\BitLocker\ExchangeAdminWebModule -TreatWarningsAsErrors`.

## File integration

Copy these files from the package into the host:

| Package source | Host destination |
| --- | --- |
| `src\Services\BitLockerRecoveryIdentifier.cs` | `Services\BitLockerRecoveryIdentifier.cs` |
| `src\Services\BitLockerRecoveryService.cs` | `Services\BitLockerRecoveryService.cs` |
| `src\Services\BitLockerLiveDirectorySearch.cs` | `Services\BitLockerLiveDirectorySearch.cs` |
| `src\Components\Pages\BitLockerRecovery.razor` | `Components\Pages\BitLockerRecovery.razor` |
| `tests\BitLockerRecoveryTests.cs` | `ExchangeAdminWeb.Tests\BitLockerRecoveryTests.cs` |
| `docs\BitLockerRecovery.md` | `docs\BitLockerRecovery.md` |
| `docs\BitLockerRecoveryModule-Plan.md` | `docs\BitLockerRecoveryModule-Plan.md` |

Use normal file copy or patch application. Do not edit unrelated host files.

Two host files must also be updated, and neither is optional under repo policy:

- **`README.md`** -- the repo guidance calls it the full behavior reference and
  every module has a section there. A new user-facing page with no README entry
  is documentation drift.
- **`.agents\state.md`** -- the host's single current-state entry point, kept
  current by the working agent as work lands.

## Versioning

**No base app version bump.** Adding a module does not bump `<VersionPrefix>`
(`docs\ProjectConstitution.md` Deployment And Versioning; `.agents\decisions.md`
2026-07-21). Only the new module's own `Version` is set, and it is `1.0.0`.

This is stated explicitly so the bump is not added "for completeness" during
integration.

## Host registration

1. In `Program.cs`, add these service registrations alongside the other module
   service registrations:

   ```csharp
   builder.Services.AddScoped<IBitLockerLiveDirectorySearch, PowerShellBitLockerLiveDirectorySearch>();
   builder.Services.AddScoped<BitLockerRecoveryService>();
   ```

2. In `Modules\ModuleCatalog.cs`, add the descriptor from
   `integration\ModuleCatalog.snippet.cs` inside `RegisterAll()`.
3. Place it after the `DhcpAuthorization` descriptor so Infrastructure modules
   remain grouped. Keep `SortOrder = 810`.
4. Keep `Version = "1.0.0"`, `EnabledByDefault = false`, and
   `MainPermission = new("Access", "BitLockerRecovery", FailClosed: true)`.

## Test updates

Update `ExchangeAdminWeb.Tests\ModuleCatalogTests.cs` for the new catalog entry:

1. `Catalog_HasExpectedModuleCount`: update the expected total from 24 to 25.
   Update the adjacent comment from 23 operational + 1 config-only to
   24 operational + 1 config-only.
2. `Catalog_GetConfigurablePolicyAliases_Matches...`: add
   `BitLockerRecovery` to the expected alias set. The alias count goes from
   33 to 34.

Do not weaken catalog tests. These failures are the intended guard that proves
the module was added to the catalog.

## Audit note

The page audits a search as successful when archive results are returned even
if requested live AD fallback failed and the UI shows a warning. `LogLookupAction`
has no `extra` parameter, so recording the degradation would mean changing a host
audit signature -- out of scope here.

Add the behaviour note to `docs\BitLockerRecovery.md` under the existing Audit
actions table. This is unconditional, not "if documentation needs it": an
undecided item at execution time is an item that gets skipped.

## Verification

Run host verification after integration, from `D:\source\ExchangeAdminWeb`:

```powershell
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

**Always target `ExchangeAdminWeb.slnx`.** A bare `dotnet test`, or one aimed at
a `.csproj`, resolves only the web project and **silently runs zero tests** --
a recorded trap in `.agents\repo-guidance.md`. A "passing" run that tested
nothing is the failure mode this line exists to prevent.

The format check is not optional: it is what CI enforces, and targeting the
`.slnx` matches the gate.

Required expected result:

- Host app builds.
- Host test suite passes, including the new `BitLockerRecoveryTests`.
- Catalog tests pass with the updated count and alias set.

If failures occur, fix only issues caused by this integration. Do not clean up
unrelated host warnings or failing tests in the same slice.

## Manual validation after host build

Manual validation requires a running host and appropriate operator access:

1. Enable `BitLockerRecovery` in Admin Settings.
2. Configure section access for the operator test account.
3. Set `ArchiveDatabasePath` to a local SQLite archive path on the web server.
4. Search a current machine by name and confirm masked archive results.
5. Search a historical/deleted machine and confirm `Archive only` status.
6. Search by full key ID, short key ID, and pasted 48-digit recovery password.
7. Reveal one key and confirm the reveal audit records machine/key metadata but
   not the recovery password.
8. If a Delinea AD reader secret and the ActiveDirectory PowerShell module are
   available, check `Search live Active Directory too` and confirm the expected
   delay plus live result or partial warning.
9. Break live AD config and confirm archive results still show with a warning.

## Commit

Commit the host integration as one slice after build and tests pass. The host
repo uses conventional commits; match it:

```text
feat(bitlocker): add the BitLocker recovery module
```

Do not push without explicit owner approval.

## Rollback

If integration fails before commit, revert only the files touched by this plan.
If it is committed and must be backed out, create a new revert commit rather
than rewriting history.
