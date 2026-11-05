# dov-v4: Verify stored directory objects - the config pane's text settings (slice V4)

**Severity**: MEDIUM - feature slice. The Module Configuration settings that hold a directory object as text - Migration's
Excluded AD Groups, Emergency Disable's lockdown OU, the AD Attribute Editor and BitLocker Recovery search bases, and the eight
Conference Rooms group addresses - could not be checked: a deleted group, a moved OU, a search base that no longer exists or an
address that is not a group looked the same as a good one until the module refused (or, for a search base, quietly found
nothing). Plan `docs/DirectoryObjectVerify-Plan.md` (Approved 2026-11-04), A5-A8, B.1-B.3, C.2 (rows A5-A8), C.3, D and section
E "V4"; decision `.agents/decisions.md` 2026-11-04.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/dov-v4` (worktree `D:\source\ExchangeAdminWeb-wt\dov-v4`, from master `b6281502`)
**Commit**: see `git log b6281502..fix/dov-v4` (`fix(dov-v4): ...` x2, `record(dov-v4): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built), per plan point

1. **Field types (C.3)** (`Modules/ModuleConfigField.cs`, `Modules/ModuleCatalog.cs`). `ConfigFieldType` gains `SearchBase` (any
   container a search can start at) and `RecipientGroup` (an Exchange group address); `ModuleConfigField` gains an optional
   `ListSeparator`. `ExcludedADGroups` is `AdGroup` with `','`, `DefaultSearchBase` `SearchBase` with `';'`,
   `ActiveDirectorySearchBase` `SearchBase`, the eight Conference Rooms group settings `RecipientGroup`; `LockdownOuDn` keeps
   `OU`. Stored values, descriptions and every module's runtime parsing are unchanged.
2. **A5: the Migration lookup extracted (B.2)** (`Services/MigrationService.cs`). `TryResolveExcludedGroup(ps, configured,
   domains)` returns `ExcludedGroupResolution.Found(groups)` / `NotFound(absent per domain)` / `Unreadable(domain, reason, cause)`
   with the same reads, the same not-found translation (`IsNotFound`) and the same messages; `ResolveExcludedGroup` is its thin
   caller and throws exactly as before (none -> the "could not be resolved in any domain of the forest (...)" message; unreadable
   -> the same message with the same inner exception; a group without a DN -> the same message). `ForestDomains` is internal so
   Verify asks for the domains as the module does. Every existing Migration test passes unchanged; M04 shows they bite.
3. **One adapter, one store** (`Services/ModuleConfigVerifyAdapter.cs`, store `VerifyIgnoreList.ConfigStore` = "Config"). Its
   `Settings` table names the twelve settings, their module, rule and list separator. Items: one per distinct entry of a list,
   split as the module splits it (`Split(sep, RemoveEmptyEntries | TrimEntries)`), one per non-blank single value; row key =
   the setting key (each verified key belongs to one module - pinned by test), stored value = the entry. Rules:
   - A5 `ExcludedGroup` (InDirectory): the forest's domains (`MigrationService.ForestDomains`, once per press) and each entry
     through `TryResolveExcludedGroup` in the press's warm session: one group -> Verified; groups in several domains ->
     Ambiguous ("domain: DN; ..."); none -> Missing; unreadable -> Unreadable.
   - A6 `OrganizationalUnit` (InDirectory, the DN forest-checked by the engine): `Get-ADOrganizationalUnit -Identity <dn> -Server
     <the DN's own domain>` with the directory-read credential (new `PowerShellProtectedGroupDirectory.OrganizationalUnitExists`,
     on `IVerifyDirectory`): the OU -> Verified; the AD module's not-found (`IsConfirmedAbsence` = `ExchangeServiceBase.IsNotFound`,
     the shape Migration reads) -> Missing - which is also its answer for a container that is not an OU (plan B.2: missing, not
     wrong type, because the module refuses it exactly as it refuses a missing OU); any other failure, or not exactly one row ->
     Unreadable.
   - A7 `SearchBase` (InDirectory, forest-checked): a read of the DN on a DC of its own domain (`ReadByDns`): any object -> Verified,
     none -> Missing; the DN is compared without the spacing it was typed with around `,`/`=`.
   - A8 `RecipientGroup` (OutsideDirectory): `ConferenceRoomService.LookUpRecipientsForVerifyAsync` - one `Get-Recipient -Identity
     <address> -ErrorAction Stop` per address in the module's pooled Exchange session (`RunPooledReadAsync`, Get- only, on the
     read-cancellation tripwire), each through `ReadUnlessNotFound`, the base's not-found translation (category ObjectNotFound,
     a NotFoundException reason or FQID, or Exchange's "couldn't be found" sentence about that address) -> no rows -> Missing;
     any other failure -> Unreadable. One recipient whose `RecipientType` is a mail-enabled universal distribution or security
     group, a non-universal group or a dynamic distribution group -> Verified; another type -> WrongClass(type); several ->
     Ambiguous. Production reaches the scoped service through `ConferenceRoomRecipientGroupLookup` (a scope per press). No
     protected-principal check is added (Known Failure Class 3).
