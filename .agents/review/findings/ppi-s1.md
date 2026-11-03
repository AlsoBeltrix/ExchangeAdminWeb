# ppi-s1: protected users, groups and OUs stored and compared by immutable identifier (slice S1)

**Severity**: HIGH - protected users are compared by string (`ProtectedPrincipalService.CheckDirectUserMatches`), so
another account carrying a protected user's address in `mail` or UPN is protected in her place and a protected user whose
address changes is not; protected OUs by DN suffix, so a renamed OU stops protecting; protected groups by a DN re-resolved on
every check. Entry validation accepted an ambiguous User, Group or OU and stored whichever object AD returned first, and
looked users and OUs up in the local domain only. Owner ruling 2026-11-03 (`.agents/decisions.md`), plan
`docs/ProtectedPrincipalIdentifiers-Plan.md` (Approved 2026-11-03), slice S1.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/ppi-s1` (worktree `D:\source\ExchangeAdminWeb-wt\ppi-s1`, from master `f4eaa649`)
**Commit**: `d22e992b`, `dd18c667`, `b3813a0f`, `45049f6e` | **Reviewer**: implementer (candidate)

## Evidence

- Plan A.1 (file:line at `c94b443e`): `ProtectedPrincipalEntryValidator.cs:95` refused ambiguity for GroupTarget only;
  `CanonicalValue` stored UPN/mail or DN; `ADDirectorySearchService.cs:483-497` validated users and OUs in the local
  domain; `ProtectedPrincipalService.cs:1471-1538` compared users by string and OUs by DN suffix.

## Approach (as built)

- **Storage.** Kinds `user_sid`, `group_sid`, `ou_guid`, value `identifier|label` (`ProtectedPrincipalIdEntry`, split on
  the first `|`); one `InsertAll` writes every kind into its own kind; `ProtectedPrincipalData` carries `UserIds`,
  `GroupIds`, `OuIds` as init properties. The older build's reader ignores the new kinds and enforces the companion.
- **Validator.** Ambiguity refused for every kind; identifier required (objectSid for User/Group, objectGUID for OU and
  GroupTarget); well-known SIDs refused as Groups; an accepted entry returns the identifier row and its companion. Users
  validated through the global catalog (no catalog: Unavailable); DN-shaped OUs routed to their domain when the forest's
  crossRefs know it, NotFound otherwise.
- **Comparison.** Per base kind, legacy rows no identifier row pairs with are evaluated exactly as before; companions are
  not; an identifier that does not parse is evaluated by its label. `user_sid` by SID against
  `ResolvedDirectoryPrincipal.ObjectSid` (now filled by `ResolveViaActiveDirectory`); `group_sid` as a SID entry of
  `ProtectedGroupMembership.Evaluate`, which now matches a SID the target's token carries before any resolve read;
  `ou_guid` by one read of the target's containers by DN in its own domain (`ProtectedOuMembership`), incomplete reads
  failing closed as the group check does.
- **Page.** A new entry is stored as identifier row plus companion; Remove deletes both; the not-in-AD sweep checks only
  rows enforced by name. Help and README updated (Known Failure Class 5).

## Where the code goes past or beside the plan's text

- `user_sid` against a target that carries no SID (a resolution that did not read it - every `ResolvedDirectoryPrincipal`
  built outside `ResolveViaActiveDirectory`, until S3) compares by the row's label, as its companion did. Strict SID-only
  comparison would drop protection on those paths (Known Failure Class 3). The plan says "compare `target.ObjectSid`
  exactly" and is silent on a null SID.
- `CheckWriteTarget` is synchronous and directory-free by construction, so it compares `ou_guid` rows by their label (the
  DN suffix the companion got) and `user_sid` rows by the same null-SID fallback. The plan's C.2 names `CheckAsync` only.
- The token short-circuit applies to every SID-shaped group entry, legacy SID rows included. It only adds matches (a SID
  the token carries was a match whenever its resolve succeeded, and an incomplete check otherwise).
- `ou_guid` reads stop below the domain head (`DC=` components are not read); a stored OU is always an OU.

## Guard proof

Each reverted, the named test failed, restored, the suite passed (details in the commit bodies):

| Revert | Failing test |
|---|---|
| ambiguity refused for GroupTarget only | `Decide_AnAmbiguousMatch_IsRefused_ForEveryKind` (User, Group, OU) |
| user id row compared by label string | `AUserSidRow_MatchesTheTargetBySid_AndNotByASharedAddress` |
| token short-circuit disabled | `AGroupSidEntryTheTokenCarries_MatchesWithNoResolveRead` |
| OU compared by label DN | `AnOuGuidRow_MatchesAfterTheOuIsRenamed`, `AnOuGuidRow_DoesNotMatchAnotherOuThatTookItsName` |
| `UserIds` written into kind `user` | `Tripwire_AnOlderBuildsReader_IgnoresTheIdentifierKinds_AndNoIdentifierIsEverWrittenIntoALegacyKind` |
| companion not removed | `Remove_DeletesTheIdentifierRowAndItsCompanion` |
| split on the last separator | `Format_Then_Parse_RoundTrips_SplittingOnTheFirstSeparatorOnly` |
| unmigrated rows made inert | `ALegacyUserRowWithNoCompanion_IsStillEvaluatedExactlyAsBefore` |

## Gates (worktree, 2026-11-03)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: succeeded, 0 errors; no warning in a changed file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 8349 passed, 0 failed, 0 skipped.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1`: passed.
- Not run: any live directory check (the GC user lookup, the crossRef routing and the container GUID read are proven on
  stand-ins and fakes only).

## Left for later slices

Migration pass, Resolve now, flagging badge and the AdminSettings version patch (S2); the SID binder and GUID-proof
retirement (S3); Conference Rooms (S4); module-admin grants (S5).
