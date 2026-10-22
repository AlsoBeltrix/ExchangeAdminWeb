# aud-a28n: Protected principals, module config and module admins are still replaced wholesale from the page's state

**Severity**: MEDIUM - the aud-a28i / aud-a28k class on three more authorization-adjacent stores
under the shared config database (Architectural Invariant 2). (1) The protected-principals save
replaces the whole rule set from the page's lists: a page holding at least one rule removes a rule
another admin or the other instance added since it loaded; aud-a28j refuses only the
empty-over-non-empty case and records the rest. (2) `ModuleConfig.SaveConfigAsync` replaces a
module's whole config, and `SaveModuleAdminsAsync` the whole admin list, from the page's state, with
no compare against the rows under the lock.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
