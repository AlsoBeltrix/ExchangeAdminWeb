# aud-a28j: Settings saves with no corrupt-store re-check - a protected-principals save after a failed load wipes every protection rule

**Severity**: HIGH - `AdminSettings.razor` `SaveProtectedPrincipals` has no corrupt/unreadable
re-check and the Save button stays enabled when the load failed, so a save then writes the page's
EMPTY lists over the protected-principal store and removes every protection rule in one click. The
protected-principal store is the gate every mutating module consults (Known Failure Class 3). Same
class, lower impact: `ExchangeOnlineConfig.razor` `SaveModuleStatus` has no `IsStoreCorrupt`
refusal, and `SaveExoConfig` guards a corrupt config only with a disabled button, which a stale
render or a second tab can defeat.
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-af0cdb58244ff4bed`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28g/h/a26f/a28i agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `f1b4692d`: `Components/Pages/AdminSettings.razor` `SaveProtectedPrincipals` (refusals:
Authorization, ValidationInProgress; no store-state check; the page's lists are empty when the load
threw or the store read as corrupt); `Components/Pages/ExchangeOnlineConfig.razor` `SaveModuleStatus`
and `SaveExoConfig`. Compare `ModuleConfig.razor`'s saves, which refuse `StoreCorrupt` before writing,
and aud-a28f/a28i's allowlist save, which re-reads under the lock.

## Approach (to be written by the implementing agent before code)

- Every settings save that replaces a store's content re-checks the store immediately before the
  write (fresh read, or inside the write transaction as aud-a28i does) and refuses - audited
  `refusedAt: StoreCorrupt` / `StoreUnreadable`, `mutationAttempted: false` (aud-a28h helpers) -
  when the store is corrupt, unreadable, or when the page's loaded state did not come from a
  successful load. A page whose load failed must not offer Save at all, and the handler must refuse
  even if invoked (the button is not the guard).
- Protected principals: additionally refuse a save that would empty a store that currently holds
  rules unless the page explicitly loaded them and the admin removed them one by one (an
  empty-over-non-empty save from a page whose load produced nothing is refused with a message
  saying the rules could not be read and nothing was changed). No guess.
- Tests through `SettingsPageHarness` (aud-a28h): a failed load followed by Save writes nothing and
  leaves one refusal record; a corrupt store refuses; a healthy load and save still succeeds;
  ExchangeOnlineConfig's two saves refuse a corrupt store. Mutations at the call site: drop the
  re-check; drop the empty-over-non-empty refusal.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `374718b3`, with the partial
protected-principals save below folded in by codex attr-batch-4 (`83cb9992`). One commit
`fix(settings): a settings save re-checks its store and refuses a page that did not load it, and a
protected-principals save decides under the write lock and reports a partial save (aud-a28j)`, first
of three on this branch (then aud-a28k, aud-a26h). Behaviour: Admin Settings and Module Configuration
and the shared stores (base app version) and Exchange Online Config (the Exchange Online module's
version); the supervisor owns both bumps.

Survey at `374718b3` - every settings save that replaces a store's content:

| Page | Handler | Fresh re-check before the write | Page's load state consulted | Save offered after a failed load |
|---|---|---|---|---|
| `AdminSettings.razor` | `SaveEnablement` 560 | `IsStoreCorrupt` 582 | no (`enablementStoreCorrupt` 539) | yes (save bar 325) |
| | `SaveProtectedPrincipals` 961 | none | no: a load error (695) leaves the five lists empty | yes |
| `ModuleConfig.razor` | `SaveConfigAsync` 1105 | `IsModuleCorrupt` 1131 | no (`moduleConfigCorrupt` 961) | yes (save bar 670) |
| | `SaveModuleEnablementAsync` 1173 | `IsStoreCorrupt` 1194 | no (960) | yes |
| | `SaveSectionAccessAsync` 1246 | `IsFragmentCorrupt` 1267 | no (962); and `IsSectionAccessConfigured` (963) throws on an unreadable store, so that load fails outright | yes |
| | `SaveModuleAdminsAsync` 1352 | none | none: `GetModuleAdmins` (967) reads an unreadable store as no admins | yes |
| `ExchangeOnlineConfig.razor` | `SaveModuleStatus` 199 | none; `GetAllEnablement` (221) on a corrupt store is the all-disabled fallback, written back for every module (224) | none | yes (59) |
| | `SaveExoConfig` 251 | none | `configCorrupt` only through the button (81); a load that threw (193) leaves blank fields, `configCorrupt` false | yes |

Not content replaces: `SaveLogLevel` (one key; aud-a28k) and `SaveAllowlistAsync` (decides under the
lock since aud-a28i; its Save is withheld on a corrupt allowlist like the rest).

1. **Load state.** Each page records, per store, whether its load read the store: the existing
   corrupt flags, plus `moduleAdminsUnreadable` (`ModuleAdmin.TryGetAllGrants`), `ppLoaded`
   (`LoadEffectiveConfig` returned no error), and on Exchange Online Config `enablementStoreCorrupt`
   (`IsStoreCorrupt` at load) and `exoConfigLoaded` (`LoadExoConfig` neither found the module corrupt
   nor threw). `IsSectionAccessConfigured` is not asked when the section store is already corrupt, so
   that load completes and shows its banner instead of failing the whole page.
2. **Save not offered.** The save bar's Save on both tabbed pages is disabled while the current tab's
   store did not load (`CanSaveCurrentTab()`); Exchange Online Config's two buttons each follow their
   own store. New banners for the module-admins and Exchange Online enablement load failures, naming
   the table.
3. **The handler refuses even if invoked.** After the authorization re-check, through
   `LogSettingsSaveRefused` directly above the return, `mutationAttempted: false`:
   `StoreCorrupt` when the fresh re-check finds the store corrupt or unreadable now (kept where it
   exists; new on `SaveModuleAdminsAsync`, `SaveModuleStatus`, `SaveExoConfig`); `StoreUnreadable`
   when the page's own load did not read the store, so what it would write is not the store's state.
4. **Protected principals decide inside the write** (the aud-a28i shape). New
   `ProtectedPrincipalRepository.Replace(decide)`: one `_store.Write` (BEGIN IMMEDIATE) reads the
   rows and the presence marker on its own transaction and hands them to `decide`; null throws a
   private `NothingWritten`, so a refusal rolls back, change token included; returns the rows as read
   and as read back. New `ProtectedPrincipalService.ReplaceConfig(config, pageLoadedRules)` returns
   `Saved | StoreCorrupt | EmptyOverNonEmpty` with the before and after rows: `StoreCorrupt` when the
   legacy file was not imported or the rows cannot be read under the lock; `EmptyOverNonEmpty` when
   the new config holds no rule of any of the five kinds, the rows under the lock hold at least one,
   and the page loaded none - refused saying the rules could not be read (or there were none when
   the page loaded) and that nothing was changed. A page that loaded rules and had the admin remove
   them one by one still saves. No merge and no guess: the admin reloads.
5. **The folded-in partial save.** The directory-read secret id is written only when the admin changed
   it (`SecretIdDelta`), after the rows, in its own try, through new
   `ModuleConfigService.UpdateModuleValues` (upserts the named keys inside one write and returns the
   module's values as read under the lock); `SaveDirectoryReadSecretId` uses it. Its store is
   re-checked (`IsModuleCorrupt`) before anything is written. Records: the rows' success record with
   the real before and after (`Users:<value>`, `Groups:`, `OUs:`, `Patterns:`, `Targets:`, the rows read
   under the lock and read back - aud-a28k's before-state for this save lands here, because the
   partial-save record needs it); a changed secret's own success record (section
   `ProtectedPrincipals:DirectoryReadSecretId`); a secret write that throws after the rows landed
   leaves the rows' success record and a failure record for the secret, and the message says the
   rules were saved and the secret was not. The rows re-baseline; the secret stays pending.
6. **Registries.** `ClickGateRegistry`: all three pages NotYetConverted ("tier 2, not approved"), no
   line keys, no `ExpectedLineCount` - nothing re-anchors. No handler is added; `ProgressRegistry`'s
   pins hold.

Tests, new `ExchangeAdminWeb.Tests/SettingsSaveStoreCheckTests.cs` through `SettingsPageHarness` (a
second service over a second store on the same temp file stands in for the other instance): on each
of the three pages a load that could not read the store, then Save -> nothing written, exactly one
`StoreUnreadable` refusal, Save not offered; a store corrupt at save -> one `StoreCorrupt` refusal; a
healthy load and save still succeed. Protected principals: a failed load, an entry added, Save ->
refused, every stored rule kept; a page that loaded no rules saving over rules the other instance
added since -> `EmptyOverNonEmpty`, the rules kept; the rows unreadable under the lock -> refused, no
write; a throwing secret save after the rows saved -> the rows' success record (real removals and
additions) and the secret's failure record, the message says the rules were saved. Mutations at the
call site: (1) the protected-principals re-check dropped; (2) the empty-over-non-empty refusal
dropped; (3) an Exchange Online Config corrupt refusal dropped; (folded) one record for both again.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-af0cdb58244ff4bed`, the fix's services and tests in place and the three pages as
they were at `374718b3` swapped in (`git show 374718b3:<page>`; AdminSettings
`52C61BEFEBB229D9CB4664352DF4DA49`, ModuleConfig `AD1ACBD470556AC4E3E99BCA8354F85A`,
ExchangeOnlineConfig `612C8AD2520137908DDC20A156745D15`), build 0 errors: `SettingsSaveStoreCheckTests`
16 failed / 2 passed. Every "Save not offered" assertion failed - the button was live on every page whose
load could not read its store. Where the case got past that: the empty-over-non-empty save deleted the rule
the other instance had added (`Collections differ`, `[]`); the module-admins save over an unreadable store
dropped the second group; the Exchange Online status save over a corrupt store wrote the all-disabled
fallback for every module (`AccountLockoutRemediation:false ...`); the connection save over a corrupt store
replaced the organization; the rows unreadable under the lock were recorded as a failure, not a refusal;
the partial secret save left one record; the access page could not load at all (`config store unreadable`
out of `IsSectionAccessConfigured`). The two that passed: the legacy-file case, which calls the new service
directly, and the healthy-load control. The fixed bytes were restored to their MD5s afterwards.

