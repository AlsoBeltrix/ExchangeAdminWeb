# aud-a28n: Protected principals, module config and module admins are still replaced wholesale from the page's state

**Severity**: MEDIUM - the aud-a28i / aud-a28k class on three more authorization-adjacent stores
under the shared config database (Architectural Invariant 2). (1) The protected-principals save
replaces the whole rule set from the page's lists: a page holding at least one rule removes a rule
another admin or the other instance added since it loaded; aud-a28j refuses only the
empty-over-non-empty case and records the rest. (2) `ModuleConfig.SaveConfigAsync` replaces a
module's whole config, and `SaveModuleAdminsAsync` the whole admin list, from the page's state, with
no compare against the rows under the lock.
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-a5f93bbf442feb5e2`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28j/a28k/a26h agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At the aud-a28j/a28k/a26h branch tip `ade2a1d7`: `Services/ProtectedPrincipalService.cs`
`ReplaceConfig` (decides inside one locked write since aud-a28j, but replaces all rules);
`Components/Pages/ModuleConfig.razor` `SaveConfigAsync`, `SaveModuleAdminsAsync`. Compare aud-a28i's
content-version compare-and-swap and aud-a28k's per-row decisions under the lock.

## Approach (to be written by the implementing agent before code)

- Protected principals: a content version (SHA-256 over the rules, aud-a28i shape) captured at load
  and checked inside the locked write; a mismatch refuses `ConcurrentChange` (audited,
  `mutationAttempted: false`, reload and re-apply). Keep aud-a28j's refusals.
- Module config: write only the keys the admin changed, each compared to the row under the lock, or
  a content version over the module's config; module admins: per-entry decisions under the lock
  (added/removed entries compared against current rows), refusing on a concurrent change naming the
  entry. Nothing merged or guessed.
- Tests in the aud-a33b two-stores style: a rule added elsewhere between load and save survives (or
  the save is refused as a conflict); a config key changed elsewhere is not reverted; an admin added
  elsewhere is kept. Pages `NotYetConverted` in `ClickGateRegistry`; confirm. Mutations at the call
  site: drop the version check; write the whole config again; write the whole admin list again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `714078b9`. One commit
`fix(settings): a protected-principals save compares a content version under the write lock, and a
module's config and admins are written per key and per entry, decided under the lock (aud-a28n)`,
first of two on this branch (then aud-a26k). Behaviour: Admin Settings, Module Configuration and the
shared protected-principal, module-config and module-admin services (base app version); no module
version moves; the supervisor owns the bump.

Evidence re-read at `cc4f9c5a`: `AdminSettings.razor` `LoadProtectedPrincipals` 741-765 shows the
cached `LoadEffectiveConfig` lists and keeps nothing to compare the store against; `SaveProtectedPrincipals`
1020-1145 calls `ReplaceConfig` (1089), whose decide (`ProtectedPrincipalService.cs` 1031-1077) refuses
only rows unreadable under the lock and the empty-over-non-empty case, then replaces every rule.
`ModuleConfig.razor` `SaveConfigAsync` 1132-1209 reads the module outside any write (1178) and replaces
it whole from `currentConfigState` (1179; `ModuleConfigRepository.SaveModule` 78-93, delete then insert):
a key changed elsewhere since the page loaded - one the page does not show included, MailboxPermissions'
`ExcludedUsers` protection list - is written back as the page loaded it. `SaveModuleAdminsAsync`
1441-1531 replaces the module's list with the page's snapshot (1497; `ModuleAdminRepository.SetForModule`
53-59): a group added elsewhere is dropped, one removed elsewhere comes back.

