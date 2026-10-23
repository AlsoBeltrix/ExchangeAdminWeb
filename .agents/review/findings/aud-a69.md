# aud-a69: The self-grant check's grantee object lookup still runs under the app pool identity, so a group that identity cannot see is allowed with no membership read

**Severity**: MEDIUM - the aud-a60 class on the one read aud-a60 did not move. `PermissionValidator`
(around line 788) finds the grantee's directory object through `SelfGrantDirectory.FindNamedBy` under the
session identity; when Exchange Online does not name the grantee (a group only the on-premises directory
holds) and that identity cannot see the group's object, `named` is empty, `groups` is empty, and the check
returns null (allow) with no membership read - the safety rests on the deployment's read visibility
(Invariant 7). The synced-nested lookup at line 953 already fails closed.
**Status**: Complete; merged `6a971048`; codex perm-batch-8 raised one MEDIUM (admitted as aud-a73) and two LOWs (held in aud-a70)
**Branch**: `worktree-agent-ac701914283a170ea` (from master `15eb1449`)
**Commit**: plan `5d120fbc`; fix `828fc33a`; comments `a9ce01f5`
**Reviewer**: implementer (surfaced by the aud-a60/a61/a53/a59/psl-9 agent, 2026-10-23, candidate 1 of its hand-back)

## Evidence

At `c89f639c`: `Services/PermissionValidator.cs` around 788 (`FindNamedBy` under the session identity) and
824 (`if (account is null || groups.Count == 0) return null;`); `Services/SelfGrantDirectory.cs` 111, 115,
119; the aud-a60 credentialed `PowerShellProtectedGroupDirectory` path.

## Approach (to be written by the implementing agent before code)

- The grantee object lookup runs under the same directory-read credential as the membership reads (one
  session per check, from the aud-a61 run), so what the credential can see is what the check compares; when
  the credential is absent, a grantee Exchange does not name REFUSES as incomplete (the aud-a60 refusal
  text), never allows. An empty answer for a grantee Exchange does not name is "not found in the directory"
  and refuses as incomplete, not allow: a grantee that exists nowhere cannot be granted anyway, and a grantee
  the identity cannot see must not pass.
- Tests through `SelfGrantDirectoryStandIn`: a group only the directory holds, visible to the credential and
  not to the session identity, refused as a self-grant when the operator is a member; an empty lookup
  refuses as incomplete. Mutation at the call site: the lookup under the session identity again; the empty
  answer allowed again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (MEDIUM). Branch
