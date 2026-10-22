# aud-a26h: Other corrupt-store texts still name retired JSON files, and a pool-drain failure after a successful save is shown as a failed save

**Severity**: LOW - wording and reporting, no wrong write. (1) The corrupt-store banners and errors
for stores other than the allowlist (aud-a26f fixed only that one) still tell an administrator to
fix a `config/*.json` file; those stores live in the SQLite config database since the legacy import.
(2) `ExchangeOnlineConfig.razor` `SaveExoConfig`: when the write succeeds and the following
`DrainPool` fails, the page shows and records "Failed to save", though the configuration was saved;
the admin re-saves a value that is already there and the audit says a save failed that did not.
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-af0cdb58244ff4bed`
**Commit**: `3a4d2c96` (plan `6869fd81`; comments `6aaa1133`)
**Reviewer**: implementer (surfaced by the aud-a28g/h/a26f/a28i agent, 2026-10-22, candidates 7 and 8 of its hand-back)

## Evidence

At `f1b4692d`: `git grep -n "\.json" Components/Pages/ModuleConfig.razor Components/Pages/AdminSettings.razor
Components/Pages/ExchangeOnlineConfig.razor Services/` for the corrupt-store and load-error texts
(enablement, section access, module admins, protected principals, EXO config); `ExchangeOnlineConfig.razor`
`SaveExoConfig`, the `DrainPool` call after the write inside the same try.

## Approach (to be written by the implementing agent before code)

- Texts: name the real store (the page or tab where the setting is edited, or the table in
  `docs/ConfigDbOperations.md` with both pools stopped), and the legacy JSON file only where an
  import of it failed. README likewise. Test: the messages contain the table/page name and not the
  retired file name (this finding is about the text).
- Save then drain: the save's success record and message are written when the write succeeds; a
  drain failure is reported as its own warning ("saved; the Exchange Online connection pool could not
  be drained: <reason>; recycle the pool") and recorded as a separate failed step, not as a failed
  save. Test: a failing drain after a successful write leaves one success record for the save and a
  distinct failure for the drain; the message says saved. Mutation at the call site: fold the drain
  back into the save's try.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `374718b3`. One commit
`fix(settings): corrupt-store texts name the config database table, and a failed pool drain after a
saved connection is its own warning (aud-a26h)`, after aud-a28k on the same branch. Behaviour: Admin
Settings and Module Configuration texts (base app version) and Exchange Online Config (the Exchange
Online module's version); the supervisor owns both bumps.

Texts at `374718b3`: `AdminSettings.razor` 73-76 and 585 (`config\modules-enabled.json`);
`ModuleConfig.razor` 111 and 1197 (`modules-enabled.json`), 151-154 and 1270 (`sectionaccess.json`), 162
(the never-configured note: "no config\sectionaccess.json"), 353-356 and 1134 (`module-config-{id}.json`);
`ExchangeOnlineConfig.razor` 51 (`module-config.json`); Admin Settings' protected-principals load error,
which is `LoadEffectiveConfig`'s operator-facing "Contact your administrator" shown on the
administrator's own page; `README.md` 87, 749, 776. The services' own texts name no JSON file
(the allowlist's are aud-a26f's). `SaveExoConfig` 353-372: the write, `DrainPool` (359), then the
success record, all in one try whose catch reports "Failed to save".

1. **Which store.** The stores whose failed legacy import keeps them fail-closed say so: new read-only
   `LegacyImportFailed` on `ModuleEnablementService`, `SectionAccessService` and
   `ProtectedPrincipalService` (the existing flag). Legacy variant: names `config\<file>.json`, says it
   was not imported and is left in place; fix - correct or remove it, then recycle the application
   pool, since the import runs at start. Database variant: names the table (`docs/ConfigDbOperations.md`
   "Where things are"); fix - correct it or restore a verified backup as that document describes, with
   both application pools stopped, then reload. Module config and module admins have no fail-closed
   legacy state: database variant only. The protected-principals load error on Admin Settings names
   its cause - the legacy file, the Mailbox Permissions module configuration (its ExcludedUsers list),
   or the `protected_principal` table; the operator-facing text the modules show is unchanged.
2. **One place.** The texts move into one new `Services/SettingsStoreText.cs`, so a banner and its
   save's refusal cannot drift apart again; aud-a28j's new refusals use it too.
3. **README.** The config pages' stores are the config database's tables; the `Security:AllowedGroups`
   fallback applies while section access has never been configured; the JSON shown is the legacy
   appsettings `Security:SectionAccess` block.
4. **Save, then drain.** In `SaveExoConfig` the write, its success record and the saved message come
   first; `DrainPool` runs after them in its own try. A drain that throws: a warning ("... saved. The
   Exchange Online connection pool could not be drained: <reason>. Recycle the application pool ..."),
   its own failed record (section `ExchangeOnlineConfig:ConnectionPool`, through `LogSettingsSaveFailed`),
   the activity completed as not succeeded with that warning; never the save's catch. A warning alert
   style is added beside the success and error ones.
5. **Registries.** No handler added; `ProgressRegistry`'s `SaveExoConfig` pins hold (both calls stay
   inside the activity, before the finally's Complete). ClickGateRegistry: nothing line-keyed.

Tests, new `ExchangeAdminWeb.Tests/SettingsStoreTextTests.cs` (the pages rendered to their frame lists,
the aud-a26f way, and the refusals read from the handlers): for each store, a corrupt database names
its table and `docs/ConfigDbOperations.md` and not the retired file; a refused legacy import names the
file; the never-configured note names no file; the protected-principals load error names its cause;
README tripwire. A failing drain after a successful write leaves one success record for the save and a
distinct failure for the drain, the message says saved, and the stored connection is the new one.
Mutation at the call site: (7) the drain folded back into the save's try; extras - a legacy variant
deleted; the drain's own record deleted.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-af0cdb58244ff4bed` on aud-a28k (`b2afe0e2`), the fix's services and tests in place
and the three pages and `README.md` as they were at `b2afe0e2` swapped in (AdminSettings
`303A84E590A0F32B2C759BEBC0770FBB`, ModuleConfig `E4A92FD33662A2557FD89C21B4AE4168`, ExchangeOnlineConfig
`5DCBF849D4CE0D272E3D529B541D3B5A`, README `593111E3464167CA49C2A3D2D2558669`), build 0 errors:
`SettingsStoreTextTests` 16 failed / 3 passed. Every database-variant banner and refusal named a retired
file or no table (`Sub-string not found` / `Sub-string found`); the protected-principals load error was the
operators' "Contact your administrator" text; the never-configured note named `config\sectionaccess.json`;
the README still said the config pages persist to the JSON files; and the failing drain turned a saved
connection into one "Failed to save" record (`Values differ`: 1 record, expected 2). The three that passed:
the two legacy-file banners, which named their file in every case and so were right by accident, and the
Exchange Online enablement banner, written with its table by aud-a28j. The fixed bytes were restored to
their MD5s afterwards.

