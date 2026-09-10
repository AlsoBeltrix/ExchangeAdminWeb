# Cloud Password Reset Module (Entra ID cloud-only accounts)

Status: **Draft -- awaiting owner go.** S0 (the owner-resolution survey) is a hard gate on
the rest: its hit rate decides whether this design is viable at all. D1 is settled (the app
generates the password); D3 is answerable only after S0.

New module `CloudPasswordReset`. **The base app version bumps** -- this stream adds a public
method to `Services/EmailService.cs` and three optional members to `ADSearchResult`, both
shared infrastructure (Constitution, Deployment And Versioning). The "adding a module does not
bump the base version" exception does not apply, because this is not only a module.

Revision 2026-09-10 (fourth) folds in the codex review of `c493b2a..7c47c3c` and the two owner
rulings it produced: a post-write send failure fails closed with no reveal, downstream mail
delivery is out of scope, and the AD lookup is short three fields the corroboration and leaver
rules need. See the Review log at the end. The third revision's two overturned premises still
govern and are kept below because both reversals are load-bearing:

1. The first draft gated on "target holds an admin role". The owner corrected the premise --
   *almost no non-admin accounts are in scope* -- so that tier fenced nothing and was removed,
   along with an invented `BlockedDirectoryRoles` config field the owner never asked for.
2. The second draft had the password displayed in the UI, then emailed, then displayed again.
   The settled answer is **emailed to the account owner, invisible to the operator**, with an
   on-screen reveal available only under a second permission and only where email cannot
   reach. The owner's words: *"we need reliable email notification for users and admins and no
   visibility of the password for the tech making the change unless we gate that with another
   permission level."*

## Purpose

Owner request 2026-09-10, verbatim: *"can we explore writing a module for this that would
allow L2 to reset microsoft passwords? currently we sync on-prem ad to Azure, but we also
have Azure-only accounts for admins and other tactical needs. L2 can reset local AD
passwords, but not Azure."*

Give L2 a password reset path for Entra ID **cloud-only** accounts -- the population that has
no on-premises object and is therefore unreachable from the existing AD tooling.

## The population, and what follows from it

The cloud-only population in this tenant is **almost entirely admin and tactical accounts**,
several hundred of them. That is not an edge case to be gated; it is the module's subject.

1. **A privileged-target permission tier keyed on "the target is an admin" would be
   decoration.** Nearly every target trips it, so every operator must hold it to use the
   module at all -- the `idm-3` decorative-control class. The second permission this plan does
   carry is keyed on something else entirely: whether the operator may *see* the password.
2. **Many targets have no mailbox of their own.** An Entra-only admin or automation identity
   commonly has no Exchange recipient. The password therefore cannot be sent to the account
   being reset; it must go to the human who owns that account, at their corporate mailbox.
3. **The account does not know who owns it.** This is the central problem of the design and
   the reason for S0. See below.

## Why delivery, not visibility

If the operator sees the password, resetting a Global Administrator is an account takeover.
If the operator never sees it, the same act is a nuisance: the owner is inconvenienced until
they read their mail, and the whole thing is audited. The owner's ruling, verbatim: *"those
passwords should be emailed to the owner of the cloud account's @analog.com email address,
not displayed in the UI. therefore, it's irrelevant if someone changes someone else's PW
since they never see it."*

That reasoning holds only while the destination address is **derived, never chosen**. An
operator who can influence where the mail goes can mail themselves the password, and the
design inverts. Nothing on the page accepts, suggests, or displays an editable destination.

## The owner-resolution problem

There is no reliable link from a cloud-only account to its owner. The owner, verbatim:

> *"nothing reliable. the naming convention changed over the years. current ones SHOULD be
> `<samaccountname>-CLD@analog.onmicrosoft.com` for the entra account [...] older ones are
> just `first.last@analog.onmicrosoft.com` or `samaccountname@analog.onmicrosoft.com` or
> `first.last_CLD@analog.onmicrosoft.com`. that's not a reliable match."*

A stored mapping table was proposed and **rejected** by the owner: *"cannot store it. we're
not going to change several hundred cld accounts and we're not going to create an instantly
stale map."* That rejection is correct and this plan does not revisit it. A map records who
owned an account on the day someone typed it in; a derivation records who owns it now, and
refuses when the answer stopped being knowable.

### The derivation, computed fresh on every reset

Nothing is persisted. The answer is recomputed at each attempt, so a leaver whose AD account
is gone stops resolving on the next attempt rather than continuing to receive mail.

From the cloud account's UPN local part (`jsmith-CLD` in
`jsmith-CLD@analog.onmicrosoft.com`), build candidate on-premises keys:

1. The local part with a trailing `-CLD` or `_CLD` removed, case-insensitive (`jsmith`).
2. The local part unchanged -- covers the older `first.last@` and `samaccountname@` shapes.

Look each candidate up with `ADDirectorySearchService.ValidateExists(candidate, "User")`
(`Services/ADDirectorySearchService.cs:242`). That method is the right instrument and not the
autocomplete `Search`: it is an **exact-match** LDAP query
(`BuildExactMatchFilter`, `:432`), it distinguishes "the directory says no" from "the lookup
never ran" (`:225-228`), and it reports multi-match separately through
`DirectoryValidationResult.Ambiguous` (`:805-808`). `Search` is a substring query built for
autocomplete -- `jdoe` also matches `jdoe2` (`:230-232`) -- and must never be used here.

Resolution rules, fail-closed throughout:

