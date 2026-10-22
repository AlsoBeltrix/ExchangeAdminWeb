# aud-a26n: Settings pages - the EXO connection save and the secret write still overwrite unseen changes, masked secret fields never show a change, repository whole writers stay public, clearing the secret shows the fallback, and two comments are wrong

**Severity**: LOW - the aud-a28k/a28n class on two remaining writes plus labelling and dead surface.
(1) The Exchange Online connection save writes all three keys whatever changed, so a key changed
elsewhere and not edited here is reverted. (2) The directory-read secret id is written with no
compare against the value the page loaded, so a concurrent change to it is overwritten. (3)
Module-config records mask secret fields as `Key=***` on both sides, so a changed secret field never
shows as added or removed in the audit (the reviewer cannot see that it changed at all). (4) The
repository-level whole writers for module config, module admins and protected principals stay public
with no tripwire (the aud-a26k tripwire covers the service layer only). (5) With the appsettings
fallback set, clearing the secret id writes an empty value and the page then shows the fallback
again, so the admin cannot tell the clear took. (6) Comments in `ModuleConfig.razor` (~696) and
`AdminSettings.razor` (~346) say dictionary copies keep the store's case-insensitive comparer; on
.NET 10 they do not (harmless today).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28n/a26k agent, 2026-10-22, candidates 2 to 7 of its hand-back)

## Evidence

At `3ef12e60`: `Components/Pages/ExchangeOnlineConfig.razor` `SaveExoConfig` (`UpdateModuleValues`
with all three keys); `Components/Pages/AdminSettings.razor` `SaveProtectedPrincipals` (the secret
write) and the clear path; `Services/ModuleConfigService.cs` masking in the settings-change record;
`Services/Storage/ModuleConfigRepository.cs`, `ModuleAdminRepository.cs`, `ProtectedPrincipalRepository.cs`
public whole writers; the two comments.

## Approach (to be written by the implementing agent before code)

