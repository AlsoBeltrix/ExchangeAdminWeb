# aud-a60: Self-grant check - a Microsoft 365 group is not classified as a group, and the ambient-identity membership reads can fail open under security-trimmed directory visibility

**Severity**: HIGH (both parts) - fail-open authorization (Known Failure Class 3). (1) `Get-Recipient`
reports a Microsoft 365 group with `RecipientType=UserMailbox` and `RecipientTypeDetails=GroupMailbox`;
`PermissionValidator.IsGroupRecipient` checks only `RecipientType`, so the validator never enters the
cloud-group comparison and a grant to a cloud-only M365 group proceeds without reading its membership even
when the operator is a member. The aud-a52 tests mask it by giving the M365 stub a `RecipientType`
containing "Group". (2) The aud-a52 membership reads run under the app pool / session identity with no
credential; where that identity can discover the catalog and resolve the grantee group but cannot read some
`member` or universal-group links (ACL trimming), AD returns no rows rather than an error,
`ProtectedGroupMembership.Evaluate` answers no match with `Incomplete=false`, and the grant is allowed
though the data was unavailable. The safety then rests on the deployment's read visibility, which
Architectural Invariant 7 forbids.
**Status**: Fixed on branch, not merged, not pushed; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: plan `fa1ed15c`; fix `e06c1c2d`; comments `1c982906`
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-6, 2026-10-22, candidates 1 and 2)

## Evidence

At `ea5e1a72`: `Services/PermissionValidator.cs` around 977 (`IsGroupRecipient`) and the cloud-group path
(`CompareInExchangeGroupAsync`); `Services/ExchangeGroupMembers.cs` (`Get-Recipient` and the
`RecipientTypeDetails` it already reads); `Services/PowerShellProtectedGroupDirectory.cs` around 230
(`UnderSessionIdentity`, no credential) and its credentialed constructor used by `ProtectedPrincipalService`;
`ExchangeAdminWeb.Tests/ExchangeGroupMembersTests.cs` and `PermissionValidatorSelfGrantTests.cs` (the M365
stub's `RecipientType`).

## Approach (to be written by the implementing agent before code)

- `ResolvedRecipient` carries `RecipientTypeDetails`; a recipient is a group when either the type or the
  details name one (`GroupMailbox`, `MailUniversalDistributionGroup`, `MailUniversalSecurityGroup`,
  `DynamicDistributionGroup`, `RoomList`, and the rest Exchange documents); the test stub uses the real
  shape (`RecipientType=UserMailbox`, `RecipientTypeDetails=GroupMailbox`).
- The membership reads for the self-grant check run under the same explicitly scoped directory-read
  credential the protected-principal check uses (the credentialed `PowerShellProtectedGroupDirectory`,
  through the module credential service - no new secret shape); when that credential is not configured or
  cannot be read, the self-grant group check REFUSES as incomplete (fail closed), naming the setting. The
  session-identity factory is removed. If a verifiable visibility check is possible instead (prove the
  `member` attribute readable on the group before trusting an empty answer), record why it was or was not
  chosen; the credential is the default.
- Tests: the real M365 shape refused when the operator is a member; a membership read that returns no rows
  for a group the directory holds is incomplete, not "not a member"; no credential configured refuses.
  Mutations at the call site: `RecipientType` only again; the session-identity reads again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21, "EVERYTHING"). Branch `worktree-agent-a83ff1596fd6eee17` from master
`a7300dee`: one fix commit `fix(permission-validator): ... (aud-a60)`, first of four (aud-a61, aud-a53 and aud-a59 follow, in
that order - all touch the validator or the two pages), then one comments-only commit after every mutation round. Behaviour:
the shared `PermissionValidator`, `ExchangeIdentityResolver` and its records, `PowerShellProtectedGroupDirectory` and
`SelfGrantDirectory` (base app version) and what a Mailbox Permissions or Calendar Permissions grant refuses (both module
versions); the bumps are the supervisor's.

