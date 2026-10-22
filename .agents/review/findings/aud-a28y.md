# aud-a28y: Self-grant check - an unreadable config or a failed resolver skips the object-id comparison and allows the grant; a non-boolean appsettings value throws on every check

**Severity**: MEDIUM/HIGH - fail-open authorization (Known Failure Class 3). (1) `ValidateSelfGrantAsync`
compares the actor and the grantee by object id after a string match; when the module config cannot be
read or the resolver fails, `ResolveToObjectIdAsync` swallows the error and returns null, the comparison
is skipped and the grant is ALLOWED. A self-grant through an alias the string match misses therefore passes
exactly while Exchange cannot answer. (2) An appsettings `Security:PreventSelfGrant` that is not a boolean
makes `GetPreventSelfGrant` throw from `bool.Parse` on every self-grant check instead of refusing with a
reason (pre-existing; aud-a28t fixed the stored value's parse, not the appsettings one).
**Status**: Fixed on the branch (fix commit; comments commit to follow); review pending
**Branch**: `worktree-agent-a4c579a99f70ca768`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28t/a26q agent, 2026-10-22, candidates 2 and 3 of its hand-back)

## Evidence

At `18c62ce9`: `Services/PermissionValidator.cs` `ValidateSelfGrantAsync`, `ResolveToObjectIdAsync` (the
swallowed error, the null return and the skipped comparison), `GetPreventSelfGrant` (the `bool.Parse` on
the appsettings value).

## Approach (to be written by the implementing agent before code)

- When the resolver fails or the config is unreadable, the self-grant check REFUSES: "the self-grant check
  could not be completed: <reason>; nothing was written" - the aud-a28t refusal shape; the string match
  alone never clears a grant when the object-id comparison was wanted and unavailable.
- `GetPreventSelfGrant` treats an unparseable appsettings value like an unparseable stored value: refuse,
  name the setting, never throw.
- Tests through the validator's fakes (a resolver that throws; a config read that fails; an appsettings
  value "yes"); mutations at the call site: the null-skips-comparison path again; `bool.Parse` again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Branch
`worktree-agent-a4c579a99f70ca768` from master `18530b58`: one fix commit `fix(permission-validator): ... (aud-a28y)`,
first of three (then aud-a28x and aud-a26t, which touches the same file after it), and one comments-only commit after all
three. Behaviour: the shared `PermissionValidator` that Mailbox Permissions and Calendar Permissions reach for the
self-grant check, and the four places that record its refusal (base app version); no module version moves; the
supervisor owns the bump.

Evidence re-read at `18530b58`:
- `PermissionValidator.ValidateSelfGrantAsync` 583-617: after the string match (588-592) the object-id comparison runs
  only `if (resolver != null)` (598) and only when both `ResolveToObjectIdAsync` answers are non-null (603); no resolver,
  a null answer and an exception (caught at 611-614, "falling back to string matching") all return null - allowed.
- `ExchangeIdentityResolver.ResolveToObjectIdAsync` 16-37 catches every exception and returns null, so a lookup that
  could not run - Exchange unreachable, the pool refusing the borrow because the Exchange Online config cannot be read
  (the "module config cannot be read" of this finding), not configured - is the same null as "no such recipient". Its
  only caller is this check (`ExchangeWriteHarness.ExchangeKnows` 500 stubs it). `ResolveRecipientAsync` 40-70 is the
  fail-closed contract (`IIdentityResolver` 37-49: null only for Exchange's own "couldn't be found", every other failure
  throws), and the class already asks it that way for the excluded-user check (`LookUpRecipientAsync` 177-196: no
  resolver, or a lookup that throws, is "not answered").
- `GetPreventSelfGrant` 84-102: no stored value -> `bool.Parse(_config["Security:PreventSelfGrant"] ?? "true")` (95), which
  throws `FormatException` for a value such as "yes" (or blank) - every self-grant check, sync and async, then throws
  instead of refusing.
- The callers refuse on any non-null answer and record it under `*_SelfGrantAttempt` with the fixed detail "Self-grant
  blocked: User attempted to grant permissions to themselves": `MailboxPermissions.razor` 431/446,
  `CalendarPermissions.razor` 406/416, `MailboxPermissionService` 274/281, `CalendarPermissionService` 162/169. Their
  protected-target denials already record the validator's own text (`errorDetail: validationError`).