- EXO connection save and the secret write: per-key decisions under the lock against the loaded
  value (aud-a28n's `SaveChangedValues`), refusing `ConcurrentChange` naming the key, no-op when
  already applied.
- Masked fields: record `Key=***` only on the side that has a value, and a `changedSecretKeys` list
  naming which secret keys changed without their values.
- Repository whole writers: internal, with the tripwire extended to the repository layer.
- Clearing the secret: the page shows the stored state (cleared) distinctly from the appsettings
  fallback that applies at runtime ("not set in the database; the configured fallback applies").
- Comments: use a case-insensitive comparer explicitly where the copy is made, or correct the
  comment.
- Tests through `SettingsPageHarness` and two stores on one temp file; mutations at the call site:
  write all three keys again; write the secret without compare; mask both sides again; make a
  repository writer public again; show the fallback for a cleared secret again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `fbae13c7`; parts 7 and 8 (below) folded
in at `d7d41457`. One commit `fix(settings): ... (aud-a26n)` covering parts 1-8, after aud-a28p on this branch.
Behaviour: Admin Settings, Module Configuration and the shared module-config, protected-principal and storage
services (base app version) and Exchange Online Config (the Exchange Online module's version); the supervisor
owns both bumps.

Evidence re-read at `1db00236`:
- `ExchangeOnlineConfig.razor` `LoadExoConfig` 201-227 shows each key from its own fail-open `GetValue` read,
  falling back to appsettings, and keeps nothing of what the store held; `SaveExoConfig` 317-497 upserts all
  three keys whatever changed (`UpdateModuleValues` 444-449), so a key changed elsewhere and not edited here is
  written back.
- `AdminSettings.razor` `SaveDirectoryReadSecret` 1228-1249 -> `ProtectedPrincipalService.SaveDirectoryReadSecretId`
  1292-1299 (`UpdateModuleValues`, no compare); `LoadDirectoryReadSecret` 804-809 shows
  `TryGetDirectoryReadSecretId` 1276-1288, the stored id or else the appsettings fallback, so a cleared or
  unusable stored id shows the fallback.
- `ModuleConfig.razor` `SaveConfigAsync` record 1212-1223 through `MaskIfSecret` 1629-1635: `***` for a side
  with a value, `(empty)` for one without, so a value changed to another value records `Key=***` on both sides
  and the two cancel in `added`/`removed`. No catalog field is `IsSecret` today.
- `ModuleConfigRepository.SaveModule` 78-93, `ModuleAdminRepository.SetForModule` 53-59,
  `ProtectedPrincipalRepository.Save` 69-89: public; production callers only the three internal service fixtures
  (`git grep`); `WholeStoreWriterTripwireTests` 1-103 checks the service layer and the two `SaveAll`s only.
- Comments `AdminSettings.razor` 345-348 (copies 590, 676) and `ModuleConfig.razor` 694-697 (copies 996, 1230):
  `new Dictionary<,>(source)` takes the default comparer, not the source's.
- `ModuleConfig.razor` `SaveSectionAccessAsync` 1428-1430: `SaveGroupDisplayNames(accessDisplayNames)` - every
  name the page loaded - after every save that is not refused, `NothingToSave` included.
- `AdminSettings.razor` `SaveProtectedPrincipals` 1177-1200: on `AlreadyApplied` the page keeps the version the
  outcome returns (1187) and its own lists, which `SameRules` matched ignoring case.

1. **Exchange Online connection, per key.** `LoadExoConfig`: one `TryGetModuleConfig` read gives the fields
   (appsettings fallbacks as today) and what the store held for each key (`exoStored`, absent as empty), and the
   values shown are kept (`exoShown`). Unreadable with the module marked present: the corrupt banner as today;
   unreadable otherwise: not loaded, the existing "could not be read" banner and Save withheld (it used to show
   the appsettings fallbacks). `SaveExoConfig`, after its validation and aud-a28j's re-check: the keys whose
   trimmed field differs from the trimmed value shown go to `SaveChangedValues(ExchangeOnline, stored, wanted)`.
   `StoreCorrupt` -> refusal `StoreCorrupt` (the re-check's banner state); `ConcurrentChange` -> refusal
   `ConcurrentChange` naming each key by its field label; a key already holding the wanted value ->
   `alreadyApplied` on the success record, not written; the rest written, the record's before from the lock and
   after as read back, as today. A key not edited is never written - one shown from the appsettings fallback
   stays the fallback. Nothing edited -> no write, no record. Every save that passes the refusals still drains
   the pool, as today: the drain is what makes the stored settings live on this instance, and it is not a write.
   The baselines follow what was written.
2. **The directory-read secret, compared.** New `ProtectedPrincipalService.SaveDirectoryReadSecretId(loaded,
   wanted)` -> `SaveChangedValues(ProtectedPrincipals, ...)` for the one key; the one-argument
   `UpdateModuleValues` form becomes an internal test fixture (ProtectedPrincipalServiceTests seed through it).
   The page passes its baseline - the stored value it loaded (part 5) - and the trimmed field. `ConcurrentChange`
   and `StoreCorrupt` -> a refusal record under `ProtectedPrincipals:DirectoryReadSecretId` (`mutationAttempted:
   false`) and the message that the rules were saved and the credential was not (reload and re-apply): the rules
   are written first (aud-a28j's order, unchanged), so this is aud-a28j's partial-save path for a refused write.
   Already applied -> the secret's success record, nothing added or removed, `alreadyApplied`. After it lands the
   field and its baseline hold the value written.
3. **Masked secret fields.** `MaskIfSecret` is kept (`***` only on a side that holds a value). The success
   record gains `changedSecretKeys`: the written secret keys whose value changed (before under the lock against
   after as read back, ordinal), names only.
4. **Repository whole writers.** The three become `internal`, documented as fixtures reached in production only
   from the internal service fixtures. `WholeStoreWriterTripwireTests`: (a) reflection - the three are internal;
   (b) source - `.SaveModule(` and `.SetForModule(` anywhere in production, and `.Save(` in any production file
   that names `ProtectedPrincipalRepository`, are reached only from inside `ModuleConfigService.SaveModuleConfig`,
   `ModuleAdminService.SetModuleAdmins` and `ProtectedPrincipalService.SaveConfig`.
5. **A cleared secret.** `TryGetDirectoryReadSecretId` (page-only caller) is replaced by
   `TryGetDirectoryReadSecret(out DirectoryReadSecretSetting)`: one read; `Stored`, the raw value ("" when
   absent); `FallbackInEffect`, the appsettings id when the stored value gives the lookups none (`PositiveId`),
   else null. `GetDirectoryReadSecretId`, the runtime's read, is unchanged. The field and its baseline hold
   `Stored`, never the fallback; a caption under it while the fallback applies: "Not set in the database; the
   configured fallback applies: Secret ID {id} (Security:ProtectedPrincipalDirectoryReadSecretId in
   appsettings.json)", with "The value in the database is not a usable Secret ID" for a non-blank stored value.
   Recomputed from the value written after a secret save.
6. **The two comments.** The four copies name `StringComparer.OrdinalIgnoreCase`; their sources
   (`GetAllEnablement`, `ReadModule`) are case-insensitive, so the copy cannot meet two casings of one key. The
   comments say what is true: a copy does not inherit its source's comparer; the access baseline is keyed by the
   catalog's aliases, as `accessState` is.
7. **Display names.** `SaveGroupDisplayNames` runs only when the access save wrote (`Saved`), with only the names
   picked from the directory on this page (`pickedDisplayNames`, filled beside `accessDisplayNames` by
   `RememberGroupDisplayName` 1681-1702) for groups the save added to an alias it wrote (`DisplayNamesToStore`,
   internal static, pure). A no-op save writes no name and moves no change token; a written save no longer
   writes back names it loaded, so a name changed elsewhere is not reverted. The names stay cosmetic and
   unaudited, written after the grants in their own call, as today.
8. **Already-applied rules.** On `AlreadyApplied`, after its record, the five lists are set from
   `outcome.After` (the rows under the lock) and the baselines follow, so the page shows - and a later save
   writes - the stored entries. Chosen over a case-blind version: casing is not what a rule matches on, but it is
   what the store holds and shows, and a version blind to it lets a later save write the stale casing back with
   nothing refusing it - the same outcome. Rehydrating keeps aud-a28n's "the lists shown and the version held are
   one read".
9. **Registries and docs.** ClickGateRegistry: the three pages NotYetConverted ("tier 2, not approved"), no
   line-keyed entry, no ExpectedLineCount - confirmed at `1db00236`; nothing re-anchors. No handler added.
   ProgressRegistry: `SaveExoConfig`'s opening pin follows its write (`ModuleConfigSvc.SaveChangedValues(`).
   `SettingsSaveAuditTests.EveryRefusalAndCatchInASettingsSave_WritesItsRecord` covers the new refusal returns.
   `docs/AdminModuleDeveloperGuide.md`'s `SaveChangedValues` line names its callers.

Tests, new `ExchangeAdminWeb.Tests/SettingsConnectionAndSecretSaveTests.cs` through `SettingsPageHarness` (the
other instance is a second service over its own store on the same file; one landing inside a save runs from the
store's `BeforeWrite`). Connection: a key changed elsewhere and not edited is not reverted, the record naming
only the admin's key; the admin's own key changed elsewhere refuses naming it, one refusal, nothing written;
the same inside the save; an equal change is a no-op (token unchanged); nothing edited writes nothing. Secret: a
change elsewhere is not overwritten - the secret's refusal, the rules' success, the message; the same inside the
save; an equal change is a recorded no-op. Masking: a secret field changed value to value is named in
`changedSecretKeys`, absent from `added`/`removed`, its values nowhere in the record; set from empty it is
`(empty)` -> `***`. A cleared secret with the fallback configured renders "Not set in the database" and the
fallback id with an empty field; a stored id with the fallback configured renders the id and no caption;
clearing it and saving renders the caption. Display names: an all-already-applied access save writes no name and
leaves the token; a removal save does not revert a name changed elsewhere; `DisplayNamesToStore` keeps only
picked names for groups added to written aliases. Rules: after an `AlreadyApplied` where the other save changed
only casing, the page's lists are the stored rows and a later save keeps their casing. Tripwire: the three
repository writers. Mutations at the call site: (2) all three connection keys written again; (3) the secret
written without compare; (4) masked both sides with nothing naming the change (`changedSecretKeys` dropped); (5)
a repository writer public again; (6) the fallback shown for a cleared secret again; (7) the display names
written again from the page's whole map on every save not refused; (8) the stale lists kept on `AlreadyApplied`.

## Guard proof

Pending.

## Folded in: two LOW items from the codex review of aud-a28n/a26k (attr-batch-8, 2026-10-22)

7. **Section-access no-op still writes stale display names.** `ModuleConfig.razor` around 1430: when
   `SaveEditedAliases` returns `AlreadyApplied` for every alias, `SaveGroupDisplayNames` still updates
   `group_display_name` rows from the page's stale `accessDisplayNames` and bumps the shared change
   token, which can revert newer display names. Skip display-name writes on no-op paths, or scope them
   to aliases actually written and record them separately. Test: an all-already-applied save writes no
   display name. Mutation: write them again.
8. **Protected-principal `AlreadyApplied` keeps the page's stale casing.** `AdminSettings.razor` around
   1187: another save changed only the casing of a rule entry (same effective set); this page gets
   `AlreadyApplied`, stores the returned version, but keeps its stale list values, so a later save from
   the same page passes the version check and writes the stale casing back with little audit signal.
   On `AlreadyApplied` rehydrate the page's lists and baselines from `outcome.After` (or canonicalise
   case in the version if casing is not meaningful - say which). Test: after an `AlreadyApplied` the
   page's lists equal the store's rows. Mutation: keep the stale lists.
