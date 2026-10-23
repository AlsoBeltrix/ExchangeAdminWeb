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
**Status**: Complete; merged `fce22146`; codex perm-batch-5 raised two MEDIUMs, admitted as aud-a52
**Branch**: `worktree-agent-ac928f0732402c4c5`
**Commit**: `4cc5756b` (fix, tests, record); plan `1272a8cf`; comments `bce8d047`
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

## Fix

`Services/PermissionValidator.cs`:
- `ValidateSelfGrantAsync(ClaimsPrincipal? actingUser, string affectedUser)`. The setting first, as before; no authenticated
  principal refuses ("the signed-in operator could not be identified"); the logon-name string match (`IdentitiesMatch`)
  stays, refusal-only and before any I/O. Then one `SelfGrantDirectory` session (opened through `Task.Run`; a session that
  cannot be opened refuses: "the on-premises directory could not be read").
- `EstablishOperator`: by the `PrimarySid` claim (a canonical SID, `SelfServiceGroupService.IsSecurityIdentifier`, else
  refused as malformed) through the catalog; exactly one object, or refused ("the on-premises directory holds no account /
  N accounts for the signed-in operator '<name>'"); the identity put to Exchange is the UPN claim (`ClaimTypes.Upn`, then
  `preferred_username`) when present, else that object's `userPrincipalName`, blank refused. No SID: the UPN claim, its
  object when the forest holds one (several refused, none: no directory object). Neither claim refused. A read that throws
  refused.
