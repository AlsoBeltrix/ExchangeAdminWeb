# aud-a28r: A Secret ID refused under the lock leaves the protected-principals save partial

**Severity**: LOW/MEDIUM - a partial after a successful write, the aud-a2f class on a settings page.
Since aud-a26n the directory-read Secret ID is compared against the value the page loaded inside
the locked write, but the rules are written FIRST: when the secret compare refuses `ConcurrentChange`,
the rules are already saved and the secret is not. aud-a28j's folded-in part records both outcomes
honestly (a success record for the rules, a refusal for the secret, a message saying which part
landed), so nothing is silent; but the save the admin confirmed as one change lands as half.
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-a992f9d6471a07501`
**Commit**: `dbcac40f` (plan `c8deba78`; comments `07ef4e8e`)
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

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a992f9d6471a07501`, the fix's services and tests in place and `Components/Pages/AdminSettings.razor`
as it was at `55a111e9` swapped in (`git show`; `git hash-object` equal to the blob `93e5ee89`, MD5
`D8C205490ED99BC818FC93716440236F`), build 0 errors: `SettingsWholeProtectedSaveTests`, `SettingsConnectionAndSecretSaveTests`
and `SettingsSaveStoreCheckTests` 5 failed / 37 passed. A secret changed elsewhere between load and save, and one landing as
the secret's store opened its first write, each had the rules written anyway (`"SamAccountNamePatterns":["svc-*"]` stored,
`[]` expected; `sam_pattern:svc-*` where `user:cfo@contoso.com` was the first row) and left two records where one refusal was
expected; a secret store that could not open a write had the rules written and then failed the secret. The 37 that passed
include the clean save, the named window - the old order produces it too - and the service's compare, which is new code and
was not swapped. The fixed page was restored to `A14C80B2B968A6501BAA61EE476C7A10` and touched.

## Fix

`Services/ModuleConfigService.cs`: `SaveChangedValues` and new `CompareChangedValues` share one private
`DecideChangedValues(moduleId, loaded, wanted, writeIfClear)` - the per-key decision inside one `UpdateModuleIf`. With
`writeIfClear` false a passing decision stores nothing either, so the compare's transaction is rolled back, change token
included; `Saved` from the compare means it passed.

`Services/ProtectedPrincipalService.cs`: `CompareDirectoryReadSecretId(loaded, wanted)`, the compare for the one key.

`Components/Pages/AdminSettings.razor` `SaveProtectedPrincipals`: when the secret changed, after its two existing refusals and
before `ReplaceConfig`, the compare against the baseline; `StoreCorrupt` and `ConcurrentChange` refuse the whole save through
`LogSettingsSaveRefused` under `ProtectedPrincipals`, saying nothing was saved, the rules included. The secret's own write
after the rules is unchanged.

`docs/AdminModuleDeveloperGuide.md`: `CompareChangedValues` in the module-config API, and the rule for a save across two
stores.

Tests: new `ExchangeAdminWeb.Tests/SettingsWholeProtectedSaveTests.cs` (6 cases). `SettingsConnectionAndSecretSaveTests`: its
two cases for a secret changed before the save - between load and save, and as the secret's store opens its first write,
which is now the compare's - assert the whole-save refusal, one record, where they asserted the partial; the partial's helper
is gone. `SettingsSaveStoreCheckTests.AdminSettings_ASecretSaveThatThrowsAfterTheRulesSaved_RecordsBoth_AndSaysTheRulesWereSaved`:
the secret's store fails from the rules' write on (`ProtectedStore.BeforeWrite`) rather than from the start, because a store
failing from the start now fails the compare and with it the whole save; its assertions are unchanged. One case beyond the
plan: `ASecretStoreThatCannotOpenAWrite_FailsTheWholeSave_BeforeTheRules`, since the compare holds the secret's write lock and
that is new behaviour.