| Outcome across all candidates | Result |
|---|---|
| Any candidate returns `Unavailable` | **Refuse.** The lookup never ran; this is not an absence. |
| Exactly one distinct AD user, `Ambiguous` false, non-blank `Email` | **Owner resolved.** |
| Exactly one distinct AD user, blank `Email` | **Unresolved** -- no mailbox to send to. |
| Zero found | **Unresolved.** |
| Two or more distinct users, or any `Ambiguous` | **Refuse as ambiguous.** Never pick one. |

Two distinct candidates resolving to the *same* AD user is one match, not two.

**Name corroboration.** A sAM collision across the forest's two domains
(`ad.analog.com`, `winroot.analog.com` -- `ADDirectorySearchService.cs:518`) could resolve to
the wrong person with a plausible-looking result. So a resolved owner is accepted only if the
AD user's given name and surname both appear, case-insensitively and in any order, within the
cloud account's Graph `displayName`. That tolerates `Smith, John` against
`John Smith (Cloud Admin)` and rejects an unrelated `jsmith` in the other domain. A
corroboration failure downgrades to **unresolved**, never to a send.

The exact tolerance is a tuning question that S0 answers with real data, not a guess made
here.

**Three AD fields the lookup does not currently return.** `ValidationProperties`
(`Services/ADDirectorySearchService.cs:407-414`) asks LDAP for `DisplayName`,
`DistinguishedName`, `SamAccountName`, `UserPrincipalName` and `mail` for a User, and
`ADSearchResult` (`:829-838`) carries exactly those. Corroboration needs `GivenName` and
`Surname`, and the leaver rule needs `Enabled`. All three must be added to the User branch of
`ValidationProperties` and to `ADSearchResult` as optional members with null defaults -- the
pattern `ObjectSid` and `DnsDomain` already use (`:819-821`) -- so no existing construction
site changes. This lands in S1. Stated here because a plan that corroborates on data the
query never requested would compile, pass its tests against a mock, and silently corroborate
nothing against a real directory.

### What happens to each outcome

- **Owner resolved** -- the reset proceeds and the password is emailed to that mailbox. The
  operator is told only *that* it was sent and to whom by display name, never the address and
  never the password.
- **Unresolved** -- refused for an operator holding only the main permission, with a message
  naming why (no match / no mailbox / name mismatch). Available to the reveal tier below.
- **Ambiguous or Unavailable** -- refused for everyone, including the reveal tier. An
  ambiguous derivation and a dead directory are not conditions a higher permission should
  paper over.

## S0 -- the survey that gates this plan

The design lives or dies on how much of the population resolves. Before any code is written,
a **read-only** survey runs the derivation across every cloud-only account and reports:
resolved / unresolved-no-match / unresolved-no-mailbox / unresolved-name-mismatch /
ambiguous, with a sample of each failure class.

`tools/Get-CloudAccountOwnerCoverage.ps1`, `-PlanOnly`-shaped like every other ops script
(`.agents/repo-guidance.md` Architectural Invariant 4), reading Graph and AD and writing
nothing. Pester coverage in `tests/ps/` for the candidate-derivation function, which is pure
string work and testable without a directory.

The owner sets the threshold after seeing the numbers. As a marker, not a rule: a high rate
makes the reveal tier a rare exception and this design sound; a low rate makes the exception
path the normal path, the reveal tier meaningless, and the design wrong -- at which point
this plan is replaced, not amended.

**No slice after S0 starts until the owner has seen the result and said go.**

## Scope

IN: cloud-only Entra ID user accounts (`onPremisesSyncEnabled` not true), including
role-holding admin and tactical accounts. One target per operation.

OUT: synced accounts (mastered on-premises -- L2 already resets those); guest / external
(`userType` `Guest`); MFA methods (that is `MfaReset`); enabling, unblocking or unlocking an
account; any `passwordProfile`-adjacent property other than the password itself; bulk reset;
any operator-supplied destination address; any stored owner mapping.

**Self-reset is structurally impossible and needs no guard.** The app authenticates operators
against on-premises AD; every target here is cloud-only by definition. The two populations
cannot intersect, so an operator cannot be their own target. Recorded explicitly because a
reviewer reading only the Graph surface will otherwise raise it (it was raised once already).

**The related case -- an operator resetting a cloud account whose derived owner is
themselves -- is also not a guard.** Codex raised it over `7c47c3c` (finding cpr-1,
`.agents/review/cpr-1.contested.md`); it is declined. If the derivation resolves to the
operator, the operator already holds that cloud account, so mailing them its new password
grants them nothing they did not have. The `ValidateSelfGrantAsync` precedent
(`Services/PermissionValidator.cs:280`) blocks giving yourself rights over **someone else's**
mailbox, which is an escalation; this is not. The only real hazard in the neighbourhood is a
derivation that resolves to the wrong person, and that hazard is identical whoever clicks the
button -- it is handled by corroboration and by the refuse-on-ambiguity rule above, not by a
self-check.

## The Graph surface, and why the obvious API is the wrong one

Verified against Microsoft Learn 2026-09-10.

**`POST /users/{id}/authentication/methods/28c10230-.../resetPassword` is not usable by this
app.** Its permissions table reads, verbatim:

| Permission type | Least privileged | Higher privileged |
| --- | --- | --- |
| Delegated (work or school account) | UserAuthenticationMethod.ReadWrite.All | Not available. |
| Delegated (personal Microsoft account) | Not supported. | Not supported. |
| **Application** | **Not supported.** | **Not supported.** |

