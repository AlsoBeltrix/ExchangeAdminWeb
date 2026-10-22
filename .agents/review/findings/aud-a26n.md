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
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-a3c115693e85a6623`
**Commit**: `bec91eeb` (plan `ff78ff27`; comments `389a34a2`)
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

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a3c115693e85a6623` on aud-a28p (`0b92c35f`), the fix's tests in place (less
`DisplayNamesToStore_IsThePickedNamesOfTheGroupsAddedToTheAliasesWritten`, whose helper does not exist before the fix)
and the seven production files as they were at `0b92c35f` swapped in (`git show`, each `git hash-object` equal to its
blob: ExchangeOnlineConfig `95DBBF0E79BC458FA8BE18EE1D8CCFD3`, AdminSettings `9FE272BD2B2122704A57A7A163912717`,
ModuleConfig `A301E76C06777CB4703EC6B4DBC115AE`, ProtectedPrincipalService `062DE6CF308D399A1A730EBA53057678`,
ModuleConfigRepository `0401B9B2CD30B99903F567F8F5A2400B`, ModuleAdminRepository `9D72FE52FE45691408DB83B0DA29527B`,
ProtectedPrincipalRepository `395B9059629A583C8750FFF8600E82DC`), build 0 errors: `SettingsConnectionAndSecretSaveTests`
and `WholeStoreWriterTripwireTests` 19 failed / 9 passed. All six connection cases failed: a certificate changed
elsewhere was reverted (`"CN=EXO-Automation"` stored, `"CN=EXO-Rotated"` expected); a certificate shown from the
fallback was written into the store; the admin's own key changed elsewhere was overwritten rather than refused,
between load and save and inside it (`"fabrikam..."` stored, `"northwind..."` expected); the equal change and the
unedited save both wrote (change token moved). All three secret cases failed: a change elsewhere was overwritten
(`"4242"` stored, `"888"` expected), between load and save and inside it, and the equal change wrote (`updated_at`
moved). The cleared secret showed the fallback in its field (`"55"`, `""` expected) and no caption after a clearing
save; both secret-field records lacked `changedSecretKeys`; both display-name cases reverted the newer name
(`EXAMPLE\Old-A`) and the no-op moved the token; the already-applied rules page kept `"cfo@contoso.com"`; the three
repository writers were public. The nine that passed are the seven aud-a26k tripwires, the source tripwire for a
repository writer reached outside its fixture - no page calls one, and the guard is for the next - and the control
in which a stored id with a fallback configured shows the id. Every file was restored to its MD5 and touched.

## Fix

`Components/Pages/ExchangeOnlineConfig.razor`: `LoadExoConfig` - one `TryGetModuleConfig` read fills the fields
(appsettings fallbacks as before) and keeps `exoShown` (the values shown) and `exoStored` (the store's, absent as
empty); unreadable with the module marked present -> the corrupt banner as before; unreadable otherwise -> not
loaded, the "could not be read" banner and Save withheld, where it used to show the fallbacks as if loaded.
`SaveExoConfig`: after the validation and aud-a28j's re-check, the keys whose trimmed field differs from the trimmed
value shown go to `SaveChangedValues` with the values the store held; `StoreCorrupt` and `ConcurrentChange` (each key
by its field label, `ConnectionFieldName`) refuse through `LogSettingsSaveRefused`; the success record's before is
from the lock and its after as read back, with `alreadyApplied` when any; nothing edited -> no write and no record
("unchanged; nothing was written"); the drain runs after every save that passes the refusals, as before; the
baselines follow what was written. `ConnectionFields` builds the three-key map both use.

`Services/ProtectedPrincipalService.cs`: `DirectoryReadSecretSetting(Stored, FallbackInEffect)`;
`TryGetDirectoryReadSecret(out setting)` replaces `TryGetDirectoryReadSecretId` (its one caller was the page);
`DescribeDirectoryReadSecret(stored)`, the lookups' `PositiveId` rule; `SaveDirectoryReadSecretId(loaded, wanted)` ->
`SaveChangedValues` for the one key; the one-argument form is internal (test fixture).

`Components/Pages/AdminSettings.razor`: `LoadDirectoryReadSecret` shows the stored value and keeps `ppSecretFallbackId`;
the caption under the field while the fallback applies (`SecretFallbackText`). `SaveDirectoryReadSecret` passes the
baseline and the trimmed field; `ConcurrentChange` and `StoreCorrupt` are refusals under
`ProtectedPrincipals:DirectoryReadSecretId` with the message that the rules were saved and the credential was not;
already applied is the secret's success record with `alreadyApplied`; on success the field holds the value read back
and the caption is recomputed. `SaveProtectedPrincipals`, on `AlreadyApplied` and after its record, sets the five
lists from `outcome.After`; the baselines follow. The enablement baseline's copies name the comparer and the comment
says what is true.