## Fix

`Services/Storage/ProtectedPrincipalRepository.cs`: `Replace(decide)` - one `_store.Write` (BEGIN
IMMEDIATE) reads the rows and the marker on the write's own transaction (`ReadSnapshot`; a read that throws
is `ProtectedPrincipalSnapshot.Unreadable`) and hands them to `decide`; a returned set replaces the table
(delete, the five kinds inserted, marked present) and is read back before the commit; null throws the
private `NothingWritten`, which `SqliteConfigStore.Write` turns into a rollback - change token included -
and which is caught at once. New records `ProtectedPrincipalSnapshot` and `ProtectedPrincipalReplaceOutcome`,
`ProtectedPrincipalData.RuleCount`; the two readers gain transaction-aware overloads.

`Services/ProtectedPrincipalService.cs`: `ReplaceConfig(config, pageLoadedRules)` returns
`ProtectedPrincipalSaveOutcome(Status, Before, After)`: `StoreCorrupt` when the legacy file was not imported
(nothing opened) or the rows cannot be read under the lock; `EmptyOverNonEmpty` when the new set holds no
rule of any kind, the rows under the lock hold one or more, and the page loaded none; otherwise the rules
are replaced, read back and the cache invalidated. `SaveDirectoryReadSecretId` writes its one key through
`UpdateModuleValues` and returns the before and after.

