# ppi-s2: legacy protected users, groups and OUs converted to identifier rows; unconverted entries flagged (slice S2)

**Severity**: HIGH - after S1, every protected user, group and OU entered before identifiers were stored (and every one an
older build's whole-store save strips of its identifier row) is still compared by name: a user by address, an OU by DN
suffix, a group by a DN re-resolved on every check. Nothing converted them and the admin page did not say which entries
were affected; its "not in AD" hover said such an entry "protects nothing", false for a Group (it fails every check
closed) and for a User (it still matches a target carrying that exact value). Owner ruling 2026-11-03
(`.agents/decisions.md`), plan `docs/ProtectedPrincipalIdentifiers-Plan.md` (Approved 2026-11-03), slice S2.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/ppi-s2` (worktree `D:\source\ExchangeAdminWeb-wt\ppi-s2`, from master `853275e4`)
**Commit**: `d6c175a0`, `b41c6f53` | **Reviewer**: implementer (candidate)

## Evidence

- Plan B.2-B.5 (at `853275e4`): `ProtectedPrincipalIdEntry.Unpaired` rows enforced by name with no conversion path;
  `Components/Pages/AdminSettings.razor` `StaleBadge` hover "so it protects nothing"; `ProtectedGroupMembership.cs`
  Resolve's unresolvable entry marks the check incomplete (fail closed).

## Approach (as built)

- **Lookups.** `ProtectedPrincipalIdentifierResolver` (pure): users by UPN/mail or bare sAMAccountName at the global
  catalog, `DOMAIN\name` in its domain, a DN in its domain, always `(objectCategory=person)(objectClass=user)`; groups by
  `ProtectedGroupMembership.Resolve` (made internal), the check's own binding; OUs by a read of the DN for objectGUID.
  Exactly one match converts; zero / several / well-known SID / lookup failed leaves the row with its reason.
- **One operation.** `ProtectedPrincipalService` resolves the pass inside one `DirectoryReadOperation`: one credential
  fetch, one warm session, the shared throttle.
- **Write.** `ProtectedPrincipalRepository.AddIdentifierRows`: one transaction; each row re-checked under the lock
  (aud-a33b); never rewrites a legacy row; rolls back when nothing is written.
- **Pass.** `ProtectedPrincipalIdentifierMigration` singleton: status enum, never throws, one audit event per pass,
  flagged rows in `app_setting` `protected_principal_identifier_flags` (display-only); started from `Program.cs` after
  the section-access pass on the thread pool and not awaited.
- **Page.** "not converted" badge per unpaired row with the last pass's reason and what the entry does per kind; a pane
  warning with the count; **Resolve now** (busy guard, unsaved-changes refusal, auth re-check, status-frame activity,
  the activity's token only); "not in AD" hover corrected per kind; pane note and README updated; AdminSettings 1.2.4.

## Where the code goes past or beside the plan's text

- B.4 names users by "UPN or mail". The pass also resolves the other forms the page accepts and the legacy matcher
  compares: a bare sAMAccountName (forest-wide, exactly-one), `DOMAIN\name` (in that domain), a DN. An object-GUID value
  is left as "not looked up; re-pick". Each conversion only narrows a by-name match to one object, as B.2 requires.
- Unlike `SectionAccessSidMigration`, one unresolvable row does not hold back the rest: each row is its own decision and
  an unconverted row loses nothing (D1), so all-or-nothing resolution would only delay protection.
- B.3 says "at startup ... beside the section-access pass". It is invoked there but runs on the thread pool, not awaited
  (the section-access pass is synchronous): its lookups depend on the secret store and AD. One-shot per start, not a timer.
- Resolve now is refused while the tab has unsaved changes (the pass writes and the page reloads its lists); the plan is
  silent. A pass already running in the instance (startup or another admin) answers `AlreadyRunning`.
- The audit event is `LogModuleAction` action `ProtectedPrincipalIdentifierMigration`, category `AdminSettings`; a
  startup pass with nothing to do files nothing, a Resolve now with nothing to do files one.
- Test support: the stand-in forest now reads `objectCategory`.

## Guard proof

Each reverted, the named test failed, restored, the suite passed (details in the commit bodies):

| Revert | Failing test |
|---|---|
| `DOMAIN\name` looked up at the catalog | `EachKind_ConvertsOnExactlyOneMatch` |
| ambiguity rule disabled | `ZeroManyAndUnavailable_LeaveTheRow_WithTheReason` |
| identifier row written into the legacy kind | `APass_WritesEachIdentifierRowBesideItsLegacyRow_AndRecordsTheRowsItLeft` |
| pairing ignored when choosing rows | `ASecondPass_IsANoOp` |
| under-lock re-check skipped | `ARowChangedBetweenTheReadAndTheWrite_IsNotConverted_AndNothingIsHalfWritten` |
| identifier row written without the legacy label | `AnOlderBuildsWholeStoreSave_ThenAPass_RestoresTheIdentifierRows` |
| unmigrated Group rows made inert | `AnUnmigratedGroupRowThatCannotResolve_StillFailsTheCheckClosed_BeforeAndAfterAPass` |
| credential fetched once per row | `OnePass_FetchesTheDirectoryReadCredentialOnce_ForEveryRow` |
| startup pass run synchronously | `TheStartupPass_DoesNotDelayStartup_WhenTheDirectoryHangs` |
| Group "not in AD" tip says "protects nothing" | `TheBadges_SayWhatAnUnconvertedOrMissingEntryStillDoes` |
| unsaved-changes refusal removed | `ResolveNow_IsRefused_WhileTheTabHasUnsavedChanges_AndRunsNothing` |
| busy guard removed | `ResolveNow_PressedWhileAPassFromThisPageRuns_DoesNotStartAnother` |

## Gates (worktree, 2026-11-03)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: succeeded, 0 errors, 2 warnings (none in a changed file).
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 8363 passed, 0 failed, 0 skipped.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1`: passed.
- Not run: any live directory or startup check (the GC/DOMAIN/DN lookups and the startup pass are proven on the
  stand-in forest and fakes only); the page was not rendered in a browser.

## Left for later slices

The SID binder and GUID-proof retirement (S3); Conference Rooms (S4); module-admin grants (S5); records (S6).
