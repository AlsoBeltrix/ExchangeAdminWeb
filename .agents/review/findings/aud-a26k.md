# aud-a26k: Settings pages - a no-op concurrent change is refused, two store writers have no caller, the directory-read secret reads fail-open, and the drain failure uses the generic action

**Severity**: LOW - usability, dead surface and labelling; no wrong write. (1) Section access refuses
`ConcurrentChange` even when the other change already equals this save's target (the admin is told
to reload and re-apply a change that is already in place). (2) `SaveEnablement(map)` and
`SaveSectionAccess(map)` have no app caller since aud-a28k; only tests use them, and they still
write whole maps. (3) Admin Settings shows the directory-read secret id from a fail-open read, so an
unreadable store looks the same as an unset one (it is no longer written back blank since aud-a28j,
so the harm is display). (4) The drain-failure record (aud-a26h) uses the generic settings-change
action; a reviewer filtering by action cannot tell it from a settings change.
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-a5f93bbf442feb5e2`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28j/a28k/a26h agent, 2026-10-22, candidates 3 to 6 of its hand-back)

## Evidence

At the aud-a28j/a28k/a26h branch tip `ade2a1d7`: `Components/Pages/ModuleConfig.razor`
`SaveSectionAccessAsync` (the per-alias compare); the section-access and enablement stores'
map-writing methods; `Components/Pages/AdminSettings.razor` (the secret-id read); `Components/Pages/ExchangeOnlineConfig.razor`
(the `ExchangeOnlineConfig:ConnectionPool` failure record's action).

## Approach (to be written by the implementing agent before code)

- Concurrent change equal to the target: treat as a no-op for that alias (record it as such), not a
  refusal; refuse only when the current row differs from both what the page loaded and what it
  wants to write.
- Remove the callerless map writers (or make them internal to the audited path) and move their tests
  to the per-row paths; a tripwire refuses a production caller.
- Secret-id read: distinguish unreadable from unset on the page ("could not be read" with the
  remediation per aud-a26f/a26h text) and refuse a save that depends on it while unreadable.
- Drain failure: a dedicated audit action (new `AuditService` method; add it to
  `UsageTelemetryServiceTests`' every-public-audit-method list) so it filters apart.
- Tests through `SettingsPageHarness`; pages `NotYetConverted` in `ClickGateRegistry` - confirm.
  Mutations at the call site: refuse the equal change again; make a map writer public again; read the
  secret fail-open again; use the generic action again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `714078b9`. One commit
`fix(settings): a concurrent change equal to the save is a recorded no-op, the whole-map writers leave the
public surface, an unreadable directory-read secret says so and refuses its save, and a failed pool drain has
its own audit action (aud-a26k)`, after aud-a28n on this branch. Behaviour: Module Configuration, Admin
Settings and the shared section-access, enablement, protected-principal and audit services (base app version)
and Exchange Online Config (the Exchange Online module's version); the supervisor owns both bumps.

Evidence re-read at `cc4f9c5a`:
- `SectionAccessService.SaveEditedAliases` 253-315 adds every edited alias whose rows under the lock differ
  from the loaded list to `changed` (281-282) and refuses the whole save, even when the rows already hold the
  edited list.
- `ModuleEnablementService.SaveEnablement(map)` 162-178 and `SectionAccessService.SaveSectionAccess(map)`
  229-236: public whole-map writers, no production caller (`git grep`: tests only). Since aud-a28j
  `ProtectedPrincipalService.SaveConfig` 1001-1012 is in the same state, and aud-a28n leaves
  `ModuleConfigService.SaveModuleConfig` 82-91 and `ModuleAdminService.SetModuleAdmins` 136-143 so.
- `AdminSettings.razor` 762: `GetDirectoryReadSecretId()` reads module config fail-open
  (`ModuleConfigService.ReadModuleConfig` 166-178), so an unreadable store shows blank, or as the appsettings
  fallback; after a failed rules load (744-753) the secret is not read and shows blank. A page whose secret
  read failed and which later saves a changed id writes over a value it never showed: aud-a28j's fresh
  `IsModuleCorrupt` re-check passes once the store reads again.
- `ExchangeOnlineConfig.razor` 482 records the drain failure through `LogSettingsSaveFailed`: action
  `UpdateSectionAccess`, the settings-change action.

1. **Equal change = recorded no-op.** In `SaveEditedAliases` an edited alias whose rows under the lock differ
   from the loaded list but equal the edited one (sets, ignoring case) is `AlreadyApplied`: not written, not a
   refusal; only an alias that differs from both refuses `ConcurrentChange`. The outcome gains `AlreadyApplied`;
   nothing left to write is `NothingToSave` (rollback). Page: one success record per already-applied alias
   (section = alias, nothing added or removed, `alreadyApplied` naming it) beside the written aliases' records;
   a save whose every edited alias was already applied says so. aud-a28n applies the same rule to its keys,
   entries and rule set.
2. **Whole-map writers off the public surface.** The five above become `internal`: test fixtures only
   (`InternalsVisibleTo`), documented as neither compared nor audited. Made internal rather than removed (the
   aud-a28l precedent removed a writer with one test caller): about forty fixture call sites seed through them,
   in test files other in-flight branches edit. The named two's own tests move to the per-row paths
   (`ModuleEnablementServiceTests` save round trips through `SetModulesEnabled`, `SectionAccessServiceTests`'
   through `SaveEditedAliases`). New tripwire `WholeStoreWriterTripwireTests`: (a) reflection - none of the five
   is public; (b) source - no production `.cs`/`.razor` calls any of them, and the two named stores'
   repository-level whole-table `SaveAll` is called in production only from inside those internal fixtures, so
   nothing bypasses the per-row paths through a repository (the aud-a28l depth).
3. **Secret unreadable, not unset.** New `ModuleConfigService.TryGetModuleConfig(moduleId, out values)` and
   `ProtectedPrincipalService.TryGetDirectoryReadSecretId(out id)` (false when the ProtectedPrincipals module
   config cannot be read; otherwise the parse and appsettings fallback of `GetDirectoryReadSecretId`, which the
   runtime keeps unchanged). Page: new `LoadDirectoryReadSecret()`, run whether or not the rules load;
   `ppSecretUnreadable` disables the field and shows that it could not be read, with
   `SettingsStoreText.ModuleConfig("ProtectedPrincipals")`; `SaveProtectedPrincipals` refuses a changed secret
   while it is set (`StoreUnreadable`, `mutationAttempted: false`, before anything is written). A rules-only save
   does not depend on the secret and still saves.
4. **Drain failure, own action.** New `AuditService.LogConnectionPoolDrainFailed(performedBy, ip, section,
   errorDetail)`: action `DrainConnectionPool`, category `AdminSettings` (beside the save it follows), result
   Failed. `ExchangeOnlineConfig.razor` 482 calls it inside a catch that logs an audit failure. Added to
   `UsageTelemetryServiceTests`' every-public-audit-method list (417-440).
5. **Registries.** ClickGateRegistry: all three pages NotYetConverted ("tier 2, not approved"), no line keys -
   confirmed at `cc4f9c5a`; nothing re-anchors. No handler added (`LoadDirectoryReadSecret` is called from the
   load, not markup). The stale `LoadState` comment in `ModuleConfig.razor` (1003-1008, "SaveSectionAccess ->
   SaveAll -> ClearAndInsert REPLACES the whole store") is corrected.

Tests, new `ExchangeAdminWeb.Tests/SettingsNoOpAndUnreadableSecretTests.cs` through `SettingsPageHarness`: an
access change made elsewhere equal to the page's edit is a no-op success naming the alias, nothing written, no
refusal; one that differs from both still refuses; the secret unreadable at load shows as unreadable and its
field is disabled; the secret unreadable at load and readable at save - a changed id is refused
`StoreUnreadable`, nothing written, one refusal; a rules-only save with the secret unreadable still saves.
`SettingsStoreTextTests`' drain case asserts the record's action and category. `WholeStoreWriterTripwireTests`
as above. Mutations at the call site: (4) the equal change refused again; (5) the secret read fail-open again;
(6) the generic action again; extra - a map writer public again.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a5f93bbf442feb5e2` on aud-a28n (`650a359b`), the fix's tests, harness helper and new
`AuditService` method in place and the three pages and five services as they were at `650a359b` swapped in
(AdminSettings `953712CCA64AF1C16C06512A80CE0A18`, ModuleConfig `3BD655E9CAE772A24B5DA7E702A60CDB`,
ExchangeOnlineConfig `5999D2DC9C30F98581F0827A634E1C5F`, SectionAccessService `F705FDAE9B4266C9D295D3837D754FA1`,
ModuleEnablementService `8F06D5BD98521E31AD8347BA0AC07A25`, ModuleConfigService `81FFA6EF1FF5AA564610E122662292B7`,
ModuleAdminService `9EF6C587A7787CD922E1BB4655607AB6`, ProtectedPrincipalService `A146E2C8586748B2D9737FC68E7C2B1C`),
build 0 errors: 13 failed / 1 passed of the new and changed cases. The access change made elsewhere equal to the save
was refused (`"Failed"`, expected `"Success"`), and with it the edit on the other alias (`[GroupA]` left, `[]`
expected); the drain failure carried the settings action (`"UpdateSectionAccess"`, expected `"DrainConnectionPool"`);
all five whole-store writers were public, and the repository tripwire's fixture anchor was missing. The four secret
cases failed as stale (`ppSecretUnreadable` / `LoadDirectoryReadSecret` not on the page), so mutation 5 below is the
behavioural proof for them. The one that passed is `NoProductionSource_CallsAWholeStoreWriter`: since aud-a28n no page
calls a whole-store writer, and the guard is for the next one. The fixed bytes were restored to their MD5s and touched.