- Exchange Online as aud-a28y left it, asked about that identity; then `CompareInDirectory`: `FindNamedBy(affectedUser,
  <Exchange's canonical address for it>)` (a failed search refuses: "the on-premises directory could not resolve '<x>'");
  any object found that is the operator (`IsTheOperator`: the account's `objectGUID`, or the account's or Exchange
  recipient's primary address) refuses as a self-grant; a grantee Exchange names as a group (`RecipientType` contains
  "Group") with no group found under its address refuses as incomplete; no directory account ends the check; for each
  group found, the primary group (`ProtectedGroupMembership.PrimaryGroupSid` against the group's `objectSid`) or a direct
  `member` link (`SelfGrantDirectory.HasDirectMember`) refuses as a self-grant, and a read that fails as incomplete
  ("whether you are a member of the group '<x>' could not be read").
- The refusals share `SelfGrantCheckIncompleteBecause(reason)`; `SelfGrantCheckIncomplete(identity)` is its Exchange
  Online case, text unchanged. `ValidateSelfGrant` (synchronous) removed. `SelfGrantDirectoryRunspace` the test seam.
- Folded in: `PreventSelfGrantFromAppsettings` warns through a static `ConcurrentDictionary` of the values already named,
  so once per process per value; the refusal is unchanged.

`Services/SelfGrantDirectory.cs` (new): `SelfGrantDirectory.Open(createRunspace)` - runspace, `ActiveDirectoryModuleImport.
Import`, `(Get-ADForest -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1` -> `host:3268`, no host refused; reads
`Get-ADObject -LDAPFilter -Server -Properties -ErrorAction Stop`, `HadErrors` a failure, each row parsed strictly
(`ToObject`: DN, `objectGUID`, class required; the primary address from the `SMTP:` proxy, else `mail`; every `smtp:` proxy
and `mail` kept). `FindBySid` (`ProtectedGroupMembership.GroupLookupFilter`), `FindByUserPrincipalName`, `FindNamedBy`
(`GranteeFilter`), `HasDirectMember` (the group's own domain by `GroupManagementService.ServerFromDn`; the group read there
by `ExactDnFilter` must return exactly it, then `DirectMemberFilter`). Ambient identity, nothing named.
`SelfGrantDirectoryObject` the row.

Callers: `MailboxPermissions.razor` 431 and `CalendarPermissions.razor` 406 pass `validationAuthState.User` (same line;
808 and 762 lines kept); `MailboxPermissionService` 274 and `CalendarPermissionService` 162 pass `actingUser`.

Tests: new `ExchangeAdminWeb.Tests/SelfGrantDirectoryStandIn.cs` (the forest, its stub runspace and an LDAP filter reader
that refuses what it does not evaluate); `ExchangeWriteHarness` gains `Forest`, `Operator`, `OperatorLogonName`,
`OperatorUpn` and sets the seam on `NothingProtectedValidator`. `PermissionValidatorSelfGrantTests` rewritten: 57 cases -
the operator's Exchange identity (3), the operator not established (12), the directory comparison (6), groups (9), aud-a28y's
Exchange cases (12), the bulk loops (6), the source and the shape (9: the records, the principal at every call site, the
sync check gone). New `PermissionPagesSelfGrantTests` (2): both pages' `SubmitSingle` driven by reflection, an alias of the
operator's own mailbox refused through the principal and recorded with the validator's text. `PermissionValidatorTests`:
the six synchronous tests moved to the async check (9 rows), `CreateValidator` defaulting to an empty stand-in forest.
`PermissionValidatorConfigReadTests`: the self-grant rows ask the async check over a principal; the aud-a28y case expects
the operator's UPN; the log case uses a value of its own; new
`AnAppsettingsValueThatIsNeitherTrueNorFalse_IsNamedOnce_NotOnEveryValidation` (the fold-in).
`MailboxPermissionWriteIdentityTests` / `CalendarPermissionWriteIdentityTests`: the bulk-write case runs as the harness's
operator. `ProgressRegistry`: the two pages' SubmitSingle reasons name the directory read.

No test reaches the production runspace: the whole suite was run once with `SelfGrantDirectory.DefaultRunspace` made to
throw and log each call to a scratch file - 6,071 passed / 3 skipped / 0 failed and no call logged - then the file was
restored byte for byte (MD5 `6D0DD0DA1F0039BCB10D7E85CB0D0405`) and touched.

Caller-visible change, by design: a Mailbox Permissions or Calendar Permissions grant, single or bulk, is now also refused
(1) as a self-grant when the directory names the grantee as the operator's own account or address, or as a group holding
the operator directly or as their primary group; (2) as a check that could not be completed when the operator cannot be
established in the directory (no principal, no SID or UPN claim, a SID the forest holds no single account for, an account
without a UPN), when the directory cannot be read, when the grantee or a group's membership cannot be read, or when
Exchange names the grantee a group the directory does not hold (a cloud-only group). Exchange Online is asked about the
operator's UPN, never the logon name, so the refusal for an Exchange that cannot answer names the UPN. A bulk loop handed no
principal refuses its grant rows. Each check opens a directory session (a runspace and the ActiveDirectory module): one per
single grant and one per bulk row, the cost the protected-target check's AD resolution already pays per row. An
unparseable appsettings `Security:PreventSelfGrant` is named once per process, not on every check.

## Guard proof

No separate pre-fix reproduction: every new test calls the check with a principal, a signature the old validator does not
have. M1 (the logon name put to Exchange) and E0 (no directory step after Exchange) are the old control flow at its call
site; between them they are the check as it stood.

Session-local driver (`mutate.ps1`, session scratchpad `a43`, log `a43-mutations.log`): the fix staged; per mutant: MD5,
one exact-text replacement that must match exactly once, MD5, a Debug build of the test project with 0 errors required,
`dotnet test --no-build` filtered to
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~WriteIdentityTests|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~BulkPermission|FullyQualifiedName~BulkAndOnPrem|FullyQualifiedName~OutOfOffice|FullyQualifiedName~ProtectedPrincipalExclusionsReadTests|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry`
(705 tests), the failing names taken from the run, then the file restored from the git index (`git checkout --`), its
write time advanced so MSBuild rebuilds it, and its MD5 re-checked. Fixed-file MD5s: `Services/PermissionValidator.cs`
`3A05F11B4D2A8D36FC76AEB3E39DBB48`, `Services/SelfGrantDirectory.cs` `6D0DD0DA1F0039BCB10D7E85CB0D0405`. Every mutant built
with 0 errors and its file was restored to that MD5 each time. After the round, the restored tree rebuilt and the same
filter: 705/705 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | `ValidateSelfGrantAsync`: the logon name put to Exchange, and named in its refusal, again in place of the directory's UPN | `8ADB158091AFE57573431139C98A5E45` | 10: `TheOperatorIsPutToExchange_AsTheDirectorysUserPrincipalNameForTheirSid_NotTheLogonName`, both `AUserPrincipalNameClaim_IsPutToExchangeAsGiven` rows, `ACheckExchangeCannotAnswer_IsRefused_NamingWhoCouldNotBeResolved(unreachable: "jdoe@contoso.test")`, `NoIdentityResolver_RefusesTheGrant_AsACheckThatCouldNotBeCompleted`, both bulk loops' `(shape: "unreachable")`, `PermissionValidatorConfigReadTests.ExchangeOnlineRowsThatCannotBeRead_RefuseASelfGrant_AsACheckThatCouldNotBeCompleted`, and both pages' `PermissionPagesSelfGrantTests` cases (the alias grant went through to the stand-in write) |
| M2 | `CompareInDirectory`: the objects the directory names never compared with the operator (`foreach (var candidate in Array.Empty<SelfGrantDirectoryObject>())`) - the on-prem branch skipping the comparison | `70E8C0051ED82846CF1982322F5FE967` | 4: the three `AGranteeOnlyTheDirectoryNames_IsComparedAsADirectoryObject_AndTheOperatorIsRefused` rows, `AnotherDirectoryObjectCarryingTheOperatorsPrimaryAddress_IsTheOperator` |
| M3 | `CompareInDirectory`: the group branch removed (`foreach (var group in Array.Empty<SelfGrantDirectoryObject>())`) | `9CC84E6F0623EDD30E02FC153BCE6601` | 8: the three `AGroupTheOperatorIsADirectMemberOf_IsRefused` rows, `TheOperatorsPrimaryGroup_IsAGroupTheyAreAMemberOf`, `AGroupWithoutTheOperator_IsAllowed_AndItsMembershipIsAskedInItsOwnDomain`, the three `AMembershipThatCannotBeRead_IsRefused_AsIncomplete` rows |
| F1 | fold-in, `PreventSelfGrantFromAppsettings`: the once-guard removed | `D75DD67CDC126871094BDC9119A7EA4D` | `PermissionValidatorConfigReadTests.AnAppsettingsValueThatIsNeitherTrueNorFalse_IsNamedOnce_NotOnEveryValidation` |
| E0 | extra: the whole directory step skipped after Exchange (`return null;`) - the old flow | `9B7D6EEFD904A2930A59C83EC665895E` | 15: every directory and group case above, `AGranteeTheDirectoryCannotBeAskedAbout_IsRefused_AsIncomplete`, `AGroupExchangeNamesThatTheDirectoryDoesNotHold_IsRefused_AsIncomplete` and `ADirectoryObjectThatIsNotTheOperator_IsAllowed` (no search made) |
| E1 | extra, `EstablishOperator`: a SID the forest holds no object for taken as an operator with no directory object, the logon name put to Exchange | `CA06E1A2ACD3B8EBA9B92D0EC6F61449` | `AnOperatorTheDirectoryCannotEstablish_IsRefused_AndExchangeIsNotAsked(shape: "SID unknown")` |
| E2 | extra: a group Exchange names that the directory does not hold allowed when Exchange calls it cloud-only (`&& grantee.ExistsOnPrem`) | `E34680FC72832713961987B2B0EC8B39` | `AGroupExchangeNamesThatTheDirectoryDoesNotHold_IsRefused_AsIncomplete` |
| E3 | extra: a membership read that failed taken as "not a member" (`continue;`) | `70A1606D9AF8F7B76B095BF7EF0AFB35` | the three `AMembershipThatCannotBeRead_IsRefused_AsIncomplete` rows |
| E4 | extra: the primary-group check removed | `9E383B00B671A81B262F158B5812B29C` | `TheOperatorsPrimaryGroup_IsAGroupTheyAreAMemberOf` |
| E5 | extra, `SelfGrantDirectory.HasDirectMember`: the membership asked of the global catalog instead of the group's own domain | `BA00BD295F31EA8E90BCD0F7FE4FE5A6` | 6: the three `AGroupTheOperatorIsADirectMemberOf_IsRefused` rows (the catalog holds no global or domain-local group's links; the universal row names the wrong server), `AGroupWithoutTheOperator_IsAllowed_AndItsMembershipIsAskedInItsOwnDomain`, `AMembershipThatCannotBeRead_IsRefused_AsIncomplete` for the unreachable domain and the domain that does not answer |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 59 warnings, the baseline, none in a touched
  file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,071
  passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0
  failed, 6,074 total, 5m54s; nothing hung, no Sequence file. Forty cases are this commit's (57 for 20 in
  `PermissionValidatorSelfGrantTests`, 2 in `PermissionPagesSelfGrantTests`, 1 in `PermissionValidatorConfigReadTests`).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. The pages: 808 and 762 lines.

No AD, Exchange Online, Graph, DHCP or IIS call: the directory is `SelfGrantDirectoryStandIn`, Exchange a stand-in resolver
or the real resolver over a pool whose connect seam throws, and the whole suite was run once with the production runspace
made to throw (above). No coder self-review verdict is claimed.

## Comments after the round

Written after both rounds (aud-a43, aud-a44), in their own commit `bce8d047`, checked by `codelines.ps1` (session
scratchpad `a43`): every added or removed line of the five code files is a whole-line `//` or `///` comment or blank (131
lines), and each file's code lines - blanks and comments stripped - hash the same at the fix commits and after. A file with
no change fails the check (control run on an untouched file). This record's files: `Services/PermissionValidator.cs` (65
lines), `Services/SelfGrantDirectory.cs` (52), `Services/MailboxPermissionService.cs` (3), `Services/CalendarPermissionService.cs`
(3); code-line MD5s `F9269D49F3605F7CF586F5A83119EAA2`, `8CD1761A78560DBB25CDA59FEDC10D4F`, `2E515480D485C5A562C70368B53846B8`,
`1D98529C3B56EC49363DBD28F0A4E61D`, equal before and after. The pages are untouched by it.