`Components/Pages/ModuleConfig.razor`: the config record's `changedSecretKeys` (`IsSecretField`, which `MaskIfSecret`
now shares); the config baseline's copies name the comparer, and the comment is corrected; `pickedDisplayNames`,
filled beside `accessDisplayNames` by `RememberGroupDisplayName`; the access save writes display names only on `Saved`,
through `DisplayNamesToStore`.

`Services/Storage/ModuleConfigRepository.cs`, `ModuleAdminRepository.cs`, `ProtectedPrincipalRepository.cs`:
`SaveModule`, `SetForModule`, `Save` internal, each documented as reached only from its service fixture.

Tests: new `ExchangeAdminWeb.Tests/SettingsConnectionAndSecretSaveTests.cs` (18 cases);
`WholeStoreWriterTripwireTests` gains `ARepositoryWholeWriter_IsNotPublic` (3) and
`ARepositoryWholeWriter_IsReachedOnlyFromItsServiceFixture`; `ProgressRegistry`'s `SaveExoConfig` opening pin is
`ModuleConfigSvc.SaveChangedValues(`. `docs/AdminModuleDeveloperGuide.md` names `SaveChangedValues`' callers. Two
departures from the plan's wording, neither in what is decided: the secret's equal-change case checks the row's
`updated_at` rather than the change token, which is the database's and moves with the rules' own write in the same
save; and a case beyond the plan pins that a connection key shown from the fallback and not edited is not written.

Caller-visible change, by design: on Exchange Online Config a save writes only the fields the admin changed, and is
refused, naming the field, when one of them was changed elsewhere to another value; a field shown from appsettings
and not edited stays the appsettings value. On Admin Settings a changed directory-read Secret ID is refused, after
the rules were saved, when the stored value changed since the page loaded; the field shows the value in the
database, empty when cleared, with a caption naming the appsettings fallback while that applies; and a rules save
already applied elsewhere leaves the page holding the stored entries. On Module Configuration a changed secret field is named
in its record, and an access save stores display names only for groups it added from the picker. The three
repository whole writers are no longer public; nothing in the app called them.

## Guard proof

