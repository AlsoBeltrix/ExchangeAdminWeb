# aud-a69: The self-grant check's grantee object lookup still runs under the app pool identity, so a group that identity cannot see is allowed with no membership read

**Severity**: MEDIUM - the aud-a60 class on the one read aud-a60 did not move. `PermissionValidator`
(around line 788) finds the grantee's directory object through `SelfGrantDirectory.FindNamedBy` under the
session identity; when Exchange Online does not name the grantee (a group only the on-premises directory
holds) and that identity cannot see the group's object, `named` is empty, `groups` is empty, and the check
returns null (allow) with no membership read - the safety rests on the deployment's read visibility
(Invariant 7). The synced-nested lookup at line 953 already fails closed.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); not started
**Branch**: -
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

## Guard proof

Pending.