## Branch-tip gates - 2026-10-22 (`bce8d047`: both fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 59 warnings, the baseline, none in a touched
  file of the branch.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0, 6,078 passed / 3 skipped (the
  three known AD skips) / 0 failed, 6,081 total, 5m11s; nothing hung, no Sequence file.
- `ClickGate*` 316/316 and `ProgressRegistry*` 35/35, each run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. The pages: 808 and 762 lines.

## Closeout

Fixed on branch `worktree-agent-ac928f0732402c4c5`, not merged, not pushed. Due at merge: the base app version (the shared
`PermissionValidator`), and the Mailbox Permissions and Calendar Permissions module versions (what their grants refuse, and
the Exchange identity their refusals name). The bumps are the supervisor's: `ExchangeAdminWeb.csproj` and
`Modules/ModuleCatalog.cs` were outside this brief. This record starts from master's text (`df81c034`, with the folded-in
section) and keeps that section byte-identical.

## Known gaps

- Labelled assumption: the app signs in with Negotiate only and its principal carries `Name` (`DOMAIN\user`) and
  `PrimarySid` and no UPN claim (`Program.cs` 44-45; no `IClaimsTransformation` in the code base;
  `OperatorEmailResolver`'s remarks). So the UPN-claim branch does not run today; the operator's Exchange identity is the
  `userPrincipalName` of the object the forest's catalog holds under their SID. Not verified against a live token.
- Labelled assumption: the directory reads run under the app pool's identity, as the other operator-identity reads do, and
  rest on that identity reading every object and member link the writing directory would resolve the grantee to - the
  condition decisions 2026-07-31 records for this deployment. Where a deployment hides an object or a group's membership
  from it, the read finds nobody or no member and the check falls back on Exchange's answer and the string match. The
  owner has not ruled on this identity for this check specifically.
- Labelled assumption: a search sent to `<catalog>:3268` with no search base covers the forest - what every forest search
  here rests on (`PowerShellProtectedGroupDirectory`, `GroupManagementService`, `ADDirectorySearchService`). Not probed.
- Membership is direct (a `member` link) or the primary group, as briefed; a group that holds the operator only through
  another group is not detected. `ProtectedGroupMembership.Evaluate` (transitive, forest-wide, fail-closed) is the shape
  that would close it.
- The grantee is searched by `userPrincipalName`, `mail`, `proxyAddresses`, `mailNickname`, `sAMAccountName`, `name` and,
  for a DN, `distinguishedName`; Exchange also accepts a canonical name, a `legacyExchangeDN` or a GUID, which this search
  does not cover (Exchange's own comparison still runs).
- A grantee Exchange names as a cloud-only group is refused as incomplete: its membership is not in the directory. An
  Exchange-side membership read would let such a grant through; not in scope.
- Each check opens a directory session (a runspace and the ActiveDirectory module): once per single grant, once per bulk
  row (at most `BulkCsvRowLimit.MaxRows`, 200).
- `ExecuteOnPrem` (both pages) re-runs the target check before the on-premises write but not the self-grant check, which
  ran in `SubmitSingle` before the confirmation pause; the pages are line-keyed, so not changed here (hand-back candidate).
- The stand-ins stand in for the directory and Exchange; nothing ran against a forest or a tenant.

## Folded in: a malformed appsettings Security:PreventSelfGrant warns on every validation (codex, perm-batch-4, 2026-10-22) - LOW

`Services/PermissionValidator.cs` around line 116: when the stored value is absent or blank and appsettings
holds an unparseable value ("yes"), aud-a28y's warning is emitted on every self-grant validation - one per
row of a bulk upload - for a static typo. Keep the fail-closed `true`; warn once per process per observed
invalid value (a static set of values already warned). Test: two validations, one warning. Mutation at the
call site: the once-guard removed.