## Fix

`Services/SettingsStoreText.cs` (new): the store-and-fix sentence for each store - `Enablement`,
`SectionAccess` and `ProtectedPrincipals` in two variants (the legacy file that was not imported: named,
left in place, fixed by correcting or removing it and recycling the application pool; or the table in the
config database, fixed per `docs/ConfigDbOperations.md`, or from a verified backup, with both application
pools stopped), `ProtectedPrincipals` with a third for an unreadable Mailbox Permissions ExcludedUsers list,
and `ModuleConfig(moduleId)` and `ModuleAdmins()` in the database variant only.
`ModuleEnablementService`, `SectionAccessService`, `ProtectedPrincipalService`: read-only
`LegacyImportFailed` (the existing flag).

Pages: every corrupt-store banner and save refusal on the three pages now ends in that sentence -
Admin Settings' Modules banner and both enablement refusals (`EnablementRefusal()`), its protected-principals
load error (built on the page from the cause, not the operators' text) and its two protected-principals
refusals; Module Configuration's status, access and configuration banners, their refusals, and aud-a28j's
module-admins banner and refusal; Exchange Online Config's connection banner (it named `module-config.json`),
its load-failure and enablement banners and their refusals (`EnablementRefusal()`). The never-configured
section-access note names no file. `README.md` 87, 749, 776: the config pages store to the config database's
tables; the `Security:AllowedGroups` fallback applies while section access has never been saved; the JSON
block shown is the legacy appsettings shape.

`Components/Pages/ExchangeOnlineConfig.razor` `SaveExoConfig`: the write, its success record and the saved
message (and the activity's outcome) come first; `DrainPool` then runs in its own try. A drain that throws
leaves the success record, adds a failed record under `ExchangeOnlineConfig:ConnectionPool` through
`LogSettingsSaveFailed`, shows the warning "... saved. The Exchange Online connection pool could not be
drained: <reason>. Connections opened before the save may still use the previous settings - recycle the
application pool to close them." in the new warning style (`statusIsWarning`), and completes the activity as
not succeeded with that warning; the save's catch is no longer reachable from the drain.

Tests: new `ExchangeAdminWeb.Tests/SettingsStoreTextTests.cs` (19 cases); `SettingsPageHarness` takes an
optional `beforeServices` action, run on the content root before any service is built, where a test puts a
legacy file the import then refuses.

Caller-visible change, by design: the corrupt-store banners and refusals on Admin Settings, Module
Configuration and Exchange Online Config name the config database table and its documented fix, or - only
where its import failed - the legacy file; the protected-principals load error on Admin Settings says which
of its three causes it is. A failed pool drain after a saved connection reads as a saved connection with a
warning, and is recorded as a failed drain beside the save's success, not as a failed save.

## Guard proof

Same driver, filter
`FullyQualifiedName~SettingsStoreTextTests|FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests`
(95 tests). Fixed-file MD5s: `Components/Pages/ExchangeOnlineConfig.razor` `2B3CD770E7668ABEE8ACAE5F22B2DBB2`,
`Components/Pages/AdminSettings.razor` `ED4F5DC8F6F197E83EF18503F24C5896`, `Services/SettingsStoreText.cs`
`E3867513F65BFECC3325A85C6A5936E1`; every mutant built with 0 errors and every file was restored to its MD5.
(HE3 ran first on AdminSettings `6415DA8E2948CC17CBD0BB70C465C9E5`, mutant `88017E0F2C8C759054EFC7F737BE8362`,
the same three failing; one refusal's wording - "the problem shown on this tab" - was then corrected and HE3
re-run on the final bytes, as tabled.)

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 7 | `ExchangeOnlineConfig.SaveExoConfig`: the drain folded back into the save's try (`DrainPool` straight after the write, its own catch gone) | `B9C8B4E02194FB7B707014310CE16D73` | `ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_IsItsOwnWarning_NotAFailedSave` (1 record - "Failed to save" - expected 2) |
| HE1 | extra, `SettingsStoreText.Enablement`: the legacy variant deleted | `B24253220272C8EFF088E93BAC1295E3` | `AdminSettings_ALegacyEnablementFileTheImportRefused_IsNamedInTheBanner` |
| HE2 | extra, `SaveExoConfig`: the drain's own record deleted | `A0556023998A45F63FBDA663AE7A70ED` | `ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_IsItsOwnWarning_NotAFailedSave` (1 record, expected 2) |
| HE3 | extra, `AdminSettings.LoadProtectedPrincipals`: the load error back to the operators' text | `64A74B5F9088DE865F40DE44EC4EA490` | both `AdminSettings_TheProtectedPrincipalsLoadError_NamesItsCause` rows, `AdminSettings_ALegacyProtectedPrincipalsFileTheImportRefused_IsNamedInTheLoadError` |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's
bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; warnings all pre-existing, none in a touched
  file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`: exit 0,
  5,321 passed / 3 skipped (the three known AD skips) / 0 failed, 5,324 total, 5m13s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check`: exit 0.
  `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: the failing
drain is driven by the config store's read switch, and the pool holds no runspace. No coder self-review
verdict is claimed.

## Known gaps

- `ProtectedPrincipalService.LoadEffectiveConfig`'s own errors ("Contact your administrator") are unchanged:
  they are what a module shows an operator whose action was refused, not an administrator's fix.
- A failed legacy import clears only when the application pool recycles - the import runs as the service
  is built - which the text now says; nothing re-imports at runtime.
- The drain's failed record uses the settings-change action under its own section; a dedicated audit action
  would read better in the Admin Event Log but would mean a new AuditService method, outside this finding.
- The banners are read from the compiled pages' frames; no browser rendered them.

## Branch-tip gates - 2026-10-22 (`6aaa1133`: all three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, warnings all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,321 passed / 3 skipped (the three known AD skips) / 0 failed, 5,324 total, 5m15s. No
  hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`:
  exit 0. `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
