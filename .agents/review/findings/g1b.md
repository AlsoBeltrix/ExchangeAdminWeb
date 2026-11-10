# g1b: a synced user in another domain with no Exchange recipient is not protected by the cloud-user modules

**Severity**: HIGH - fail-open by absence. For a directory-synced user whose account is outside the domain `Get-ADUser`
defaults to and who has no Exchange recipient, `ResolveWithExchangeFallbackAsync` returns NotFound; MFA Reset, Risky
Users and Intune Devices then checked a stand-in principal with no SID, DN or sAMAccountName, so no user, group, OU or
pattern rule could match, and the write went ahead. The only thing keeping it safe was the shape of one forest and of
who has a mailbox, which Architectural Invariant 7 forbids as a safety argument. Report 2 bug 8 (MFA Reset's stand-in
carried a null Entra object id) is closed here too.
**Status**: Fixed on branch; awaiting merge
**Branch**: `fix/p0-1-cloud-user-protection` (worktree `D:\source\ExchangeAdminWeb-wt\p0-1`, from master 6758e796)
**Commit**: 5725b83b, 4b589d26 (shared gate), da6fdc42 (Risky Users), 2aa05d21 (Intune Devices), 7511b091 and b9d5ac91
(MFA Reset), 9bea9590 (Cloud Password Reset), f0da22cc (help), 49f86af3 (versions) | **Implementer**: Claude (supervised agent)
**Reviewer**: Gemini 4 Argon (High) through agy, two rounds, 2026-11-10; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.1 (with g2, pattern B3)

## Evidence (master 6758e796)

- `Services/ProtectedPrincipalService.cs` `ResolveViaActiveDirectory` runs `Get-ADUser -LDAPFilter` with no `-Server`:
  the default domain only. `ResolveWithExchangeFallbackAsync` returns NotFound when AD misses and Exchange has no
  recipient; the global catalog is read only by the SID bind, which this path never reached.
- `MfaReset.razor`, `RiskyUsers.razor`, `IntuneDevices.razor`: on NotFound each built an unresolved principal (UPN,
  address, MFA Reset with `EntraObjectId: null`) and ran `CheckAsync`, which can match only address or object-id rows.
- `IOnPremisesSidSource` already read Graph's `onPremisesSecurityIdentifier` by Entra object id, and was not consulted.
- Cloud Password Reset was not exposed (it refuses synced accounts first) and moved for consistency.

## Fix (as landed)

- `ProtectedPrincipalService.ResolveCloudUserAsync(entraObjectId, upn)`: Graph is asked for the user's on-premises SID
  through the existing SID source (by object id; by UPN when the write names its user by UPN alone). A SID: the one
  user object holding it, read at the global catalog (`FindObjectsBySidAsync` + `SelectSidObject`), so every rule
  applies whatever domain holds it; the principal carries the account's mail (or the UPN) and the Entra object id. No
  SID and Graph says not synced: resolved exactly as the modules resolved a cloud identity before (AD, then Exchange,
  then a principal of the UPN and object id, "unresolved"). Refused: Graph not asked or failed, a synced user with no
  SID, a sync flag Graph did not answer, no or several holders of the SID; a UPN Graph holds no user for is NotFound.
- `ProtectedPrincipalServicing.ForCloudUserAsync` (beside `ForWriteTargetAsync`): one timed check, the rules, and the
  servicer rule unchanged (note qualified "directory-unresolved" as before); returns a refusal kind the caller words.
- `GraphOnPremisesSidSource`: the answer also carries Graph's sync flag (true; false or null = cloud-only; absent or not
  a boolean = unanswered) and, for a read by UPN, the object id and whether Graph answered 404. One Graph request per
  check, under the protection service's own Graph Directory Read Credential, inside the check's operation.
