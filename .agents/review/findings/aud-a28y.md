# aud-a28y: Self-grant check - an unreadable config or a failed resolver skips the object-id comparison and allows the grant; a non-boolean appsettings value throws on every check

**Severity**: MEDIUM/HIGH - fail-open authorization (Known Failure Class 3). (1) `ValidateSelfGrantAsync`
compares the actor and the grantee by object id after a string match; when the module config cannot be
read or the resolver fails, `ResolveToObjectIdAsync` swallows the error and returns null, the comparison
is skipped and the grant is ALLOWED. A self-grant through an alias the string match misses therefore passes
exactly while Exchange cannot answer. (2) An appsettings `Security:PreventSelfGrant` that is not a boolean
makes `GetPreventSelfGrant` throw from `bool.Parse` on every self-grant check instead of refusing with a
reason (pre-existing; aud-a28t fixed the stored value's parse, not the appsettings one).
**Status**: Plan written; in progress
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

## Guard proof

Pending.
