# aud-a26k: Settings pages - a no-op concurrent change is refused, two store writers have no caller, the directory-read secret reads fail-open, and the drain failure uses the generic action

**Severity**: LOW - usability, dead surface and labelling; no wrong write. (1) Section access refuses
`ConcurrentChange` even when the other change already equals this save's target (the admin is told
to reload and re-apply a change that is already in place). (2) `SaveEnablement(map)` and
`SaveSectionAccess(map)` have no app caller since aud-a28k; only tests use them, and they still
write whole maps. (3) Admin Settings shows the directory-read secret id from a fail-open read, so an
unreadable store looks the same as an unset one (it is no longer written back blank since aud-a28j,
so the harm is display). (4) The drain-failure record (aud-a26h) uses the generic settings-change
action; a reviewer filtering by action cannot tell it from a settings change.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
