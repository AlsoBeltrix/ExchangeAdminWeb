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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