Every Graph module here authenticates app-only with a client secret out of Delinea
(`Services/MfaResetService.cs:20-46` is the pattern). That shape structurally cannot call
`resetPassword`. Reaching it would need a delegated flow in which each signed-in L2 operator
personally holds Authentication Administrator -- a different authentication architecture for
the whole app, and it would hand L2 the role directly rather than mediating it, which defeats
the module.

**The app-only path is `PATCH /users/{id}` with `passwordProfile`.** Learn, verbatim: *"In
app-only scenarios using Microsoft Graph application permissions,
User-PasswordProfile.ReadWrite.All is the least privileged permission."*

| Operation | Method and path | Permission | Success |
|---|---|---|---|
| Resolve target | `GET /users/{upn}?$select=id,displayName,userPrincipalName,accountEnabled,onPremisesSyncEnabled,userType` | `User.Read.All` | 200 |
| Read active roles | `GET /users/{id}/transitiveMemberOf/microsoft.graph.directoryRole?$select=id,displayName,roleTemplateId` | `RoleManagement.Read.Directory` (ASSUMPTION -- confirm at consent; `Directory.Read.All` is the wider fallback) | 200 |
| Reset password | `PATCH /users/{id}` body `{"passwordProfile":{"password":"...","forceChangePasswordNextSignIn":true}}` | `User-PasswordProfile.ReadWrite.All` | 204 |

All v1.0; `GraphTokenClient` hardcodes that base (`GraphTokenClient.cs:16`).
`Services/GraphTokenClient.cs:134` already exposes `PatchWithStatusAsync`, so no new Graph
plumbing is needed.

Consequences of the PATCH path, all load-bearing:

1. **No system-generated password option.** That is a `resetPassword` feature only. The caller
   supplies the password, so the module generates its own (see **The generated password**).
2. **The write is synchronous.** `resetPassword` is long-running (`202` + `Location`
   polling); PATCH returns `204` and is done. No polling loop to get wrong.
3. **A rejected password returns `400`.** Tenant banned-password and complexity policy are
   evaluated server-side. The page must surface a policy rejection as such, never as success.
4. **The grant reaches every account in the tenant, and that is the requirement.** The role
   restrictions Microsoft documents under "Who can reset passwords" bound *delegated* callers
   through the signed-in admin's own role; an application permission carries no role. This
   module must be able to reset Global Administrator passwords -- nearly every target is an
   admin account, so that is its normal operating mode, not a corner of it. Not an open
   question and not a risk to re-litigate: settled with the population, 2026-09-10. The
   controls that do apply are the delivery model, the scarcity of the reveal permission, and
   the Delinea secret.

   One thing here is a **test, not a decision**: Learn states the permission with no app-only
   role carve-out, and this repo verifies doc claims rather than trusting them. The first live
   call must confirm the PATCH actually succeeds against an admin-role target, using a
   disposable account.

## The synced-account rule

A synced account's password is mastered on-premises: writing `passwordProfile` there either
fails or is overwritten at the next sync. Preflight reads `onPremisesSyncEnabled` and refuses
any target where it is true, naming the on-premises path in the refusal.

Fail-closed corollary: absent a definite `onPremisesSyncEnabled: false`, the target is
refused. An unreadable or missing property is a refusal, never an assumption of cloud-only
(Known Failure Class 3).

## The PIM trap

`transitiveMemberOf/microsoft.graph.directoryRole` returns **active** assignments only. A
PIM-*eligible* administrator who has not activated reads as holding no roles. Recorded because
it is silent: nothing errors, the target simply looks ordinary.

Roles are read for **display only** and gate nothing, so this trap costs accuracy on a panel
rather than a fence. Reading PIM eligibility (`roleEligibilityScheduleInstances`) needs
`RoleEligibilitySchedule.Read.Directory` and Entra ID P2; OUT of scope, and a plan revision if
the owner wants it.

## The two permissions

```
MainPermission = new("Access", "CloudPasswordReset", <description>, FailClosed: true)
GranularPermissions = [
    new("Reveal", "CloudPasswordResetReveal", <description>, FailClosed: true)
]
```

`Modules/AdminModuleDescriptor.cs:18` names that field `GranularPermissions`, and only
`FailClosed` entries become grantable section-access keys
(`Services/SectionAccessService.cs:57-58`). The shape mirrors the existing
`MailboxPermissionsOnPrem` / `MigrationCreate` granular entries
(`Modules/ModuleCatalog.cs:154`, `:210`).

- **`CloudPasswordReset`** -- reset an account whose owner resolves. The password is emailed
  and never shown. This is the L2 permission.
- **`CloudPasswordResetReveal`** -- additionally proceed when the owner does **not** resolve,
  and see the password once on screen. This is the exception path for ownerless automation
  identities and accounts whose naming defeats the derivation. It should be held by a handful
  of people, not by L2 as a body.

The reveal permission is not a "target is an admin" tier and does not repeat that mistake: it
is keyed on the delivery path, which genuinely varies per target and is not tripped by nearly
every operation.

The fences that bind, in evaluation order:

1. **Section access** -- who holds `CloudPasswordReset` at all. Fail-closed
   (`ModulePermission.FailClosed: true`, `Modules/ModulePermission.cs:3`).
