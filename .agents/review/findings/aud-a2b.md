# aud-a2b: Three remaining ways a protected mailbox can be reached

**Severity**: HIGH - each lets an operation proceed against a mailbox the configuration
protects.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-acf74809ad7ee3f7d`
**Commit**: `<pending>`

## Evidence

Raised by codex-commercial / `gpt-6-astra` / max while reviewing `efa08c5` (`aud-a2`). All
three are **pre-existing**. `aud-a2` fixed which identity is compared; these are about the
comparison reaching the wrong object, or not happening at all. Filed together because they
share one root: **nothing establishes that the object checked and the object written are the
same object.**

The fourth finding of that review was a regression `aud-a2` introduced and is fixed in its
repair commit, not carried here.

### 1. Legacy excluded-users does string-only comparison after a failed lookup

`Services/PermissionValidator.cs:94`, `:425`. Without central configuration, protection falls
back to `MailboxPermissions/ExcludedUsers`. If object-ID resolution fails during
initialisation, the incomplete cache is accepted with `_initFailed = false`. With no IDs
obtained, later target checks degrade to string comparison - so a secondary alias passes, for
up to the 30-minute cache lifetime, including after the directory has recovered. A failed
target-ID lookup also falls through to "not excluded".

**This contradicts `aud-a2`'s own comment that the directory is "always asked"** - true of the
central path, false of this one.

### 2. An AD match does not identify the Exchange write target

`Services/ProtectedPrincipalService.cs:440`, `:563`, `Services/MailboxPermissionService.cs:52`.
A typed name can be the `sAMAccountName` of an unprotected, non-mail-enabled AD account **and**
the Exchange alias of a protected mailbox. AD answers first and returns the unrelated account;
a `Resolved` status stops Exchange being consulted. The check passes. The write then hands the
original string to Exchange, which resolves aliases, and lands on the protected mailbox.

### 3. Unknown sync metadata is treated as permission to skip rules

`Services/ExchangeIdentityResolver.cs:114`, `Services/ProtectedPrincipalService.cs:535`. When
AD misses an alias and Exchange returns missing or unparseable `IsDirSynced`, the mapper
reports cloud-only. The resulting principal has no DN and substitutes primary SMTP for UPN, so
a genuinely synced mailbox can miss protection configured by its distinct UPN or by on-premises
membership. The comments calling this "conservative" are wrong - unknown metadata produces the
permissive result.

## Predicted observable failure

A non-servicer grants themselves access to a protected VIP mailbox by typing a name that AD
resolves to a harmless account and Exchange resolves to the VIP (hole 2). Or, during a
directory wobble, by typing any alias while the excluded-user cache holds no object IDs
(hole 1).

## Approach - not designed

The unifying requirement: **the identity that passes the check must be the identity that is
written**, and it must be established by whichever directory will perform the write. Options
to weigh:

- Resolve against Exchange for Exchange-targeted operations rather than preferring AD, or
  require both to agree before accepting `Resolved`.
- Carry the resolved identity forward into the write instead of re-passing the typed string,
  so the two cannot diverge.
- Fail closed on unknown `IsDirSynced` rather than defaulting to cloud-only.
- For hole 1, treat an incomplete excluded-user cache as unavailable rather than usable.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21).

**Hole 2 - the object checked is the object Exchange will write.** New entry point
`ProtectedPrincipalService.ResolveExchangeTargetAsync` for operations Exchange performs with the
operator's string. It asks Exchange first - the directory that performs the write - and AD
second, and accepts an answer only when the two name the same object:

- Exchange recipient synced: the checked principal is the AD object behind its canonical primary
  address. If AD's own reading of the typed string is a DIFFERENT object (compared by objectGUID),
  the string names two objects and the result is `Ambiguous`. If AD holds no object under the
  canonical address, the directories disagree and the result is `Unavailable`.
- Exchange recipient cloud-only: if AD resolves the typed string to an on-premises object, that
  is two objects again - `Ambiguous`; otherwise the cloud-only principal, as today.
- Exchange has no such recipient: AD's answer stands (an Exchange write cannot land anywhere);
  neither directory - `NotFound`.
- Exchange could not be asked, or AD is ambiguous or unavailable: refused as today.

Used by `PermissionValidator.ValidateTargetMailboxAsync` (Mailbox Permissions, Calendar
Permissions, Out of Office) and by `ConferenceRoomProtectionGate` (Conference Rooms), the gates
whose writes hand the typed identity to Exchange. AD-written and Graph-written modules keep
`ResolveWithExchangeFallbackAsync`: their write targets are bound by DN or UPN, not re-resolved
by Exchange.

**Hole 3 - unknown sync metadata is a failed lookup.** `ExchangeIdentityResolver.MapRecipient`
throws when `IsDirSynced` is absent or unparseable, the way it already throws for a missing
primary address, so every caller reads it as `Unavailable`. The two tests that pinned
"undetermined means cloud-only" are rewritten, not worked around.

**Hole 1 - the legacy excluded-users list.** Initialisation resolves each configured entry
through `IIdentityResolver.ResolveRecipientAsync`, which throws when Exchange could not answer
and returns null only for an affirmative "no such recipient". A throw fails the whole load
closed (the existing `_initFailed` path); a resolved entry contributes its canonical primary
address and object id. The target check resolves the target the same way: a failed lookup
refuses, and a resolved target is excluded when its canonical address or object id is. Group
members' own strings are still added from the expansion, but are no longer each sent to Exchange
- the expansion already returns their primary address. A failed load is retried after one
minute instead of being served for thirty.

**Diagnostics (and one fail-open they hid).** `ResolveWithExchangeFallbackAsync`'s alias branch
returned AD's `NotFound` for a synced recipient whose canonical address AD does not hold. That is
the directories disagreeing, the same as the same-address branch beside it, and it becomes
`Unavailable` - which also closes it for the gates that ALLOW on NotFound (Conference Rooms,
Group Management, Blocked Senders, Migration), where it was a fail-open, not just a wrong
message. The validator's log line naming Group/OU/Pattern rules is corrected.

**Files.** `Services/ProtectedPrincipalService.cs`, `Services/PermissionValidator.cs`,
`Services/ConferenceRoomProtectionGate.cs`, `Services/ExchangeIdentityResolver.cs`,
`Services/IIdentityResolver.cs` (doc), and the tests for each.

**Mutation, at the call sites.** Revert `PermissionValidator` and `ConferenceRoomProtectionGate`
to `ResolveWithExchangeFallbackAsync` (a gate test in each fails); remove the objectGUID
agreement check (the collision test fails); restore `_ => false` for unknown `IsDirSynced`
(the rewritten test fails); restore the swallowed init lookup, and the fall-through on a failed
target lookup (a legacy-list test each fails); restore NotFound from the alias branch (its test
fails).

## Also recorded - operator-facing diagnostics that name the wrong cause

Not security defects; they send support in the wrong direction.

- `Services/PermissionValidator.cs:199` still says Group/OU/Pattern rules are configured when
  an address-only configuration fails to resolve.
- `:206` says neither directory has the recipient, which is false when Exchange found a synced
  recipient and the follow-up canonical-address AD lookup returned `NotFound`
  (`ProtectedPrincipalService.cs:501`). The denial is correct; the explanation is not.

**Correction to the note above, found while fixing it:** the second bullet is not only a
diagnostic. `ResolveWithExchangeFallbackAsync` returned that `NotFound` to EVERY caller, and the
callers read `NotFound` as "no directory object": some allow outright (Conference Rooms,
Migration, Group Management's string pre-gate - its resolved-member gate then checks the member
it writes), others check only the user rows against the typed string (Blocked Senders, MFA
Reset, Intune Devices, Risky Users, Cloud Password Reset). Either way a synced recipient reached
by an alias whose canonical address AD does not hold had its group and OU rules never evaluated.
That is a fail-open, closed in this commit with the rest of the record.

## Guard proof

| Test | What it pins |
| --- | --- |
| `ProtectedPrincipalExchangeFallbackTests.ExchangeTarget_AnAdAccountSharingTheString_DoesNotStandInForTheMailbox` | hole 2 itself: the sAMAccountName/alias collision is `Ambiguous`, and the AD-first resolution beside it still accepts the unrelated account (the contrast is asserted) |
| `ExchangeTarget_AgreeingDirectories_ResolveToTheMailboxUser`, `ExchangeTarget_AnAliasAdDoesNotKnow_ResolvesToTheObjectExchangeWillWrite`, `ExchangeTarget_ACloudOnlyRecipient_ResolvesToItsCanonicalAddress`, `ExchangeTarget_NoSuchRecipient_FallsBackToTheAdObject` | the agreeing cases still resolve, so the refusal cannot be passing by refusing everything |
| `ExchangeTarget_ACloudOnlyRecipientWithAnAdNamesake_IsAmbiguous`, `ExchangeTarget_ASyncedRecipientAdHoldsNoObjectFor_IsUnavailable`, `ExchangeTarget_ExchangeCannotBeAsked_IsUnavailable_EvenWhenAdAnswers`, `ExchangeTarget_AdThatCannotAnswerCleanly_Refuses` (2), `ExchangeTarget_NeitherDirectory_IsNotFound` | every other outcome of the new entry point |
| `IsSameDirectoryObject_ComparesGuidsFirst_ThenDns_AndNeverAssumes` | the agreement rule: objectGUID decides, two objects with no identifiers are not "the same" |
| `PermissionValidatorTests.AnAdMatchExchangeDoesNotAgreeWith_IsNotTheObjectChecked` | the validator's call site: an AD-first "Resolved, unprotected" must not be what is checked |
| `ConferenceRoomProtectionGateTests.AnAdMatchExchangeDoesNotAgreeWith_DoesNotStandInForTheRoom` | the room gate's call site, same shape |
| `ExchangeIdentityResolverTests.MapRecipient_IsDirSyncedAbsent_FailsTheLookup`, `MapRecipient_IsDirSyncedUnparseable_FailsTheLookup` | hole 3; **these two are the tests that used to assert "defaults to cloud-only", rewritten to the opposite** |
| `PermissionValidatorTests.LegacyList_ASecondaryAddressOfAnExcludedMailbox_IsRefused`, `LegacyList_AnUnrelatedMailbox_IsAllowed` | hole 1 itself, and its opposite direction |
| `LegacyList_AnExcludedEntryExchangeCouldNotResolve_FailsClosed`, `LegacyList_ATargetExchangeCouldNotResolve_FailsClosed`, `ValidateTargetMailbox_ExclusionConfigured_ExchangeCannotBeAsked_FailsClosed` | the two swallowed failures, and a list that cannot ask Exchange at all |
| `LegacyList_AFailedLoad_IsRetriedAfterAMinute_NotServedForTheCacheLifetime` | a failed load refuses, keeps refusing inside the retry window, and recovers after it |
| `ProtectedPrincipalExchangeFallbackTests.AliasReResolution_CanonicalAddressUnknownToAd_IsUnavailable_NotNotFound` | the alias branch's `NotFound` is `Unavailable` |

Existing tests changed: `ValidateTargetMailbox_ExclusionConfigured_NoExchangeConfig_KeepsLiteralMatch`
and `ValidateTargetMailbox_InitSucceeds_WhenExoNotConfigured` ran with no resolver and passed
only because the failed lookup was swallowed; they now run with a resolver that affirmatively
knows nobody (renamed to say so), and the no-resolver case has its own fail-closed test. The two
cache-freshness tests get the same resolver. The scripted resolution fakes in
`PermissionValidatorTests`, `ConferenceRoomProtectionGateTests`, `ConferenceRoomServicerTests`
and `ConferenceRoomBulkProcessorTests` now script `ResolveExchangeTargetAsync`, the seam those
gates call; the first two also script the AD-first seam with the WRONG answer, so a reverted
call site gets an answer instead of failing for an unrelated reason.

Mutations, each at the call site, each run this session; every file restored byte-identical,
touched and rebuilt green:

| Mutation | File md5 (before = restored) | Mutated md5 | Named test | Result |
| --- | --- | --- | --- | --- |
| validator calls `ResolveWithExchangeFallbackAsync` again | `PermissionValidator.cs` `e10836972dc6c05330e5351031e6b61e` | `26accfda690f2cce15f0237089cd2866` | `AnAdMatchExchangeDoesNotAgreeWith_IsNotTheObjectChecked` | FAIL |
| room gate calls `ResolveWithExchangeFallbackAsync` again | `ConferenceRoomProtectionGate.cs` `45277d82ac8e6a2fbef8f1e0d9c8d81f` | `c1fa73ef4d68e0f44bad4d47479a5084` | `AnAdMatchExchangeDoesNotAgreeWith_DoesNotStandInForTheRoom` | FAIL |
| drop the objectGUID agreement check | `ProtectedPrincipalService.cs` `99c7b0a034ab13fb59463ac3b48ca49f` | `66390fc6f99a8224ced6818e3e9d4185` | `ExchangeTarget_AnAdAccountSharingTheString_DoesNotStandInForTheMailbox` | FAIL |
| unknown `IsDirSynced` reads `false` again | `ExchangeIdentityResolver.cs` `dd9200a9d7483f9b3829418c51782256` | `70d1570e88ce652d8124f8416da260df` | `MapRecipient_IsDirSyncedAbsent_FailsTheLookup` | FAIL |
| swallow a failed lookup during the load | `PermissionValidator.cs` `e10836972dc6c05330e5351031e6b61e` | `8464826fe34040fd5ffc0d10dcc08377` | `LegacyList_AnExcludedEntryExchangeCouldNotResolve_FailsClosed` | FAIL |
| a failed target lookup falls through to "not excluded" | `PermissionValidator.cs` `e10836972dc6c05330e5351031e6b61e` | `166533dfccb6fccf156d993e0c224f8a` | `LegacyList_ATargetExchangeCouldNotResolve_FailsClosed` | FAIL |
| the alias branch passes `NotFound` through | `ProtectedPrincipalService.cs` `99c7b0a034ab13fb59463ac3b48ca49f` | `1329ac4e5ec064f74132ebf0afbc0482` | `AliasReResolution_CanonicalAddressUnknownToAd_IsUnavailable_NotNotFound` | FAIL |
| a failed load is served for the full 30 minutes | `PermissionValidator.cs` `e10836972dc6c05330e5351031e6b61e` | `d470e1a334f963033ab8df19ebac2412` | `LegacyList_AFailedLoad_IsRetriedAfterAMinute_NotServedForTheCacheLifetime` | FAIL |

## Known gaps

**Source-level and fake-driven, not live.** Probing Active Directory or the live tenant is
forbidden without a direct owner go, so nothing here shows how this tenant's aliases and
`sAMAccountName`s actually overlap, or that Exchange always reports `IsDirSynced`.

**What "the object written" still means.** The write paths are unchanged: they still hand the
operator's string to Exchange, which resolves it again. The check now resolves that string the
same way, in the same directory, moments earlier, and refuses unless AD agrees by objectGUID -
so the two can only diverge if the directory changes between check and write. Binding the
write itself to the checked object's immutable id would close that window too, but means
changing every write call in three pages and their bulk paths (and the page line counts the
ClickGate registry is keyed on); it was not done here.

**Residuals left on purpose, recorded so nobody reads them as covered:**

- A recipient Exchange Online does not have (an unsynced on-premises mailbox) is checked as AD
  reads the string. An on-premises Exchange write resolving that string by alias to a different
  object is not detected. Hybrid sync makes such a mailbox rare; the check cannot see on-prem
  Exchange's resolution.
- Blocked Senders and Migration keep the AD-first resolution: both key on an SMTP address, which
  cannot be a sAMAccountName, so hole 2's collision cannot arise there; an address that is one
  object's UPN and another's mail is already `Ambiguous` in AD.

**Operator-visible changes, all in the refusing direction:**

- Mailbox Permissions, Calendar Permissions, Out of Office and Conference Rooms now ask Exchange
  for every validated target. Exchange unreachable refuses, where an AD-resolved target used to
  proceed without it.
- A synced recipient whose AD object does not carry its Exchange primary address in `mail` or
  `userPrincipalName` refuses (directories disagree) in those four modules.
- With the legacy `MailboxPermissions/ExcludedUsers` list configured, the validator refuses when
  Exchange cannot be asked - at load, or for the target. A failed load is retried after a minute.
  An entry Exchange REJECTS rather than reporting "couldn't be found" - an identity form it does
  not accept, or one matching several recipients - fails the whole load the same way, so every
  Mailbox, Calendar and Out of Office validation refuses until that entry is corrected. The
  entries on any deployment were not inspected (they are runtime data on the hosts); the warning
  logged at load names the failure.
  Each validated target with such a list also costs one more Exchange lookup; the load costs one
  per configured entry instead of three per group member.
- A recipient whose sync state Exchange does not report refuses everywhere the resolver is used.

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `efa08c5`. Findings 3, 4 and 5 of
5, plus the diagnostics note. Raw output: `.agents/review/out/aud-a2.txt` (machine-local,
gitignored).

Finding 2 of that review - Out of Office checking one mailbox and writing another - is **not**
recorded here: it is already planned as `A7` in `docs/RepoAudit-Remediation-Plan.md`, and the
reviewer reached it independently.

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-acf74809ad7ee3f7d`. Independent review of the fix pending.

## Closeout

Fixed on branch, not merged, not pushed. Shared infrastructure (`ProtectedPrincipalService`,
`ExchangeIdentityResolver`, `PermissionValidator`) moves the base app version at merge. Module
behaviour changes: Mailbox Permissions, Calendar Permissions, Out of Office and Conference Rooms
(their target resolution and the legacy list). The alias-branch and `IsDirSynced` fixes reach
every caller of `ResolveWithExchangeFallbackAsync`, in those narrow cases only.