## Fix

`Services/SectionAccessService.cs` `SaveEditedAliases`: per edited alias under the lock - rows as the page loaded
them -> written; rows already the edited list (sets, ignoring case) -> `AlreadyApplied`, not written; anything else ->
`ConcurrentChange`, the whole save refused as before. `SectionAccessSaveOutcome` gains `AlreadyApplied`; nothing left to
write is `NothingToSave` (rollback). `Components/Pages/ModuleConfig.razor` `SaveSectionAccessAsync`: one success record
per already-applied alias (its before and after, equal, so nothing added or removed; `alreadyApplied` naming it) beside
the written aliases' records; "already held these changes ... nothing needed writing" when every edited alias was.

Whole-store writers internal: `ModuleEnablementService.SaveEnablement`, `SectionAccessService.SaveSectionAccess`,
`ModuleConfigService.SaveModuleConfig`, `ModuleAdminService.SetModuleAdmins`, `ProtectedPrincipalService.SaveConfig`. The
named two's own tests moved: `ModuleEnablementServiceTests` `SetModulesEnabled_WritesTheModulesItIsGiven_ThatCanBeReadBack`,
`SetModulesEnabled_SecondSave_OverwritesFirst`, `DisableModule_SaveThenRead_ReturnsFalse` (now through `SetModulesEnabled`);
`SectionAccessServiceTests` `SaveEditedAliases_ReadsBackWhatWasSaved`, `SaveEditedAliases_ReplacesOnlyTheEditedAliases`.
New `ExchangeAdminWeb.Tests/WholeStoreWriterTripwireTests.cs`: (a) each of the five is internal (reflection); (b) no
`.cs`/`.razor` under `Services/`, `Components/`, `Modules/`, `Authorization/`, `Middleware/`, `Models/` or the root calls one
(comments blanked); (c) the enablement and section-access repositories' `SaveAll` is reached in production only from inside
the two internal fixtures.