Same driver as aud-a28p, filter
`FullyQualifiedName~SettingsConnectionAndSecretSaveTests|FullyQualifiedName~WholeStoreWriterTripwireTests|FullyQualifiedName~SettingsPerRowSaveTests|FullyQualifiedName~SettingsNoOpAndUnreadableSecretTests|FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~SettingsStoreTextTests|FullyQualifiedName~GroupGrantValidationTests|FullyQualifiedName~ProtectedPrincipalServiceTests|FullyQualifiedName~ProgressRegistryTests`
(262 tests). Fixed-file MD5s: `Components/Pages/ExchangeOnlineConfig.razor` `F323839B32AC753B55A2E626042A1339`,
`Components/Pages/AdminSettings.razor` `B1D55A1327BD610535F469400A5DADA4`, `Components/Pages/ModuleConfig.razor`
`9D7FE1ED6F327BB571504EC8EDC8F7D5`, `Services/Storage/ModuleConfigRepository.cs` `FAC5E79CD39CC23A6590800D518A0EC5`,
`Services/ModuleConfigService.cs` `624AB9B2F188CCF5982268CA7D9C36A7` (unchanged by this commit; E5 only); every mutant
built with 0 errors and every file was restored to its MD5. After the round, the restored tree rebuilt and the same
filter: 262/262 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 2 | `ExchangeOnlineConfig.SaveExoConfig`: all three connection keys written again (`UpdateModuleValues(..., wanted)` in place of the per-key call) | `F7439FA9B85B3A9AA75A59BB36157094` | `ExchangeOnlineConfig_AKeyChangedElsewhereAndNotEditedHere_IsNotReverted` (`"CN=EXO-Automation"` written back), `..._AKeyShownFromTheFallbackAndNotEdited_IsNotWritten`, `..._TheAdminsOwnKeyChangedElsewhere_RefusesTheSave_NamingIt` and `..._InsideTheSave_...` (`"fabrikam..."` over `"northwind..."`), `..._AnEqualChangeMadeElsewhere_IsANoOp_NotARefusal`, `..._NothingEdited_WritesNothing_AndRecordsNothing` (token moved); collateral `ProgressRegistryTests.AReportedOperationContainsTheCallItClaimsToCover` (the pin) |
| 3 | `AdminSettings.SaveDirectoryReadSecret`: the secret written without compare (the one-argument writer) | `10843A33B7D35E2170AC96E74224AC0C` | `AdminSettings_ASecretChangedElsewhere_IsNotOverwritten_AndItsSaveIsRefused` and `..._InsideTheSave_...` (`"4242"` over `"888"`), `..._ToTheSameValue_IsARecordedNoOp` (`updated_at` moved) |
| 4 | `ModuleConfig.SaveConfigAsync`: masked both sides with nothing naming the change (`changedSecretKeys` dropped) | `CFBE5D9570DD3BFAEAB4D66B008B3ACD` | `ModuleConfig_ASecretFieldChangedValueToValue_IsNamedInChangedSecretKeys_AndNotAsAddedOrRemoved`, `ModuleConfig_ASecretFieldSetFromEmpty_IsMaskedOnlyWhereItHasAValue` (`[]`, `["ApiToken"]` expected) |
| 5 | a repository writer public again (`public void SaveModule(`) | `393F7913524EBF9739C8EEE36618CAF9` | `ARepositoryWholeWriter_IsNotPublic(ModuleConfigRepository.SaveModule)` |
| 6 | `AdminSettings.LoadDirectoryReadSecret`: the fallback shown for a cleared secret again (`FallbackInEffect?.ToString() ?? Stored`) | `1D442579807C6A3554CEBCD37644FCE6` | `AdminSettings_AClearedSecretWithAFallbackConfigured_ShowsNotSetInTheDatabase` (`"55"`, `""` expected) |
| 7 | `ModuleConfig.SaveSectionAccessAsync`: the display names written again from the page's whole map on every save not refused | `8DC93471A3A6AEF76CC65BF2271FE385` | `ModuleConfig_AnAccessSaveWhoseEditWasAlreadyApplied_WritesNoDisplayName` (token 4 -> 5), `ModuleConfig_AnAccessSave_DoesNotRevertADisplayNameChangedElsewhere` (`EXAMPLE\Old-A` back) |
| 8 | `AdminSettings.SaveProtectedPrincipals`: the stale lists kept on `AlreadyApplied` (`if (false && ...)`) | `C927CFABA10EBF4898E0BC978DBDCC51` | `AdminSettings_AfterAnAlreadyAppliedSave_ThePageHoldsTheStoredRows_AndALaterSaveKeepsTheirCasing` (`"cfo@contoso.com"`, `"CFO@Contoso.com"` expected) |
| E4 | extra, `MaskIfSecret`: an empty side masked too (the two checks swapped) | `E4780EF53DA05A33889ADA5407C74E0A` | `ModuleConfig_ASecretFieldSetFromEmpty_IsMaskedOnlyWhereItHasAValue` (`removed` empty, `["ApiToken=(empty)"]` expected) |
| E5 | extra: a production caller of a repository whole writer (`_repository.SaveModule(` in a new public method of `ModuleConfigService`) | `D1941749CD42D10607C9EB95DC923168` | `ARepositoryWholeWriter_IsReachedOnlyFromItsServiceFixture` (`Services/ModuleConfigService.cs:26 .SaveModule(`) |
| E7 | extra, `SaveSectionAccessAsync`: on `Saved`, the whole map written rather than the picked names of added groups | `85B4A7BF92BFEC73F491954655571F41` | `ModuleConfig_AnAccessSave_DoesNotRevertADisplayNameChangedElsewhere` |

Part 6 has no mutation: the copies name a comparer whose sources are already case-insensitive, so no behaviour
moves (the record called it harmless); the comments now say what is true.

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,495 passed / 3 skipped (the three known AD skips) / 0 failed, 5,498 total, 5m23s. No hang. (After aud-a28p,
  `0b92c35f`: 5,473 / 3 / 0.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: no case adds a group
grant or picks one from the directory, the harness leaves the directory services unset, and the drains run over a
pool that holds no runspace. The pages were not rendered in a browser. No coder self-review verdict is claimed.

## Known gaps

- A secret refused under the lock is refused after the rules were saved (aud-a28j's order, unchanged): the rules are in
  effect, the credential is not, and the page says so and records both. Reload and re-apply.
- A connection field shown from the appsettings fallback and not edited stays the fallback, so the store can hold
  some keys while the runtime falls back for the others; the page shows the effective values and still does not mark
  which come from appsettings.
- Every Exchange Online save that passes the refusals drains this instance's pool, an unchanged one included, as
  before; a change saved on the other instance still drains only that instance's pool.
- The secret field still takes any text; a value that is not a Secret ID makes the lookups fall back, which the caption
  now says ("The value in the database is not a usable Secret ID"), but nothing refuses it.
- `changedSecretKeys` has no production effect until a module declares an `IsSecret` field; none does today, so the
  cases give the page a descriptor that declares one.
- A group added by pasting its SID, not picked, gets no stored display name from the save; the table carries any name
  it already holds for that SID. Before, the page's loaded name could be written back over a newer one.
- After an already-applied rules save the lists take the store's order and casing.
- Two in-process stores on one temp file stand in for the two instances; nothing ran against the shared database.

## Branch-tip gates - 2026-10-22 (`389a34a2`: both fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,495 passed / 3 skipped (the three known AD skips) / 0 failed, 5,498 total, 5m15s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

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
