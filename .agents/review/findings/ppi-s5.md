# ppi-s5: module-admin grants stored by name are converted to group SIDs; the rest are listed on the Admins pane (slice S5)

**Severity**: HIGH - since aud-a1 every module-admin grant stored by name has authorised nothing (`IsModuleAdmin` reads
usable SIDs only), and nothing converted them or said which grants were affected: `GetUnusableGrants` /
`TryGetUnusableGrants` built the inventory with no production caller, and aud-a1's Known gaps carried "the non-resolving-grant
list has NOT been produced". Owner ruling 2026-11-03 (`.agents/decisions.md`, item 8: "stored names are migrated to SIDs by
directory lookup; a name that does not resolve to exactly one group is listed on the Module Configuration page for
re-picking. Nothing the directory can resolve is re-picked by hand."), D3 settled (convert every name that resolves to
exactly one group, effective at once; the audit event lists each). Plan `docs/ProtectedPrincipalIdentifiers-Plan.md`
(Approved 2026-11-03), B.6, slice S5.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/ppi-s5` (worktree `D:\source\ExchangeAdminWeb-wt\ppi-s5`, from master `73d1fdf5`)
**Commit**: see `git log master..fix/ppi-s5` (pass and page commit, records commit) | **Reviewer**: implementer (candidate)

## Evidence

- At `73d1fdf5`: `Services/ModuleAdminService.cs` `IsModuleAdmin` -> `GroupMembershipChecker.UsableSidsOnly`; the
  inventory methods had no caller outside tests; `Components/Pages/ModuleConfig.razor` Admins pane rendered a legacy name
  row exactly like a SID row; `.agents/review/findings/aud-a1.md` Known gaps.
- Supervisor-verified: prod's build evaluates a grant with `WindowsPrincipal.IsInRole(string)`, which accepts a SID string,
  so replacing a name with its SID in place is safe for both builds sharing the database.

## Approach (as built)

- **Lookup (no second resolver).** `ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants` beside S2's `Resolve`,
  sharing its per-row loop (`ResolveEach`: a throw is `DirectoryUnavailable` for that grant alone). Parsing is
  `SectionAccessGroupIdentity.Parse`, section access's own. `DOMAIN\name` -> `ResolveDomainServer` (S2's crossRef read) and
  a search of that domain only; a bare name -> the global catalog. One `Get-ADGroup -LDAPFilter` through the new
  `GroupLookupKey.AnyGroupName`: `(&(objectCategory=group)(|(sAMAccountName=v)(cn=v)(name=v)(displayName=v)))`, values
  escaped by `EscapeLdapFilter`. Exactly one group (distinct by DN) converts; zero -> "not found"; several -> "N groups
  match: <DN>; <DN>"; a failed read -> "directory unavailable: <cause>"; a group whose SID is well-known or not a domain
  account's -> "well-known SID refused". A stored value shaped like a SID but unusable, or a `BUILTIN\` / `NT AUTHORITY\`
  prefix, is refused without a lookup.
- **One operation.** `ProtectedPrincipalService` implements the new `IModuleAdminGrantResolver` through the body S2's
  `ResolveLegacyRowsAsync` used, now shared as `ResolveInOneOperationAsync`: one `DirectoryReadOperation`, one
  directory-read credential fetch, the shared throttle, one warm session for the whole pass.
- **Write.** `ModuleAdminRepository.ReplaceNamesWithSids` (via `ModuleAdminService.ReplaceNameGrantsWithSids`, under the
  service's write lock): one transaction; each row re-checked under the lock - a grant no longer present is left and
  reported changed (aud-a33b); `UPDATE ... SET admin_group = <sid>` in place, or `DELETE` of the name row when the module
  already holds that SID; nothing to write rolls back. No DDL.
- **Pass.** `ModuleAdminSidMigration` singleton (S2's shape): inventory from `TryGetUnusableGrants` (unreadable ->
  `StoreUnavailable`, nothing written); each distinct value resolved once across modules; status reuses
  `ProtectedPrincipalMigrationStatus`; never throws; one audit event per pass (`LogModuleAction`, action
  `ModuleAdminSidMigration`, category `AdminSettings`, target `Module Admins`; `converted` "module: name -> sid", `left`
  "module: name (reason)", `changedElsewhere`); flagged grants kept in `app_setting` `module_admin_sid_flags`
  (display-only). Started from `Program.cs` after the S2 pass on the thread pool, not awaited.
- **Page.** The Admins pane loads this module's grants stored by name through `TryGetUnusableGrants` (its first production
  caller) and marks each row *not converted*, with the last pass's reason visible under the name and the full explanation on
  hover; the existing Remove sits beside each (hover: it grants nothing, so removing it takes no access away), applied by
  the tab's Save, which removes exactly that row. A warning with the count and **Resolve now** (busy guard, unsaved-changes
  refusal, `AdminSettings` re-check, status-frame activity with its own token, reloads the module's list after); a result
  line. Pane note rewritten: what a SID grant is, what a name grant does (nothing), what the pass does.
- **Help (Known Failure Class 5).** Pane note, badge and reason text, Resolve now and Remove hovers; README Module Admins
  bullet. `ProgressRegistry` reports `ResolveAdminsNowAsync`.

## Where the code goes past or beside the plan's text

- B.6 says "Same credential and concurrency rules as section access's pass". Section access's pass is all-or-nothing and
  synchronous; this follows S2's identifier pass instead: each grant its own decision (an unresolvable name was inert
  before and stays inert, so holding the others back would only delay access the owner ordered restored), run on the
  thread pool and not awaited.
- A grant whose SID the module already holds is deleted rather than updated (the table's primary key forbids a duplicate);
  the grant is then held once, by SID. Recorded as converted.
- `BUILTIN\` / `NT AUTHORITY\` prefixes and SID-shaped unusable values are refused without a lookup ("well-known group /
  SID refused"); a `DOMAIN\` the forest does not have is "directory unavailable: <cause>", as S2 records it.
- The flagged list is not a separate list: the pane's own rows carry the badge and reason, so Remove is the existing one and
  the tab's Save applies it with every guard that save already has.
- A converted grant shows its SID on the pane (module-admin grants have no display-name store; a picked grant shows the
  same). Unchanged behaviour, noted.
- Resolve now is refused while the Admins tab has unsaved changes; a pass already running answers `AlreadyRunning`.
- Versions: the Module Configuration page belongs to no catalog module (`Modules/ModuleCatalog.cs` has no entry; the
  CancellationDepth B2 precedent, `d2ef22bf`, bumped nothing for it). Base 2.36.0 is undeployed and absorbs it. No bump.
- ClickGate: `ModuleConfig.razor` is "tier 2, not started" in `ClickGateRegistry`; no line-keyed entry, nothing re-anchored.
- Test support: the stand-in forest gains `Entry.DisplayName` and reads `displayName`; the settings harness builds and
  injects the pass.

## Guard proof

Each reverted, the named test failed, restored, the suite passed:

| Revert | Failing test |
|---|---|
| P1 `DOMAIN\name` searched at the catalog | `ADomainQualifiedGrant_ResolvesOnlyInItsDomain_NotToASameNamedGroupOfAnotherDomain` (+2) |
| P2 `displayName` dropped from the search | `TheSearch_MatchesTheSamAccountName_TheCommonName_AndTheDisplayName_GroupsOnly` |
| P2b `sAMAccountName` dropped | same |
| P2c `cn` and `name` dropped | same |
| P3 ambiguity rule disabled | `ZeroManyUnavailableAndWellKnown_AreLeft_WithTheirReason`, `GrantsThatDoNotResolveToExactlyOneGroup_AreListedWithTheirReason_AndNothingIsWritten` (+1) |
| P4 well-known SID result accepted | `ZeroManyUnavailableAndWellKnown_AreLeft_WithTheirReason` |
| P5 conversion skipped | `AConvertedGrant_AuthorisesAtOnce_AndTheAuditListsIt` (+5) |
| P6 credential fetched once per grant | `OnePass_FetchesTheDirectoryReadCredentialOnce_ForEveryGrant` |
| P7 pass reads every grant, not the unusable ones | `ASecondPass_IsANoOp` (+2) |
| P8 under-lock re-check skipped | `AGrantChangedBetweenTheReadAndTheWrite_IsNotConverted` |
| P9 startup pass run synchronously | `TheStartupPass_DoesNotDelayStartup_WhenTheDirectoryHangs` |
| P10 no Remove beside a flagged grant | `TheAdminsPane_ListsEachGrantStoredByName_WithItsReason_AndARemoveBesideIt` |
| P11 Remove takes every flagged grant | `RemoveBesideAFlaggedGrant_DeletesExactlyThatRow` |
| P12 unsaved-changes refusal removed | `ResolveNow_IsRefused_WhileTheTabHasUnsavedChanges_AndRunsNothing` |
| P13 busy guard removed | `ResolveNow_PressedWhileAPassFromThisPageRuns_DoesNotStartAnother` |
| P14 each module's copy of a value looked up | `OneValueHeldByTwoModules_IsLookedUpOnce_AndConvertedInBoth` |
| P15 name row updated when the SID is already held | `AGrantWhoseSidTheModuleAlreadyHolds_IsRemoved_LeavingTheGrantHeldOnce` |

## Gates (worktree, 2026-11-03)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: succeeded, 0 errors, 19 warnings (none in a changed file).
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 8412 passed, 0 failed, 0 skipped.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1`: passed.
- Not run: any live directory read (the DOMAIN lookup is proven on a fake directory, the attribute match and the one fetch
  on the stand-in forest only); startup was not run; the page was not rendered in a browser.


## Left for later slices

Records, plan status and state (S6). Dev: at its first start on this build every module-admin name that names exactly one
group starts granting module admin; the pass's audit event lists each.