2. **Server-side re-check immediately before the write** --
   `AuthorizationService.AuthorizeAsync(authState.User, "CloudPasswordReset")`, and separately
   `"CloudPasswordResetReveal"` before any reveal, mirroring
   `Components/Pages/MfaReset.razor:250-258`. The `@attribute [Authorize(Policy = ...)]` on
   the page is navigation control, not the gate (Constitution: UI hiding is not security).
3. **Delivery** -- an unresolved owner refuses unless the reveal permission is held.
4. **Protected principals** -- below.

## Protection, and the gap this population sits in

**Known gap, stated plainly: the protected-principal list cannot hold a cloud-only account
today.** The matching engine can compare an Entra object id
(`ProtectedPrincipalService.MatchesIdentity`, `Services/ProtectedPrincipalService.cs:712-729`),
and two modules already feed it one. But the only way to add an entry validates the input
against on-premises AD (`Components/Pages/AdminSettings.razor:849`) and refuses a miss with
"cloud-only objects cannot be protected"
(`Services/ProtectedPrincipalEntryValidator.cs:85`). So no cloud-only account can be listed,
and protection cannot fence this module's population.

This plan does **not** fix that, and does not pretend the fence is armed. What carries the
risk instead is the delivery model: an operator holding only the main permission never sees
the password, so resetting even a Global Administrator gains them nothing beyond disruption.
The reveal permission is where the residual risk sits, which is why it is separate, scarce,
and audited. Fixing the entry path is worthwhile work -- it also affects `MfaReset` and
`RiskyUsers` -- but it is a different work stream and not a prerequisite here.

The check still runs, because a cloud account that *does* have an Exchange recipient can
match, and because the code must be correct on the day the entry path is fixed. Copy the flow
at `Components/Pages/MfaReset.razor:262-374`, which exists because of
`docs/ProtectedPrincipalGapFix-Plan.md` GAP B:

- `ProtectedPrincipalService.ResolveWithExchangeFallbackAsync(upn)` -- **not** the AD-only
  resolve, which reports every cloud-only object as NotFound and silently skips protection.
- `ResolutionStatus.Unavailable` or `Ambiguous` refuses, audited.
- Resolved: `CheckAsync(resolved)`; `CheckFailed` refuses with its reason; `IsProtected`
  refuses unless `ProtectedPrincipalServicing.NoteFor(Servicers, user, "CloudPasswordReset",
  matchedRules, qualifier)` returns a servicer note.
- **Unresolved (null):** absence from AD and Exchange does not prove absence from Entra, and
  per "The population" that branch is a large part of this module's input. Build a
  `ResolvedDirectoryPrincipal` from the raw identity and `CheckAsync` it anyway rather than
  skipping the check.
- The whole protection block sits in a `catch (Exception)` that blocks as a precaution
  (`MfaReset.razor:368-374`).

**One deliberate improvement over MfaReset.** Its unresolved branch passes `EntraObjectId:
null` (`MfaReset.razor:335`) because it has no object id to hand. This module has already
resolved the target through Graph before protection runs, so it passes the real `user.id` as
`EntraObjectId`. That field is consulted by `MatchesIdentity`
(`ProtectedPrincipalService.cs:718-722`). Do not copy the `null`.

Servicer override plumbing is `ProtectedPrincipalServicing.NoteFor` and `.Extra`
(`Services/ProtectedPrincipalServicing.cs`), and **`"CloudPasswordReset"` must be added to
`ModulesWithProtectedPrincipalServicing` (`Components/Pages/ModuleConfig.razor:681-688`) in
the same commit as the `NoteFor` call** -- that file's own remark at `:679` requires it, and
omitting it is the `ppsvc-1` / `pgwt-1` / `idm-3` recurrence: the capability exists in code
and grants nothing from the admin UI.

## Ticket

Inject `ITicketValidator` (`Services/TicketValidationService.cs:29`) into the service and call
`ValidateAsync("CloudPasswordReset", ticket)` -- the shape
`Services/BitLockerRecoveryService.cs` already uses. Add the `ValidateTickets` Boolean config
field the validator reads (`ConfigFieldType.Boolean`, default `"false"`; it renders as a
checkbox, never a text input). Off means any non-blank ticket is accepted as audit metadata;
on means it must validate through ServiceNow. Both `Rejected` and unavailable-ServiceNow
refuse before any Graph call; the validator already fails closed on corrupt module config and
on a non-boolean switch value (finding btv-1).

This adopts the newer seam rather than `MfaReset.razor:241`'s direct
`ServiceNow.ValidateTicketAsync`.

## The generated password

Owner ruling 2026-09-10, settling D1: **the app chooses the password**, and the algorithm is
the one in `D:\source\pwgen` -- *"use the algorithm it's using, not the code."* The model is
Rust; this is a C# reimplementation of its method, not a port of its source, and not a
dependency on that binary.

It is a diceware-style passphrase generator: words from a fixed list, mixed capitalisation,
symbol separators, and digit-and-symbol padding to hit an exact target length, with a measured
entropy floor and a retry loop that refuses rather than degrades.

**Fixed parameters. None of these is a config field** -- an operator who can widen them can
weaken every password the module issues.

| Parameter | Value |
|---|---|
| Word list | 7,771 English words, 3-9 characters, lowercase (four contain a hyphen: `drop-down`, `felt-tip`, `t-shirt`, `yo-yo`) |
| Length | 18-32 characters, target chosen uniformly per password |
| Word count | 2-6 |
| Separators | `!@#$%&*?+=` |
| Entropy floor | 60 bits |
| Attempts | 100, then **refuse** |

**The method, step by step.**