`Services/ModuleConfigService.cs`, `Services/Storage/ModuleConfigRepository.cs`: `UpdateModuleValues` /
`UpdateModule(moduleId, values)` - one write that upserts only the named keys and marks the module
present, returning the module's values as read under the lock and as left (`ModuleConfigUpdate`); raises
`ConfigSaved` as `SaveModuleConfig` does.

`Components/Pages/AdminSettings.razor`: `ppLoaded`, set only when `LoadEffectiveConfig` returned no error;
`CanSaveCurrentTab()` withholds Save on the Modules tab while `enablementStoreCorrupt` and on the Protected
tab until `ppLoaded`. `SaveEnablement`: after the fresh `IsStoreCorrupt` refusal, `StoreUnreadable` while the
page's toggles are not the store's. `SaveProtectedPrincipals`: after Authorization and ValidationInProgress,
`StoreUnreadable` when `!ppLoaded`; when the secret id changed, `StoreCorrupt` if the ProtectedPrincipals
module config cannot be read; then `ReplaceConfig(newConfig, LoadedRuleCount() > 0)` and its `StoreCorrupt`
and `EmptyOverNonEmpty` refusals; then the rules' success record, `Kind:value` before (the rows under the
lock) and after (read back) with `counts`; then, only when it changed, the secret through
`SaveDirectoryReadSecret` - its own success record under `ProtectedPrincipals:DirectoryReadSecretId`, or on a
throw its own failure record and the message "The protection rules were saved and are in effect, but the
directory-read credential (Delinea Secret ID) was not saved: ... Save again to retry it." The rules
re-baseline and the secret stays pending.

