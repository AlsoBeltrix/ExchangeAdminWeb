# ppi-s3: a synced Exchange write target is bound by its on-premises SID; the mailbox-GUID proof is retired (slice S3)

**Severity**: HIGH - `ResolveExchangeTargetAsync` found a synced mailbox's Active Directory object by its canonical
address (`mail` / UPN / sAMAccountName, local domain only) and then proved it by a search for `msExchMailboxGuid` (aud-a2c).
An address is not an identifier: the proof refused every mailbox whose account is in another domain or whose GUID was never
stamped on-premises, and the object it checked was still found by a string. Owner ruling 2026-11-03 (`.agents/decisions.md`,
"The protection check reads the on-premises SID from Graph ..."), plan `docs/ProtectedPrincipalIdentifiers-Plan.md`
(Approved 2026-11-03), sections C.2, E, H, I.1, slice S3.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/ppi-s3` (worktree `D:\source\ExchangeAdminWeb-wt\ppi-s3`, from master `28f6ba67`)
**Commit**: see `git log master..fix/ppi-s3` (binder commit, Admin Settings field commit) | **Reviewer**: implementer (candidate)

## Evidence

- Plan C.1 (at `c94b443e`): `ProtectedPrincipalService.ResolveExchangeTargetAsync` -> `ProveMailboxBackingAsync` ->
  `FindMailboxGuidHoldersAsync` (`msExchMailboxGuid` search, default domain) -> `SelectMailboxBacking`.
- Plan I.1 Outcome: Exchange Online `Get-User` `Sid` is cloud-generated; Graph `onPremisesSecurityIdentifier` is the source.

## Approach (as built)

- **SID source.** `IOnPremisesSidSource` / `GraphOnPremisesSidSource` (singleton, `Program.cs`): `GET
  /users/{ExternalDirectoryObjectId}?$select=onPremisesSecurityIdentifier,onPremisesSyncEnabled` through
  `GraphTokenClient.GetWithStatusAsync`, the secret from the new app-wide setting `ProtectedPrincipals` /
  `GraphDirectoryReadSecretId` only (nothing read from Cloud Password Reset's setting; no fallback). Outcomes: Reported
  (canonical SID), NoneReported (a 200 with the attribute absent, null or empty), NotAnswered (no setting - logged naming
  the field -, unreadable setting, secret unreadable or missing a field, token failure, any non-200, non-SID value, no
  usable object id). The Graph client and its token are kept per Secret ID; a changed ID rebuilds, a 401/403 or token
  failure drops it. Nothing from Secret Server is logged. The first Reported read per process logs at Information with the
  SID's domain part only.
- **Binder (C.2).** Steps 1-2 unchanged. Step 3: the source; NotAnswered -> Unavailable (step 6). Step 4: one
  `Get-ADObject -Server <gc:3268> -LDAPFilter (objectSid=...)` (`ADDirectorySearchService.BuildSidFilter`) in a lent warm
  session, the catalog discovered from the host's forest (`PowerShellProtectedGroupDirectory.GlobalCatalog`) and kept by the
  service until a read fails; `SelectSidObject` (pure): zero -> Unavailable, two or more -> Ambiguous, one not `user` /
  `inetOrgPerson` -> Ambiguous, one without DN/objectGUID or under another SID -> Unavailable; else the principal with
  objectSid, objectGUID, DN, the recipient's canonical address, Entra object id and ExchangeGuid. Then the unchanged aud-a2b
  typed-string agreement (objectGUID). Step 5: NoneReported -> the canonical-address path with its one-match rule, logged
  "no on-premises SID reported for {Identity}; bound by address". All inside the existing operation: one directory-read
  credential fetch per check.
- **AD-first entry point.** `ResolveWithExchangeFallbackAsync`'s alias branch binds by SID when the source reports one; not
  re-ordered; ExchangeGuid is not carried on that path (aud-a2d keeps it `ResolveExchangeTargetAsync`'s alone).
- **Retired.** `ProveMailboxBackingAsync`, `SelectMailboxBacking`, `FindMailboxGuidHoldersAsync`,
  `FindMailboxGuidHoldersViaActiveDirectory`, `ParseMailboxGuid`, `BuildMailboxGuidFilter`, `MailboxGuidHolder`, their tests
  and refusal texts; a source-scan tripwire keeps them out. `ValidatedMailbox` kept; its doc and `ResolvedRecipient` /
  `ResolvedDirectoryPrincipal` docs rewritten; `ResolvedRecipient.OnPremisesSid` added (never filled by `MapRecipient`).
- **Timing line.** Unchanged when no SID-source read happened; otherwise the same line gains ", {SidReads} SID source reads
  from {SidSource} in {SidMs} ms" inside its parentheses (`DirectoryReadOperation.CountSidSourceRead`). On the stand-in a
  synced target's first check reads 6 directory reads (typed string, catalog discovery, read by SID, target, token, group),
  1 fetch, 1 Graph read; later checks 5 / 1 / 1.
- **Admin Settings.** "Graph Directory Read Credential (Delinea Secret ID)" beside the Directory Read Credential: hand-bound
  input, one dirty change, Secret-ID validation, unreadable/corrupt and concurrent-change refusals compared before the rules
  are written (aud-a28r), saved after them under its own audit section `ProtectedPrincipals:GraphDirectoryReadSecretId`;
  help text (what it is for, `User.Read.All`, what empty does), pane note and README pointer.
- **Versions.** Base 2.36.0 unchanged. Mailbox Permissions 1.6.9, Calendar Permissions 1.4.9, Out of Office 1.1.4 (its
  `ValidateTargetMailboxAsync` reaches the binder), Conference Rooms 2.19.12 (its gate reaches the binder until S4),
  Admin Settings 1.2.5. Blocked Senders not bumped: its gate calls only `ResolveWithExchangeFallbackAsync`.

## Where the code goes past or beside the plan's text

- Source 3 (on-premises Exchange `Get-User` Sid) left out: the protection service holds no on-premises Exchange session -
  `ExchangeServiceBase`'s `:1004` connection belongs to the module services - so it would mean a new credential and session
  for a second opinion no caller asks for.
- The alias branch of `ResolveWithExchangeFallbackAsync` keeps today's address re-resolve when the source reports no SID
  OR cannot be asked (C.2 says "when a SID is available"): failing it closed there would refuse every alias-addressed
  synced target of its 13 callers while the field is empty. Nothing that was refused before is now allowed.
- The plan names the catalog read "1"; its discovery (`Get-ADForest`) is a second read on a service's first check only.
- The bound principal's `PrimarySmtpAddress` is Exchange's canonical address and `EntraObjectId` the recipient's id, so a
  legacy user row by address still matches the mailbox whatever its account's `mail` holds (adds matches only).
- `ConferenceRoomProtectionGate` untouched; `ConferenceRoomAmbiguityMessageTests.AUserAndAGroup_...` given a source that
  reports no SID so its collision still reaches the address path. `ConferenceRoomService.cs:1725`'s comment still names
  aud-a2c - left for S4. The stand-in forest's `OtherDomainSid` has sub-authorities above 2^32, which `SecurityIdentifier`
  does not round-trip; the cross-domain test uses a domain SID of its own.
- Not confirmed: whether the owner read `onPremisesSecurityIdentifier` for one user before S3 (I.1 asks); the first-read
  Information line is there for the dev check.

## Guard proof

Each reverted, the named test failed, restored, the suite passed:

| Revert | Failing test |
|---|---|
| M1 the SID branch binds by address | `ExchangeTarget_AnotherAccountCarryingTheAddress_IsNeverTheObjectChecked_TheSidsAccountIs` |
| M2 zero objects not refused | `ExchangeTarget_NoObjectHoldsTheSid_IsUnavailable` |
| M3 two objects take the first | `ExchangeTarget_TwoObjectsHoldTheSid_IsAmbiguous` |
| M4 any object class accepted | `ExchangeTarget_TheOneObjectHoldingTheSidIsNotAUser_IsAmbiguous` (computer, group, contact) |
| M5 an unanswered source falls to the address | `ExchangeTarget_TheSidSourceUnanswered_IsUnavailable_NotTheAddressFallback` |
| M6 "none reported" refused | `ExchangeTarget_GraphAnsweredWithNoSid_BindsByAddress_WithItsOneMatchRule_AndSaysSo` |
| M7 typed-string agreement removed | `ExchangeTarget_AnAdAccountSharingTheString_...`, `ExchangeTarget_TypedAsTheAddressAnotherAccountCarries_IsAmbiguous` |
| M8 the read by SID fetches its own credential | 9 tests, incl. `DirectoryReadCredentialOnceTests.*`, `ProtectedPrincipalSidBindingTests.ATargetInAnotherDomain_*` |
| M9 Graph read not counted | `ProtectionCheckTimingTests.ATargetValidation_...`, `ACheckBegunAroundAResolution_...` |
| M10 alias branch skips the SID | `Fallback_AnAliasOfASyncedMailbox_IsBoundBySid_WhenGraphReportsOne` |
| M11 read by SID sent to the local domain | `ATargetInAnotherDomain_IsBoundAtTheCatalog_...`, `ATargetInAnotherDomain_IsCheckedInItsOwnDomain_...` |
| M12 a failed Graph read reads as "none" | `AFailedThrottledOrUnreadableAnswer_IsNotAnswered_NeverNoneReported` (4 cases) |
| M13 Graph client not kept | `TheClientAndItsToken_AreKept_ForTheSecretId_AndAChangedIdBuildsANewOne` |
| M14 first-read line logs the whole SID | `AReportedSid_IsReturnedCanonical_..._AndTheFirstIsLoggedByItsDomainOnly` |
| M15 field not counted dirty | `TheField_RoundTripsThroughTheStore_CountsAsOneChange_...` |
| M16 field not saved | `TheField_RoundTripsThroughTheStore_CountsAsOneChange_...` |
| a `MailboxGuidHolder` record added back | `RetiredMailboxGuidProofTripwireTests.TheMailboxGuidProof_IsGone_FromTheAppAndItsTests` |

## Gates (worktree, 2026-11-03)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: succeeded, 0 errors, 19 warnings (none in a changed file).
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 8395 passed, 0 failed, 0 skipped.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1`: passed.
- Not run: any live Graph, Exchange or directory read; the page was not rendered in a browser; the section E timing on dev.

## Left for later slices

Conference Rooms gate removal and its records (S4); module-admin grants (S5); records (S6). Dev: set the Graph field, check
the first-read line and the timing line (section E).
