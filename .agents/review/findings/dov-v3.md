# dov-v3: Verify stored directory objects - Module Configuration's group grants (slice V3)

**Severity**: MEDIUM - feature slice. Module Configuration's Access tab (a module's access groups and its Protection servicer
groups) and Module Admins tab had no way to say which stored grants no longer name a group: a deleted or retyped group, a
renamed one whose shown name went stale, a grant still stored by name that grants nothing, all looked the same as a good one.
Plan `docs/DirectoryObjectVerify-Plan.md` (Approved 2026-11-04), A3/A4, B.2-B.4, C.1, C.2 and section E "V3"; decision
`.agents/decisions.md` 2026-11-04.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/dov-v3` (worktree `D:\source\ExchangeAdminWeb-wt\dov-v3`, from master `c10f669f`)
**Commit**: see `git log c10f669f..fix/dov-v3` (`fix(dov-v3): ...`, `record(dov-v3): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built), per plan point

1. **One adapter, two stores** (`Services/ModuleGroupVerifyAdapter.cs`). The key shapes are the same, so one class serves
   `VerifyIgnoreList.SectionAccessStore` ("SectionAccess": every `section_access` row of the module's policy aliases and its
   `ProtectedServicer:<ModuleId>` key; row key = alias) and `VerifyIgnoreList.ModuleAdminsStore` ("ModuleAdmins":
   `module_admins` rows; row key = module id). Items: kind Group, identifier = the usable group SID as parsed
   (`SectionAccessGroupIdentity.Parse`), label = the section-access display name of that value (A4 borrows them), stored value
   exactly as stored. B.2 for a SID grant: the engine's batched global-catalog SID read; none -> Missing, several -> Ambiguous,
   class not `group` -> WrongClass, stored display name vs current `DOMAIN\sAMAccountName` -> LabelDrift (name half compared;
   see departures), else Verified. A value shaped like a SID but not a usable group SID (well-known, domain SID, SDDL alias) ->
   `WrongClass("well-known or unusable SID (...)")`, Remove only, never ignorable.