1. **The comparison asks the fail-closed lookup.** `ValidateSelfGrantAsync` keeps the string match first, then puts each
   identity, the acting user and then the grantee, to Exchange through `LookUpRecipientAsync` (so through
   `ResolveRecipientAsync`). Either one not answered -> refused, naming it: "Access denied: The self-grant check could not
   be completed: Exchange Online could not resolve '<identity>'; nothing was written. Contact your administrator." (new
   `internal static SelfGrantCheckIncomplete`). Both answered with a recipient -> refused as a self-grant, as today, when
   they are the same recipient: a shared non-blank `ExternalDirectoryObjectId` (today's rule, except that two blank ids no
   longer count as equal) or the same primary SMTP address, Exchange's canonical address (never blank: `MapRecipient`
   throws on one), which answers the question when an object id is missing. Exchange affirmatively reporting no such
   recipient for either side -> nothing to compare and the string match is the whole answer, as the excluded-user check
   reads the same null (aud-a2b) - an answer, not a failure.
2. **The fail-open lookup goes.** `ResolveToObjectIdAsync` is removed from `IIdentityResolver` and
   `ExchangeIdentityResolver`, with its stub in `ExchangeWriteHarness.ExchangeKnows`: no caller is left, and a lookup that
   cannot tell a failure from an absence is not left for the next one. The two Implemented plans that name it
   (`docs/ProtectedPrincipalResolution-Plan.md` 297, `docs/ExoDeadConnectionRetry-Plan.md` 117) get one pointer line
   each, as aud-a26q did for its renames.
3. **The appsettings fallback cannot throw.** `GetPreventSelfGrant`, no stored value: appsettings absent or blank -> true,
   the default, as today; a value `bool.TryParse` reads -> that value; anything else -> true (self-grants refused), one
   warning naming `Security:PreventSelfGrant` and the value - the aud-a28t treatment of an unparseable stored value.
4. **The record says why.** The four callers record `errorDetail: selfGrantError`, the validator's own refusal, as their
   denials already do - so a check that could not be completed is not recorded as an operator granting themselves
   access. Same-line edits: the two pages keep their line counts, so no ClickGateRegistry entry moves and no
   ProgressRegistry pin changes. No handler or markup otherwise changes.

Tests. New `ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs`, over `ExchangeWriteHarness` and its
`NothingProtectedValidator` with stand-in resolvers: (a) Exchange cannot answer for the acting user, or for the grantee,
the other side being a recipient Exchange would name as the same mailbox: refused with the could-not-be-completed text
naming that identity; (b) no identity resolver: refused the same way; (c) controls - an alias Exchange names as the acting
user (the same recipient; the same address with no object id) refused as a self-grant; two recipients Exchange names
differently, also with no object ids, allowed; Exchange reporting no recipient for either side allowed; (d) through the
Mailbox Permissions and Calendar Permissions CSV bulk loops: a row whose operator Exchange cannot resolve is refused, one
`*_SelfGrantAttempt` record carrying the could-not-be-completed text, no write, nothing connected; (e) a source check,
comments blanked, that each of the four `*_SelfGrantAttempt` records passes `errorDetail: selfGrantError`.
`PermissionValidatorConfigReadTests` (aud-a28t's real validator, pool and failing store; `ValidatorOver` gains an optional
resolver and logger): (f) the real `ExchangeIdentityResolver` over the pool, the store answering the self-grant setting's
read and failing every later one (the Exchange Online rows): refused as could-not-be-completed, nothing connected; (g) the
self-grant table gains appsettings "yes" with no stored value and with a blank one: refused, sync and async, no throw;
one warning names `Security:PreventSelfGrant`.