1. **Target length.** Pick uniformly from 18-32. Everything downstream fits this exactly.
2. **Word count.** The lower bound is the smallest count whose conservative entropy estimate
   clears 60 bits (assuming a pessimistic ~500-word pool per position, so the estimate never
   over-counts); the upper bound is what physically fits, `(target - 1) / 4` -- each word needs
   at least 3 characters plus a separator, and at least one padding digit must survive --
   clamped to 2-6. Choose uniformly between them.
3. **Word selection, fitted to a character budget.** The words must total between
   `3 * wordCount` and `target - separators - 1` characters. Try ten fully random draws first.
   If none fits, fall back to guided selection: decide each word's length first, working the
   remaining budget down, then **shuffle the chosen lengths** before assigning words to them.
   The shuffle is load-bearing -- without it the guided path emits long-word-first passwords
   and leaks structure. Words are drawn without replacement.
4. **Capitalisation.** Three styles: ALLCAPS, lowercase, Title. The multiset is balanced for
   the word count (2-3 words: one of each, truncated; 4: one style repeats; 5: two styles
   repeat once each; 6: exact pairs), then shuffled, re-shuffling up to 20 times to avoid two
   adjacent words sharing a style.
5. **Assembly.** Words joined by a separator drawn per gap. Padding fills the gap between the
   assembled length and the target: always at least one digit, and a separator symbol too
   whenever two or more padding characters are available, the rest ~85% digits / ~15% symbols,
   then shuffled. Padding is **distributed across every slot** -- before the first word,
   between words, after the last -- seeding each slot once before scattering the remainder, so
   passwords do not all end in a numeric tail.
6. **Entropy check.** Score the result: word entropy from the *effective* pool (the geometric
   mean of how many list words share each selected word's length -- not the full 7,771, because
   the length-fitting step narrows the choice), plus separator choices, plus padding, plus the
   count of legal style arrangements. If it clears 60 bits and the length is in range, keep it;
   otherwise retry.
7. **Refuse, never degrade.** If 100 attempts fail, the reset **fails**. It does not fall back
   to a shorter password, a lower floor, or a different generator.

**What changes for this app.**

- **CSPRNG, not `System.Random`.** Every draw -- word, style, separator, digit, slot, shuffle
  -- uses `System.Security.Cryptography.RandomNumberGenerator` (`GetInt32`, which rejection-
  samples, and a Fisher-Yates shuffle built on it). `System.Random` anywhere in this file is a
  defect, and a test asserts the type is not referenced.
- **The word list is embedded**, as an embedded resource compiled into the assembly, not a file
  beside it that a host could edit. Its provenance and licence are recorded when it lands.
- **The password never leaves the generator except to the PATCH body and the owner's email.**
  No candidate, no rejected attempt, no length or entropy figure tied to a specific reset
  reaches a log line, the audit event, or the operation trace.
- **Entra policy compliance is asserted, not assumed.** Output is 18-32 characters (Entra
  allows 8-256) and always carries digits, symbols and at least two of upper/lower case, so the
  three-of-four character-class rule is met by construction. The separator set `!@#$%&*?+=` and
  the hyphen in those four words must be confirmed against Microsoft's allowed-character list
  in S3 before the first live call -- this repo checks doc claims rather than trusting them.
  A test asserts every generated password satisfies the policy as written.

## Delivery, and the ordering trap that comes with it

The password must exist before it can be sent, and it cannot be un-set once PATCHed. So the
write necessarily precedes the send, and a send that fails afterwards leaves an account whose
password nobody knows.

**Pre-write gates -- all of these are checked before the PATCH:**

1. The owner resolved, or the reveal permission is held.
2. `EmailService.UserNotificationsEnabled` (`Services/EmailService.cs:438`) is **true** when
   the run depends on email. This is a deployment-wide switch that outranks anything the
   module wants, and its own remark warns that a caller which cannot say so on screen has
   built a decorative control (`:431-437`). Here it is worse than decorative: a silent
   suppression would lock the owner out of their account. If it is off, the reset is refused
   before the write, naming the switch.
3. A non-blank destination address on the resolved AD user.

**Post-write send failure -- fail closed, no reveal.** Owner ruling 2026-09-10: *"if the send
itself fails, then fail closed."* If the PATCH succeeds and the SMTP send then fails, the
password is **discarded, not displayed**, to any tier. The page says the password was changed
but could not be delivered, and the event is audited as
`CloudPasswordReset_DeliveryFailed`.

The account is momentarily in a state where nobody knows its password. That is recoverable
and does not need an escape hatch on this page: running the reset again generates a new
password and attempts a new send. Showing the password instead would hand every operator a
way to see one by provoking a send failure, which is the whole model inverted for a condition
that fixes itself on retry.

**Downstream delivery is out of scope, by owner ruling the same day.** The app knows only
whether the SMTP handoff succeeded. A message that is accepted and then bounces, lands in
junk, or hits a full mailbox is invisible to it, and this module does not attempt to track
that. "Delivered" in this plan means "accepted by the mail server", everywhere it appears.

**The email itself.** `EmailService` has no way to send arbitrary text -- every public method
is purpose-built with a hardcoded subject and body (`SendUserNotificationAsync` `:169`,
`SendOofNotificationAsync` `:314`, `SendGroupMembershipUserNotificationAsync` `:371`,
`SendDeviceActionUserNotificationAsync` `:456`) and the raw send is
`private SendEmailAsync` (`:754`). So this stream adds one public method,
`SendCloudPasswordResetAsync`, following the `SendDeviceActionUserNotificationAsync` shape:
`virtual` for test seams, and **returning whether it actually sent**, so a suppressed send is
recorded rather than assumed (`:446-450` documents exactly this reasoning). Adding it is the
shared-infrastructure change that bumps the base app version.