Evidence re-read at `a7300dee`:
- `PermissionValidator.IsGroupRecipient` 977-978 reads `RecipientType` alone, and `CompareInDirectoryAsync` 790 enters the
  group branch only through it, so a grantee Exchange reports as `RecipientType=UserMailbox`, `RecipientTypeDetails=GroupMailbox`
  never reaches `CompareInExchangeGroupAsync` and is compared as a person. `ExchangeIdentityResolver.MapRecipient` 107-112 does
  not carry `RecipientTypeDetails` at all. `ExchangeGroupMember.IsGroup` (`IIdentityResolver.cs` 45) is the same test for a
  member, so a Microsoft 365 group listed inside a cloud group is neither read (`ExchangeGroupMembers` 78) nor kept out of the
  people compared with the operator (`PermissionValidator` 885) - the same defect one level down. The aud-a52 stubs mask both:
  `ExchangeGroupMembersTests.Group` and `Nested` give a Microsoft 365 group `RecipientType=MailUniversalDistributionGroup`.
- `SelfGrantDirectory` 63 builds `Groups` with `PowerShellProtectedGroupDirectory.UnderSessionIdentity` (46-54), whose `Run`
  passes no `-Credential` (230-232); `CompareInDirectoryAsync` 816 evaluates over it. An identity that finds the group but
  cannot read some `member` or universal links gets no rows, not an error, and `ProtectedGroupMembership.Evaluate` answers
  "no match", complete. The protected-principal check reads the same evaluator under its directory-read credential
  (`ProtectedPrincipalService.CheckGroupMembershipAsync` 1443-1497: `GetDirectoryReadSecretId`,
  `DelineaService.GetCredentialsBySecretIdAsync`, `new PowerShellProtectedGroupDirectory(ps, credential)` at 1557) and
  refuses when it is not configured or cannot be had.
- Decisions 2026-07-31 put the Protected Principals page's input validation under the app pool's identity on least
  privilege ("use it only when necessary"), explicitly conditioned on this deployment's read visibility and explicitly not
  to be inherited. That ruling covers a resolver - an existence check of what an admin typed - not an authorization check
  whose empty answer clears a grant. Architectural Invariant 7 (2026-09-11) forbids resting this check's safety on the
  deployment's visibility, which is what the session-identity reads do: here the credential is necessary.

1. **A Microsoft 365 group is a group.** `ResolvedRecipient` gains `RecipientTypeDetails` (a last optional positional
   parameter, so every construction keeps compiling) and `IsGroup`; `MapRecipient` reads it from the `Get-Recipient` row.
   One classifier for a recipient and a member, `ExchangeRecipientKinds.NamesAGroup(type, details)` in `IIdentityResolver.cs`:
   a group when either field names one - carries "Group" (`GroupMailbox`, `MailUniversalDistributionGroup`,
   `MailUniversalSecurityGroup`, `MailNonUniversalGroup`, `DynamicDistributionGroup` and the other group kinds Exchange
   documents) or is `RoomList`. It reads broadly on purpose: a person read as a group refuses as incomplete (its membership
   cannot be read), a group read as a person is allowed. `PermissionValidator.IsGroupRecipient` and `ExchangeGroupMember.IsGroup`
   both use it. The stubs take the real shape: `ExchangeGroupMembersTests` gives a Microsoft 365 group `RecipientType=UserMailbox`,
   `RecipientTypeDetails=GroupMailbox`, and the validator tests give a Microsoft 365 grantee the same.
