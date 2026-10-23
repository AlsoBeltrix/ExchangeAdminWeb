# aud-a69: The self-grant check's grantee object lookup still runs under the app pool identity, so a group that identity cannot see is allowed with no membership read

**Severity**: MEDIUM - the aud-a60 class on the one read aud-a60 did not move. `PermissionValidator`
(around line 788) finds the grantee's directory object through `SelfGrantDirectory.FindNamedBy` under the
session identity; when Exchange Online does not name the grantee (a group only the on-premises directory
holds) and that identity cannot see the group's object, `named` is empty, `groups` is empty, and the check
returns null (allow) with no membership read - the safety rests on the deployment's read visibility
(Invariant 7). The synced-nested lookup at line 953 already fails closed.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); planned
**Branch**: `worktree-agent-ac701914283a170ea` (from master `15eb1449`)
**Commit**: -
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

## Guard proof

Pending.