`Components/Pages/ModuleConfig.razor`: `moduleAdminsUnreadable` from `ModuleAdmin.TryGetAllGrants`, the
module's groups taken from the same read, with a banner naming `module_admins`; `IsSectionAccessConfigured`
asked only when the section store is not already corrupt; `CanSaveCurrentTab()` for all five tabs.
`StoreUnreadable` refusals on the load flags in `SaveConfigAsync`, `SaveModuleEnablementAsync`,
`SaveSectionAccessAsync` (before the directory confirmation) and `SaveModuleAdminsAsync`;
`SaveModuleAdminsAsync` re-reads its store after the directory confirmation, refuses `StoreCorrupt` when it
cannot, and takes its before-state from that read.

`Components/Pages/ExchangeOnlineConfig.razor`: `enablementStoreCorrupt` (read at load) and
`exoConfigLoaded` (set only when `LoadExoConfig` finished without its corrupt branch or its catch), each with
a banner naming the table and its button withheld - Save Status and its toggle; Save Connection on
`!exoConfigLoaded`, which covers `configCorrupt`. `SaveModuleStatus`: fresh `IsStoreCorrupt` -> `StoreCorrupt`,
then the load flag -> `StoreUnreadable`. `SaveExoConfig`: `!exoConfigLoaded` -> `StoreUnreadable` after
Authorization; fresh `IsModuleCorrupt` -> `StoreCorrupt` after validation, immediately before the write.

Tests: new `ExchangeAdminWeb.Tests/SettingsSaveStoreCheckTests.cs` (18 cases). `SettingsPageHarness`: the
three handler shells render their markup only when a test reads it (`IMarkupShell`, used by `RenderedText`
and the new `IsButtonDisabled`), and `CreateOtherProtectedPrincipals()`, the other instance.