1. **Protected principals: a content version.** `ProtectedPrincipalRepository.ReadSnapshot()` becomes
   public over a fresh read (rows and marker; unreadable as `Unreadable`). `ProtectedPrincipalService.RulesVersion`:
   SHA-256 over the five kinds' rows, each sorted, and the presence marker - the aud-a28i `AllowlistVersion`
   shape: the rules' own content, not the store's change token, which moves on every config write by either
   instance and misses a writer that bypasses the store. New `LoadRulesForEditing()` returns the rules and the
   version of one read (null: the legacy file was not imported, or the rows cannot be read). The page keeps
   `LoadEffectiveConfig` as its load gate (errors and banner unchanged) and takes its lists and version from
   `LoadRulesForEditing`, so what it shows and the version it holds are one read.
   `ReplaceConfig(config, pageLoadedRules, loadedVersion)`: inside the write, after aud-a28j's `StoreCorrupt`
   and `EmptyOverNonEmpty`, a version that no longer matches the rows under the lock refuses `ConcurrentChange`,
   nothing written (rollback, change token included) - unless the rows already hold exactly the rules this
   save writes (per kind, as sets ignoring case, marker set): `AlreadyApplied`, nothing written, not a refusal
   (the aud-a26k rule, below). The outcome carries the version of the rows left on `Saved` and `AlreadyApplied`;
   the page keeps it, so a second save from one page is not refused. Page: `ConcurrentChange` ->
   `LogSettingsSaveRefused(..., "ConcurrentChange", ...)` ("changed after this page loaded them ... reload the
   page and re-apply your changes"); `AlreadyApplied` -> the rules' success record with nothing added or removed
   and `alreadyApplied` naming the entries the admin changed, then the secret as on `Saved`. Nothing merged or
   guessed.
2. **Module config: only the changed keys, each decided under the lock.** New
   `ModuleConfigRepository.UpdateModuleIf(moduleId, decide)`: one write; `decide` sees the module's values read
   on the write's transaction (null when that read throws) and returns the keys to upsert, or null to store
   nothing (rollback, change token included); returns the values as read and, after a write, as read back.
   New `ModuleConfigService.SaveChangedValues(moduleId, loaded, wanted)` -> `ModuleConfigSaveOutcome(Status,
   ConflictingKeys, AlreadyApplied, Before, After)`, per key the admin changed (absent read as empty, values
   compared ordinal): the row still as the page loaded it -> written; already the wanted value ->
   `AlreadyApplied`, not written; anything else -> `ConcurrentChange` naming every such key, nothing written;
   nothing to write -> `NothingToSave`; values unreadable under the lock -> `StoreCorrupt`. A key the admin did
   not change is never written. Page `SaveConfigAsync`, after its existing refusals: the keys whose value
   differs from the loaded baseline (the `RecountConfig` diff), with loaded and wanted values; `StoreCorrupt`
   and `ConcurrentChange` (each key by its field label) refuse through `LogSettingsSaveRefused`; the success
   record lists the written keys with the values under the lock as before (masked as today), and
   `alreadyApplied` when any; nothing changed -> "unchanged; there was nothing to save", no record (the aud-a28k
   access-save precedent).
3. **Module admins: per-entry decisions under the lock.** New `ModuleAdminRepository.ApplyChanges(moduleId,
   add, remove)`: one write; reads the module's groups on the write's transaction (unreadable: nothing written,
   reported); an added group already present and a removed group already absent are `AlreadyApplied`; the rest
   are inserted or deleted one by one; nothing to write rolls back; returns the groups as read and as left.
   `ModuleAdminService.ApplyAdminChanges` holds the service's lock round it. A membership entry has two states,
   the page's loaded one and the wanted one, so the row under the lock cannot differ from both: the refusal the
   approach anticipates has no state to fire on (aud-a28k's enablement reasoning, and the aud-a26k rule), and no
   `ConcurrentChange` is built for it. Groups the admin did not touch are never written: one added elsewhere is
   kept, one removed elsewhere stays removed. Page `SaveModuleAdminsAsync`: added and removed from the confirmed
   snapshot against the loaded baseline, never the live list; aud-a28j's fresh `TryGetAllGrants` re-check stays;
   the success record's before and after are the rows under the lock and as left, with `alreadyApplied` when any.
4. **Registries and tripwires.** ClickGateRegistry: AdminSettings.razor and ModuleConfig.razor NotYetConverted
   ("tier 2, not approved"), no line-keyed entry, no ExpectedLineCount - confirmed at `cc4f9c5a`; nothing
   re-anchors. No handler added; ProgressRegistry's pins hold. `GroupGrantValidationTests`' admins-save tripwire
   (146-158) follows the new persist call - same property: confirm, then persist the confirmed snapshot's delta,
   never the live list. `SettingsSaveAuditTests`' handler tripwire covers the new refusal returns.

Tests, new `ExchangeAdminWeb.Tests/SettingsPerRowSaveTests.cs` through `SettingsPageHarness` (aud-a33b style;
new `CreateOtherModuleConfig()` and `CreateOtherModuleAdmins()`: a second service over its own store on the same
file). Protected principals: a rule added elsewhere between load and save refuses `ConcurrentChange`, the rule
kept, one refusal; the same landing inside the save (`ProtectedStore.BeforeWrite`); the same rule added elsewhere
is a no-op success naming it; two saves from one page both land. Module config: a key changed elsewhere is not
reverted, the record names only the admin's key; the admin's own key changed elsewhere refuses naming it, one
refusal, nothing written; the same inside the save (`ModuleConfigStore.BeforeWrite`); an equal change is a
no-op. Module admins: a group added elsewhere is kept; a group removed elsewhere is not restored; an equal
removal is a no-op. Mutations at the call site: (1) the protected-principals version check dropped; (2)
`SaveConfigAsync` writes the whole config again (`SaveModuleConfig(module.Id, currentConfigState)`); (3)
`SaveModuleAdminsAsync` writes the whole list again (`SetModuleAdmins(module.Id, snapshot)`).

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a5f93bbf442feb5e2`, the fix's services, harness and tests in place and
`Components/Pages/ModuleConfig.razor` as it was at `cc4f9c5a` swapped in (`git show
cc4f9c5a:Components/Pages/ModuleConfig.razor`, MD5 `617B0AF0E7DEE9EB0D76219803C318EA`), build 0 errors:
`SettingsPerRowSaveTests` 7 failed / 4 passed. Every module-config and module-admins case failed: a key
changed elsewhere was reverted (`PreventSelfGrant` `"true"` stored, `"false"` expected; the `ExcludedUsers`
protection list with it); the admin's own key changed elsewhere was overwritten rather than refused, between
load and save and inside the save (`"4242"` stored, `"88"` expected); a group added elsewhere was dropped
(`[]` stored); a group removed elsewhere came back (`Assert.Empty` failed); and the two equal changes wrote
anyway (change token 2 -> 3). The four that passed are the protected-principals cases, which run on the fixed
Admin Settings: its pre-fix bytes do not compile against the new `ReplaceConfig` signature, so mutation 1 below
stands in for them. The fixed page was restored to `3BD655E9CAE772A24B5DA7E702A60CDB` and touched.

## Fix

`Services/Storage/ProtectedPrincipalRepository.cs`: public `ReadSnapshot()` - one fresh read of the rows and
the marker, an unreadable store as `ProtectedPrincipalSnapshot.Unreadable`.

`Services/ProtectedPrincipalService.cs`: `RulesVersion(snapshot)` - SHA-256 over the marker and each kind's rows,
sorted (the aud-a28i shape). `LoadRulesForEditing()` - the rules and the version of one read (null: legacy file not
imported, or unreadable; an unconfigured store reads as no rules). `ReplaceConfig(config, pageLoadedRules,
loadedVersion)` - after aud-a28j's two refusals, a version that no longer matches the rows under the lock is
`ConcurrentChange`, or `AlreadyApplied` when the rows already hold exactly this save's rules (per kind, as sets
ignoring case, marker set); both write nothing (rollback). The outcome carries the version of the rows as they
stand on `Saved` (read back) and `AlreadyApplied`.

`Services/Storage/ModuleConfigRepository.cs`: `UpdateModuleIf(moduleId, decide)` - one write; `decide` sees the
module's values on the write's transaction (null when that read throws) and returns the keys to upsert or null
(rollback); returns `ModuleConfigConditionalUpdate(Written, Before, After)`.

`Services/ModuleConfigService.cs`: `SaveChangedValues(moduleId, loaded, wanted)` -> `ModuleConfigSaveOutcome(Status,
ConflictingKeys, AlreadyApplied, Before, After)`; per wanted key, ordinal, absent as empty: the row as loaded ->
written; the row already wanted -> already applied; else conflicting. Any conflict -> `ConcurrentChange`, nothing
written; nothing to write -> `NothingToSave`; unreadable -> `StoreCorrupt`. Raises `ConfigSaved` only when it wrote.

`Services/Storage/ModuleAdminRepository.cs`: `ApplyChanges(moduleId, add, remove)` - one write; the module's groups
read on the write's transaction; a present add and an absent remove are already applied; the rest inserted or
deleted one by one; nothing to write, or rows unreadable, rolls back. `ModuleAdminChangeOutcome(Readable, Written,
Before, After, AlreadyApplied)`. `Services/ModuleAdminService.cs`: `ApplyAdminChanges` under the service's lock.

`Components/Pages/AdminSettings.razor`: `LoadProtectedPrincipals` keeps `LoadEffectiveConfig` as the gate and takes
the lists and `ppRulesVersion` from `LoadRulesForEditing` (null -> the load error naming the `protected_principal`
table, Save withheld). `SaveProtectedPrincipals` passes the version; `ConcurrentChange` refuses (record
`refusedAt: ConcurrentChange`, "reload the page and re-apply your changes"); `AlreadyApplied` writes the rules'
success record with nothing added or removed and `alreadyApplied` naming the admin's changed `Kind:value` entries
(`ChangedRuleEntries`), then goes on to the secret; the page keeps the version the outcome returns.

`Components/Pages/ModuleConfig.razor`: `SaveConfigAsync` passes the changed keys (`ChangedConfigKeys`, the
`RecountConfig` diff, which now shares it) with their loaded and wanted values to `SaveChangedValues`;
`StoreCorrupt` and `ConcurrentChange` (each key by its field label and key, `ConfigKeyName`) refuse through
`LogSettingsSaveRefused`; the success record lists the written keys, before from the lock and after as read back,
masked as before, plus `alreadyApplied`; no key changed -> "unchanged; there was nothing to save", no record.
`SaveModuleAdminsAsync`: aud-a28j's fresh re-check kept; added and removed from the confirmed snapshot against the
loaded baseline to `ApplyAdminChanges`; rows unreadable under the lock refuse `StoreCorrupt`; the record's before
and after are the rows under the lock and as left, plus `alreadyApplied`; no edit -> no write, no record.

Tests: new `ExchangeAdminWeb.Tests/SettingsPerRowSaveTests.cs` (11 cases); `SettingsPageHarness`:
`CreateOtherModuleConfig()`, `CreateOtherModuleAdmins()`. `GroupGrantValidationTests`' admins-save tripwire follows
the new persist call (`ModuleAdmin.ApplyAdminChanges(module.Id, added, removed)`, the delta taken from the snapshot,
never the live list); `SettingsSaveStoreCheckTests`' legacy-file case passes the new `loadedVersion`.

Caller-visible change, by design: a protected-principals save from a page loaded before another save of the rules
is refused and recorded as `ConcurrentChange` - the admin reloads and re-applies - unless the rules already hold
exactly what it would write, which is recorded as already applied and writes nothing. A module-config save writes
only the keys the admin changed and is refused, naming the key, when one of them was changed elsewhere to another
value; keys it did not change - the ones the page does not show included - are left as they are. A module-admins
save adds and removes only the groups the admin did, so a group added or removed elsewhere stays so. A save with
nothing changed writes nothing and says so.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad's `a28n-a26k-work`): back up, mutate by exact text (one match
required), MD5, `dotnet build` of the test project, `dotnet test` filtered to
`FullyQualifiedName~SettingsPerRowSaveTests|FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~SettingsStoreTextTests|FullyQualifiedName~GroupGrantValidationTests`
(125 tests), restore from the backup, set the write time, MD5 re-confirmed. Fixed-file MD5s:
`Services/ProtectedPrincipalService.cs` `A146E2C8586748B2D9737FC68E7C2B1C`, `Components/Pages/ModuleConfig.razor`
`3BD655E9CAE772A24B5DA7E702A60CDB`, `Components/Pages/AdminSettings.razor` `953712CCA64AF1C16C06512A80CE0A18`,
`Services/ModuleConfigService.cs` `81FFA6EF1FF5AA564610E122662292B7`; every mutant built with 0 errors and every file
was restored to its MD5. After the round, the restored tree rebuilt and the same filter: 125/125 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `ProtectedPrincipalService.ReplaceConfig`: the version check dropped (`if (false && ...)`) | `FE2034D214C72C4646DB0B638FFA487E` | `AdminSettings_ARuleAddedElsewhereSinceThePageLoaded_RefusesTheSave_AndTheRuleStays` and `..._InsideTheSave_...` (the other instance's `user:ceo@contoso.com` deleted: `Collections differ at index 0`), `AdminSettings_TheSameRuleAddedElsewhere_IsANoOp_NotARefusal` (written: token 2 -> 3) |
| 2 | `ModuleConfig.SaveConfigAsync`: the whole config written again (`ModuleConfigSvc.SaveModuleConfig(module.Id, currentConfigState)` above the per-key call) | `CABA07C35FCB9ED3F987240F20904C8A` | `ModuleConfig_KeysChangedElsewhereSinceThePageLoaded_AreNotReverted` (`PreventSelfGrant` back to `"true"`), `ModuleConfig_TheAdminsOwnKeyChangedElsewhere_RefusesTheSave_NamingTheKey` and `..._InsideTheSave_...` (`"88"` overwritten), `ModuleConfig_AnEqualChangeMadeElsewhere_IsANoOp_NotARefusal` |
| 3 | `ModuleConfig.SaveModuleAdminsAsync`: the whole list written again (`ModuleAdmin.SetModuleAdmins(module.Id, snapshot)` above the per-entry call) | `738F67637586746B6BBA4CD38D6761DE` | `ModuleConfig_AnAdminGroupAddedElsewhere_IsKept` (`[]` stored), `ModuleConfig_AnAdminGroupRemovedElsewhere_IsNotRestored` (the removed group back), `ModuleConfig_AnEqualAdminRemovalElsewhere_IsANoOp`; collateral `SettingsSaveAuditTests.ModuleConfig_AnAdminsSave_LeavesExactlyOneSuccessRecord` |
| E1 | extra, `AdminSettings.SaveProtectedPrincipals`: the version the save returned not kept | `EF5434C6AE770AFB1B0033231FE23E73` | `AdminSettings_TwoSavesFromOnePage_BothLand` (the second save refused) |
| E2 | extra, `ModuleConfigService.SaveChangedValues`: the per-key compare made always-true | `7CE45BBF7421E312B721A396B6E4D35A` | `ModuleConfig_TheAdminsOwnKeyChangedElsewhere_RefusesTheSave_NamingTheKey`, `..._InsideTheSave_...`, `ModuleConfig_AnEqualChangeMadeElsewhere_IsANoOp_NotARefusal` |
| E3 | extra, `ReplaceConfig`: the already-applied case folded into the refusal | `8F24A670F5DEC3AD6E6F83D3ED7DB37A` | `AdminSettings_TheSameRuleAddedElsewhere_IsANoOp_NotARefusal` (`"Failed"`, expected `"Success"`) |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,425 passed / 3 skipped (the three known AD skips) / 0 failed, 5,428 total, 5m12s. No hang. (Baseline at
  `cc4f9c5a`: 5,414 / 3 / 0.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check`: exit 0.
  `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: no case adds a group
grant, and the harness leaves the directory services unset. The pages were not rendered in a browser. No coder
self-review verdict is claimed.

## Known gaps

- The protected-principals version is the whole rule set's: a page loaded before any other save of the rules is
  refused even when that save touched rules this admin did not edit (the aud-a28i allowlist gap). Deliberate: the admin
  reloads and re-applies; nothing is merged or guessed.
- Module admins have no conflict state, so concurrent edits to different groups combine, each decided on its own row:
  two admins each removing one of a module's two groups leave it none. It fails toward fewer grants, and it is the
  per-entry decision the approach asked for.
- After an admins save the page's list is not refreshed from the store: a group added elsewhere appears on reload. The
  record's before and after (the rows under the lock and as left) name it.
- A changed secret config field still records `Key=***` on both sides, so its change shows in neither `added` nor
  `removed` (pre-existing masking, unchanged).
- The Exchange Online connection save (`UpdateModuleValues`) still writes all three keys whatever the admin changed, and
  the directory-read secret is written when changed with no compare against the value the page loaded: outside this
  finding's three saves.
- `ModuleConfigService.SaveModuleConfig` and `ModuleAdminService.SetModuleAdmins` have no production caller after this
  fix; aud-a26k, next on this branch, takes them off the public surface.
- Two in-process stores on one temp file stand in for the two instances; nothing ran against the real shared database.