2. **The membership reads run under the directory-read credential.** The evaluator's reads for the self-grant check (the
   operator and their primary group, ancestry in their domain and at the catalog, each group in its own domain) run through
   the credentialed `PowerShellProtectedGroupDirectory(ps, credential)` - its constructor and reads unchanged - in the check's
   session (`SelfGrantDirectory.GroupsUnder(credential)`). The credential is the protected-principal directory-read
   credential, had as the protected-principal check has it: the Secret ID `ProtectedPrincipalService.GetDirectoryReadSecretId()`
   reads (Admin Settings, Protected principals, "Directory Read Credential"), fetched by `DelineaService.GetCredentialsBySecretIdAsync`
   (resolved through the validator's scope factory; test seam `DirectoryReadSecretFetch`). No new secret shape and no new
   setting. Not `ModuleCredentialService`: it reads a module's `DelineaSecretId`, which for these two modules is the on-premises
   Exchange write credential - a write-capable secret where a read-only one is named for this purpose (the Constitution's
   "shared infrastructure credentials ... explicitly named and scoped") - and the Protected Principals rows hold no
   `DelineaSecretId`; I read the record's "through the module credential service" as the shared credential path, not that
   class. The credential is had only when there is a group to evaluate - a grant to a person needs none - and the check
   refuses as incomplete when it cannot be had, naming it: its setting unreadable (`TryGetDirectoryReadSecret` false; never the
   appsettings fallback in its place), not configured, or the secret not retrieved (null, or a throw): "whether you are a
   member of the group '<x>' could not be read: the protected-principal directory-read credential is not configured (Admin
   Settings, Protected principals)" and its two siblings. `PowerShellProtectedGroupDirectory.UnderSessionIdentity`, its
   constructor and flag, and `SelfGrantDirectory.Groups` are removed: every read of that class carries its credential. The
   operator and grantee lookups (`FindBySid`, `FindByUserPrincipalName`, `FindNamedBy`) stay under the session identity, as
   aud-a43 placed them: they are resolver reads, outside this finding (named in the hand-back).
   Why not a visibility check instead: proving `member` readable on the granted group says nothing of the groups nested in
   it - the in-chain rule walks the links the reader can see and silently drops the rest, and the groups whose links are hidden
   are exactly the ones the check does not know of - nor of the catalog's universal links; a proof per hop is the whole
   membership read again, under the identity whose visibility is the open question. The credential is the deployment's named
   directory-read identity, the one the protected-principal check already rests on.

Tests. `SelfGrantDirectoryStandIn`: the stubs take `-Credential` and record who each call ran as (`Identities`, beside
`Calls`); a group can hide its member links from a read with no credential (`MembersHiddenFromSession`), as a link the
reader cannot see reads as no row. `ExchangeWriteHarness.NothingProtectedValidator`: a stored directory-read Secret ID and a
fetch seam answering it with a stand-in account (`DirectoryReadAccount`), counting fetches; the Protected Principals rows
optionally over a store that cannot be read. `PermissionValidatorSelfGrantTests`:
- a Microsoft 365 group in its real shape, cloud-only: the operator a member - refused as a self-grant, Exchange asked for its
  members; not a member - allowed, after the read;
- a group whose links the session identity cannot see, holding the operator directly and through a nested group: refused as a
  self-grant, and every membership read ran as the directory-read account, none without a credential;
- the credential not to be had - not configured, its setting unreadable, the secret not retrieved, the fetch throwing - each
  refused as incomplete naming it, no membership read made; a grant to a person with no credential configured still allowed;
- the aud-a43 and aud-a52 group cases carried over, now read under the credential.
`ExchangeGroupMembersTests`: the stubs in the real shape; a Microsoft 365 group nested in a cloud group read as a group; the
classifier over each kind. `ExchangeIdentityResolverTests`: `MapRecipient` carries `RecipientTypeDetails`, and a
`UserMailbox`/`GroupMailbox` row is a group. `ProtectedGroupMembershipTests`: the reads carry their credential and no
session-identity form remains (in place of aud-a52's two-form test).

Mutations at the call site (md5, mutate, rebuild, filtered tests, failing names, restore from git, touch, rebuild, md5
equal, green): M1 `IsGroupRecipient` on `RecipientType` alone again; M2 the session-identity reads again -
`PowerShellProtectedGroupDirectory.cs` and `SelfGrantDirectory.cs` as at `a7300dee` and the evaluation over `directory.Groups`
with no credential read. Extras: E1 `ExchangeGroupMember.IsGroup` on `RecipientType` alone; E2 `MapRecipient` dropping
`RecipientTypeDetails`; E3 a credential that cannot be had taken as "not a member"; E4 the credentialed directory handed no
credential.

Files: `Services/IIdentityResolver.cs`, `Services/ExchangeIdentityResolver.cs`, `Services/PermissionValidator.cs`,
`Services/PowerShellProtectedGroupDirectory.cs`, `Services/SelfGrantDirectory.cs`, `ExchangeAdminWeb.Tests/SelfGrantDirectoryStandIn.cs`,
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, `ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs`,
`ExchangeAdminWeb.Tests/ExchangeGroupMembersTests.cs`, `ExchangeAdminWeb.Tests/ExchangeIdentityResolverTests.cs`,
`ExchangeAdminWeb.Tests/ProtectedGroupMembershipTests.cs`, this record.

Labelled assumptions (not probed; no test makes a live call): `Get-Recipient` reports a Microsoft 365 group as
`RecipientType=UserMailbox`, `RecipientTypeDetails=GroupMailbox` (the reviewer's reading; the classifier also takes the
`MailUniversalDistributionGroup` type `Get-UnifiedGroup` reports); the protected-principal directory-read credential reads
every `member` and universal link the protected-principal check needs - the self-grant check now rests on that same named
credential, and on nothing the app pool can or cannot see.

Caller-visible change, by design: a grant to a Microsoft 365 group, or to a cloud group nesting one, is checked against its
members. A grant to any group the directory holds needs the protected-principal directory-read credential and is refused as
incomplete, naming it, while that credential is not configured or cannot be retrieved. Each group grantee costs one secret
fetch.

## Fix

- `Services/IIdentityResolver.cs`: `ResolvedRecipient` gains `RecipientTypeDetails` (its last parameter, optional) and
  `IsGroup`; new `ExchangeRecipientKinds.NamesAGroup(type, details)` - either kind carrying "Group", or `RoomList`;
  `ExchangeGroupMember.IsGroup` asks it too.
- `Services/ExchangeIdentityResolver.cs` `MapRecipient`: `RecipientTypeDetails` from the `Get-Recipient` row.
- `Services/PermissionValidator.cs`: `IsGroupRecipient` is `recipient.IsGroup`. `CompareInDirectoryAsync`, with groups to
  evaluate and an operator account, first has the credential (`DirectoryReadCredentialAsync`): the setting probed
  (`TryGetDirectoryReadSecret`; false refuses with `DirectoryReadCredentialUnreadable` and one error naming the rows), the
  Secret ID read as the protected-principal check reads it (`GetDirectoryReadSecretId`; none refuses with
  `DirectoryReadCredentialNotConfigured`), the secret fetched (`DirectoryReadSecretFetch`, a test seam, else
  `FetchDirectoryReadSecretAsync`: `DelineaService` from the validator's scope factory, a throw when none is registered) - a
  throw, a null or a blank account refuses with `DirectoryReadCredentialNotRetrieved` - and the account named `DOMAIN\name`
  unless the secret names it qualified (the protected-principal check's form). Each refuses "whether you are a member of the
  group '<x>' could not be read: <the problem>", one warning logged. The evaluation runs over `directory.GroupsUnder(credential)`.
- `Services/SelfGrantDirectory.cs`: `Groups` removed; `GroupsUnder(credential)` is the credentialed
  `PowerShellProtectedGroupDirectory` over this session's runspace.
- `Services/PowerShellProtectedGroupDirectory.cs`: `UnderSessionIdentity`, its constructor and its flag removed; the class is
  byte for byte what it was before aud-a52 (`132d6e97`) but for its summary line.

Tests: `SelfGrantDirectoryStandIn` - the search stubs and `Get-ADForest` take `-Credential`; `Identities`, `CallsAs` and
`MembershipReads` say who each call ran as; `MembersHiddenFromSession` hides a group's links from a read with no credential
(`AddGroup(membersHiddenFromSession:)`). `ExchangeWriteHarness.NothingProtectedValidator` stores Secret ID
`DirectoryReadSecretId` (4242) and sets the fetch seam to a stand-in that gives `svc-dirread` of `CONTOSO` for it and counts
`DirectoryReadFetches`; it takes the Protected Principals module config optionally. `PermissionValidatorSelfGrantTests` +10:
`AMicrosoft365Group_InTheShapeGetRecipientGives_IsCheckedAgainstItsMembers` (the operator inside: refused; not: allowed;
Exchange asked once either way), `AMembershipTheSessionIdentityCannotSee_IsReadUnderTheDirectoryReadCredential_AndRefused` (the
granted group's links hidden; a nested group's: refused, every `Get-ADGroup` read and the catalog's discovery as
`CONTOSO\svc-dirread`, the operator's own lookup as the session, one fetch), `AGroupGrant_WithoutTheDirectoryReadCredential_IsRefused_AsIncomplete_AndNoMembershipIsRead`
(not configured, its setting unreadable, not retrieved, the fetch throwing, a blank account: the refusal naming each, no
`Get-ADGroup` read at all), `AGrantToAPerson_NeedsNoDirectoryReadCredential`. `ExchangeGroupMembersTests`: the stubs in the
real shape (`Microsoft365Type`, `UserMailbox`); +15 - `AMicrosoft365GroupNestedInIt_IsAGroup_ByItsDetails_AndIsRead` and
`ARecipientIsAGroup_WhenEitherKindExchangeGaveNamesOne` (14 rows, the member and the recipient alike).
`ExchangeIdentityResolverTests.MapRecipient_CarriesRecipientTypeDetails_AndAMicrosoft365GroupIsAGroup`.
`ProtectedGroupMembershipTests.EveryRead_CarriesItsCredential_AndNoFormWithoutOneRemains` replaces aud-a52's two-form test:
both reads and the catalog's discovery as the credential's account, every instance constructor taking a credential, no static
factory of the class.

## Left for a follow-up (file owned by another agent)

- `Components/Pages/AdminSettings.razor` 282 (the placeholder "Required when groups or OUs are configured") and 289 ("Read-only
  AD credential for group membership and OU checks."): the Directory Read Credential is now also what a Mailbox Permissions or
  Calendar Permissions grant to a group needs - without it every such grant is refused as incomplete. Both texts should say
  so, for example "Required when groups or OUs are configured, and for Mailbox and Calendar Permissions grants to groups".

## Guard proof

Session-local driver `mutate.ps1` (session scratchpad `a60`, log `a60-mutations.log`): the fix staged; per mutant: the MD5 of
each file it touches, the edits (an exact-text replacement that must match exactly once, or the whole file as at a revision),
the MD5 again, a Debug build of the test project with 0 errors required, `dotnet test --no-build` filtered to
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ProtectedGroupMembership|FullyQualifiedName~ResourceDomainDenies|FullyQualifiedName~GroupMemberNesting|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeIdentityResolver`
(442 tests), the failing names taken from the run, then each file restored from the git index (`git checkout --`), its write
time advanced so MSBuild rebuilds it, and its MD5 checked equal to the one before. Fixed-file MD5s:
`Services/PermissionValidator.cs` `64ACACCBBB3CD100A02870CDF3F5AC6B`, `Services/IIdentityResolver.cs`
`7883E50450CF26010DF4D47ABB849B85`, `Services/ExchangeIdentityResolver.cs` `554F27730DC13ED6844A97FC311BA413`,
`Services/PowerShellProtectedGroupDirectory.cs` `6FE3B486BFE41365BD00F4E99F8FE1A1`, `Services/SelfGrantDirectory.cs`
`E93483FD83D4B08CAF206E6835BF35AD`. Every mutant built with 0 errors and every restore matched. After the round, the restored
tree rebuilt and the filter: 442/442 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | `IsGroupRecipient` on `RecipientType` alone again (the line as it stood) | `PermissionValidator.cs` `63E9FF247BE1C42ADEDF45452FF9313D` | 2 of 442: both `AMicrosoft365Group_InTheShapeGetRecipientGives_IsCheckedAgainstItsMembers` rows - the operator inside allowed; not inside, Exchange never asked |
| M2 | the session-identity reads again: `PowerShellProtectedGroupDirectory.cs` and `SelfGrantDirectory.cs` as at `a7300dee`, the evaluation over `directory.Groups` with no credential had | `763F5452C35110B068AE9FC1179ABDA5`, `655601E2340033D00267B8A3AE2E8AD9`, `PermissionValidator.cs` `1EF05D71F91FEA3AA49E2B208882D982` | 8 of 442: both `AMembershipTheSessionIdentityCannotSee_...` rows (allowed), all five `AGroupGrant_WithoutTheDirectoryReadCredential_...` rows (allowed, read without it), `ProtectedGroupMembershipTests.EveryRead_CarriesItsCredential_AndNoFormWithoutOneRemains` |
| E1 | extra: `ExchangeGroupMember.IsGroup` on `RecipientType` alone | `IIdentityResolver.cs` `499ECE73EC55AFE6273BEC3604F8279F` | 5 of 442: `ACloudGroupNestedInIt_IsReadToo`, `AMicrosoft365GroupNestedInIt_IsAGroup_ByItsDetails_AndIsRead` (the nested Microsoft 365 group not read), three `ARecipientIsAGroup_...` rows (`UserMailbox`/`GroupMailbox`, no type/`GroupMailbox`, no type/`RoomList`) |
| E2 | extra: `MapRecipient` dropping `RecipientTypeDetails` | `ExchangeIdentityResolver.cs` `821C9053AE91C4C3CF583BA8F40D3907` | 1 of 442: `MapRecipient_CarriesRecipientTypeDetails_AndAMicrosoft365GroupIsAGroup` |
| E3 | extra: a credential that cannot be had taken as "not a member" (`return null;`) | `PermissionValidator.cs` `81E512FC67CD39F0D0D5414794DC9E1D` | 5 of 442: every `AGroupGrant_WithoutTheDirectoryReadCredential_...` row - allowed |
| E4 | extra: `GroupsUnder` handing the directory no credential (`null!`) | `SelfGrantDirectory.cs` `095D349FEF45B7084B5690794CB7F564` | 2 of 442: both `AMembershipTheSessionIdentityCannotSee_...` rows - the reads carried none, the hidden links read as no rows, allowed |

No separate pre-fix reproduction: M1 is the classification as it stood, and M2 the reads as they stood, each at its call site.

## Known gaps

- Labelled assumptions, as the plan records them: a Microsoft 365 group's kinds as `Get-Recipient` reports them, and the
  protected-principal directory-read credential reading every link the membership needs. Nothing ran against a tenant or a
  forest.
- The operator's and the grantee's lookups (`SelfGrantDirectory.FindBySid`, `FindByUserPrincipalName`, `FindNamedBy`) still
  run under the app pool's identity: resolver reads, outside this finding. A grantee object hidden from that identity reads as
  no object, so a group Exchange Online does not name at all - one only the on-premises directory holds - is then allowed with
  no membership read. A candidate in the hand-back.
- A deployment with no directory-read credential configured cannot grant Mailbox Permissions or Calendar Permissions to a
  group the directory holds: refused as incomplete, naming the setting (the Admin Settings follow-up above).

## Comments after the round

In the branch's comments commit `1c982906`, checked comment-only by `codelines.ps1` (session scratchpad `a60`): every added or
removed line is a whole-line `//` or `///` comment, and each file's code lines - blanks and whole-line comments stripped - hash
the same before and after: `Services/PermissionValidator.cs` (8 changed lines) `352BCD7055AFBE0CFE4B8F6706B4C14F`,
`Services/IIdentityResolver.cs` (4) `94BDA56D9D4EAD6C78683BFC71A8F82D`. It corrects `CompareInDirectoryAsync`'s remark,
which still named the removed session-identity reads (`SelfGrantDirectory.Groups`), and names the bound and the run where the
cloud read is described. The other comments of this record were written with the fix.

## Gates - 2026-10-23 (branch tip `1c982906`: the five fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors; 19 warnings - the baseline, 19 at
  `a7300dee`, the same nineteen - none in a touched file. NuGet audit on (NU1902/3/4 errors): none raised.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,387 passed / 3 skipped (the
  three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 6,390 total, 6m55s;
  nothing hung (the blame folder empty, no Sequence file). After aud-a60 alone: 6,333 / 3 / 0.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `git add -A` then `tools/Test-AsciiOnly.ps1`: exit 0 ("ASCII lint passed"). The pages: 808 and 762 lines.

No AD, Exchange Online, Graph, DHCP or IIS call: the directory is `SelfGrantDirectoryStandIn`, Exchange the harness's pool over
stand-in cmdlets or a stand-in resolver, the on-premises session the harness's `DefineOnPremSession`, the secret store a
stand-in fetch, the pool's connect step a local runspace of stand-in functions. No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a83ff1596fd6eee17`, not merged, not pushed. Due at merge: the base app version - the shared
`PermissionValidator`, `IIdentityResolver`'s records (`ResolvedRecipient.RecipientTypeDetails`, `ExchangeRecipientKinds`),
`ExchangeIdentityResolver`, `PowerShellProtectedGroupDirectory` (also the protected-principal check's and the self-service
DACL check's reads; their credentialed path is unchanged, the class byte for byte as before aud-a52 but its summary) and
`SelfGrantDirectory` - and the Mailbox Permissions and Calendar Permissions module versions: what a grant to a group is
refused or allowed on, and that it now needs the protected-principal directory-read credential. Deployment note for the
release: a deployment that has not configured the Directory Read Credential (Admin Settings, Protected principals) refuses
every Mailbox Permissions and Calendar Permissions grant to a group the directory holds, naming the setting.
