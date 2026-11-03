# ppi-s6: identifier conversion is a one-time db command-line step; the check never resolves or matches a stored name (slice S6)

**Severity**: HIGH - the S2 and S5 startup passes, their flags, both Resolve now buttons and badges, and the evaluator's
name-resolution and shared-name branches (sweep-c item 17's first build, `db0813b9`) kept stored names live in the new
build: an unconverted protected user or OU was still matched by its text, a protected group by a name lookup at every
check. Owner, 2026-11-03: "why do we need to build in a conversion pass and carry that tech debt when we could just
write something to update the db before deploying." ... "I don't want that to be part of the deployment pipeline."
Plan `docs/ProtectedPrincipalIdentifiers-Plan.md` section K (Approved 2026-11-03), K.3, slice S6; decision
`.agents/decisions.md` 2026-11-03 "Identifier conversion is a one-time command-line step, not a startup pass".
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-c` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-c`, from `712694a6`, which merged master `37b74833`)
**Commit**: see `git log 712694a6..fix/sweep-c` (`fix(ppi-s6): ...` x2, `record(ppi-s6): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `712694a6`: `Program.cs` registered and started `ProtectedPrincipalIdentifierMigration` and `ModuleAdminSidMigration`;
  `ProtectedPrincipalService.CheckDirectUserMatches`/`CheckOuMatches` matched unpaired legacy rows by text (D1);
  `ProtectedGroupMembership.Candidates` looked up `DOMAIN\Name` and bare names and `Evaluate(configuredEntries: true)`
  let a name several groups carry protect the members of each and a stale entry protect nobody (`Result.Stale`).

## Approach (as built), per K.3 point

1. **The verbs** (`Services/Storage/ConfigDbIdentifiers.cs`, dispatched from `Services/Storage/ConfigDbCli.cs`; the
   db mode still reads no appsettings.json and starts no host). `db convert-identifiers <db> [--plan]`: reads (read-only
   connection) every user/group/ou row with no identifier companion and every module-admin grant that is not a usable SID;
   resolves each once through `ProtectedPrincipalIdentifierResolver` (B.4 step 3 rules for rows, B.6 for grants, a grant
   value held by several modules looked up once); any row resolving to none or several objects, a well-known SID, or a
   failed read -> nothing written, exit 2, every offending row printed `kind: value - reason`. `--plan` prints the
   would-be writes and never opens the database for writing. A real run writes everything in ONE `SqliteConfigStore.Write`
   transaction (BEGIN IMMEDIATE, change token bumped), after re-reading the work under the write lock: if it is not
   exactly what was resolved, nothing is written and it says to run again. Exit 0 prints `kind: legacy value -> identifier`
   per conversion (`module-admin <module>: name -> SID` for grants) and `converted N`; a second run prints
   `nothing to convert ...`. `db identifier-status <db>`: read-only, one line per identifier row (`converted` /
   `identifier`), per unpaired legacy row (`legacy`) and per grant (`grant-sid` / `grant-name`), a count line, exit 2 while
   any is `legacy` or `grant-name`. Output contract extended: report on stdout after exit 0, on stderr ending in an
   `error:` line after exit 2. Directory: `Services/InvokingUserIdentifierDirectory.cs`, its own runspace with the
   ActiveDirectory module, every read with no `-Credential` (the invoking user), no Delinea, no `DirectoryReadOperation`;
   the global catalog from `Get-ADForest` and the NetBIOS mapping from `Get-ADRootDSE`/CN=Partitions of the host's own
   forest. The resolution was moved, not copied: `ProtectedGroupMembership.Resolve`/`ByName`/`One` and the DN /
   `DOMAIN\Name` / bare-name branches of `Candidates` now live in `ProtectedPrincipalIdentifierResolver.ResolveGroupEntry`;
   `IProtectedPrincipalResolveDirectory` is a narrow four-read interface; `PowerShellProtectedGroupDirectory.FindUsers`
   (its only caller was the resolver) is gone. Repository writes are transaction-scoped statics
   (`ProtectedPrincipalRepository.ReadRows`/`InsertIdentifierRow`, `ModuleAdminRepository.ReadAll`/`ReplaceNameWithSid`);
   `AddIdentifierRows`, `ReplaceNamesWithSids` and their outcome records are gone.
2. **Removed**: both migration classes, `Program.cs` registrations and invocations, the `protected_principal_identifier_flags`
   and `module_admin_sid_flags` keys and their readers, `ProtectedPrincipalService`'s two resolver interfaces and
   `ResolveInOneOperationAsync`, both Resolve now buttons and handlers, both badges and their tips, the two
   `ProgressRegistry` entries, the harness wiring, and the four test files. Tests that pinned resolution logic were
   re-homed into `ExchangeAdminWeb.Tests/ConfigDbIdentifierTests.cs`; the page tests that pinned the lists and Remove into
   `IdentifierConversionPageTests.cs`. ClickGate: `ClickGateRegistry` has no line entries for either page (both "tier 2,
   not started"), so nothing to re-anchor.
3. **SIDs only.** `ProtectedGroupMembership.Candidates` answers a SID (catalog) and a DN (read in its own domain, kept
   because `SelfGrantRun` and `ResourceDomainDenies` hand the evaluator DNs of groups they have just read - see below);
   any name entry -> incomplete `NotConverted` ("not converted - run db convert-identifiers"). No match or several ->
   incomplete, named. `Result.Stale`, `configuredEntries`, the shared-name branch and their tests are gone.
   `ProtectedPrincipalService.NotConvertedProblems` names every unpaired legacy user, group (unless stored as a SID) and OU
   row, and every identifier row whose identifier cannot be read; `CheckAsync` refuses through `IncompleteReason` unless a
   rule matched (no directory, no credential needed when nothing else needs it); `CheckWriteTarget` does the same for
   users and OUs (it has no Groups rule). Legacy rows are never string-matched. The banner now opens "Protected-principal
   check was incomplete - ..." (it names user and OU rows too); its no-cause fallback text is unchanged.
4. **Module-admin grants.** `IsModuleAdmin` unchanged; the Admins pane lists a name grant with the fixed text
   `not converted - run db convert-identifiers` (`ModuleConfig.NotConvertedAdminText`) and its Remove.
5. **Save writes both forms - verified, not re-implemented.** `AdminSettings.razor:1327` adds through
   `ProtectedPrincipalIdEntry.AddWithCompanion` (identifier row plus legacy companion), `:554` removes through
   `RemoveWithCompanion` (both, unless another identifier row still pairs with the companion), and the save goes
   `ProtectedPrincipalService.ReplaceConfig` -> `ProtectedPrincipalRepository.Replace` -> `InsertAll`, which writes all
   eight kinds.
6. **Help and docs.** Admin Settings pane paragraph, its count warning and the "not in AD" tips; Module Configuration
   Admins pane paragraph and warning; README Module Admins, Protected Users and a new Troubleshooting entry;
   `docs/ConfigDbOperations.md` "Converting protected principals and module-admin grants to identifiers" (K.4 run-book
   and the old-build-save constraint); `docs/ConfigDbToolInApp-Plan.md` 3.1 pointer.
7. **Versions.** AdminSettings 1.2.5 -> 1.2.6. Base 2.36.0 unchanged.

## Where the code goes beside K.3's text

- **`Candidates` keeps the DN case as well as the SID case.** K.3 point 3 says "keeps only the `Sid` case", and the same
  point says the self-grant and self-service checks "are unchanged". Both hand `Evaluate` DNs
  (`PermissionValidator.cs:959` passes `groups`, DNs; `ResourceDomainDenies.cs:107` passes `c.Dn`), so removing the DN
  case would change them. The DN read is kept for those callers only: the protected-principal check never passes a stored
  DN, because its unpaired legacy rows are refused before `Evaluate` (`GroupEntries` passes only SIDs). Supervisor: the
  owner may want this one line.
- **A protected group whose SID no group carries any more refuses every check that finds no other match**, named. This
  follows from removing `Result.Stale` and `configuredEntries` as K.3 orders; it reverses item 17's first build for that
  case. A deleted group's `group_sid` row must be removed by an admin.
- **Module Configuration has no catalog entry**, so K.3 point 7's ModuleConfig patch bump has nothing to bump (as ppi-s5
  recorded). Only AdminSettings is bumped.
- **An identifier row whose identifier cannot be read** (a hand-edited row) now refuses the check, named "its stored
  identifier cannot be read", where S1 compared it by its label. It is a name match the K rule removes; `convert-identifiers`
  cannot repair it (its companion is paired), so the message says to re-pick it.
- **Left as S1 built them, not named in K.3:** a `user_sid` row is still compared by its label when the TARGET carries no
  SID (a cloud-only recipient), and the directory-free write-target check still compares an `ou_guid` row by its label's
  DN suffix. Both are the identifier row's own label, not an unconverted name; changing them was not in K.3.
- **Page help points at README, Troubleshooting** rather than `docs/ConfigDbOperations.md`: the literal path is what the
  allowlist-corrupt banner test keys on, and the README entry points onward.

## Guard proof

Each revert applied, the solution built, the named tests run and seen to fail, then restored.

| Revert | Failing tests |
|---|---|
| Write despite offending rows | `OneEntryThatDoesNotResolve_WritesNothing_...`, `ADirectoryThatCannotBeOpened_WritesNothing_...` |
| `--plan` writes | `Plan_ReportsEachConversion_AndWritesNothing` |
| No re-read under the write lock | `ARowChangedWhileTheLookupsRan_WritesNothing_AndSaysToRunAgain` |
| Status exit 0 with legacy rows | `Status_ListsEveryRowAndGrantInItsState_AndExitsTwoWhileAnyIsStoredByName` |
| Grants not replaced | `ARealRun_AddsEachIdentifierBesideItsLegacyRow_...`, `Status_AfterAConversion_IsExitZero_OnStdout` |
| Directory opened before the nothing-to-do check | `NothingToConvert_NeverOpensTheDirectory` |
| Two `--plan` flags accepted | `AnyOtherArgumentShape_IsUsage("convert-identifiers a.db --plan --plan")` |
| Invoking-user directory keys on sAMAccountName only | `TheVerbsDirectory_ReadsAsTheInvokingUser_AtTheHostForestsCatalog_GroupsOnly` |
| `DOMAIN\Name` searched forest-wide | `ADomainQualifiedGroupEntry_ResolvesInTheNamedDomain_NotAnother`, `AGroupEntryForADomainTheForestLacks_...` |
| No common-name fallback | `AGroupName_FallsBackToTheCommonName_OnlyWhenNoSamAccountNameMatches` |
| `NotConvertedProblems` returns none | `ARowStoredByNameOnly_RefusesTheCheck_NamingIt_...` (4), `ALegacyUserRowWithNoCompanion_IsNeverMatchedByName_...`, `TheWriteTargetCheck_RefusesOnAUserRowStoredByNameOnly_...` |
| Write-target refusal removed | `TheWriteTargetCheck_RefusesOnAUserRowStoredByNameOnly_InsteadOfMatchingItsText` |
| Legacy SID group rows not evaluated | `ALegacyGroupRowStoredAsASid_IsComparedByThatSid` |
| Refusal ignores another rule's match | `ARowStoredByNameOnly_DoesNotCancelAMatchOnAnIdentifierRow` |
| Evaluator looks a name up | `ANameEntry_IsNeverLookedUp_ItIsIncomplete_NotConverted` (3) |
| A SID naming no group skipped (item 17's Stale) | `ASidEntryNamingNoGroup_IsIncomplete_AndNamed` |
| Admins pane without the fixed text | `TheAdminsPane_ListsEachGrantStoredByName_WithTheFixedText_AndARemoveBesideIt` |
| Old "not in AD" tip / old count warning | `TheNotInAdTip_...` (3), `TheProtectedPrincipalsPane_CountsEntriesStoredByName_...` |

Not separately proven: the re-homed resolver tests (`EachKind_ConvertsOnExactlyOneMatch`, `ZeroManyAndUnavailable_...`,
the two module-admin grant tests, `ABareGroupNameTwoDomainsShare_...`, `AGroupDnThatNoLongerExists_IsNotFound`) - moved
with their code from tests that bit in S2/S5; `AGroupSidRowWhoseLookupFails_...` and `RemoveBesideAGrantStoredByName_...`,
moved the same way; `AMissingDatabase_IsExitTwo_AndNothingIsCreated` (its guard is redundant with the read-only open, so no
single revert makes it fail).

## Tests changed because they pinned what K removes

Fixtures that stored a protected user, group or OU by name only now store the identifier form db convert-identifiers
writes (a `group_sid` / legacy SID, or a `user_sid` / `ou_guid` row with its label): `ProtectedPrincipalServiceTests`
(6, via `ConvertUsers`; `Check_OuMatch_IsProtected` became `Check_OuLegacyRow_IsNeverMatchedByItsDn_...`; the
no-credential test's group became a SID), `ProtectedPrincipalExchangeFallbackTests` (3), `PermissionValidatorTests`
(fixture), `EmergencyDisableServiceTests` (fixture), `ProtectedGroupWriteTargetTests` (2), `ProtectionCheckTimingTests`,
`DirectoryReadCredentialOnceTests`, `WarmDirectorySessionTests`, `PermissionPagesSelfGrantTests`,
`ProtectedPrincipalSidBindingTests`, `ConferenceRoomBinderTests`, `ProtectedPrincipalIdentifierTests` (2 rewritten).
`ProtectionCheckTimingTests` counts rose by one: a group stored by SID that the token does not carry is read at the
catalog, whose discovery the check's own directory makes (S1's path, which these fixtures did not reach before).

## Gates (worktree, 2026-11-03)

See the supervisor report for the summary lines at the tip. Not run: any live database, directory or Exchange; the verbs
against a real Active Directory (the invoking-user directory is proven over the stand-in forest only).

## Left for later

- Dropping the legacy companion rows once prod runs the new build: the same verb with a drop switch (K.1).