Mutations at the call site: (1) `ValidateSelfGrantAsync` takes a lookup it could not make for "no recipient" again - both
not-answered refusals removed, so the comparison is skipped and the grant allowed; (2) `GetPreventSelfGrant` parses
appsettings with `bool.Parse` again. Extras: (E1) only the grantee's refusal removed; (E2) the address half of the
comparison removed; (E3) the Mailbox Permissions bulk record back to the fixed detail. Plus an executed reproduction
of (e), (f) and (g) with the tests in place and the production files and the harness as at `18530b58`; (a)-(d) cannot be
run faithfully there - their stand-in resolvers do not answer `ResolveToObjectIdAsync`, which an unstubbed substitute
answers "" for both sides - so mutation 1, the old control flow at its call site, is their reproduction.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a4c579a99f70ca768`, the fix's tests in place and the files the fix changes outside the tests and
documents - `Services/PermissionValidator.cs`, `Services/IIdentityResolver.cs`, `Services/ExchangeIdentityResolver.cs`,
`Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs`, `Components/Pages/MailboxPermissions.razor`,
`Components/Pages/CalendarPermissions.razor` - and the harness `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` as they are at
`18530b58`, written by `git restore --source=18530b58 --worktree` (`git hash-object` equal to each blob: `f3dd0107`,
`3b532d32`, `df17def0`, `a2d9dec7`, `c31e12c9`, `f5046d6f`, `f6f1b7b6`, `0995c412`), build 0 errors:
`PermissionValidatorConfigReadTests|PermissionValidatorSelfGrantTests` 19 failed / 18 passed. The faithful rows:

- (f) `ExchangeOnlineRowsThatCannotBeRead_RefuseASelfGrant_AsACheckThatCouldNotBeCompleted`: allowed (`Actual: null`) - the
  real resolver's `ResolveToObjectIdAsync` swallowed the refused borrow, so the comparison was skipped; nothing connected.
- (g) the self-grant table's appsettings rows `(null, "yes")`, `("", "yes")`, `(null, " ")` and
  `AnAppsettingsValueThatIsNeitherTrueNorFalse_IsNamedInTheLog`: `FormatException` from `bool.Parse`.
- (e) the four `EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal` rows: the fixed sentence.

The stand-in cases (a)-(d) failed as well but not faithfully: the old harness answers `ResolveToObjectIdAsync` null for every
identity, so the old comparison never ran - mutation 1 below is their reproduction. Every file was then restored from the
index to its fixed MD5 and touched.

## Fix

`Services/PermissionValidator.cs`: `ValidateSelfGrantAsync` keeps the string match first, then puts the acting user and then
the grantee to Exchange through `LookUpRecipientAsync` (no resolver, or a lookup that throws, is not answered). Either one not
answered refuses with `SelfGrantCheckIncomplete(identity)`: "Access denied: The self-grant check could not be completed:
Exchange Online could not resolve '<identity>'; nothing was written. Contact your administrator." Two recipients refuse as a
self-grant when `SameRecipient` holds - a shared non-blank `ExternalDirectoryObjectId`, or the same primary SMTP address
(case-insensitive). Exchange reporting no recipient for either side leaves the string match the whole answer.
`GetPreventSelfGrant`'s no-stored-value branch goes through `PreventSelfGrantFromAppsettings`: absent or blank -> true; a
value `bool.TryParse` reads -> that value; anything else -> true, with one warning naming `Security:PreventSelfGrant` and the
value.
`Services/IIdentityResolver.cs`, `Services/ExchangeIdentityResolver.cs`: `ResolveToObjectIdAsync` removed;
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`: its stub removed from `ExchangeKnows`.
The four callers record `errorDetail: selfGrantError` (`MailboxPermissions.razor` 446, `CalendarPermissions.razor` 416,
`MailboxPermissionService.cs` 281, `CalendarPermissionService.cs` 169) - same-line edits, so the pages keep 808 and 762 lines,
ClickGateRegistry's `ExpectedLineCount`s.
`docs/ProtectedPrincipalResolution-Plan.md`, `docs/ExoDeadConnectionRetry-Plan.md`: one pointer line each.

Tests: new `ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs` (20 cases), as planned: Exchange unable to answer for
the operator or the grantee (2), no resolver (1), an alias Exchange names as the operator refused (3), recipients Exchange
names apart allowed (3), no such recipient as an answer (3), each bulk loop's refused row and its record (2 + 2), the four
callers' record source (4). `PermissionValidatorConfigReadTests` (5 cases): the real resolver over unreadable Exchange Online
rows, the appsettings rows and the warning; `ValidatorOver` takes an optional resolver factory and logger. Two additions to
the plan, no departure: the self-grant table also has a blank appsettings value (`" "`), which threw too; and each bulk loop
also records a real self-grant through an alias, to show the record is the validator's text in that case as well.

