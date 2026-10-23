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
**Status**: In progress; plan below, approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: -
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

## Guard proof

Pending.