Secret: `ModuleConfigService.TryGetModuleConfig(moduleId, out values)`; `ProtectedPrincipalService.TryGetDirectoryReadSecretId(out id)`
(false when the ProtectedPrincipals module config cannot be read; else `GetDirectoryReadSecretId`'s answer - which now
shares its parse, `PositiveId`, unchanged in behaviour). `Components/Pages/AdminSettings.razor`: `LoadDirectoryReadSecret()`,
run first in `LoadProtectedPrincipals`, so the secret is read whether or not the rules load; `ppSecretUnreadable` disables the
field and replaces its caption with "could not be read, so it is not shown and cannot be changed here" and
`SettingsStoreText.ModuleConfig("ProtectedPrincipals")`; `SaveProtectedPrincipals` refuses a changed secret while it is
set (`StoreUnreadable`, `mutationAttempted: false`), before anything is written.

Drain: `Services/AuditService.cs` `LogConnectionPoolDrainFailed(performedBy, ip, section, errorDetail)` - action
`DrainConnectionPool`, category `AdminSettings`, result Failed, through `WriteAuditEvent` (so telemetry records it).
`Components/Pages/ExchangeOnlineConfig.razor`'s drain catch calls it inside a catch that logs an audit failure.
`UsageTelemetryServiceTests`' every-public-audit-method list gains it (13 events); `SettingsStoreTextTests`' drain case asserts
the drain record's action and category, and that the save's record keeps the settings action.