Caller-visible change, by design: when Exchange cannot be asked about the operator or the grantee - unreachable, not
configured, its config unreadable, no resolver - a Mailbox Permissions or Calendar Permissions grant, single or bulk, that the
string match does not refuse is refused with the could-not-be-completed text and recorded under `*_SelfGrantAttempt` with that
text, where it used to be allowed. A self-grant's record carries the validator's "Access denied: You cannot grant permissions
to yourself." instead of the fixed "Self-grant blocked: User attempted to grant permissions to themselves". Two recipients
that both report no object id are compared by address, so they are no longer one because both ids were "". An appsettings
`Security:PreventSelfGrant` that is not a boolean refuses self-grants with a warning instead of throwing from every check.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad's `a28y-a28x-a26t`): the fix staged; per mutant: MD5, mutate by exact
text (each find exactly once), MD5, `dotnet build` of the test project, `dotnet test --no-build` filtered to
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~WriteIdentityTests|FullyQualifiedName~OutOfOfficeRequestCaptureTests|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ProtectedPrincipalExclusionsReadTests|FullyQualifiedName~ExoConnectionPool|FullyQualifiedName~ModuleEnablement|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~SharedTempDatabaseTripwireTests|FullyQualifiedName~BulkPermission|FullyQualifiedName~BulkAndOnPrem`
(408 tests), restore from the git index (`git checkout --`), touch, MD5 re-confirmed. Fixed-file MD5s:
`Services/PermissionValidator.cs` `33DBDC0E30BDDC461813621340E2BD2C`, `Services/MailboxPermissionService.cs`
`38CB4AF6F9AD58691097484FE30EF5A6`; every mutant built with 0 errors and its file was restored to that MD5 each time. After the
round, the restored tree rebuilt and the same filter: 408/408 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `ValidateSelfGrantAsync`: both not-answered refusals removed - a lookup that could not run taken for "no recipient", the comparison skipped, the grant allowed (the old control flow) | `9A4D86B47414E24E956EE76CF35DAC24` | `ACheckExchangeCannotAnswer_IsRefused_NamingWhoCouldNotBeResolved` (both rows), `NoIdentityResolver_RefusesTheGrant_AsACheckThatCouldNotBeCompleted`, both bulk loops' `(shape: "unreachable")` rows, `PermissionValidatorConfigReadTests.ExchangeOnlineRowsThatCannotBeRead_RefuseASelfGrant_AsACheckThatCouldNotBeCompleted` - each allowed (`Actual: null`) |
| 2 | `GetPreventSelfGrant`: `bool.Parse(_config["Security:PreventSelfGrant"] ?? "true")` again | `648342D565A6E2890DE39C3157ADF274` | `TheSelfGrantSetting_FallsBackToAppsettings_OnlyWhenTheModuleConfigHoldsNoValue` rows `(null, "yes")`, `("", "yes")`, `(null, " ")`; `AnAppsettingsValueThatIsNeitherTrueNorFalse_IsNamedInTheLog` - `FormatException` |
| E1 | extra: only the grantee's refusal removed | `7DD84167AC63169F715B405193DD1B2B` | `ACheckExchangeCannotAnswer_IsRefused_NamingWhoCouldNotBeResolved(unreachable: "john.doe@contoso.test")` |
| E2 | extra, `SameRecipient`: the address half removed | `B713899FDF9555B9FE005E01634124B1` | `AnAliasExchangeNamesAsTheOperator_IsRefused_AsASelfGrant` rows `(null, null)` and `("", "")` |
| E3 | extra, `MailboxPermissionService`: the fixed detail back | `627E11937F4D352AFBBAC0B1F234CAFB` | `TheMailboxPermissionsBulkLoop_RefusesTheRow_AndRecordsTheValidatorsReason` (both shapes), `EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal(relativePath: "Services/MailboxPermissionService.cs")` |

Mutant E3 is byte for byte that file at `18530b58` (the reproduction's MD5 for it is the same). The code comments are written
after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,864
  passed / 3 skipped (the three known AD skips) / 0 failed, 5,867 total, 5m45s. No hang. The 25 cases added are this
  commit's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: Exchange is a stand-in
resolver, or the real resolver over a pool whose connect seam throws, and no case here opens a session. No coder
self-review verdict is claimed.

## Known gaps

- Labelled assumption: the acting user is put to Exchange as the pages give it, the Windows logon name
  (`MailboxPermissions.razor` 265, `CalendarPermissions.razor` 262: `user.Identity?.Name`, `DOMAIN\user`). Whether Exchange
  Online resolves that form is not verified here - no live calls. Where it reports no such recipient the check has nothing
  to compare and the string match is the whole answer, as before this fix, so a grant to an alias of the operator's own
  mailbox is caught only when Exchange names the logon name. Handed back.
- The comparison is made in Exchange Online. A grant written on-premises (the pages' on-prem path) to a principal Exchange
  Online has no recipient for is compared by string only.
- A grant to a group the operator is a member of is not a self-grant to this check, which compares the two names and
  expands nothing; as before.
- The synchronous `ValidateSelfGrant` (the string match alone) is unchanged; nothing in the app calls it, the tests do.
- The stand-in resolvers and the failing store stand in for Exchange and for a store fault; nothing ran against a tenant
  or a damaged database.