Caller-visible change, by design: on Admin Settings, a protected-principals save whose Secret ID was changed elsewhere since
the page loaded is refused whole - nothing saved, the rules included, one refusal record - where it saved the rules and
refused the secret; a secret store that cannot open a write fails the whole save before the rules. A change landing inside
the save after the compare still saves the rules and refuses the secret, recorded as both, as before.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad's `a28r-a26p-work`): back up, mutate by exact text (one match required),
MD5, `dotnet build` of the test project, `dotnet test` filtered to
`FullyQualifiedName~SettingsWholeProtectedSaveTests|FullyQualifiedName~SettingsConnectionAndSecretSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~SettingsNoOpAndUnreadableSecretTests|FullyQualifiedName~SettingsPerRowSaveTests|FullyQualifiedName~ProtectedPrincipalServiceTests|FullyQualifiedName~ModuleConfigServiceTests|FullyQualifiedName~AdminPageDirtyStateTests|FullyQualifiedName~ProgressRegistryTests|FullyQualifiedName~WholeStoreWriterTripwireTests|FullyQualifiedName~ProtectedGroupWriteTargetTests|FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsStoreTextTests`
(322 tests), restore from the backup, set the write time, MD5 re-confirmed. Fixed-file MD5s:
`Components/Pages/AdminSettings.razor` `A14C80B2B968A6501BAA61EE476C7A10`, `Services/ModuleConfigService.cs`
`A53FCA7B8018A2DB031038C438077F92`; every mutant built with 0 errors and each file was restored to its MD5. After the round,
the restored tree rebuilt and the same filter: 322/322 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `AdminSettings.SaveProtectedPrincipals`: the rules written before the compare again - the compare ahead of `ReplaceConfig` disabled (`if (secretChanged && false)`), so the secret is compared only inside its own write, after the rules | `4A6C977B955030FE5E201EFA71C1BECA` | `ASecretChangedElsewhereBeforeTheSave_RefusesTheWholeSave_AndWritesNothing_RulesIncluded` (`"SamAccountNamePatterns":["svc-*"]` stored, `[]` expected), `ASecretChangedElsewhereAsTheCompareOpensItsWrite_RefusesTheWholeSave_RulesIncluded` and `ASecretStoreThatCannotOpenAWrite_FailsTheWholeSave_BeforeTheRules` (`sam_pattern:svc-*` stored), `SettingsConnectionAndSecretSaveTests.AdminSettings_ASecretChangedElsewhere_IsNotOverwritten_AndItsSaveIsRefused` and `..._InsideTheSave_IsNotOverwritten` (two records, the rules' success and the secret's refusal, where one was expected) |
| E1 | extra, `ModuleConfigService.CompareChangedValues`: the compare writes (`writeIfClear: true`) | `6A4D45056CC195608946FB8E9F4FB91D` | `TheCompare_WritesNothing_AndReportsWhatASaveWouldMeet` (`"4242"` stored by the compare, `"777"` expected), `ACleanSave_WritesTheRulesAndTheSecret_EachWithItsRecord` (the secret's record added nothing: its write found the compare's value); collateral in `SettingsSaveStoreCheckTests`: `AdminSettings_ANoRulesSaveOverRulesAddedSinceThePageLoaded_IsRefused_AndTheRulesStay` and `AdminSettings_ASecretSaveThatThrowsAfterTheRulesSaved_...` (the secret stored by a save that wrote nothing else), `AdminSettings_AChangedSecret_IsSavedWithItsOwnRecord` |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,624 passed / 3 skipped (the three known AD skips) / 0 failed, 5,627 total, 5m23s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell session, directory, Exchange Online, Delinea, SMTP, push or deployment: the one rule edit in the new cases is a
pattern, never looked up, and the harness leaves the directory services unset. The page was not rendered in a browser. No
coder self-review verdict is claimed.

## Known gaps

- The window named in plan item 3: a change to the secret landing during the rules' write of the same save - another admin, or
  the other instance, within that save's milliseconds - still leaves the rules saved and the secret refused, recorded as both;
  and a secret write that throws after the rules is aud-a28j's partial, unchanged. Closing either needs the two repositories in
  one transaction, which no repository supports today.
- The compare takes the secret's write lock to read; a database whose write lock is held past the 5-second busy timeout fails
  the save before anything is written, where it used to fail only the secret after the rules.
- Two in-process stores on one temp file stand in for the two instances; nothing ran against the shared database.

## Branch-tip gates - 2026-10-22 (`07ef4e8e`: both fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,639 passed / 3 skipped (the three known AD skips) / 0 failed, 5,642 total, 5m26s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