- Callers: Risky Users (risky user id), Intune Devices (the device's `userId`; by UPN when blank), MFA Reset (by the
  listed UPN - the module's own registration holds only `UserAuthenticationMethod.ReadWrite.All`, assumed from README,
  not verified against the live registration), Cloud Password Reset (the object id its preflight read). Their
  hand-written blocks and `ServicerNoteFor` wrappers are gone. App code: +378 / -397 lines across the slice.
- Bug 8: MFA Reset's check now carries the user's object id from Graph.

## Wording kept, per module (not unified in this slice)

| Refusal | MFA Reset | Risky Users | Intune Devices | Cloud Password Reset |
|---|---|---|---|---|
| Ambiguous | "Identity is ambiguous — matches multiple AD users." | "Identity is ambiguous - it matches multiple directory users." | "The device's primary user is ambiguous - the identity matches multiple directory users." | "The identity is ambiguous - it matches more than one directory user." |
| Unavailable | "Protection check unavailable." | same | "Protection check unavailable for the device's primary user." | "The protection check could not be completed." |
| Audit suffix | " — blocked" | " - blocked" | " - blocked" | message and refusal code |
| Protected | "This user is a protected principal. MFA reset is not permitted." | "... Risk remediation is not permitted." | "The primary user of this device is a protected principal. {Action} is not permitted." | "This account is a protected principal. Resetting its password is not permitted." |
| Exception | "Protection check error:" / audit "Protection check exception:" | same | same | "The protection check could not be completed." |

New in MFA Reset only: a UPN Graph holds no user for is refused with the module's existing read-failure words, "Could not
read MFA methods - nothing was reset. Graph request for {upn} failed: 404 NotFound." All modules: "Protection check
failed: {reason}" unchanged. The audit detail of a protected refusal keeps "Protected principal[ (directory-unresolved)]
- matched rules: ...".

## Tests

New: `CloudUserProtectionTests` (19, the gate end to end over `ExchangeWriteHarness`: the user in the other domain with
no recipient, protected by user row, group and OU; not protected; servicer; cloud-only as before; every fail-closed
case), `CloudUserProtectionCallerTests` (14, handlers and the M365 service), 9 cases in `GraphOnPremisesSidSourceTests`.
Deleted source scans and their replacements:

| Deleted | Replaced by |
|---|---|
| `RiskyUsersPageTests.RiskyUsers_ExecuteAction_ChecksProtectionBeforeTheWrite` | `CloudUserProtectionCallerTests.RiskyUsers_RefusesTheProtectedUserInTheOtherDomain_BeforeTheWrite` |
| `RiskyUsersPageTests...ChecksProtectionOnBothResolutionBranches` | `CloudUserProtectionTests.ACloudOnlyUserNobodyKnows_IsCheckedByItsUpnAndObjectId_SoALegacyExclusionByObjectIdRefuses` |
| `RiskyUsersPageTests...RefusesWhenResolutionOrTheCheckItselfFails` | `CloudUserProtectionCallerTests.RiskyUsers_GraphNotAnswered_RefusesAsUnavailable_BeforeTheWrite` |
| `RiskyUsersPageTests...HonoursTheServicerGrantOnBothBranches` | `CloudUserProtectionTests.AnAuthorisedServicer_ProceedsOnAProtectedUser_WithTheNote`, `...ServicedNote_IsQualifiedDirectoryUnresolved` |
| `IntuneDevicesPageTests...ChecksProtectionBeforeTheWrite` | `CloudUserProtectionCallerTests.IntuneDevices_RefusesTheProtectedPrimaryUserInTheOtherDomain_BeforeTheWrite` |
| `IntuneDevicesPageTests...ProtectionBindsToTheDevicesPrimaryUser` | the same test (the primary user's id and UPN are what is checked) |
| `IntuneDevicesPageTests...ChecksProtectionOnBothResolutionBranches` | `CloudUserProtectionTests.ACloudOnlyUserNobodyKnows_...` (as above) |
| `IntuneDevicesPageTests...RefusesWhenResolutionOrTheCheckItselfFails` | `CloudUserProtectionCallerTests.IntuneDevices_GraphNotAnswered_RefusesAsUnavailable_BeforeTheWrite` |
| `IntuneDevicesPageTests...HonoursTheServicerGrantOnBothBranches` | the two servicer tests above |
| `PageAuthorizationRecheckTests.MfaReset_ResolvesThroughExchange_NotAdOnly` | `CloudUserProtectionCallerTests.MfaReset_ChecksTheUserGraphNamesByItsUpn_AndRefusesTheProtectedUserInTheOtherDomain` |
| `CloudPasswordResetWritePathTests.Protection_uses_the_exchange_fallback_and_passes_the_real_entra_object_id` | `CloudUserProtectionCallerTests.CloudPasswordReset_ChecksTheAccountByTheObjectIdGraphRead` |
| `ProtectionCheckCallSiteTests` rows S6, S12, S13, S14 (and S9, see g2) | one row for `ForCloudUserAsync`; `CloudUserProtectionTests.AnAccountNoRuleNames_Proceeds_WithNoNote` asserts one credential fetch |

Adjusted, not deleted: the two early-exit census counts (Intune 10 to 6, Risky 9 to 5), the Intune servicer opt-in pin
(now `ForCloudUserAsync`), three `ProgressRegistry` entries (two pins become one), page-run stand-ins that override the
new resolve (`RiskyUsersTicketPresenceTests`, `MfaResetNotificationGuardTests`, `CloudPasswordResetPageRun`).

## Guard proof (fix reverted, targeted run, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| The four pages to master (with the M365 service, see g2) | 11 of 14 caller tests | the Risky, Intune, MFA Reset (both) and Cloud Password Reset caller tests, and the six M365 ones in g2 |
| `ResolveCloudUserAsync` to the old pages' resolve (AD, Exchange, stand-in) | 14 of 19 | user row, group, OU, servicer, outsider, synced no SID, sync flag unanswered, Graph not answered, catalog failing, SID nobody holds, two holders, by-UPN, by-UPN cloud-only, UPN not found |
| Sync flag and by-UPN object id in the SID source; the blank-UPN guard | 5 | three `AnAnswerWithNoSid_SaysWhetherGraphReportsTheObjectSynced` cases, `AReadByUpn_AsksForTheUserByItsUpn_AndCarriesItsObjectId`, `ABlankUpn_Refuses_AsUnavailable_AskingNothing` |
| MFA Reset page to master, and the 404 flag | 4 | `MfaReset_ACloudOnlyUserNobodyElseKnows_IsCheckedWithItsObjectId_SoALegacyExclusionByObjectIdRefuses` (bug 8), the other two MFA Reset caller tests, the 404 case of `AReadByUpn_ThatCannotNameTheUser...` |

Not biting by design (behaviour kept, pass both ways): the allow case, the two cloud-only "as before" tests, the
unresolved servicer qualifier. Restored: the full suite passes (gates in the hand-back).

## Behaviour that changes beyond the defect (for the owner)

- The Graph Directory Read Credential is now needed by all five modules for every target, cloud-only users included:
  while it is empty or Graph cannot be read, every such write is refused as unavailable. A deploy prerequisite.
- STOP item, not chosen by the agent: an account whose Graph object is no longer synced (`onPremisesSyncEnabled` false)
  but still reports an on-premises SID that no account in the forest holds is refused (the plan's rule: a bind by SID
  that finds nothing refuses). Today such a user resolves as cloud-only and proceeds. Owner to rule.
- A legacy exclusion listed by Entra object id now also matches a synced user (the bound principal carries the id).
- A synced user whose address another account in the default domain also carries is checked as the account holding
  its SID, not that other account (the object checked is the object written).

## Left alone, and why

- G1a (the same-address branch of `ResolveWithExchangeFallbackAsync`) is slice 0.2. A13 and A33 (unwired members near
  this code) are phase 1. The three scoped exemptions are untouched.
- `IntuneDevices.razor` `ServicedExtra` was already unused before this slice (unwired code, phase 1).