Caller-visible change, by design: on all three pages a tab whose store did not load offers no Save, and a
save from such a page, or over a store that reads as corrupt, is refused and recorded, where it used to
write the page's fallback state. A protected-principals save holding no rules over a store holding some is
refused unless the page had loaded rules. A changed directory-read secret id gets its own record, an
unchanged one is no longer rewritten, and a secret that fails after the rules saved is reported as exactly
that. The module-admins tab shows a banner when its store cannot be read.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad's `a28j-a28k-a26h-work`): back up, mutate by exact
text, MD5, `dotnet build` of the test project, `dotnet test` filtered to
`FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests` (65 tests),
restore from the backup, set the write time, MD5 re-confirmed. Fixed-file MD5s:
`Components/Pages/AdminSettings.razor` `6C3C208CCC8503C14C8B0B27981B2212`,
`Components/Pages/ModuleConfig.razor` `1A5A38150093E0CE63DE5CA50F655482`,
`Components/Pages/ExchangeOnlineConfig.razor` `3F5014B1374DCE1530C053E296D262B7`,
`Services/ProtectedPrincipalService.cs` `56B60A8EBEEB7C9A49C1834AF3070765`; every mutant built with 0 errors
and every file was restored to its MD5.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `ProtectedPrincipalService.ReplaceConfig`: the re-check dropped (the legacy-file return and the unreadable-under-the-lock refusal) | `FC865218E9C065CA3D800A19207BA75D` | `ProtectedPrincipals_ASaveOverALegacyFileThatWasNotImported_IsRefused_AndLeavesTheStoreUnconfigured` (the store marked configured over the unimported file), `AdminSettings_RulesUnreadableAsTheSaveWrites_AreRefused_AndNothingIsWritten` (recorded as a failure, not a refusal) |
| 1b | extra, `AdminSettings.SaveProtectedPrincipals`: the page's `!ppLoaded` refusal dropped | `0B1110C6919E34D5EAF2791C53DFD2D4` | `AdminSettings_AProtectedPageWhoseLoadFailed_DoesNotOfferSave_AndItsSaveKeepsEveryRule` (`Collections differ at index 0`: both stored rules replaced by the one pattern the page added) |
| 2 | `ReplaceConfig`: the empty-over-non-empty refusal dropped | `00EA28D27AFF75A298EEBCE9346D16F1` | `AdminSettings_ANoRulesSaveOverRulesAddedSinceThePageLoaded_IsRefused_AndTheRulesStay` (the other instance's rule deleted) |
| 3 | `ExchangeOnlineConfig.SaveExoConfig`: the `IsModuleCorrupt` refusal dropped | `F62A79EA77EB7B3CCC35CF35DC1FA921` | `ExchangeOnlineConfig_AConnectionSaveOverACorruptStore_IsRefused_AndKeepsTheConnection` (the stored organization replaced) |
| F | folded: one record for both again (the secret written unguarded before the rules' record) | `8F1B4EEC628763676FAFB7450DF35FA0` | `AdminSettings_ASecretSaveThatThrowsAfterTheRulesSaved_RecordsBoth_AndSaysTheRulesWereSaved` (1 record, expected 2), `AdminSettings_AChangedSecret_IsSavedWithItsOwnRecord` |
| E1 | extra, `AdminSettings.CanSaveCurrentTab`: Save always offered | `A1274D8870326F388629725522E4CF9F` | `AdminSettings_AProtectedPageWhoseLoadFailed_...`, `AdminSettings_AnEnablementPageWhoseLoadFailed_...` |
| E2 | extra, `ModuleConfig.SaveModuleAdminsAsync`: the fresh re-check made unreachable | `AE7EB0B2C9BADE806318EB9DF36881DE` | `ModuleConfig_AnAdminsSaveOverAStoreUnreadableNow_IsRefused_AndKeepsTheGroups` (the second group dropped) |
| E3 | extra, `ModuleConfig.LoadState`: `IsSectionAccessConfigured` asked of a corrupt store again | `5EA5AD9A89E8F72139E6FA233F0F62F4` | `ModuleConfig_AnAccessPageWhoseLoadFailed_Loads_DoesNotOfferSave_AndItsSaveKeepsTheGrants` (`config store unreadable` out of the load) |
| E4 | extra, `ExchangeOnlineConfig.SaveModuleStatus`: the fresh refusal made unreachable | `2515A366C3849C016CA62B3C29D1569C` | `ExchangeOnlineConfig_AStatusSaveOverACorruptStore_IsRefused_AndDoesNotWriteTheFallback` (the fallback written) |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's
bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; warnings all pre-existing, none in a touched
  file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`: exit 0,
  5,291 passed / 3 skipped (the three known AD skips) / 0 failed, 5,294 total, 5m28s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check`: exit 0.
  `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: no case adds
a usable group grant, the harness leaves the directory services unset, and no rendered row carries a stored
display name (the one path that would translate a SID). The pages were not rendered in a browser. No coder
self-review verdict is claimed.

## Known gaps

- The protected-principals save still replaces the whole rule set from the page's lists: on a page holding
  at least one rule, a rule another admin added after it loaded is removed by its save - now named in the
  record, whose before-state is the rows it replaced. Only a page that loaded no rules is refused. A content
  version compared under the lock, as aud-a28i does for the allowlist, would close it; outside this finding.
- `SaveConfigAsync` and `SaveModuleAdminsAsync` still replace a module's whole config or admin list from the
  page's state, so a value or group changed elsewhere on that module since the page loaded is overwritten
  (and recorded). The aud-a28k class, outside its listed saves.
- "Save not offered" is the rendered disabled attribute on the compiled page; no browser or live circuit ran.
- Two in-process stores on one temp file stand in for the two instances.

## Folded in: a partial protected-principals save audited only as failed (codex, attr-batch-4, 2026-10-22)

`AdminSettings.razor` `SaveProtectedPrincipals` (around line 1004 at `f1b4692d`): `SaveConfig(newConfig)`
succeeds, then `SaveDirectoryReadSecretId(...)` throws. The rules are live, the page says "Failed to
save", and the only record is a generic failed `ProtectedPrincipals` settings save with no
before/after detail. Save the rows and the directory-read secret in one atomic store write (one
`Write` callback, aud-a28i shape), or - if the secret id lives in another store - audit the partial
outcome explicitly: a success record for the rows that landed (real before/after) and a failure record
for the secret, and a message that says which part landed. Test: a throwing secret save after a
successful rows save leaves the rows' success record and the secret's failure record, and the message
says the rules were saved. Mutation at the call site: one record for both again.