`worktree-agent-ac701914283a170ea` from master `15eb1449`: this record alone, then one fix commit
`fix(permission-validator): ... (aud-a69)`, second of three (after aud-a71's `bc1183ab`, before aud-a72); comments, if any,
in one comments-only commit after the three mutation rounds. Behaviour: the shared `PermissionValidator` and
`SelfGrantDirectory` (base app version) and what a Mailbox Permissions or Calendar Permissions grant refuses (both module
versions); the bumps are the supervisor's.

Evidence re-read at `15eb1449`:
- `PermissionValidator.CompareInDirectoryAsync` 782-866: the grantee is looked up first (788,
  `directory.FindNamedBy(affectedUser, grantee?.PrimarySmtpAddress)`), under the session's identity - `SelfGrantDirectory.Search`
  (192-216) passes no `-Credential` - and the directory-read credential is had only afterwards, only when a group was found
  (830). A grantee Exchange does not name (`grantee` null) whose object that identity cannot see reads as no row: `named` and
  `groups` are empty and 824 returns null - allowed, no membership read, no credential asked for. The safety rests on what the
  app pool's identity can see (Invariant 7): the read aud-a60 left under it (its Known gaps), where aud-a43 placed it as a
  resolver read.
- `CompareInExchangeGroupAsync` 944-970: a synced group Exchange lists in a cloud-only group is found by
  `FindNamedBy(address, address)` (953) under the session's identity, and not found refuses (963-967) - closed, but a group
  hidden from that identity and visible to the credential is refused unevaluated, and what was found is not read under the
  identity the evaluation reads under.
- `SelfGrantRun.DirectoryReadCredentialAsync` (aud-a61) has the credential once per run; the directory session is one per check.
- Decisions 2026-07-31 (the Protected Principals page's resolver under the app pool's identity, conditioned on this deployment,
  not to be inherited) and 2026-09-11 (Invariant 7): as aud-a60 records, an authorization check whose empty answer clears a
  grant may not rest on the deployment's visibility.

1. **The grantee is looked up under the directory-read credential.** `CompareInDirectoryAsync` has the credential first, from
   the run (once per run), and looks the grantee up under it: `SelfGrantDirectory.FindNamedBy(identity, canonicalAddress,
   credential)`, the same `Get-ADObject` search at the forest's catalog with `-Credential`. One directory session per check, as
   before; the evaluation uses the credential already had. The operator's own lookups (`FindBySid`, `FindByUserPrincipalName`)
   stay the session's - outside this finding; none or several accounts already refuse.
2. **Without the credential, a grantee Exchange does not name is refused.** The credential not to be had (its setting
   unreadable, not configured, not retrieved) and Exchange holding no recipient for the grantee: refused as incomplete, no
   lookup made - "the on-premises directory could not resolve '<x>': <the credential's problem>", the lookup's own refusal with
   aud-a60's problem texts (`DirectoryReadCredentialUnreadable`, `...NotConfigured`, `...NotRetrieved`). A grantee Exchange
   names is compared by Exchange as before; the directory's lookup of it then runs under the session's identity
   (`FindNamedByUnderSessionIdentity`), an answer that can only add a refusal - an object it finds is there; a group it finds
   still needs the credential for its membership and refuses without it (aud-a60). So a deployment without the credential
   still grants to a person Exchange names (aud-a60's `AGrantToAPerson_NeedsNoDirectoryReadCredential`).
3. **An empty lookup for a grantee Exchange does not name refuses.** Exchange holding no recipient for it and the directory,
   read under the credential, holding nothing it names: "neither Exchange Online nor the on-premises directory holds '<x>', so
   whether it is you could not be checked" - never an allow. A grantee that exists nowhere cannot be granted anyway; one that
   cannot be seen must not pass.
4. **The synced-nested lookup under the same credential.** `CompareInExchangeGroupAsync`'s lookup of a synced group (953)
   runs under the credential, so the evaluation reads what was found under the identity that found it; not found still
   refuses. Without the credential it refuses at once, naming it - "whether you are a member of the group '<x>' could not be
   read: <problem>", the evaluation's own refusal without it - and nothing is looked up as the session. Closed, as before.

Labelled assumption, aud-a60's: the protected-principal directory-read credential sees every object a grantee can name. The
check rests on that named credential, and on what the app pool can see only as evidence that refuses, when the credential is
missing and Exchange names the grantee.

Tests. `SelfGrantDirectoryStandIn`: an object can be hidden from a read made with no credential (`Entry.HiddenFromSession`,
`AddGroup(hiddenFromSession:)`) - such a read sees no row for it and none of its links, as a directory answers an identity that
may not read an object. `PermissionValidatorSelfGrantTests`:
- a group only the directory holds (Exchange names nothing), hidden from the session identity: the operator inside, refused as
  a self-grant; not inside, allowed after its membership is read; the grantee's lookup as the directory-read account, none as
  the session; one fetch;
- a grantee neither Exchange nor the directory holds: refused as incomplete, looked up under the credential;
- a grantee Exchange does not name, without the credential (aud-a60's five shapes): refused naming the credential's problem; no
  lookup of it made under any identity, no membership read;
- a person Exchange names, without the credential: allowed, the lookup the session's (aud-a60's test, its remark and
  assertions updated); with it: the lookup as the directory-read account (`ADirectoryObjectThatIsNotTheOperator_IsAllowed`
  gains the identity);
- one run, several grantees: the credential fetched once, every lookup under it;
- a synced group nested in a cloud-only group, hidden from the session identity: the operator inside, refused; not inside,
  allowed; without the credential: refused naming it, the nested group not looked up as the session.
Fixtures that granted to a name neither Exchange nor the forest held - allowed on exactly the empty answer this closes - get the
grantee they meant: `PermissionPagesSelfGrantTests` (the forest holds the `other` account its grants name),
`PermissionValidatorTests.ValidateSelfGrant_DifferentUser_ReturnsNull` (Exchange names the grantee), and any other the full suite
shows, each named in the Fix.

Mutations at the call site (the protocol aud-a71 records): M1 the grantee's lookup under the session identity again
(`FindNamedByUnderSessionIdentity` at the call site); M2 the empty answer allowed again (the not-found refusal removed). Extras:
E1 the credential-absent refusal removed (a grantee Exchange does not name looked up as the session); E2 the synced-nested lookup
under the session identity again; E3 `SelfGrantDirectory.Search` passing no `-Credential`.

Files: `Services/PermissionValidator.cs`, `Services/SelfGrantDirectory.cs`, `ExchangeAdminWeb.Tests/SelfGrantDirectoryStandIn.cs`,
`ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs`, the fixture tests named above, this record.

Caller-visible change, by design: a grant to a grantee Exchange Online holds no recipient for is checked against the directory
as the directory-read credential sees it, and refused as incomplete when that credential is not to be had (naming it) or when
nothing anywhere holds the grantee. A group hidden from the app pool's identity and visible to the credential is evaluated, not
missed or refused. With the credential configured every grant, not only one to a group, costs one secret fetch per run (a single
grant; a bulk file once).

## Fix

- `Services/PermissionValidator.cs` `CompareInDirectoryAsync`: the credential first, `run.DirectoryReadCredentialAsync(DirectoryReadCredentialAsync)`
  (once per run). No credential and no Exchange recipient for the grantee: refused, "the on-premises directory could not
  resolve '<x>': <problem>", one warning, no lookup. No credential and an Exchange recipient: one warning that the lookup runs
  as the session, then `FindNamedByUnderSessionIdentity`. A credential: `FindNamedBy(affectedUser, grantee?.PrimarySmtpAddress,
  credential)`. After the lookup, no Exchange recipient and nothing found: refused, "neither Exchange Online nor the on-premises
  directory holds '<x>', so whether it is you could not be checked", one warning. The membership refusal without the credential
  (aud-a60) reads the credential already had. `CompareInExchangeGroupAsync` takes the credential and its problem: a synced
  group among the members, without the credential, refuses at once with the evaluation's own refusal naming the problem; with
  it, is found by `FindNamedBy(address, address, credential)`. The method's remarks say where the lookup runs.
- `Services/SelfGrantDirectory.cs`: `FindNamedBy(identity, canonicalAddress, credential)` (a null credential throws) and
  `FindNamedByUnderSessionIdentity(identity, canonicalAddress)`; `Search` takes the credential and passes `-Credential` when it
  has one; `FindBySid` and `FindByUserPrincipalName` pass none, as before. The class remarks say which lookups run as which
  identity.

Tests: `SelfGrantDirectoryStandIn` - `Entry.HiddenFromSession` (and `AddGroup(hiddenFromSession:)`): a search made with no
credential returns no row for the object, and follows none of its links. `PermissionValidatorSelfGrantTests` +12 -
`AGroupOnlyTheDirectoryHolds_HiddenFromTheSessionIdentity_IsFoundUnderTheDirectoryReadCredential` (the operator inside: refused;
not: allowed; either way the lookup as `CONTOSO\svc-dirread`, every membership read as it, one fetch),
`AGranteeNeitherExchangeNorTheDirectoryHolds_IsRefused_AsIncomplete` (the lookup as the credential's, no membership read),
`AGranteeExchangeDoesNotName_WithoutTheDirectoryReadCredential_IsRefused_AsIncomplete_AndNotLookedUp` (aud-a60's five shapes,
through `WithoutTheCredential`, now shared with aud-a60's group test: the problem named, no lookup of the grantee at all),
`OneRun_HasTheDirectoryReadCredentialOnce_AndLooksEveryGranteeUpUnderIt` (three checks, two grantees: one fetch, three lookups
as the credential's), `ASyncedGroupInACloudOnlyGroup_HiddenFromTheSessionIdentity_IsFoundUnderTheDirectoryReadCredential`
(inside: refused; not: allowed; the nested lookup as the credential's),
`ASyncedGroupInACloudOnlyGroup_WithoutTheDirectoryReadCredential_IsRefused_NamingIt_AndNotLookedUpAsTheSession`. Updated, not
weakened: `ADirectoryObjectThatIsNotTheOperator_IsAllowed` pins the lookup as the credential's in place of its presence alone;
`AMembershipTheSessionIdentityCannotSee_IsReadUnderTheDirectoryReadCredential_AndRefused` pins the grantee's lookup as the
credential's beside the operator's as the session's; `AGrantToAPerson_NeedsNoDirectoryReadCredential` pins the session's lookup
and says why it may stand.

Fixtures that granted to a name neither Exchange nor the forest held, allowed on exactly the empty answer this closes - the
first two found by reading every test that reaches the check before the fix was built, the bulk two by the full suite on the
fixed tree (6,558 passed / 2 failed; no other failure):
`PermissionPagesSelfGrantTests` (its constructor adds the `other` account its grants name), `PermissionValidatorTests.
ValidateSelfGrant_DifferentUser_ReturnsNull` (Exchange names `jdoe@company.com` as a recipient of its own),
`MailboxPermissionWriteIdentityTests.TheBulkLoop_WritesEachRowsOwnValidatedMailbox_AndRefusesARowWithNone` and
`CalendarPermissionWriteIdentityTests.TheBulkLoop_WritesEachRowsOwnValidatedCalendar_AndRefusesARowWithNone` (the forest holds
the trustee's account).

## Guard proof

The aud-a71 driver and protocol (that record's Guard proof; log `a69-mutations.log`). Filter
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ProtectedGroupMembership|FullyQualifiedName~ResourceDomainDenies|FullyQualifiedName~GroupMemberNesting|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ValidatedWriteCallSite|FullyQualifiedName~BulkPermission|FullyQualifiedName~BulkAndOnPrem`
(901 tests). Fixed-file MD5s: `Services/PermissionValidator.cs` `72438140F12AE68686B017DFBA83461E`,
`Services/SelfGrantDirectory.cs` `40EE42B5FD3CB088E9C80333182EAEE2` (unmutated: `ExchangeAdminWeb.Tests/SelfGrantDirectoryStandIn.cs`
`56B33BB82A7093A4B1736D4C7CB5F4F5`, `ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs` `EE72E8C967E964EA92AFB43D61D4DD4B`,
`ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs` `DC02B29DD7443F6A863086CC8AE5BC71`,
`ExchangeAdminWeb.Tests/PermissionValidatorTests.cs` `135E175AF6D744316CA5E928A710B37B`,
`ExchangeAdminWeb.Tests/MailboxPermissionWriteIdentityTests.cs` `E13C2FC286F42AC89421F5390E44C867`,
`ExchangeAdminWeb.Tests/CalendarPermissionWriteIdentityTests.cs` `68A0CDE031FAFD38F2CCC9B03B3A7C21`). Every mutant built with 0
errors and every restore matched. After the round, the restored tree rebuilt and the filter: 901/901 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the grantee's lookup under the session identity again (`FindNamedByUnderSessionIdentity` at the call site) | `PermissionValidator.cs` `8C98A6F3793F3707298579A71046F796` | 7 of 901: both `AGroupOnlyTheDirectoryHolds_HiddenFromTheSessionIdentity_...` rows (the hidden group not found - refused as found nowhere, the operator inside or not), `AGranteeNeitherExchangeNorTheDirectoryHolds_IsRefused_AsIncomplete`, `ADirectoryObjectThatIsNotTheOperator_IsAllowed`, `OneRun_HasTheDirectoryReadCredentialOnce_AndLooksEveryGranteeUpUnderIt` and both `AMembershipTheSessionIdentityCannotSee_...` rows (the lookups as the session) |
| M2 | the empty answer allowed again (the not-found refusal never taken) | `2AB70E4FA9A713CD59E8D64E85D7DC4B` | 1 of 901: `AGranteeNeitherExchangeNorTheDirectoryHolds_IsRefused_AsIncomplete` - allowed |
| E1 | extra: the credential-absent refusal never taken (a grantee Exchange does not name looked up as the session) | `204051698F5DA2C36181D789007AEB16` | 5 of 901: every `AGranteeExchangeDoesNotName_WithoutTheDirectoryReadCredential_...` row - allowed on the session's read |
| E2 | extra: the synced-nested lookup under the session identity again | `44DD0033BFA723B8C3D62280E816E819` | 2 of 901: both `ASyncedGroupInACloudOnlyGroup_HiddenFromTheSessionIdentity_...` rows - refused unevaluated |
| E3 | extra: `SelfGrantDirectory.Search` passing no `-Credential` | `SelfGrantDirectory.cs` `034DE0A66D16BAB672A2E166F42DE39C` | 9 of 901: the seven of M1 and both `ASyncedGroupInACloudOnlyGroup_HiddenFromTheSessionIdentity_...` rows |
| E4 | extra: without the credential, the synced nested group looked up as the session first (the early refusal removed) | `PermissionValidator.cs` `AB7B0A45AE813B88AF70EBE437F9E9DA` | 1 of 901: `ASyncedGroupInACloudOnlyGroup_WithoutTheDirectoryReadCredential_IsRefused_NamingIt_AndNotLookedUpAsTheSession` - the session's lookup made |

No separate pre-fix reproduction: M1 is the lookup as it stood, at its call site, and M2 its empty answer as it stood.

## Known gaps

- Labelled assumption, aud-a60's: the protected-principal directory-read credential sees every object a grantee can name.
  Nothing ran against a forest; the stand-in models what each identity can see.
- Without the credential, a grantee Exchange names is still looked up as the session - refusal-only evidence beside Exchange's
  own comparison. Where the typed name also names, in the directory, an object Exchange Online does not (a security group not
  synced, say) that the app pool cannot see, and the on-premises write resolves the name to it, that object is not compared.
  It needs the credential to be missing - a deployment the check already refuses every group grant on - and a name the two
  directories resolve apart. A candidate in the hand-back (LOW); refusing every grant without the credential would close it.
- The operator's own lookups (`FindBySid`, `FindByUserPrincipalName`) stay the session's. By the SID claim a hidden or doubled
  account refuses; by a UPN claim alone - which the Negotiate sign-in never carries (aud-a43's labelled assumption) - an
  account hidden from the session reads as no account, and no on-premises group is then asked about. A candidate in the
  hand-back (LOW).
- Every grant the check reaches the directory for now fetches the directory-read secret once per run (one HTTP call to the
  secret store; the store keeps no cache), where only a grant to a group did.

## Left for a follow-up (file owned by another agent)

- `Components/Pages/AdminSettings.razor` 282 (`placeholder="Required when groups or OUs are configured"`) and 289 ("Read-only
  AD credential for group membership and OU checks."), as at `15eb1449`: aud-a60 recorded that the Directory Read Credential is
  also what a Mailbox Permissions or Calendar Permissions grant to a group needs. After aud-a69 it is also what a grant to a
  grantee Exchange Online holds no recipient for needs, and it is used for every grant's directory lookup when configured. The
  texts could say so, for example "Required when groups or OUs are configured, and for Mailbox and Calendar Permissions grants
  to groups and to grantees only the on-premises directory holds". Not changed: the page is another agent's.

## Comments after the round

In the branch's comments commit `a9ce01f5`, checked comment-only by `codelines.ps1` (session scratchpad): its 8 changed lines
in `Services/PermissionValidator.cs` are all whole-line `///` comments, and the file's code lines - blanks and whole-line
comments stripped - hash `9F85DD5A5F74351E4C2679A9EC48C95A` before and after. `ValidateSelfGrantAsync`'s remarks name the
grantee's lookup under the credential and the refusal of a grantee nothing holds; `CompareInExchangeGroupAsync`'s name the
synced nested group's lookup under it and the refusal without it (and, for aud-a72, whose members a dynamic group's are). The
other comments of this record were written with the fix.

## Gates - 2026-10-23 (branch tip `a9ce01f5`: the three fixes and the comments)

As aud-a71's record gives them: the Release build 0 errors and the 19 baseline warnings, none in a touched file; `dotnet test`
6,569 passed / 0 skipped / 0 failed, nothing hung (6,560 / 0 on this fix's commit `828fc33a`); format, `git diff --check HEAD`
and the ASCII lint exit 0; the pages 808 and 762 lines. No AD, Exchange Online, Graph, DHCP or IIS call. No coder self-review
verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-ac701914283a170ea`, not merged, not pushed. Due at merge: the base app version - the shared
`PermissionValidator` and `SelfGrantDirectory` - and the Mailbox Permissions and Calendar Permissions module versions: what a
grant is refused on. Deployment note for the release: with the Directory Read Credential (Admin Settings, Protected
principals) configured, every Mailbox Permissions and Calendar Permissions grant fetches it once per check (once per bulk file);
without it, a grant to a grantee Exchange Online holds no recipient for is refused as incomplete, naming the setting, as a grant
to a group the directory holds already was (aud-a60).