The body names the account that was reset, the ticket, and the password, and says the
password must be changed at next sign-in. It does **not** name the operator.

## Audit and notification

- **No new `AuditService` method.** Use the generic `Audit.LogModuleAction(performedBy, ip,
  action, category, target, success, ticket, errorDetail, extra)`
  (`Services/AuditService.cs:199`), `category: "CloudPasswordReset"`, actions
  `CloudPasswordReset_Preview`, `CloudPasswordReset_Execute`,
  `CloudPasswordReset_Revealed` and `CloudPasswordReset_DeliveryFailed`.
- **The serviced note rides `extra`, never `errorDetail`.** `LogModuleAction` writes
  `["error"] = success ? null : errorDetail`, so a detail passed as `errorDetail` on a success
  is silently discarded -- the failure that lost an authorised-servicer record once already
  (`AuditService.cs:22-37`). Pass `ProtectedPrincipalServicing.Extra(note)`.
- **Every reveal is its own audited event.** There is exactly one condition that permits a
  reveal -- an unresolved owner plus the reveal permission -- and it is recorded as such. A
  reveal is the one path where an operator learns a credential; it is never folded into the
  ordinary success record.
- Every gate refusal is its own audited event carrying its reason.
- **Administrator email** on every real attempt via `Email.SendAdminNotificationAsync`
  (`Services/EmailService.cs:39`, `virtual` and test-seamable), armed only after the ticket
  and authorization pre-gates pass -- the `notifyAdmins` pattern at
  `MfaReset.razor:234-260`. It names the target, the operator, the ticket and whether the
  password was revealed. It never contains the password.