2. **Each store's own rule for a grant stored by name, no third rule (Invariant 7).** A3: `SectionAccessSidMigrationPlanner.Plan`
   over the DI `ISectionAccessGroupDirectory` (`SectionAccessGroupDirectory`: `DOMAIN\Name` in that domain, a bare name in the
   host's own domain - `Get-ADGroup` with no `-Server`), one distinct value at a time; `DirectoryUnavailableException` ->
   NotConverted(unreadable). A4: `ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants` in the press's directory
   session (`DOMAIN\name` in that domain, a bare name at the global catalog, forest-wide), mapped by V1's
   `ProtectedPrincipalVerifyAdapter.ResolutionOf`. Both discover their domain at run time.
3. **Engine** (`Services/DirectoryObjectVerifier.cs`): one additive lookup kind, `ResolveOutsideDirectory`, and its default
   adapter method `ResolveOutsideDirectoryAsync` (findings, not objects; a throw or a short answer is unreadable for the batch).
   `VerifyResolution` gains an optional `Label` (the display name a conversion stores). Nothing else in the engine changed.
4. **Write primitives** (`Services/Storage/ModuleGroupVerifyStore.cs`, used by `SectionAccessRepository` and
   `ModuleAdminRepository`): one write transaction that reads the tab's rows under the lock, refuses unless they are still the
   version Verify read (`VersionOf`: SHA-256 of the configured flag and the sorted `rowKey\tvalue` lines), lets the action decide
   and write, reads back, and - Remove and Ignore - compare-and-swaps `verify_ignores` against the value read beside the item,
   pruning entries of grants the store no longer holds. A refusal rolls everything back (no change token). Primitives:
   `RemoveWithIgnores`, `RenameDisplayName` (through `SectionAccessRepository.SaveDisplayNames`' write, for that SID only;
   refused unless the grant is there and its stored name is still the one Verify read), `ConvertOne` (A3 through
   `ConvertToSids`' write for exactly `[SidConversion(alias, value, sid, displayName)]`, refused unless it converted that one
   row; A4 through `ConfigDbIdentifiers.ConvertModuleAdminGrant`), `AddVerifyIgnore` (grant re-checked under the lock). Section
   access writes also refuse while section access is not configured (the gates read the appsettings fallback then).
   `ConvertToSids` and `SaveDisplayNames` gained `(connection, transaction)` cores that the public methods now call - same SQL.
5. **Ordinary saves prune too**: `SectionAccessRepository.ReplaceAliases` (the Access tab's save) and
   `ModuleAdminRepository.ApplyChanges` (the Module Admins save, when it deletes) run `ModuleGroupVerifyStore.Prune` in their own
   transaction, so a grant removed with the tab's Remove + Save, or replaced through Replace + Save, takes its ignores with it.
6. **Actions** (`Services/ModuleGroupVerifyActions.cs`, V1's shape): every action and the press ask the `AdminSettings` policy
   for the clicking admin's current principal first - a module admin who is not a global admin is refused, nothing written,
   audited `LogSettingsSaveRefused(..., "Verify:<Store>", "Authorization", ...)`; then the action must be one the finding offers
   and the item must belong to the tab's scope (`NotOffered`). Success: `LogSettingsChange(user, ip, "Verify:SectionAccess" |
   "Verify:ModuleAdmins", before, after, {action, finding, observed, module, row})`; refusal `LogSettingsSaveRefused`
   (Authorization / NotOffered / VersionChanged / RowChanged / IgnoresChanged / IgnoresUnreadable / StoreUnreadable); failure
   `LogSettingsSaveException`. Remove and Ignore re-read the list and retry once on IgnoresChanged. A press writes one success row
   with its counts and `verify_last` (V1's store-keyed map) under `SectionAccess:<ModuleId>` / `ModuleAdmins:<ModuleId>`.
   `Rebase` keeps the other findings at the write's version, with the display names it left.
7. **Ignores are display-only here (B.4)**: `CanIgnore` only for a usable-SID grant's Missing / LabelDrift / WrongClass /
   Ambiguous; no authorization path reads `verify_ignores` (pinned by test against `GroupAuthorizationHandler`,
   `ProtectedPrincipalServicerService.Evaluate` and `ModuleAdminService.IsModuleAdmin`). NotConverted and Unreadable never offer
   Ignore and the engine never applies one to them.
8. **Page** (`Components/Pages/ModuleConfig.razor`): **one Verify button per tab** - the Access tab's covers the module's access
   groups and its Protection servicer groups (both `section_access`, and the servicer section lives inside that tab, sharing its
   load and save path), the Module Admins tab's the module's grants. `btn btn-outline-success`, "Verifying...", offered to a
   global admin only, not while the tab is dirty, a save or another Verify/action runs, the store is unreadable, or (Access) section
   access is unconfigured; `Progress.Begin("Module Configuration", ..., ActivitySize.Items(n))`; badges beside each grant and a
   finding row (detail + action buttons) under it; summary "Verified N entries at <time>: ..."; "Last verified ... by ..." from
   `verify_last`. A press shows the rows it verified and makes them the tab's baseline (so a later Save compares against what
   Verify read); an edit made while the press or an action ran is kept and the findings are not shown against it. The handlers
   check only that the tab is idle, so a direct invocation by a non-global admin reaches the re-authorization and is audited.
   ClickGate: `ClickGateRegistry` has no line entries for ModuleConfig.razor ("tier 2, approved 2026-10-12, not started").
   `ProgressRegistry`: `VerifyGroupsAsync` (Reports, `Verifier.VerifyAsync(`) and `OnGroupVerifyActionAsync` (Exempt) added.
9. **Help** (Known Failure Class 5): both tabs' pane notes (`AccessVerifyPaneNote`, `AdminsVerifyPaneNote`, each naming its
   store's name rule), button tips, every badge's and action's hover (`GroupVerifyBadgeParts`, `GroupVerifyActionTip`); the
   Module Admins note now names Verify's Convert beside `db convert-identifiers`; README Per-Module Config Pages gains the Verify
   paragraph (V1's voice; "An ignore on these tabs changes only what Verify reports, never who is granted anything") and the
   servicer section a bullet. `docs/ConfigDbOperations.md` unchanged: the status printout's meaning is unchanged (only the
   `ProtectedPrincipals group_sid ... Missing` line changes enforcement, as it says).
10. **Versions**: Module Configuration has no `ModuleCatalog` entry (plan header: "Module Configuration has no catalogue entry"),
    so it rides the base app version; the base stays 2.36.0 (undeployed, already absorbing this work stream). No bump.

## Where the code goes beside the plan's text

- **Engine extension.** The V1 record expected V3/V4 to add adapters only. A3's rule (B.2: the migration planner over
  `SectionAccessGroupDirectory`) answers with a resolution - one group, none, several, or no answer - from its own session, not
  with directory objects, so it fits neither `InDirectory` (that hands the press's warm session) nor `OutsideDirectory` (that
  answers objects, which would lose none-vs-several and the unreadable-name case). One additive kind,
  `ResolveOutsideDirectory`, mirrors `InDirectory`'s findings contract outside the session.
- **A3 names are read by the migration's directory, not the shared directory-read credential's warm session** (plan B.1 puts AD
  items in one session). B.2 binds A3 to `SectionAccessGroupDirectory.FindGroupsByName`, whose "bare name = host's own domain"
  is `Get-ADGroup` with no `-Server` under the app's identity in its own runspace; reproducing it in the warm session would be a
  third rule. SID grants still go through the one session. The planner gained `RowMigrationResult.Outcome` (init-only, set where
  it already classified the match count) so Verify can tell none from several; its decisions and the migration are unchanged.
- **LabelDrift compares the name half** of the stored `DOMAIN\name`: the SID fixes the domain (another domain's group has another
  SID) and Verify reads no NetBIOS names. Assumption, unverified against AD: a NetBIOS domain rename would not be reported. A grant
  with no stored display name is never "name changed" (B.2: "where one exists").
- **Remove is "Remove now"**, a direct primitive as on Admin Settings (V1), not the tab's staged Remove; C.2's "pane's existing
  remove + ignore cleanup" is honoured by the ordinary Remove + Save also pruning (point 5). **Replace is the tab's picker and
  save path** (C.2): the click is re-authorized (a refusal audited under `Verify:<Store>`) and takes the grant out of the tab's
  list; the admin picks and saves, and the save's own confirmation, compare-under-lock, audit rows and prune fire. A Replace
  click that is allowed writes nothing, so its success is the save's audit row (the alias / `ModuleAdmins:<id>`), not a
  `Verify:<Store>` row.
- **Rename renames the SID's display name on every alias holding it** - `SaveDisplayNames` keys by group value, not
  (alias, value), and the names are cosmetic.
- **Verify on the Access tab is not offered before section access is configured**, and its writes refuse then: unconfigured, the
  gates read the appsettings fallback, not the stored rows.
- **Commit trailer**: `Co-Authored-By: Claude Opus 5.5` (the model that did the work), not the brief's "Claude Fable 5.1".

## Tests (new)

`ExchangeAdminWeb.Tests/ModuleGroupVerifyEngineTests.cs` (adapter over stand-ins) and
`ExchangeAdminWeb.Tests/ModuleGroupVerifyStoreTests.cs` (real SQLite through the real services; the page through
`SettingsPageHarness`). Plan E "V3" items: every finding kind per tab - `AccessTab_EveryFinding_...` (access and servicer rows),
`ModuleAdminsTab_EveryFinding_...`, `ASessionThatCannotBeHad_...`; Rename only that SID's name - `AccessRename_...`,
`ModuleAdminRename_...`; Convert exactly that alias/value - `AccessConvert_...`, that module - `ModuleAdminConvert_...`; Remove
prunes atomically and a stale list removes nothing - `Remove_TakesTheGrantAndItsIgnores_...` x2; Replace prunes through the
save - `Replace_TakesTheGrantOutOfTheList_...` x2; ordinary Remove + Save prunes - `TheAccessTabsOrdinaryRemoveAndSave_...`,
`TheModuleAdminsTabsOrdinaryRemoveAndSave_...`; version mismatch refuses - `EveryDirectPrimitive_Refuses...` x8,
`AnIgnoreAfterTheGrantWasRemoved_...` x2; module admin (not global) refused and audited -
`AModuleAdminWhoIsNotAGlobalAdmin_IsRefusedEveryVerifyAction_...` x12 (press, Rename, Convert, Replace, Remove, Ignore per
store), `AModuleAdmin_IsNotOfferedVerify_AndADirectPressIsRefusedAndAudited`; success and refusal in separate rows -
`AGlobalAdminsSuccess_AndARefusal_...` x2; bare name host's domain (A3) vs forest-wide (A4), never the other way -
`ABareName_ResolvesInTheHostsDomain_ForSectionAccess_AndForestWide_ForModuleAdmins_NeverTheOtherWayRound`; NotConverted never
offers Ignore - `AGrantStoredByName_IsNeverIgnored_...` x2, `AnIgnoreOfAGrantStoredByName_IsRefused_...` x2, the page test;
an ignore changes no authorization - `AnIgnoreOnTheseStores_ChangesNoAuthorization_AccessServicerOrModuleAdmin`; page -
`APress_WithNoDirectoryReadCredential_...`, `Verify_IsNotOffered_WhileTheTabHasUnsavedChanges_...`,
`EachFinding_ShowsItsBadgeAndHelp_...`, `EveryBadgeAndAction_CarriesItsHelp_...`. 48 new tests. No existing assertion changed;
`SettingsPageHarness.OpenModuleConfig` injects the page's new `Verifier` (settable `ModuleConfigVerifier`), and
`ProgressRegistry` gained the two ModuleConfig entries.

## Guard proof

Each mutation applied to the code at `109a5e31`, the test project built, the `ModuleGroupVerify*` tests (48) run and seen to
fail, the file restored by `git checkout` (scripted; tree clean afterwards). 29 mutations, 29 caught.

| Mutation | Failing tests |
|---|---|
| M01 LabelDrift never reported | `AccessTab_EveryFinding_...`, `ModuleAdminsTab_EveryFinding_...` |
| M02 No class check | both EveryFinding tests |
| M03 Unusable SID treated as a name | both EveryFinding tests |
| M04 A3 names resolved by the A4 (forest-wide) rule | `ABareName_ResolvesInTheHostsDomain_...`, `AccessTab_EveryFinding_...` |
| M05 A4 names resolved by the A3 (host-domain) rule | `ABareName_...`, `AGrantStoredByName_IsNeverIgnored_...` (ModuleAdmins), `ModuleAdminsTab_EveryFinding_...` |
| M06 Planner reports several as none | `AccessTab_EveryFinding_...` |
| M07 A name grant ignorable | `AGrantStoredByName_IsNeverIgnored_...` x2 |
| M08 Convertible name offers Ignore | `AGrantStoredByName_IsNeverIgnored_...` x2, `EachFinding_ShowsItsBadgeAndHelp_...` |
| M09 A3 Rename writes every SID's name | `AccessRename_WritesOnlyThatSidsDisplayName_AndNoGrant` |
| M10 A4 Rename writes every SID's name | `ModuleAdminRename_WritesOnlyThatSidsBorrowedDisplayName_AndNoGrant` |
| M11 A3 Convert converts the value on every alias | `AccessConvert_PersistsThroughConvertToSids_ForExactlyThatAliasAndValue` |
| M12 A4 Convert converts the name in every module | `ModuleAdminConvert_..._ForThatModuleOnly` |
| M13 A3 Remove without ignore compare-and-swap/prune | `Remove_TakesTheGrantAndItsIgnores_...` (SectionAccess) |
| M14 A4 Remove without ignore compare-and-swap/prune | `Remove_TakesTheGrantAndItsIgnores_...` (ModuleAdmins) |
| M15 No version check | `EveryDirectPrimitive_Refuses...` x8 |
| M16 No authorization at the click | `AModuleAdminWhoIsNotAGlobalAdmin_...` x12, `AGlobalAdminsSuccess_AndARefusal_...` x2, `AModuleAdmin_IsNotOfferedVerify_...` |
| M17 Authorization refusal not audited | same 15 as M16 |
| M18 Access ordinary save keeps the removed grant's ignores | `TheAccessTabsOrdinaryRemoveAndSave_...`, `Replace_...` (SectionAccess) |
| M19 Module Admins ordinary save keeps them | `TheModuleAdminsTabsOrdinaryRemoveAndSave_...`, `Replace_...` (ModuleAdmins) |
| M20 Access Replace does not take the grant out of the list | `Replace_...` (SectionAccess) |
| M21 Module Admins Replace does not take it out | `Replace_...` (ModuleAdmins) |
| M22 Section access gates drop SIDs ignored as missing (the plausible wrong implementation) | `AnIgnoreOnTheseStores_ChangesNoAuthorization_...`, plus two prune tests |
| M23 Verify offered while the tab is dirty | `Verify_IsNotOffered_WhileTheTabHasUnsavedChanges_...` |
| M24 Press not re-authorized | `AModuleAdmin_IsNotOfferedVerify_AndADirectPressIsRefusedAndAudited` |
| M25 Ignore tip does not say it changes no grant | `EveryBadgeAndAction_CarriesItsHelp_...` |
| M26 Engine skips the ResolveOutsideDirectory path | `ABareName_...`, `AGrantStoredByName_...` (SectionAccess), `AccessTab_EveryFinding_...` |
| M27 Press stores no verify_last | `APress_WithNoDirectoryReadCredential_...` |
| M28 No NotOffered check | `AnIgnoreOfAGrantStoredByName_IsRefused_...` x2 |
| M29 Retry reuses the stale ignore list | `Remove_TakesTheGrantAndItsIgnores_...` x2 |

Not separately proven: `AnIgnoreAfterTheGrantWasRemoved_IsRefused_...` x2 (the version check refuses first, so its row check
is caught only with M15's version check gone); `ASessionThatCannotBeHad_...` (engine behaviour V1 owns). Item 7's ignore test
has no V3 code to revert (no gate reads the list), so it is proven against M22, the plausible wrong implementation.

## Gates

At `109a5e31` (code unchanged by the record commit), from the worktree root: `dotnet build ExchangeAdminWeb.slnx -c Release
--no-incremental` 0 Error(s), 19 Warning(s) (the baseline); `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`
Passed 8563 / 8563 (8515 + 48 new); `dotnet format --verify-no-changes` exit 0; `git diff --check HEAD` exit 0;
`tools/Test-AsciiOnly.ps1` passed. Not run: any live database, directory or Exchange; `SectionAccessGroupDirectory` and the
global-catalog SID reads against a real forest (proven over stand-ins); the page in a browser; PSScriptAnalyzer/Pester (no
PowerShell changed).

## Left for later

- V4 per plan E (text settings A5-A8), and the run-book's "press Verify on each page" post-deploy sentence with the last pane.
