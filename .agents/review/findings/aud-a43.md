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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Branch
`worktree-agent-ac928f0732402c4c5` from master `1410013e`, the record text taken from master `df81c034` (the folded-in
LOW). One fix commit `fix(permission-validator): ... (aud-a43)`, first of two (aud-a44 follows; both touch the two pages),
then one comments-only commit after both mutation rounds. Behaviour: the shared `PermissionValidator` (base app version)
and what the Mailbox Permissions and Calendar Permissions grants refuse (both module versions); the supervisor owns the
bumps.

Evidence re-read at `1410013e`:
- `MailboxPermissions.razor` 265 / `CalendarPermissions.razor` 262: `currentUser = user.Identity?.Name`; 431 / 406 hand
  that string to `ValidateSelfGrantAsync`, which puts it to Exchange (`PermissionValidator` 615). The bulk loops do the
  same (`MailboxPermissionService` 274, `CalendarPermissionService` 162) though they hold the principal (`actingUser`).
- The app signs in with Negotiate only (`Program.cs` 44-45); nothing transforms its claims (no `IClaimsTransformation`
  in the code base), and `OperatorEmailResolver`'s remarks record that the token carries no UPN, mail or email claim -
  the operator's address there is looked up by the `PrimarySid` claim. So today the UPN-claim branch the Approach names
  never runs, and the identity Exchange is asked about must come from the directory.
- `ValidateSelfGrantAsync` 599-630: a string match, then two Exchange lookups; no directory object is consulted, and a
  grantee that is a group is compared as a recipient only (599-630). The page asks before it knows where the mailbox
  lives (location read at 461 / 431, after the check), so the same check serves the cloud and the on-prem write.
- `ValidateSelfGrant` 585-597: the string match alone, no production caller (tests only:
  `PermissionValidatorTests` 234-284, `PermissionValidatorConfigReadTests` 201, 227, 239, 252).
- `PreventSelfGrantFromAppsettings` 107-118: the warning on an unparseable appsettings value is written on every call.

1. **Who the operator is.** `ValidateSelfGrantAsync(ClaimsPrincipal? actingUser, string affectedUser)` - the name and the
   call text the ProgressRegistry pins stay. No authenticated principal refuses. The logon-name string match stays first
   and refusal-only (no I/O). Then the operator is established in the directory: by the `PrimarySid` claim through the
   forest's global catalog (`(objectSid=...)`), else, when the principal carries a UPN claim (`ClaimTypes.Upn`, then
   `preferred_username`), by `(userPrincipalName=...)`. Exactly one object is the operator; none or several for a SID, a
   failed read, or an account without a user principal name refuse with the aud-a28y shape: "Access denied: The
   self-grant check could not be completed: <reason>; nothing was written. Contact your administrator." The identity put
   to Exchange is the UPN claim when present, else that object's `userPrincipalName` - never the logon name. A UPN claim
   the forest holds no account for is an operator with no directory object: nothing on-premises can be them.
2. **Exchange Online**, as aud-a28y left it, with that identity: either lookup not answered refuses naming it; one
   recipient (shared object id, or the same primary address) refuses as a self-grant.
3. **The directory, every time.** The grantee is searched across the forest (global catalog) by the identities
   Exchange accepts for a grantee - `userPrincipalName`, `mail`, `proxyAddresses` (`smtp:`), `mailNickname`,
   `sAMAccountName` (also the name half of `DOMAIN\name`), `name`, and `distinguishedName` for a DN - plus Exchange's
   canonical address for it when Exchange named one. Any object found that is the operator - the same `objectGUID`, or
   the same primary SMTP address as the operator's directory object or Exchange recipient - refuses as a self-grant. A
   search that fails refuses. It runs on the cloud path too: the check is asked before the page knows which directory
   writes, and the operator's identity already needs the directory.
4. **Groups.** For each group found, the operator's direct membership: the group as their primary group (the group's
   `objectSid` against the operator's domain SID and `primaryGroupID`), or a `member` link, asked in the group's own
   domain (the DNS name its DN's DC components give; `GroupManagementService.ServerFromDn`) - the one place every direct
   member link of a group is held whatever its scope, where the operator's `memberOf` read in their own domain misses a
   domain-local or universal group of another domain. The group is first read there by its DN, so a domain that does not
   answer for it is a read that failed, not a group without the operator. A member refuses as a self-grant; a read that
   fails, a group that names no domain, or a primary group that cannot be derived refuses as incomplete. A grantee
   Exchange names as a group that the directory does not hold under its canonical address refuses as incomplete (its
   membership cannot be read). An operator with no directory object cannot be a member of an on-premises group
   (Constitution, Protected Principals).
5. **Where the reads run.** New `Services/SelfGrantDirectory.cs`: one session per check - a fresh runspace,
   `ActiveDirectoryModuleImport.Import`, the forest's global catalog from `Get-ADForest` at runtime (the
   `PowerShellProtectedGroupDirectory.GlobalCatalog` idiom, nothing named or configured), `Get-ADObject -LDAPFilter`
   with `-ErrorAction Stop`, a cmdlet error or a row without a DN, `objectGUID` or class a failed read. Under the app
   pool's identity, as the other operator-identity reads (`OperatorEmailResolver`, `OperatorMembershipDirectory`) and
   the excluded-group check aud-a23r extends: read-only, and the directory-read secret stays with the protected-principal
   check (decisions 2026-07-31 keeps that secret to where it is needed). Run off the caller's thread (`Task.Run`). The
   validator's test seam is `internal Func<Runspace> SelfGrantDirectoryRunspace`, the `ADOrganizationalUnitService`
   shape; production keeps the default runspace.