4. **Writes (C.2, B.3)** (`Services/Storage/ModuleConfigRepository.cs`, on V3's `ModuleGroupVerifyStore.Write`). The pane's
   version is the SHA-256 of its verified settings' values exactly as stored. `RemoveVerifyEntry` takes the one entry out by
   the module's own parsing - a list keeps every other segment exactly as stored, in order; a single value, or a list left with
   no entry, becomes empty - refused unless the values are still the version Verify read and the entry is still there, and
   refused when it would empty a `WarnBeforeLastEntry` setting (`DefaultSearchBase`) unconfirmed; then compare-and-swaps the
   ignore list and prunes ignores of entries no verified setting holds any more, in the same transaction. `AddVerifyIgnore`
   re-checks the entry under the lock. `ModuleGroupVerifyStore.Write`/`Prune` gained an optional `identityOf` (default the V3
   rule, unchanged). The pane's ordinary save (`UpdateModuleIf`, and `UpdateModule`) prunes the same way when it writes a
   verified setting. `ModuleConfigService.ApplyVerifyWrite` runs under its write lock and raises `ConfigSaved`.
5. **Actions (C.2, R3)** (`Services/ModuleConfigVerifyActions.cs`, V3's shape). Every action and the press re-authorize at the
   click as the page's own gate: the `AdminSettings` policy OR `ModuleAdminService.IsModuleAdmin(moduleId, user)`. Refusals are
   audited `LogSettingsSaveRefused(..., "Verify:Config", "Authorization" | "NotOffered" | "VersionChanged" | "RowChanged" |
   "IgnoresChanged" | ...)`, successes `LogSettingsChange(..., "Verify:Config", before, after, {action, finding, observed, module,
   row})`, failures `LogSettingsSaveException`. Remove and Ignore retry once on IgnoresChanged. **Edit...** (the `Replace`
   action) writes nothing: authorized and checked as offered, then the page opens the field - the OU browser for an OU or
   search-base DN - for the pane's ordinary edit and Save. **Remove now** of the last AD Attribute Editor search base returns
   `NeedsConfirmation` with "no search-base boundary will remain: the editor may then search the whole directory" until
   confirmed. Ignore is display-only: no module reads the list. A press writes one success row with its counts and `verify_last`
   under `Config:<ModuleId>`.
6. **Page (C.1, C.3)** (`Components/Pages/ModuleConfig.razor`). The config pane of a module with verified settings gets its own
   Verify button (`btn btn-outline-success`, "Verifying..."), summary line, last-verified line, pane note naming the module's
   rule, and under each verified field its tip and, after a press, each entry with its badge and, under a finding, its detail and
   actions (`RenderFragment<string>` markup template). Offered whenever the pane is idle (no unsaved edit, no save, no press or
   action running, store readable) - the page's load already admits only a global admin or the module's admin; the handlers
   check only idleness, so a direct invocation still reaches the audited re-authorization. The last-entry confirmation is an
   in-page strip with "Remove the last search base" and "Cancel". Field editors: `OU` and `SearchBase` get the text box and the OU
   browser (now keyed by setting - `ouBrowserKey`, `ToggleOUBrowserFor`; a single-value setting picks one OU); a list `AdGroup`
   and `RecipientGroup` are text boxes; the single-value `AdGroup` branch is unchanged (no catalogue field uses it).
   ClickGate: `ClickGateRegistry` still has no line entries for ModuleConfig.razor ("tier 2, approved 2026-10-12, not started").
   `ProgressRegistry`: `VerifyConfigAsync` (Reports, `Verifier.VerifyAsync(`), `OnConfigVerifyActionAsync`,
   `ConfirmConfigVerifyRemoveAsync`, `CancelConfigVerifyRemove`, `ToggleOUBrowserFor` (Exempt).
7. **Reach disclosure (R5)**: every badge hover of `LockdownOuDn` and `ActiveDirectorySearchBase`, and both modules' pane notes,
   carry `ModuleConfigVerifyAdapter.ReachDisclosure` (Verify proves existence and type as read with the Directory Read Credential,
   not that the module's own account - and BitLocker's configured server - can reach it).
8. **Help (Known Failure Class 5)**: button tip, pane notes per module, field tips per setting, every badge and action hover;
   README: Migration, AD Attribute Editor, Conference Rooms, Emergency Disable and BitLocker Recovery each gain their Verify
   bullet, Per-Module Config Pages a paragraph; `docs/AdminModuleSpec.md` a "Config Field Types" table; `docs/ConfigDbOperations.md`
   the post-deploy step (plan D): after `db identifier-status`, "press Verify on each page", naming every pane.
9. **Versions**: none. Stored values and module runtime unchanged (C.3); the Migration extraction changes no observable behaviour
   (existing tests unchanged and passing; M04). Module Configuration rides the base 2.36.0 (undeployed).

## Where the code goes beside the plan's text

- **One store and one adapter for A5-A8** (plan B.1: "A5-A8 one each"). The engine maps one adapter per store name; one store
  `Config` keeps the `verify_last` key `Config:<ModuleId>` and the audit section `Verify:Config` in V3's shape, and the adapter
  dispatches by setting key to each module's rule (`ModuleConfigVerifyAdapter.Settings`). The ignore's row key is the setting key;
  that each verified key belongs to exactly one module is pinned by `TheVerifiedSettings_CarryTheirFieldTypes_AndEachKeyBelongsToOneModule`.
- **A5 reads as the module reads, not with the directory-read credential.** `TryResolveExcludedGroup` and `ForestDomains` pass no
  `-Credential`: in the press's warm session (the same `ProtectedPrincipalService.DirectorySessions` pool the eligibility check is
  lent) they run under that session's own identity, exactly as the module's check does. So A5's answer is the module's own,
  including its reach; R5's "Verify's reach is the shared credential" holds for A6/A7 only, which is why only they carry the
  disclosure. The domain list is the module's `Get-ADForest .Domains`, not V1's crossRef read (the brief said crossRefs; the plan
  says "the Migration module's own per-domain lookup"); both are discovered at run time from the host's forest (Invariant 7).
  A lookup the module would have thrown on (`Unreadable`) fails the session, as the module's check does.
- **A7 is the adapter's own read, not the engine's `Dn` lookup kind.** The engine's DN lookup matches the directory's returned DN to
  the stored text exactly; a search base typed with spaces after its commas (which `-SearchBase` accepts) would read missing. The
  adapter reads each DN on a DC of its domain (`ReadByDns`) and compares without the separator spacing. The forest check is the
  engine's, on `StoredDn`, unchanged.
- **`IVerifyDirectory` gained three members** (`OrganizationalUnitExists`, `ForestDomainNames`, `ResolveExcludedGroup`) with
  default bodies that throw - so a directory without them answers unreadable - implemented on `VerifySessionDirectory`. The V1
  header's "V3/V4 add adapters, not engine changes" holds for the engine's logic; its directory interface grew.
- **A6: a container is Missing, not WrongClass** (the brief's "What V4 builds" listed WrongClass; plan B.2 says missing). Plan B.2
  wins: `Get-ADOrganizationalUnit` answers a container with the AD module's not-found, and the lockdown step refuses it just as it
  refuses a missing OU. A read with no error and not exactly one row is unreadable, not missing (R4: only the not-found shape is an
  absence).
- **A8: several recipients is Ambiguous** (plan silent; never verified). Group types counted: `RecipientType`
  MailUniversalDistributionGroup, MailUniversalSecurityGroup, MailNonUniversalGroup, DynamicDistributionGroup. ASSUMPTION,
  unverified (no live call): Exchange Online's `Get-Recipient` reports `RecipientType` for every recipient and these are its group
  values; a recipient whose type is not read is wrong type ("recipient type not read"), never verified.
- **Edit is the `Replace` action, labelled "Edit..."**: it writes nothing - it is authorized (a refusal audited) and opens the field
  (the OU browser for an OU or search-base DN); the change goes through the pane's ordinary Save, its compare under the lock, its
  audit row and its prune. An allowed Edit click leaves no `Verify:Config` row (as V3's Replace).
- **The last-entry confirmation** is an in-page strip ("Remove the last search base" / "Cancel"; the page has no dialog), and the
  write itself refuses an unconfirmed removal of the last `DefaultSearchBase` entry (M16). The unconfirmed click writes nothing and
  is not audited - it is the question before the action; an unauthorized click is refused and audited first.
- **Duplicates**: one item per distinct entry (case-insensitive); Remove takes every segment equal to it.
- **OU browser** now offered on every `OU`/`SearchBase` field (Emergency Disable's lockdown OU and BitLocker's search base gain it,
  picking one OU), keyed by `ouBrowserKey`; `ToggleOUBrowser()` keeps its name and AD Attribute Editor default. The list `AdGroup`
  field is a text box: `ADGroupAutocomplete` replaces the whole value on a pick. No catalogue field uses the single-value `AdGroup`
  branch, which is unchanged.
- **Prune also on `UpdateModule`** (the directory-read secret and Exchange connection writes), not only the page's
  `UpdateModuleIf`; it touches the list only when a verified setting was written.
- **`ModuleGroupVerifyStore.Write`/`Prune`** (V3 code) gained an optional `identityOf`; V3's callers pass none and keep `RowIdentity`.
- **Commit trailer**: `Co-Authored-By: Claude Opus 5.5` (the model that did the work, as the attribution rules require), not the
  brief's "Claude Fable 5.1".

## Plan statements found wrong against the code

- Plan A7 cites `Services/BitLockerLiveDirectorySearch.cs:224`, `:287`; at `b6281502` the search-base read is `AddAdScope`, `:278-285`.
- The brief's "the Protected Principals pane note from V1 already says this for Emergency Disable and BitLocker": V1's
  `AdminSettings.razor` `VerifyPaneNote` says generically that Verify proves existence and kind "as read with the Directory Read
  Credential" and names neither module. V4's disclosure uses the same credential wording.
- `docs/AdminModuleSpec.md` header still reads "Version: 2.0 / Last verified against code: commit cd83823 (2026-06-25)" against
  csproj 2.36.0 - already logged as cpe-4; reported, not fixed (V4 added only the field-type table).

## Tests (new)

38 new tests in four files. `ModuleConfigVerifyExcludedGroupTests` (real engine, adapter, `VerifySessionDirectory` and
`PowerShellProtectedGroupDirectory` over `MigrationStubExchange`'s directory stand-in, and the real `MigrationService`): a GUID entry
verifies; none in any domain is missing and the module's check still throws, in the same words; several domains is several objects
and the module still excludes a member of each; a domain that cannot be asked is unreadable for that entry only; comma split.
`ModuleConfigVerifyEngineTests` (stand-in forest): a container at A6's DN is missing while the same DN at A7 verifies; A6 in its own
domain, foreign domain unreadable; each semicolon entry, any object, spacing; a failed read unreadable; A8 by recipient type;
actions per finding; the reach hover on A6 and BitLocker only; help on every badge, action, field and pane; field types and one
module per key; the A6 read's not-found translation through the real `PowerShellProtectedGroupDirectory` over a stand-in
`Get-ADOrganizationalUnit`. `ModuleConfigVerifyRecipientTests` (real `ConferenceRoomService` on a real `ExoConnectionPool`, stand-in
`Get-Recipient`): Exchange's not-found in both shapes is missing, any other exception unreadable, a not-found naming another object
unreadable; through the engine. `ModuleConfigVerifyStoreTests` (real SQLite, real services, the page through `SettingsPageHarness`):
`missing;valid` leaves `valid`; the last search base needs the confirmation and its warning (actions, write and page); one comma
entry; single values cleared x3; version mismatch refuses; ignores pruned on Remove and on the pane's ordinary Save; Ignore and
NotOffered; a module admin (not global) may act on A5-A8 (x5) and the same principal is refused and audited on V3's Access and
Module Admins; neither admin refused and audited; a module admin is offered Verify on the page; Edit opens the OU browser for the
lockdown OU, which picks one; the run-book sentence (a doc test, the repo's pattern). Existing test code changed:
`SettingsPageHarness` registers `ModuleConfigVerifyAdapter` in its default engine; `ProgressRegistry` gained five ModuleConfig
entries. No existing assertion changed. The Migration extraction's "behaviour unchanged" is held by the existing
`MigrationForestLookupTests` and `MigrationExcludedGroupTests`, unchanged and passing (M04 shows they bite).

## Guard proof

Each mutation applied alone to the code at `e5c0eb1d` (M02, M14 re-run at `2e156c1c`), the test project built, the
`ModuleConfigVerify*`, `ModuleGroupVerify*`, `MigrationForestLookup*` and `MigrationExcludedGroup*` tests run, the file restored by
`git checkout` (scripted; tree clean afterwards). 31 mutations, 31 caught. M02's first form did not compile; M14 first survived -
the comma test's spacing could not tell trimmed from as-stored - so that test was tightened in `2e156c1c` and both were re-run.

| Mutation | Failing tests |
|---|---|
| M01 A5 none-in-any-domain read as unreadable | `AValueNoDomainHolds_IsMissing_NotUnreadable_AndTheModulesOwnCheckStillThrowsForIt`, `EachCommaEntry_IsItsOwnItem_SplitAsTheModuleSplitsIt` |
| M02 A5 several domains read as verified | `AValueSeveralDomainsHold_IsSeveralObjects_AndTheModuleStillExcludesAMemberOfEach` |
| M03 A5 an unreadable domain read as missing | `ADomainThatCannotBeAsked_IsUnreadable_NeverMissing_AndOnlyForThatEntry` |
| M04 Migration's thin caller no longer throws on none | `ADnTypedIntoTheCommaSeparatedSetting_CannotBeResolved_RatherThanMatchingEveryGroupNearIt`, `AnExcludedGroupCheckThatCannotBeCompleted_IsUndetermined_NeverEligible`, `AGroupNoDomainHolds_NamesEachDomainsAnswer`, `AValueNoDomainHolds_IsMissing_NotUnreadable_AndTheModulesOwnCheckStillThrowsForIt` |
| M05 A6 by a DN read (any object), not Get-ADOrganizationalUnit | `AContainerAtTheLockdownOuDn_IsMissing_WhileTheSameDnAsASearchBase_Verifies`, `TheLockdownOu_IsReadInItsOwnDomain_AnOuVerifies_NothingIsMissing_AForeignDomainIsUnreadable` |
| M06 A6 not-found not translated (absence throws) | `TheLockdownOuRead_TranslatesOnlyTheNotFound_AsAConfirmedAbsence` |
| M07 A6 every failure read as absence | `TheLockdownOuRead_TranslatesOnlyTheNotFound_AsAConfirmedAbsence` |
| M08 A7 DN compared as typed (no spacing tolerance) | `EachSemicolonSearchBase_IsItsOwnItem_AnyObjectVerifies_AndTypedSpacingIsNotAMissingObject` |
| M09 A8 any recipient verified (no group-type check) | `AConferenceRoomsAddress_IsVerifiedOnlyAsAGroup`, `ThroughTheEngine_AGroupVerifies_AnotherRecipientIsTheWrongType_NotFoundIsMissing_AFailureIsUnreadable` |
| M10 A8 every Get-Recipient failure read as not found | `ExchangesNotFound_IsMissing_InBothItsShapes_AndAnyOtherFailure_IsUnreadable`, `ThroughTheEngine_AGroupVerifies_AnotherRecipientIsTheWrongType_NotFoundIsMissing_AFailureIsUnreadable` |
| M11 A8 Exchange's not-found not translated (read as a failure) | `ExchangesNotFound_IsMissing_InBothItsShapes_AndAnyOtherFailure_IsUnreadable`, `ThroughTheEngine_AGroupVerifies_AnotherRecipientIsTheWrongType_NotFoundIsMissing_AFailureIsUnreadable` |
| M12 a list split as one value | `EachSemicolonSearchBase_IsItsOwnItem_AnyObjectVerifies_AndTypedSpacingIsNotAMissingObject`, `ADomainThatCannotBeAsked_IsUnreadable_NeverMissing_AndOnlyForThatEntry`, `EachCommaEntry_IsItsOwnItem_SplitAsTheModuleSplitsIt`, `AModuleAdminWhoIsNotAGlobalAdmin_MayActOnTheConfigPane_AndIsRefusedAndAuditedOnTheGroupTabs`, `EveryDirectWrite_RefusesWhenTheSettingsChangedSinceVerifyRead_AndAuditsTheRefusal`, `Ignore_RecordsTheFinding_ChangesNoSetting_AndIsRefusedForAnEntryThatIsGone`, `Remove_TakesTheEntrysIgnores_AndKeepsEveryOtherIgnore`, `RemovingAnExcludedGroup_TakesExactlyTheOneCommaEntry_KeepingTheOthersAsStored`, `RemovingOneMissingSearchBase_FromMissingAndValid_LeavesTheValidOne`, `RemovingTheLastSearchBase_NeedsTheConfirmation_CarryingTheWarning_AndWritesNothingUntilConfirmed` |
| M13 Remove clears the whole list | `AModuleAdminWhoIsNotAGlobalAdmin_MayActOnTheConfigPane_AndIsRefusedAndAuditedOnTheGroupTabs`, `EveryDirectWrite_RefusesWhenTheSettingsChangedSinceVerifyRead_AndAuditsTheRefusal`, `Remove_TakesTheEntrysIgnores_AndKeepsEveryOtherIgnore`, `RemovingAnExcludedGroup_TakesExactlyTheOneCommaEntry_KeepingTheOthersAsStored`, `RemovingOneMissingSearchBase_FromMissingAndValid_LeavesTheValidOne` |
| M14 Remove re-joins the trimmed entries, not the segments as stored | `RemovingAnExcludedGroup_TakesExactlyTheOneCommaEntry_KeepingTheOthersAsStored` |
| M15 no confirmation before the last search base | `RemovingTheLastSearchBase_NeedsTheConfirmation_CarryingTheWarning_AndWritesNothingUntilConfirmed`, `TheLastSearchBase_OnThePage_AsksForTheConfirmation_WithTheWarning_ThenRemovesIt` |
| M16 the write does not hold the confirmation itself | `TheLastSearchBase_IsNotRemovedUnconfirmed_EvenByAWriteThatSkipsThePageCheck` |
| M17 global admin only (no module-admin branch) | `AModuleAdminWhoIsNotAGlobalAdmin_MayActOnTheConfigPane_AndIsRefusedAndAuditedOnTheGroupTabs`, `AModuleAdmin_IsOfferedVerifyOnTheConfigPane_AndAPressIsAuditedAndStoresTheLastVerifiedLine` |
| M18 any module's admin (or anyone) admitted | `SomeoneWhoIsNeitherAGlobalAdminNorThisModulesAdmin_IsRefusedEveryAction_WritesNothing_AndIsAudited` |
| M19 no version check under the lock | `EveryDirectWrite_RefusesWhenTheSettingsChangedSinceVerifyRead_AndAuditsTheRefusal`, `EveryDirectPrimitive_RefusesWhenTheGrantsAreNoLongerTheVersionVerifyRead_AndWritesNothing` |
| M20 the ordinary save does not prune | `ThePanesOrdinarySave_WithTheEntryGone_DeletesItsIgnores` |
| M21 Remove does not compare-and-swap or prune the ignores | `Remove_TakesTheEntrysIgnores_AndKeepsEveryOtherIgnore` |
| M22 the prune sees this module's settings only | `Remove_TakesTheEntrysIgnores_AndKeepsEveryOtherIgnore` |
| M23 no NotOffered check | `Ignore_RecordsTheFinding_ChangesNoSetting_AndIsRefusedForAnEntryThatIsGone` |
| M24 no reach disclosure in the hover | `TheReachDisclosure_IsInEveryHoverOfTheLockdownOuAndTheBitLockerSearchBase_AndNotTheOthers` |
| M25 Verify offered to global admins only | `AModuleAdmin_IsOfferedVerifyOnTheConfigPane_AndAPressIsAuditedAndStoresTheLastVerifiedLine` |
| M26 the page ignores the confirmation request | `TheLastSearchBase_OnThePage_AsksForTheConfirmation_WithTheWarning_ThenRemovesIt` |
| M27 Edit does not open the OU browser | `Edit_OnTheLockdownOu_OpensTheOuBrowserForThatSetting_WhichPicksOneOu` |
| M28 the OU browser picks several for a single DN | `Edit_OnTheLockdownOu_OpensTheOuBrowserForThatSetting_WhichPicksOneOu` |
| M29 a press stores no last-verified line | `AModuleAdmin_IsOfferedVerifyOnTheConfigPane_AndAPressIsAuditedAndStoresTheLastVerifiedLine` |
| M30 DefaultSearchBase left a Text field | `TheVerifiedSettings_CarryTheirFieldTypes_AndEachKeyBelongsToOneModule` |
| M31 the run-book step missing | `TheRunBook_SaysToPressVerifyOnEachPage_AfterTheDeploy_NamingEveryPane` |

Not separately proven: the Ignore write's own "entry still stored" refusal (the version check refuses first); `VerifySessionDirectory` failing the session on an unreadable excluded-group lookup; the Exchange lookup's scoped resolution (`ConferenceRoomRecipientGroupLookup`, production wiring only).

## Gates

At `2e156c1c` (code unchanged by the record commit), from the worktree root: `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` 0 Error(s), 19 Warning(s) (the baseline); `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` Passed 8601 / 8601 (8563 + 38 new); `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; `git diff --check HEAD` exit 0; `tools/Test-AsciiOnly.ps1` passed. Not run: any live database, directory or Exchange (`Get-ADOrganizationalUnit`, `Get-ADGroup` per domain and `Get-Recipient` proven over stand-ins only); the page in a browser; PSScriptAnalyzer/Pester (no PowerShell changed).

## Left for later

- The supervisor: master gates after the merge, the plan's Status (all four slices built), state and token log; the plan citation fixes above.
