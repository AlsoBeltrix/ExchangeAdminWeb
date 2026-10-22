# aud-a28r: A Secret ID refused under the lock leaves the protected-principals save partial

**Severity**: LOW/MEDIUM - a partial after a successful write, the aud-a2f class on a settings page.
Since aud-a26n the directory-read Secret ID is compared against the value the page loaded inside
the locked write, but the rules are written FIRST: when the secret compare refuses `ConcurrentChange`,
the rules are already saved and the secret is not. aud-a28j's folded-in part records both outcomes
honestly (a success record for the rules, a refusal for the secret, a message saying which part
landed), so nothing is silent; but the save the admin confirmed as one change lands as half.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28p/a26n agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At the aud-a28p/a26n branch tip `a904c288`, `Components/Pages/AdminSettings.razor` `SaveProtectedPrincipals`:
`ReplaceConfig` (rules, under the lock) then the secret's `SaveChangedValues` compare; the refusal
path after the rules' success record.

## Approach (to be written by the implementing agent before code)

- Decide both inside one write where the stores allow it (the rules and the ProtectedPrincipals
  module config are two stores; if one `Write` callback can span both repositories through the shared
  `SqliteConfigStore`, do that), so a secret conflict rolls the rules back too; otherwise run the
  secret's compare (read under the lock, no write) BEFORE the rules write, refuse the whole save on a
  conflict, and write the secret after the rules only when the compare passed - accepting the
  narrower window and saying so in the record.
- Tests through `SettingsPageHarness` with two stores on one temp file: a secret changed elsewhere
  before the save refuses the whole save with nothing written (rules included); a clean save writes
  both. Mutation at the call site: write the rules before the compare again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `34c7ccd4`. One commit
`fix(settings): a protected-principals save compares a changed Secret ID before it writes the rules, and a
conflict refuses the whole save (aud-a28r)`, first on this branch (then aud-a26p). Behaviour: Admin Settings and
the shared module-config and protected-principal services (base app version); no module version moves; the
supervisor owns the bump.

Evidence re-read at `55a111e9`: `AdminSettings.razor` `SaveProtectedPrincipals` 1083-1246 refuses a changed
secret the page could not load (1134) or whose rows read as corrupt (1143), then writes the rules
(`ReplaceConfig` 1165), records them, and only then decides the secret (`SaveDirectoryReadSecret` 1220, 1260-1300
-> `ProtectedPrincipalService.SaveDirectoryReadSecretId` 1325-1329 -> `ModuleConfigService.SaveChangedValues`
150-209, its compare under its own lock): a `ConcurrentChange` there is the secret's refusal after the rules'
success record.

**Shape chosen: the compare first, not one write.** One `SqliteConfigStore.Write` callback (29-44) is one
connection and one transaction, and production hands every repository the one `IConfigStore` singleton
(`Program.cs` 72-79), so SQL inside one callback could reach both tables. No repository can take part in it:
`ProtectedPrincipalRepository.Replace` (106-136) and `ModuleConfigRepository.UpdateModuleIf` (125-153) each open
their own `_store.Write`, and a second write opened inside the first is a second connection's BEGIN IMMEDIATE
waiting on the lock the first holds - the 5-second busy timeout, then SQLITE_BUSY. Spanning them is a new seam:
the protected-principal write reaching module_config's rows through ModuleConfigRepository's private helpers, past
ModuleConfigService (its write lock; `ConfigSaved`, which only it can raise), with the atomicity resting on DI
handing both repositories one database - which no type says, and which `SettingsPageHarness` deliberately does not
do (each repository over its own switchable store, so a store fault is per store). A cross-repository unit of work
for a LOW/MEDIUM finding is the new abstraction the Constitution's Code Change Discipline rules out. So:

1. **The compare writes nothing.** New `ModuleConfigService.CompareChangedValues(moduleId, loaded, wanted)`:
   `SaveChangedValues`' per-key decision (absent as empty, ordinal: still as loaded -> would be written; already
   the wanted value -> already applied; anything else -> conflicting) made inside one `UpdateModuleIf` whose decide
   stores nothing, so the transaction rolls back, change token included. One private decision serves both, so the
   compare and the write cannot drift; the outcome type is `SaveChangedValues`' (`Saved` there: the compare passed).
   New `ProtectedPrincipalService.CompareDirectoryReadSecretId(loaded, wanted)` beside `SaveDirectoryReadSecretId`.
2. **Before the rules.** `SaveProtectedPrincipals`, after the secret's two existing refusals (1134, 1143) and before
   `ReplaceConfig` (1165), when the secret changed: the compare against the baseline. `ConcurrentChange` -> refusal
   `ConcurrentChange` under `ProtectedPrincipals` (`LogSettingsSaveRefused`, `mutationAttempted: false`), the message
   naming the field and saying nothing was saved, the rules included; `StoreCorrupt` -> refusal `StoreCorrupt`.
   Already applied passes. Then the rules, then the secret as today, its own compare under its own lock.
3. **The residual window.** The rules' write sits between the compare's lock and the secret write's: a change to
   the secret landing inside that one save - another admin, or the other instance - is still refused by the secret's
   own compare after the rules landed, aud-a28j's partial path, recorded as today (the rules' success record, the
   secret's refusal, the message saying which part landed). A secret write that throws after the rules is unchanged.
4. **Registries and docs.** ClickGateRegistry: AdminSettings.razor NotYetConverted ("tier 2, not approved", 77), no
   line keys; nothing re-anchors. No handler added; ProgressRegistry's AdminSettings entry stands
   (`SaveCurrentTabAsync` exempt as a config-store write). `SettingsSaveAuditTests`'
   `EveryRefusalAndCatchInASettingsSave_WritesItsRecord` covers the new refusal returns.
   `docs/AdminModuleDeveloperGuide.md` 523-549 gains `CompareChangedValues`.

Tests, new `ExchangeAdminWeb.Tests/SettingsWholeProtectedSaveTests.cs` through `SettingsPageHarness`, two stores on
one temp file (the other instance `CreateOtherModuleConfig()`): a secret changed elsewhere between load and save ->
the whole save refused, one `ConcurrentChange` refusal, rules and secret as stored, the change token unmoved; the
same landing as the compare opens its write (`ModuleConfigStore.BeforeWrite`); a clean save writes both, two
success records; the window: a change landing as the rules' write opens (`ProtectedStore.BeforeWrite`) -> the rules
saved and the secret refused, both recorded. At the service: the compare writes nothing (token, `updated_at`) and
reports the conflict. `SettingsConnectionAndSecretSaveTests`' two cases that pinned the partial for a change made
before the save move to the whole-save refusal. Mutation at the call site: (1) the rules written before the compare
again - the compare taken out, so the secret is first compared inside its own write, after the rules.

## Guard proof

Pending.