6. **The callers** pass the principal: the pages' 431 / 406 pass `validationAuthState.User` (the token the target
   check was decided against; same-line edits, so 808 and 762 lines and no ClickGateRegistry or ProgressRegistry pin
   moves); the bulk loops pass `actingUser` (null refuses).
7. **The synchronous `ValidateSelfGrant` is removed**; its string-match cases move to the async check
   (`PermissionValidatorTests`, `PermissionValidatorConfigReadTests`).
8. **Folded in (LOW).** `PreventSelfGrantFromAppsettings` keeps refusing on an unparseable value and warns once per
   process per value: a static set of the values already named.

Tests. New `ExchangeAdminWeb.Tests/SelfGrantDirectoryStandIn.cs`: an in-memory forest (objects, member links, domains,
a global catalog) behind a runspace of stub `Import-Module`, `Get-ADForest` and `Get-ADObject` advanced functions that
record each call and evaluate the LDAP filter the code sends, reaching the whole forest only through the catalog
endpoint and one domain through its DNS name, failing as a test says. `ExchangeWriteHarness` gains an operator principal
(Name and PrimarySid) and the stand-in, set on every `NothingProtectedValidator`. `PermissionValidatorSelfGrantTests`,
rewritten over the harness, the stand-in and stand-in resolvers: (a) the operator put to Exchange is the directory's
UPN for the SID - Exchange names the UPN and an alias as one recipient and does not know the logon name: refused; a UPN
claim is used as given; (b) the operator not established - no principal, neither claim, a SID the forest holds no or
two objects for, an account with no UPN, the module, the forest, the catalog or the read failing: refused as incomplete,
Exchange not asked; (c) the directory comparison - a grantee only the directory names (Exchange: no such recipient),
by a secondary proxy address, that is the operator's object: refused; a different object: allowed; another object with
the operator's primary address: refused; the search failing: refused; (d) groups - the operator a direct member, in the
operator's domain and in another domain (the read routed there): refused; the operator's primary group: refused; a group
without the operator: allowed; the member read or the group's own read failing: refused as incomplete; a group Exchange
names that the directory does not hold: refused as incomplete; (e) aud-a28y's cases carried over (Exchange unable to
answer for either side, no resolver, the alias, the recipients apart, no such recipient as an answer); (f) the bulk loops
refuse and record the validator's text, the principal passed; (g) source checks, comments blanked: the four call sites
pass the principal, never `currentUser`; the four records keep `errorDetail: selfGrantError`; no `ValidateSelfGrant(`
method remains; (h) the folded LOW: two validations over an unparseable appsettings value (a value of the test's own),
one warning. New `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`: both pages' `SubmitSingle` driven through
reflection (injected services set, fields set, the handler invoked): an alias grant refused through the principal the
page passes, recorded under `*_SelfGrantAttempt` with the validator's text. Existing tests updated, not weakened: the
bulk-write tests of `MailboxPermissionWriteIdentityTests` and `CalendarPermissionWriteIdentityTests` pass the harness's
operator; `PermissionValidatorConfigReadTests`' self-grant rows ask the async check over a principal, and its aud-a28y
case expects the operator's UPN in the refusal.

Mutations at the call site (md5 before, mutate, rebuild, filtered tests, failing names recorded, restore from git, touch,
rebuild, md5 equal, green): M1 the logon name put to Exchange again in place of the directory's UPN; M2 the directory
comparison of the grantee skipped (the on-prem branch); M3 the group branch removed; F1 (fold-in) the once-guard
removed. Extras: E1 a SID the forest holds no object for taken as "no directory object" (allowed); E2 a group Exchange
names that the directory does not hold allowed; E3 a failed membership read taken as "not a member"; E4 the primary-group
check removed.

Files: `Services/PermissionValidator.cs`, `Services/SelfGrantDirectory.cs` (new), `Services/MailboxPermissionService.cs`,
`Services/CalendarPermissionService.cs`, `Components/Pages/MailboxPermissions.razor` (431),
`Components/Pages/CalendarPermissions.razor` (406), the tests above, this record.

## Guard proof

Pending.

## Folded in: a malformed appsettings Security:PreventSelfGrant warns on every validation (codex, perm-batch-4, 2026-10-22) - LOW

`Services/PermissionValidator.cs` around line 116: when the stored value is absent or blank and appsettings
holds an unparseable value ("yes"), aud-a28y's warning is emitted on every self-grant validation - one per
row of a bulk upload - for a static typo. Keep the fail-closed `true`; warn once per process per observed
invalid value (a static set of values already warned). Test: two validations, one warning. Mutation at the
call site: the once-guard removed.
