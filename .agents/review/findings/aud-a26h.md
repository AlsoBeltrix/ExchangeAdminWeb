# aud-a26h: Other corrupt-store texts still name retired JSON files, and a pool-drain failure after a successful save is shown as a failed save

**Severity**: LOW - wording and reporting, no wrong write. (1) The corrupt-store banners and errors
for stores other than the allowlist (aud-a26f fixed only that one) still tell an administrator to
fix a `config/*.json` file; those stores live in the SQLite config database since the legacy import.
(2) `ExchangeOnlineConfig.razor` `SaveExoConfig`: when the write succeeds and the following
`DrainPool` fails, the page shows and records "Failed to save", though the configuration was saved;
the admin re-saves a value that is already there and the audit says a save failed that did not.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-af0cdb58244ff4bed`
**Commit**: -
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

## Guard proof

Pending.