Tests: new `ExchangeAdminWeb.Tests/SettingsNoOpAndUnreadableSecretTests.cs` (6 cases) and
`ExchangeAdminWeb.Tests/WholeStoreWriterTripwireTests.cs` (7 cases); `SettingsPageHarness.IsInputDisabled`. The refusal
when an alias's rows differ from both the loaded and the wanted list stays pinned by aud-a28k's
`SettingsConcurrentSaveTests.ModuleConfig_AGrantChangedElsewhereOnAnEditedAlias_RefusesTheSave_NamingTheAlias` and its
inside-the-save twin, unchanged and green; the secret's dependent-save case drives the page's own secret load a second
time with the module-config store failing its reads, standing in for that one read failing while the rules' read
succeeded.

Caller-visible change, by design: a section-access save whose edited alias was already changed elsewhere to exactly what
the admin wanted is recorded as already applied and goes on with the rest of the save, instead of being refused. On Admin
Settings an unreadable directory-read secret says so, its field is disabled, and a save that would change it is refused
and recorded; a rules-only save still saves. A failed pool drain is filed under its own action, `DrainConnectionPool`. The
five whole-store writers are no longer part of the services' public surface; nothing in the app called them.

## Guard proof

Same driver as aud-a28n, filter
`FullyQualifiedName~SettingsNoOpAndUnreadableSecretTests|FullyQualifiedName~WholeStoreWriterTripwireTests|FullyQualifiedName~SettingsStoreTextTests|FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~SettingsPerRowSaveTests|FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~SectionAccessServiceTests|FullyQualifiedName~ModuleEnablementServiceTests`
(211 tests). Fixed-file MD5s: `Services/SectionAccessService.cs` `5A1587FCE1D57B9853DB9F8F5357E31A`,
`Components/Pages/AdminSettings.razor` `E41CF568473B404BB57F23CE11D4593B`, `Components/Pages/ExchangeOnlineConfig.razor`
`3BCBEA376BC8322F0A9EC58DD6EE73A2`, `Services/ModuleEnablementService.cs` `611669789AE7DB79D2E6BA04C4113F7D`; every mutant
built with 0 errors and every file was restored to its MD5. After the round, the restored tree rebuilt and the same
filter: 211/211 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 4 | `SectionAccessService.SaveEditedAliases`: the equal change refused again (`if (false && SameGroups(now, wanted))`) | `E30D21E6B7AC75497113841EDE3BCF72` | `ModuleConfig_AnAccessChangeMadeElsewhereEqualToTheSave_IsARecordedNoOp_NotARefusal` (`"Failed"`), `ModuleConfig_AnEqualChangeOnOneAlias_DoesNotStopTheEditOnAnother` (the other alias's edit lost: `[GroupA]` left) |
| 5 | `AdminSettings.LoadDirectoryReadSecret`: the secret read fail-open again (`GetDirectoryReadSecretId()`, unreadable never set) | `21699523EE05F08F7E0853AED660761C` | `AdminSettings_ASecretThatCouldNotBeRead_ShowsAsUnreadable_AndItsFieldIsDisabled`, `AdminSettings_AChangedSecretThePageCouldNotRead_IsRefused_AndNothingIsWritten` (`"777"` overwritten with `"4242"`) |
| 6 | `ExchangeOnlineConfig.SaveExoConfig`: the drain recorded with the generic action again (`LogSettingsSaveFailed`) | `38F01B183C75A23D5348809C3FDD2288` | `SettingsStoreTextTests.ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_IsItsOwnWarning_NotAFailedSave` (`"UpdateSectionAccess"`, expected `"DrainConnectionPool"`) |
| E4 | extra: a map writer public again (`public void SaveSectionAccess(`) | `03BC1D6A37E275088A70B7E2FFEB1A9B` | `AWholeStoreWriter_IsNotPublic(SectionAccessService.SaveSectionAccess)`, `TheNamedStoresWholeTableSave_IsReachedOnlyFromItsFixture` |
| E5 | extra, `AdminSettings.SaveProtectedPrincipals`: the dependent-save refusal dropped | `41753FE6184C1863E04D958014A358C7` | `AdminSettings_AChangedSecretThePageCouldNotRead_IsRefused_AndNothingIsWritten` (`"4242"` written) |
| E6 | extra: a production caller added (`this.SaveEnablement(new())` in ModuleEnablementService) | `B5EABD8ACAF6B3BAC810866309E612DD` | `NoProductionSource_CallsAWholeStoreWriter` (`Services/ModuleEnablementService.cs:151 .SaveEnablement(`) |
| E7 | extra, AdminSettings markup: the secret field's `disabled` dropped | `FE0C49E1E07EA9EE1DA69006A4BEC928` | `AdminSettings_ASecretThatCouldNotBeRead_ShowsAsUnreadable_AndItsFieldIsDisabled` |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes. That
commit also corrects the stale `LoadState` comment the plan names (item 5).

`docs/AdminModuleDeveloperGuide.md` "Module Configuration" listed `SaveModuleConfig` as public API; it now lists the
writers a module can call (`UpdateModuleValues`, `SaveChangedValues`, and `TryGetModuleConfig`) and says why there is no
public whole-module replace.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,438 passed / 3 skipped (the three known AD skips) / 0 failed, 5,441 total, 5m16s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: no case adds a group
grant, the harness leaves the directory services unset, and the failing drain is the config store's read switch over a
pool that holds no runspace. The pages were not rendered in a browser. No coder self-review verdict is claimed.

## Known gaps

- An already-applied alias, key, entry or rule set leaves a Success record with nothing added or removed; only its
  `alreadyApplied` field says why. The Admin Event Log shows it as a settings change that changed nothing.
- The five whole-store writers remain in production code as internal test fixtures; the tripwire, not the compiler, is
  what keeps the app from calling them. The repository-level whole writers of module config, module admins and protected
  principals (`SaveModule`, `SetForModule`, `Save`) stay public storage primitives that about thirty tests seed through; the
  tripwire's repository depth covers the two named stores' `SaveAll` only. No page injects any of these repositories today.
  Only the named two writers' own tests moved to the per-row paths; the other three's (`SaveModuleConfig_OverwritesWholeModule`,
  `SetModuleAdmins_OverwritesWholeList`, `SaveConfig_InvalidatesCache`) pin fixtures the suite relies on and stay.
- A changed secret meeting an unreadable load refuses the whole protected-principals save, rules included: the admin
  reloads. A rules-only save is unaffected.
- The dependent-save case runs the page's secret load a second time with the module-config store failing: the harness's
  read switch fails a whole store, and the rules' load gate reads the same store (the Mailbox Permissions exclusions), so a
  single load where only the secret's read fails cannot be produced otherwise.
- A pool drain that succeeds is still not recorded; only its failure has a record.
