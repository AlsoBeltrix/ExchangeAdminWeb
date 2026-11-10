# g1a: a synced recipient in another domain, typed by its primary address, is wrongly refused

**Severity**: MEDIUM - a wrong refusal, fail-closed. No protection is bypassed: the target was refused as unavailable,
never let through unchecked. It is a defect because the same recipient typed by an alias resolved, so whether a write
was allowed depended on which of the recipient's addresses the operator typed.
**Status**: Fixed on branch; awaiting merge
**Branch**: `fix/p0-2-primary-address-bind` (worktree `D:\source\ExchangeAdminWeb-wt\p0-2`, from master 4d67b81a)
**Commit**: 9fed5b96 (fix, tests, README), 28c9bc2e (version 2.36.4) | **Implementer**: Claude (supervised agent)
**Reviewer**: Gemini 4 Argon (High) through agy, two rounds, 2026-11-10; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.2

## Evidence (master 4d67b81a)

- `Services/ProtectedPrincipalService.cs` `ResolveViaActiveDirectory` runs `Get-ADUser -LDAPFilter` with no `-Server`:
  the default domain only. An account in another domain of the forest is not found by any of its addresses.
- `ResolveWithExchangeFallbackAsync` then asked Exchange. For a synced recipient it read the on-premises SID from Graph
  and bound the account holding it at the global catalog only inside `if (PrimarySmtpAddress != identity)` - the alias
  case. With the primary address typed it fell to "directories disagree, failing closed": Unavailable.
- `ResolveExchangeTargetAsync` binds through `BindSyncedAsync` whichever address was typed.

## Fix (as landed)

- `ResolveWithExchangeFallbackAsync`'s synced branch is now one call to `BindSyncedAsync`, the bind
  `ResolveExchangeTargetAsync` and `BindSyncedRecipientAsync` already share, given Active Directory's answer for the
  typed string (NotFound - the only answer that reaches Exchange). The alias branch's own copy of the SID read, the SID
  bind and the address re-resolve is deleted. The mailbox GUID is still stripped from the result, as before.
- No new member, no new option. App code: +22 / -74 lines, all in `Services/ProtectedPrincipalService.cs` (the rest of
  the commit is the test file, +273, and README, +3 / -2).
- Comments in the method touched no longer carry finding IDs or history.

## What each outcome is now, against before