- **The new password appears in exactly two places: the PATCH body and the owner's email.**
  Not the audit event, not `extra`, not the administrator email, not the operation trace, not
  a log line (Constitution, Credential Isolation: *"Never log secret values ... passwords
  ..."*). A source-text test asserts that no audit or admin-email call site in this module
  receives the password variable.

## Owner decisions

### D1 -- SETTLED 2026-09-10: the app chooses the password

Owner ruling: app-generated, using the algorithm in `D:\source\pwgen` -- *"use the algorithm
it's using, not the code."* An operator-typed password was never compatible with this design:
the operator would know it by definition and the reveal permission would mean nothing. Spec in
**The generated password**, above.

### D3 -- OPEN, after S0: the corroboration tolerance

S0 reports how many accounts fail on name corroboration specifically. If that class is large
and its samples are benign (nicknames, maiden names, initials), the rule needs loosening; if
it is small, it stays strict. Deliberately not guessed before the data exists.

## External prerequisites

Outside the codebase; neither blocks the build, both block the first live call.

1. **A dedicated Entra app registration**, admin-consented, application permissions
   `User.Read.All`, `User-PasswordProfile.ReadWrite.All` and
   `RoleManagement.Read.Directory` (record which was actually granted if the wider
   `Directory.Read.All` fallback is needed). Do not reuse the `MfaReset`, `RiskyUsers`,
   `IntuneDevices` or `M365GroupManagement` registration -- each carries an unrelated blast
   radius, and this is the grant that should never be widened by convenience (Constitution,
   Credential Isolation: module credentials are per-module).
2. **A Delinea Secret Server record** holding `Tenant ID`, `Application ID`, `Client Secret`,
   directly readable by the Delinea bootstrap credential with no checkout or approval
   workflow. Its id goes in the module's `GraphDelineaSecretId` config field.

## Catalog descriptor

```
Id = "CloudPasswordReset"
DisplayName = "Cloud Password Reset"
Description = "Reset the password of an Entra ID cloud-only account that has no on-premises Active Directory object. The new password is emailed to the account owner."
Route = "cloud-password-reset"
IconCss = "bi bi-key-fill"
Category = "Identity & Access"
SortOrder = 760                  (immediately after MfaReset at 750)
EnabledByDefault = false
IsSystemModule = false
Version = "1.0.0"
MainPermission = new("Access", "CloudPasswordReset", <description>, FailClosed: true)
GranularPermissions = [
    new("Reveal", "CloudPasswordResetReveal", <description>, FailClosed: true)
]
ConfigFields = [
    new("GraphDelineaSecretId", "Graph Delinea Secret ID", <description>, Required: true),
    new("ValidateTickets", "Validate ServiceNow tickets", <description>, Required: false,
        DefaultValue: "false", FieldType: ConfigFieldType.Boolean),
]
```

`ModulePermission.Description` is required with no default and is rendered to operators on the
Module Config Access tab (`Modules/ModulePermission.cs:3-8`, owner ruling 2026-09-02); a
catalog tripwire enforces non-blank.

## Slices

Each slice compiles and passes `dotnet test` on its own commit. Service first:
`ModuleCatalogTests.Catalog_RoutesHaveMatchingPagesAndPolicies` asserts every descriptor has a
matching page, so a descriptor-only commit fails the suite -- a mistake this repo has already
made twice (`docs/RiskyUsersModule-Plan.md`, Revision 2026-09-01).

- **S0 -- the survey. Gates everything after it.**
  `tools/Get-CloudAccountOwnerCoverage.ps1` plus Pester coverage of the candidate-derivation
  function in `tests/ps/`. Read-only. Owner reviews the hit rate and rules before S1 starts.
- **S1 -- owner resolution in C#.** `Services/CloudAccountOwnerResolver.cs`: candidate
  derivation, `ValidateExists` calls, the aggregation table above, name corroboration, and a
  status-bearing result distinguishing Resolved / Unresolved / Ambiguous / Unavailable. Pure
  logic over a seamable `ADDirectorySearchService`; tests for every row of that table,
  including the two-candidates-one-user case and the `Unavailable`-is-not-absence case.
  **The three missing AD fields land here.** `GivenName` and `Surname` (corroboration) and
  `Enabled` (the leaver rule) are added to the User branch of `ValidationProperties`
  (`ADDirectorySearchService.cs:407-414`) and to `ADSearchResult` as optional parameters with
  null defaults, so existing construction sites are unaffected (the pattern `ObjectSid`
  already uses, `:819-821`). A test must assert the User branch actually requests all three:
  without it, corroboration passes against a mock and silently corroborates nothing against a
  real directory.
- **S2 -- the password generator.** `Services/PasswordGenerator.cs` plus the embedded word
  list: the algorithm in **The generated password**, implemented in C# from the method, not
  ported from the Rust. Standalone and pure apart from the CSPRNG, so it is testable on its
  own and reusable if another module ever needs one. Tests: length always in 18-32; word count
  always 2-6; every output carries a digit, a separator symbol and mixed case; measured entropy
  always at least 60 bits; no two adjacent words share a capitalisation style; padding lands
  outside the tail often enough to prove it is distributed; 100 failed attempts refuse rather
  than emit anything; the same seed is *not* required to reproduce (there is no seeding path);
  and a source-text assertion that `System.Random` appears nowhere in the file. Statistical
  smoke test over a few thousand draws: no duplicates, and every character class present.
  **The word list carries its provenance in the same commit**: where the 7,771 words came
  from, under what licence, and a recorded count and length histogram, so a later edit that
  changes the pool is visible as a diff to a stated number rather than a silent entropy cut.
  The list is copied from `D:\source\pwgen\wordlist.txt`; that repo's licence must be checked
  and named before the copy lands, and if it does not permit redistribution the list is
  regenerated from a public source (EFF long list or similar) and the histogram re-measured.
- **S3 -- service, DI.** `Services/CloudPasswordResetService.cs`: Delinea/Graph bootstrap and
  `IsAvailable` (the `MfaResetService.cs:20-46` shape), target resolve, the synced and guest
  refusals, the display-only role read, the `ITicketValidator` gate, the generated password
  from S2, and the PATCH returning a status-bearing result. No descriptor, no page. Tests for
  every refusal path and for Known Failure Class 3: a failed Graph read must never read as
  "not synced" or "no roles". **The Entra allowed-character check lands here**: confirm
  `!@#$%&*?+=` and `-` against Microsoft's published password policy before the first live
  call, and record what was found.
- **S4 -- the email helper.** `EmailService.SendCloudPasswordResetAsync`, `virtual`, returning
  whether it sent. Base app version bump lands here. Tests including the
  `UserNotificationsEnabled`-off case returning false.
- **S5 -- descriptor and read-only page.** Catalog entry with both permissions, and
  `Components/Pages/CloudPasswordReset.razor` with search plus a preflight panel: resolved
  identity, cloud-only yes/no, roles held, protection status, and **who the password would go
  to, by display name only**. No write path. Catalog tests.
- **S6 -- the write.** The server-side authorization re-checks (both permissions), the full
  protection flow including the unresolved branch with a real `EntraObjectId`, the ticket
  gate, the pre-write delivery gates, the PATCH, `400` surfaced as a policy rejection, the
  send, the fail-closed handling of a send failure (password discarded, nothing displayed,
  `CloudPasswordReset_DeliveryFailed` audited), `LogModuleAction` for each outcome with the
  serviced note in `extra`, the administrator email, and the `ModuleConfig.razor` servicer
  opt-in entry **in this same commit**.
- **S7 -- records.** README section, plan status and traceability, `.agents/state.md`,
  `.agents/token-log.md`.

Module version stays `1.0.0` across all slices; the base app version bumps once, in S4.

## Verification

Automated, per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` -- S0 adds a `.ps1`,
  so unlike earlier drafts this stream **is** in the PowerShell gate.
- Every new test mutation-probed: revert the guard, confirm the specific test fails, restore,
  touch the file so MSBuild rebuilds, confirm green.

Manual, needing a deployed instance and the app registration -- none run at implementation
time:

1. A cloud-only account whose owner resolves resets; the owner receives the password; the
   operator's screen shows no password and no address.
2. Sign-in with the new password prompts a change.
3. A **synced** account is refused at preflight, naming the on-premises path, with no Graph
   write attempted.
4. A guest account is refused.
5. An account whose owner does not resolve is refused for a main-permission-only operator, and
   proceeds with an on-screen reveal for a reveal-permission operator. Both audited, the
   second distinctly.
6. An ambiguous derivation is refused for **both** tiers.
7. With `Email:NotifyUsersOnPermissionGrant` off, an email-path reset is refused before the
   write, naming the switch -- the account's password is unchanged afterwards.
8. A protected principal with an Exchange recipient is refused and the refusal is audited; an
   authorised servicer proceeds and the success audit carries `protectedPrincipalServiced` in
   `extra`.
9. A policy-violating password returns a stated policy rejection, not a generic failure, and
   no success is reported.
10. The audit events and the administrator email contain **no password**; the owner email
    contains it and goes to exactly one address.
11. With SMTP unreachable, a reset that passed its pre-write gates reports "changed but not
    delivered", shows no password to either tier, and audits
    `CloudPasswordReset_DeliveryFailed`. Running it again then succeeds.
12. With `ValidateTickets` on, a bad ticket refuses before any Graph call; with ServiceNow
    dormant, the switch refuses rather than passing everything through.
13. A run of generated passwords is accepted by the live Entra policy -- specifically that
    every character in `!@#$%&*?+=` and the hyphen survives a real PATCH. Any character the
    tenant rejects is dropped from the separator set and the entropy figures recomputed, in
    S3, before the module goes anywhere near production.
14. The tenant-wide reach is confirmed against a disposable account holding an admin role: the
    PATCH succeeds. Learn documents no app-only carve-out for
    `User-PasswordProfile.ReadWrite.All`, and this repo verifies rather than trusts.

## Acceptance criteria

- AC1 A target with `onPremisesSyncEnabled` true, or whose sync status could not be read, is
  refused.
- AC2 The reset uses `PATCH /users/{id}` `passwordProfile`; `resetPassword` is never called.
- AC3 Owner resolution uses `ValidateExists`, never `Search`; `Unavailable` and `Ambiguous`
  refuse for every tier; two candidates resolving to one user count as one match.
- AC4 The destination address is never accepted, suggested, or displayed as editable anywhere
  in the module; the preflight panel shows the owner by display name only.
- AC5 An operator without `CloudPasswordResetReveal` never sees the password, on any path. A
  post-write send failure discards it rather than displaying it.
- AC6 `UserNotificationsEnabled` false refuses an email-path reset **before** the PATCH.
- AC7 The password appears in no audit event, administrator email, log or trace -- enforced by
  a source-text test, not by inspection.
- AC8 Protection runs via `ResolveWithExchangeFallbackAsync` with both branches implemented,
  fails closed on `Unavailable` / `Ambiguous` / `CheckFailed`, and the unresolved branch passes
  the Graph object id as `EntraObjectId`.
- AC9 `"CloudPasswordReset"` is present in `ModulesWithProtectedPrincipalServicing`, added in
  the same commit as its `NoteFor` call.
- AC10 A Graph or AD read failure is never interpreted as a permissive answer.
- AC11 A `400` from the PATCH is reported as a password-policy rejection and never as success.
- AC12 Every gate refusal, every reveal and every delivery failure is audited; a serviced
  success carries its note in `extra`, not `errorDetail`.
- AC13 The password is generated by the app. No operator-supplied password is accepted at any
  layer -- there is no field for one in the page, the service signature, or the request model.
- AC14 Every generated password is 18-32 characters, built from 2-6 word-list words, and
  carries at least one digit, one separator symbol and both letter cases. No two adjacent words
  share a capitalisation style.
- AC15 Every generated password measures at least 60 bits of entropy under the effective-pool
  calculation. When 100 attempts fail to reach it, the generator **refuses**; it never returns
  a weaker password and never widens its own parameters to succeed.
- AC16 The generator draws only from `RandomNumberGenerator`. `System.Random` appears nowhere
  in the generator's source -- enforced by a source-text test. The word list is an embedded
  resource, and the parameters (length range, word count, separators, entropy floor, attempt
  cap) are constants, not configuration.

## Known Failure Classes checked

1. **Side-effect ordering** -- the success audit and the emails sit on the post-write path and
   are unreachable when the PATCH throws; refusal audits sit on refusal paths only. A send
   failure after a successful PATCH is its own audited outcome and must never be reported as
   either a plain success or a plain failure: the password did change.
2. **Success aggregation** -- not applicable: one target per operation, by design. If bulk is
   ever added this becomes the dominant risk, and the delivery model makes it worse, not
   better.
3. **Fail-closed authorization** -- section access, both server-side re-checks, the ticket
   gate, owner resolution and protection all deny on failure rather than defaulting
   permissive. The role read is not in this list: it is display-only and gates nothing.
4. **Stale references** -- every file and line cited in this plan was read on 2026-09-10.

## Review log

`openreview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, fallback) over
c493b2a..7c47c3c: Acceptable with changes` -- 2026-09-10, three material changes.

| # | Finding | Disposition |
|---|---|---|
| 1 | **Self-Owned Cloud Account Gap** -- an operator whose own on-prem account is the derived owner receives the password under the main permission. | **Declined**, owner challenge 2026-09-10. No escalation: the operator already holds that account. Record: `.agents/review/cpr-1.contested.md`. The self-reset section now carries the reasoning. |
| 2 | **Ungated Delivery-Failure Reveal** -- displaying the password on a post-write send failure contradicts the no-visibility model and AC7. | **Upheld and fixed.** Owner ruling 2026-09-10: *"if the send itself fails, then fail closed."* The password is discarded, not shown, to any tier; `CloudPasswordReset_DeliveryFailed` is audited; downstream (post-handoff) delivery is out of scope. |
| 3 | **Owner Corroboration Lacks Data Plumbing** -- `GivenName`/`Surname` are not in `ValidationProperties` or `ADSearchResult`. | **Upheld and fixed.** Verified true. Three fields, not two: `Enabled` was already named. Both the derivation section and S1 now require them. |

Two owner rulings the same day are folded in above and recorded in `.agents/decisions.md`.
