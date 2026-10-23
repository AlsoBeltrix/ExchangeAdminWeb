# aud-a43: Self-grant check - the operator reaches Exchange as DOMAIN\user, the on-prem path compares by string only, a grant to a group the operator belongs to is not detected, and the string-only synchronous check is still callable

**Severity**: MEDIUM - the aud-a28y class on the inputs aud-a28y did not change. (1) The pages pass
`user.Identity?.Name` (`DOMAIN\user`) as the operator identity; whether Exchange Online resolves that form
is unverified (no live call), and if it does not, Exchange reports "no such recipient", the object-id and
address comparison never runs for ANY operator and the string match is the whole check. A stable identity
(the UPN claim, or SID/objectGUID resolved through the directory) closes it. (2) On the on-prem path
(`location == "OnPrem"`) the comparison runs only in Exchange Online, so a grantee with no cloud recipient
is compared by string only. (3) A grant to a group the operator is a member of is not detected; there is no
group expansion in the self-grant check. (4) The synchronous `ValidateSelfGrant` is string-match only and
has no production caller; a future caller reopens what aud-a28y closed.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28y/a28x/a26t agent, 2026-10-22, candidates 1-4 of its hand-back)

## Evidence

At `448b02c8`: `Services/PermissionValidator.cs` around 615 (the operator identity put to Exchange), 599-630
(no group expansion), 585 (the synchronous check); `Components/Pages/MailboxPermissions.razor` 265 and 468,
`Components/Pages/CalendarPermissions.razor` 262 and 438 (the identity passed; the on-prem branch) - both
pages line-keyed by `ClickGateRegistry` (808 and 762 lines), edit in place only.

## Approach (to be written by the implementing agent before code)

- The operator identity handed to the check is the UPN claim when present (the sign-in's
  `ClaimTypes.Upn` / `preferred_username`), else the directory-resolved UPN of the Windows account (the
  forest GC search by SID, host forest discovered at runtime - Invariant 7); a check that cannot establish
  the operator's directory identity REFUSES (the aud-a28y "could not be completed" shape). Nothing names the
  environment.
- On the on-prem path the comparison runs against the on-prem directory (the grantee and the operator
  resolved to directory objects: same objectGUID or same primary SMTP), the same refusal when it cannot.
- Group grants: when the grantee resolves to a group, the operator's membership (direct, via memberOf
  across the forest, the aud-a23r shape) is checked; a member granting to their own group is a self-grant;
  an unreadable membership refuses.
- The synchronous `ValidateSelfGrant` is removed (its tests move to the async check).
- Tests through `ExchangeWriteHarness` with stand-in resolvers and the AD stand-in; mutations at the call
  site: `DOMAIN\user` passed again; the on-prem branch skipping the comparison again; the group branch
  removed.

## Guard proof

Pending.

## Folded in: a malformed appsettings Security:PreventSelfGrant warns on every validation (codex, perm-batch-4, 2026-10-22) - LOW

`Services/PermissionValidator.cs` around line 116: when the stored value is absent or blank and appsettings
holds an unparseable value ("yes"), aud-a28y's warning is emitted on every self-grant validation - one per
row of a bulk upload - for a static typo. Keep the fail-closed `true`; warn once per process per observed
invalid value (a static set of values already warned). Test: two validations, one warning. Mutation at the
call site: the once-guard removed.