| Synced recipient, AD holds nothing under the typed string | Typed by alias: before / now | Typed by primary address: before / now |
|---|---|---|
| Graph reports a SID, one user account holds it | Resolved, that account / same | Unavailable / **Resolved, that account** (the defect) |
| Graph could not be asked (no credential, failed, no source) | Unavailable / same | Unavailable / same |
| Graph reports a SID, the catalog read fails | Unavailable / same | Unavailable / same |
| Graph reports a SID, no account holds it | Unavailable / same | Unavailable / same |
| Graph reports a SID, two accounts hold it, or the holder is not a user object | Ambiguous / same | Unavailable / **Ambiguous** (still refused; the caller's ambiguous wording instead of its unavailable wording) |
| Graph answers with no SID, AD holds the canonical address | Resolved by address / same | cannot occur (the typed string is the canonical address and AD held nothing) |
| Graph answers with no SID, AD holds nothing under the canonical address | Unavailable / same | Unavailable / same |

Cloud-only recipients, recipients Exchange does not know, and every identity Active Directory resolves, is ambiguous
on, or cannot answer for, do not reach the changed branch.

Log lines only: the alias case's "is an alias of a directory-synced recipient whose on-premises SID could not be read"
is now the shared "is a directory-synced recipient whose on-premises SID could not be read", and "Exchange resolved ...
to canonical address ... - re-resolving in AD" is no longer written; "no on-premises SID reported for ...; bound by
address" is unchanged. No operator message, audit field or mail changed.

## Callers of `ResolveWithExchangeFallbackAsync`

Each now resolves a synced recipient typed by its primary address that Active Directory's address lookup does not
hold, where it refused before; nothing else differs. None was edited.

| Caller | Before, for that target | Now |
|---|---|---|
| `GroupManagementService` member gate (`:66`) | "Protection check unavailable. Cannot verify if this member is protected." | the account's rules are checked |
| `MigrationService` target gate (`:705`) | "Protection check unavailable. Cannot verify if this mailbox is protected." | the account's rules are checked |
| `BlockedSenderProtectionGate` (`:160`) | its unavailable refusal | the account's rules are checked |
| `AccountLockoutRemediationService` (`:477`, `:520`, `:718`) | "Target identity could not be resolved ..." row | the account is resolved; the guard's re-read and SID match run on it |
| `EmergencyDisable.razor` (`:361`) | its unavailable refusal | the account's rules are checked |
| `ProtectedPrincipalService.ResolveCloudOnlyUserAsync` (MFA Reset, Risky Users, Intune Devices, M365 groups, Cloud Password Reset, for a user Graph reports not synced) | Unavailable when Exchange called the recipient synced | same in every case traced: this path is reached only when Graph gave no SID or no account holds it, and both still refuse |

## Tests

New: `FallbackResolveSyncedRecipientTests` (18 cases, the real service over `ExchangeWriteHarness`'s stand-in forest
and Graph; the recipient's account is in the other domain): typed by primary address it resolves to the account
holding its SID, and that account is protected by a user row, a group of its own domain and an OU, and not protected
when no rule names it; typed by alias, the same account; Graph not answered, Graph reporting no SID, the catalog read
failing, a SID nobody holds (Unavailable) and two holders (Ambiguous), each by primary address and by alias; a
cloud-only recipient and a user of the default domain, with Graph not asked. No source scans. The existing
`ProtectedPrincipalExchangeFallbackTests` pass unchanged.

## Guard proof (fix reverted, targeted run, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| `Services/ProtectedPrincipalService.cs` to master 4d67b81a | 6 of 18 | `TypedByItsPrimaryAddress_ResolvesToTheAccountHoldingItsSid_InTheOtherDomain`, `TypedByItsPrimaryAddress_AProtectedUserRowOnItsAccount_Protects`, `TypedByItsPrimaryAddress_AProtectedGroupOfTheOtherDomainHoldingItsAccount_Protects`, `TypedByItsPrimaryAddress_AProtectedOuHoldingItsAccount_Protects`, `TypedByItsPrimaryAddress_AnAccountNoRuleNames_IsNotProtected`, `TwoObjectsHoldingTheSid_AreAmbiguous(typed: "far@fabrikam.test")` |

Not biting by design (behaviour kept, pass both ways): the alias cases, the Unavailable cases by either address, the
cloud-only recipient and the default-domain user. Restored: 18 of 18 pass (full gates in the hand-back).

## Behaviour that changes beyond the stated target (for the owner)

- The fix is not limited to another domain. Any synced recipient that Active Directory's address lookup misses under
  its primary address is now bound by SID - for example an account whose `mail` and `userPrincipalName` both differ
  from the Exchange primary address. It was refused as unavailable; it now resolves to the account holding its SID.
  This is the plan's wording ("bound by SID whether it was typed by primary address or alias") and the same reason,
  not a second one.
- Two holders of the SID, typed by primary address: still refused, now as Ambiguous (see the table).
- Once resolved and not protected, the write goes ahead in the caller as it does for any resolved target; whether each
  module's own write then finds an account in another domain was not examined here and is not part of this slice.

## Left alone, and why

- `ResolveCloudUserAsync` / `ForCloudUserAsync`, the three scoped exemptions and every module page: untouched.
- `BindSyncedAsync`'s body and the other methods' comments keep their finding IDs: only the method changed (and the one
  summary line of `BindSyncedAsync` that lists who shares it) was edited.
- `ProtectedPrincipalExchangeFallbackTests`' class comment still describes the alias re-resolve by address as the
  mechanism; it was already out of date before this slice and no test in it changed.
- No module version: no module file changed. Base app 2.36.3 to 2.36.4.
