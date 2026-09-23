# Cloud Password Reset Module (Entra ID cloud-only accounts)

Status: **Implemented 2026-09-23, unproven against the live service.** Every slice is built,
tested and reviewed; nothing in it has ever run against a real tenant, because the app
registration and the Delinea record do not exist yet. See **External prerequisites** and the
manual acceptance checklist - none of it has been run.

This revision does one thing: **the destination is derived from the target account's `employeeId`
instead of being typed by the operator**, and the force-change-at-next-sign-in checkbox now
defaults to on. Both are owner instructions that post-date the sixth revision, and they reverse
decisions that revision recorded. The sections they touch are rewritten rather than annotated -
git history holds what they said before.

The two instructions, in the owner's words:

- **2026-09-22:** *"Update O365 password change module to match on EmployeeID and use that to
  determine the target mailbox to send the new password to."* The sixth revision had the operator
  typing the address, because a survey found `employeeId` populated on 0 of 172 cloud accounts and
  stamping them was refused. The owner has since stamped the in-scope accounts, so the reason is
  gone. See **Where the password goes**.
- **2026-09-23:** *"now that we're sending passwords over email correctly, we need to DEFAULT to
  force password change so email breaches and lazy users don't cause massive security incidents."*
  Still a per-reset checkbox the operator can clear; only the default moved. See the
  force-change discussion under **The Graph surface**.

**What is built and where:** the generator (`c219a27`), the service and the forest-wide employeeId
lookup (`56451bd`), the owner's email (`e1b786a`), the descriptor and preflight page (`8a411c1`),
the write path with protection, audit and notification (`3b29e5f`), and the records (`dad322e`).

**D4 is settled: both permissions ship.** The owner's answers of 2026-09-23 presuppose the reveal
permission and extend it - a reveal holder overrides all five destination refusals, including an
unreachable directory (`.agents/decisions.md`). With the destination derived rather than typed,
reveal is the only route by which an operator can learn a generated password, so it is a real
boundary rather than the decorative control the sixth revision judged it to be.

**Five review findings, all admitted and fixed**, records in `.agents/review/findings/`:
`cpr-4` (HIGH, a missing sync property admitted a synced account), `cpr-5` (HIGH, a failed SMTP
disconnect reported a delivered password as undelivered), `cpr-6` (MEDIUM, a failed role read
rendered as "None active"), `cpr-7` (LOW, an undefined sidebar icon class).
**Three of these are the same mistake in different places** - an unanswered question read as a
negative answer - which is why that rule is stated in this plan rather than left to each site.

## Revision history

Only the reversals that still govern are kept. Superseded design text is not retained as
commentary.

- **2026-09-23 (seventh).** Destination derived from `employeeId`; force-change defaults on.
- **2026-09-22.** Hold lifted (`.agents/decisions.md`).
- **2026-09-14 (sixth).** Deleted the entire owner-derivation design after it measured 46.5%
  coverage, and moved the destination to an operator-typed field. **The seventh revision reverses
  the destination half of this.** What survives from it: no stored owner map, and the deletion of
  the corroboration and leaver rules, which are not coming back - matching on a stamped
  `employeeId` is a different mechanism from guessing at a name.
- **2026-09-10 (fourth).** Two reversals that still bind: the first draft gated on "target holds
  an admin role", which fenced nothing because nearly every target is an admin; and the second
  draft displayed the password in the UI, which settled to emailed-and-not-displayed.

New module `CloudPasswordReset`. **The base app version bumps** -- this stream adds a public
method to `Services/EmailService.cs`, which is shared infrastructure (Constitution, Deployment
And Versioning). The "adding a module does not bump the base version" exception does not apply,
because this is not only a module.

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
   module at all -- the `idm-3` decorative-control class. The second permission this plan
   carries is keyed on whether the operator may *see* the password; with the destination derived
   again, that distinction fences something real, which is what reopens D4.
2. **Many targets have no mailbox of their own.** An Entra-only admin or automation identity
   commonly has no Exchange recipient. The password therefore cannot be sent to the account
   being reset; it must go to the human who owns that account, at their corporate mailbox.
3. **The account now carries who owns it.** The owner has stamped `employeeId` onto the in-scope
   cloud accounts, which is what makes the destination derivable. Five earlier revisions tried to
   infer the owner from names and reached 46.5%; that approach is dead and is not what this is.
   Matching a stamped identifier is an exact lookup, not a guess. See **Where the password goes**.

## Why delivery, not visibility

The password is mailed rather than shown. Owner ruling 2026-09-10: *"those passwords should be
emailed to the owner of the cloud account's @analog.com email address, not displayed in the
UI."*

**The original justification holds again.** An operator who never sees the password cannot take
over the account they reset, and with the destination derived rather than typed they cannot
redirect it either. That makes the delivery model a real control, not a convenience:

1. The operator does not learn the credential of any account they reset.
2. The password reaches the owner without a second manual step, which is the point of the
   module -- the current process is L2 relaying it by phone.
3. It gives the audit record a concrete destination to carry, which an on-screen reveal does not.

The sixth revision had to downgrade point 1 to "keeps it off the operator's screen in the ordinary
case", because a typed address meant the operator could make themselves the recipient. That
caveat is gone. See **Where the password goes**.

## Where the password goes

**The module derives the destination from the target account's `employeeId`.** The operator does
not type an address and cannot choose one. Owner instruction, queue item 10, 2026-09-22: *"Update
O365 password change module to match on EmployeeID and use that to determine the target mailbox to
send the new password to."*

At each reset, for the one account the operator named:

1. Read `employeeId` from the target cloud account in Entra.
2. Find the directory user carrying that same `employeeId`.
3. Send the new password to that person's mailbox.

**One lookup, for one named account, at the moment of the reset.** The module never enumerates a
directory and never reads a population - see `.agents/decisions.md` 2026-09-22, "No tooling
enumerates the directory". The lookup is scoped to the subject of the operation, which is the
shape every other directory read in this app already takes.

**Every step fails closed for an operator holding only the reset permission, and a failure refuses
rather than falling back.** There is no operator-typed fallback: an address the module cannot
derive is one it cannot verify, and the whole point of deriving is that nobody chooses where an
admin credential is sent. The refusals:

| Condition | Refusal |
|---|---|
| Target has no `employeeId` | `DestinationNoEmployeeId` |
| No directory user carries that `employeeId` | `DestinationNoMatch` |
| More than one user carries it | `DestinationAmbiguous` |
| The matched user has no mailbox | `DestinationNoMailbox` |
| The lookup itself failed | `DestinationLookupFailed` |

**The reveal permission overrides ALL FIVE, including the directory being unreachable.** Owner
ruling 2026-09-23. The operator sees the password once on screen, nothing is mailed, and the event
is audited as a reveal carrying the refusal it overrode.

Excluding the unreachable-directory case was considered and rejected on the owner's challenge,
and the reasoning is worth keeping because it is the general shape of a bad control: **it would
have protected nothing and cost availability during an incident.** A reveal holder can already
obtain a password on the other four paths simply by choosing an account with no `employeeId`, so
denying them during an outage stops nobody determined. Meanwhile an unreachable directory is
plausibly the incident itself, and that is exactly when resetting an admin account quickly
matters. A gate that the motivated can walk around and the legitimate cannot is not a fence.

An operator WITHOUT the reveal permission is still refused on all five: they cannot be shown the
password and there is nowhere to mail it.

`DestinationAmbiguous` is the one worth stating twice: two people sharing an `employeeId` means
the module cannot say whose account it is looking at, and mailing an admin password to a guess is
worse than not resetting at all.

### Why this is a fence and the typed address was not

Until 2026-09-22 the operator typed the destination. That was forced on the design by a survey
which found `employeeId` populated on **0 of 172** cloud accounts, with the owner refusing to
stamp them (`.agents/decisions.md` 2026-09-11). The owner has since stamped the in-scope accounts,
which removes the reason.

It matters because the two designs are not equivalent:

- **Typed:** an operator can address any account's password to themselves. Nothing prevents it;
  the audit record and the administrator alert catch it afterwards. Detection, not prevention.
- **Derived:** the operator cannot choose the destination at all. An operator who wants another
  account's password has to change the directory to get it, which is a separate privileged act
  against a separate system, and is itself audited there.

So the preventive control the fifth revision traded away is back. **Do not read the superseded
sentences in git history - or any sentence in this plan that survived from them - as current.**

The audit record and the administrator alert still carry the destination, and are still required.
They are no longer the only thing standing between an operator and a credential.

### What is not built

There is no owner **map**. The 2026-09-10 ruling against storing one stands unchanged -- *"cannot
store it ... we're not going to create an instantly stale map"* -- and deriving at each reset is
what honours it. Nothing is cached, nothing is seeded, and no CSV is read: the owner's
employeeId true-up files are how the directory got populated and are **not part of this module**
(owner, 2026-09-23). The module reads the directory, live, one account at a time.

The module still does not **validate** the derived address against anything else. It is whatever
the directory says, and if the directory is wrong the mail goes to the wrong person. That is a
directory-accuracy problem with a directory-accuracy fix, not something this module second-guesses.
## Scope

IN: cloud-only Entra ID user accounts (`onPremisesSyncEnabled` not true), including
role-holding admin and tactical accounts. One target per operation.

OUT: synced accounts (mastered on-premises -- L2 already resets those); guest / external
(`userType` `Guest`); MFA methods (that is `MfaReset`); enabling, unblocking or unlocking an
account; any `passwordProfile`-adjacent property other than the password itself; bulk reset;
any stored owner mapping; any operator input that influences where the password is sent.

**Reading a directory for a population is also OUT.** The destination lookup is one query for
the one account being reset, at the moment it is reset. Nothing enumerates, surveys or
pre-computes (`.agents/decisions.md` 2026-09-22).

**Self-reset is structurally impossible and needs no guard.** The app authenticates operators
against on-premises AD; every target here is cloud-only by definition. The two populations
cannot intersect, so an operator cannot be their own target. Recorded explicitly because a
reviewer reading only the Graph surface will otherwise raise it (it was raised once already).

**An operator cannot direct the password to themselves.** They supply no address; the module
derives it. Codex raised the narrower ancestor of this over `7c47c3c` (finding cpr-1,
`.agents/review/cpr-1.contested.md`), and the sixth revision had to accept the general case as
unguarded because the address was typed. It is guarded again. The residual case is an operator
who edits the directory to redirect a future reset - a separate privileged act against a separate
system, audited there, and not something this module can or should police.

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
| Resolve target | `GET /users/{upn}?$select=id,displayName,userPrincipalName,accountEnabled,onPremisesSyncEnabled,userType,employeeId` | `User.Read.All` | 200 |
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

   **`forceChangePasswordNextSignIn` is an operator choice per reset, defaulting to off.**
   Owner ruling 2026-09-10, in two parts: forcing it always is wrong -- *"that disallows
   signin in too many instances"* -- and hard-coding it off is equally wrong, because *"the
   point to this whole app is to provide L2 with access to a subset of admin tools in an
   audited and secure interface without giving them actual elevated credentials. change on
   login is an OPTION."* The Entra portal offers this checkbox to an admin; withholding it
   here would make the app less capable than the console it replaces, which is the opposite
   of the app's reason to exist.

   So: a single checkbox on the reset panel, **CHECKED by default**. Owner ruling 2026-09-23,
   superseding the default-off position above: *"now that we're sending passwords over email
   correctly, we need to DEFAULT to force password change so email breaches and lazy users don't
   cause massive security incidents."* Mailing the password puts it in a mailbox, where it sits
   until somebody acts. Forcing a change at next sign-in closes that window: a later mailbox
   compromise, or an owner who never gets round to changing it, stops being a live credential.
   The generated password becomes a one-time handover rather than the account's standing password.

   The earlier failure mode has not gone away and is not being dismissed - some accounts in this
   population sign in by routes that cannot service a change-password prompt, and ticking the box
   for one of those returns an account nobody can sign into. **That is why it stays a checkbox the
   operator can clear per reset, and why the help text next to it must name that risk.** The
   default moved; the choice did not.

   Three things follow and are requirements, not commentary:

   - **The choice is audited.** The `CloudPasswordReset_Execute` event records which way the
     flag went, in `extra`. It changes what the account does afterwards, so it belongs in the
     record.
   - **The owner email matches the choice.** Checked: the mail says a change will be required
     at next sign-in. Unchecked: it says the password works as-is and to change it when
     convenient. It never promises a prompt that will not appear.
   - **The generated password has to stand alone regardless.** Whenever the operator CLEARS the
     box - no longer the default, but still a real path for an account that cannot service a
     change prompt - the generated password IS the account's standing password until someone
     changes it. That is what keeps the generator's 18-32 characters and 60-bit floor
     load-bearing rather than decorative.
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

Fail-closed corollary, in **three** states rather than two. Earlier revisions said "absent a
definite `onPremisesSyncEnabled: false`, refuse", which cannot be implemented: **Graph does not
send `false` for this property.** It sends `true` for a synced account and `null` for one that is
not, so requiring a literal `false` would refuse this module's entire population. What the rule
means against the real API:

| Response | State | Action |
|---|---|---|
| present, `true` | Synced | Refuse, naming the on-premises path |
| present, `null` or `false` | Cloud-only | In scope |
| absent, or any other JSON kind | **Unknown** | **Refuse** as a failed read |

The third row is the rule. The property is named in the `$select`, so its absence means the
projection did not happen - not that the account is cloud-only. An unanswered sync question is
never a permissive answer (Known Failure Class 3). The first implementation collapsed Unknown
into cloud-only and codex caught it: review finding `cpr-4`, HIGH.

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

- **`CloudPasswordReset`** -- reset the account and mail the password to the owner the module
  derived. The password is not shown and the operator does not choose where it goes. This is the
  L2 permission.
- **`CloudPasswordResetReveal`** -- additionally see the password once on screen instead of
  mailing it, for targets where no mailbox can receive it.

**D4 is open, and the derivation changes which way it should go.** The reveal permission was
scarce because an operator holding only the main permission had no route to the password. When
the destination became typeable that stopped being true and the permission became decorative.
Deriving the destination makes it true again: the main permission now genuinely cannot yield a
password to the operator, and the reveal permission is the only thing that can. So it is a real
boundary rather than the `idm-3` decorative-control class -- which is an argument for keeping it,
where the sixth revision's analysis was an argument for dropping it. Put D4 to the owner before
the page is built; the descriptor below carries the granular entry until then.

The fences that bind, in evaluation order:

1. **Section access** -- who holds `CloudPasswordReset` at all. Fail-closed
   (`ModulePermission.FailClosed: true`, `Modules/ModulePermission.cs:3`).
2. **Server-side re-check immediately before the write** --
   `AuthorizationService.AuthorizeAsync(authState.User, "CloudPasswordReset")`, and separately
   `"CloudPasswordResetReveal"` before any reveal, mirroring
   `Components/Pages/MfaReset.razor:250-258`. The `@attribute [Authorize(Policy = ...)]` on
   the page is navigation control, not the gate (Constitution: UI hiding is not security).
3. **A destination that derived cleanly** - exactly one directory user carrying the target's employee ID, with a mailbox -- or the reveal permission.
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

1. The destination derived cleanly: the target carries an `employeeId`, it matched exactly one
   directory user, and that user has a mailbox. Any other outcome refuses with its own reason
   from the table in **Where the password goes**. The alternative is the reveal path, which needs
   the reveal permission.
2. `EmailService.UserNotificationsEnabled` (`Services/EmailService.cs:438`) is **true** when
   the run depends on email. This is a deployment-wide switch that outranks anything the
   module wants, and its own remark warns that a caller which cannot say so on screen has
   built a decorative control (`:431-437`). Here it is worse than decorative: a silent
   suppression would lock the owner out of their account. If it is off, the reset is refused
   before the write, naming the switch.

**The derived address is taken as the directory gives it.** The module does not second-guess it
and has nothing to compare it against. If the directory holds the wrong mailbox for an
`employeeId`, the password goes to the wrong person, and the fix is in the directory. What the
module does guarantee is that the address came from the directory rather than from whoever is
running the reset -- the typo and self-addressing risks the sixth revision had to accept are gone
with the text box.

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

The body names the account that was reset, the ticket, and the password, and does **not** name
the operator. Its closing line is **conditional on the change-at-next-sign-in checkbox**: when
the operator ticked it, the mail says a change will be required at next sign-in; when they did
not -- the default -- it says the password works as-is and to change it when convenient. The
method therefore takes the flag as a parameter. It must never promise a prompt that will not
appear: an owner expecting one and not getting it raises a ticket, and an owner not expecting
one and hitting it on a client that cannot service the change is locked out.

## Audit and notification

- **No new `AuditService` method.** Use the generic `Audit.LogModuleAction(performedBy, ip,
  action, category, target, success, ticket, errorDetail, extra)`
  (`Services/AuditService.cs:199`), `category: "CloudPasswordReset"`, actions
  `CloudPasswordReset_Preview`, `CloudPasswordReset_Execute`,
  `CloudPasswordReset_Revealed` and `CloudPasswordReset_DeliveryFailed`. **The exact field set
  every one of these carries is specified in Audit fields, and Splunk, below** -- these events
  are forwarded to a SIEM, so the keys are an interface, not a convenience.
- **The serviced note rides `extra`, never `errorDetail`.** `LogModuleAction` writes
  `["error"] = success ? null : errorDetail`, so a detail passed as `errorDetail` on a success
  is silently discarded -- the failure that lost an authorised-servicer record once already
  (`AuditService.cs:22-37`). Pass `ProtectedPrincipalServicing.Extra(note)`.
- **Every reveal is its own audited event, and it records WHICH refusal it overrode.** A reveal
  is the one path where an operator learns a credential; it is never folded into the ordinary
  success record. `refusalReason` stays populated on a reveal, carrying the derivation failure
  the operator pushed past. "Revealed because the directory was unreachable" and "revealed
  because the account has no owner" are different facts, and after an incident the first is the
  one somebody will search for.
- Every gate refusal is its own audited event carrying its reason.
- **Administrator email** on every real attempt via `Email.SendAdminNotificationAsync`
  (`Services/EmailService.cs:39`, `virtual` and test-seamable), armed only after the ticket
  and authorization pre-gates pass -- the `notifyAdmins` pattern at
  `MfaReset.razor:234-260`. It names the target, the operator, the ticket, **the destination
  address the password was sent to**, and whether the password was revealed instead. It never
  contains the password. The destination is a required element of this mail, not an optional
  detail: owner ruling 2026-09-11, and it is one of the two places a misdirected reset becomes
  visible without anyone running a query.
- **The new password appears in exactly two places: the PATCH body and the owner's email.**
  Not the audit event, not `extra`, not the administrator email, not the operation trace, not
  a log line (Constitution, Credential Isolation: *"Never log secret values ... passwords
  ..."*). A source-text test asserts that no audit or admin-email call site in this module
  receives the password variable.

## Diagnostic logging, and the levels it uses

Owner instruction 2026-09-23: *"logging should capture as much as possible, in line with the
different event log logging levels."*

This is the third record the module writes and it is not the same as the other two. The **audit
event** answers who did what to whom. The **operation trace** is the multi-step transcript. This
is `ILogger`, the app log, and its job is to let somebody reconstruct a reset that behaved oddly
without reproducing it. Serilog reads its minimum level from configuration (`Program.cs:23-28`),
so writing at the right level is what makes verbosity a deployment choice rather than a code
change - a module that logs everything at `Information` cannot be turned down, and one that logs
everything at `Debug` cannot be turned up.

**Log generously. The limit is sensitivity, not volume.**

| Level | What goes here |
|---|---|
| `Debug` | Every step, with its inputs and outcome: target resolved and its object id, `onPremisesSyncEnabled` as read, `userType`, roles returned, the `employeeId` read off the account, the directory query issued and how many users matched, the mailbox resolved, ticket validation outcome, protection check outcome, how many generator attempts were needed. This is the level that answers "why did it choose that mailbox". |
| `Information` | The milestones a healthy reset passes: reset requested for target X by operator Y, destination derived, PATCH accepted, mail handed to SMTP, reveal used. One line each, no payloads. |
| `Warning` | Every refusal, with its reason - all five derivation failures, synced account, guest, ticket rejected, notifications disabled, protected principal. A refusal is the module working correctly, so it is not an error; it is worth seeing without turning on `Debug`. Also: a reveal, because a credential reached a human screen. |
| `Error` | Something the module depends on failed: Graph read or PATCH failed, the directory was unreachable, SMTP handoff failed, the generator refused after 100 attempts, the audit write threw, `400` from the PATCH (a tenant policy rejection - the module's request was wrong). |
| `Critical` | **The post-write send failure.** The password was changed and could not be delivered, so an account exists that nobody can sign into and nobody knows the password for. It self-heals on a retry, but it is the one state in this module where a human should be told without going looking. |

**What never appears at any level, including `Debug`:**

- The generated password, and anything derived from it - its length, its entropy, its word count,
  a hash, a prefix, a masked form. The Constitution's Credential Isolation rule is absolute here
  and `Debug` is not an exemption, because `Debug` is exactly where somebody would put it while
  diagnosing (AC7).
- The Delinea response, the Graph token, and any raw auth body.
- Raw exception text from the credential or token path. Log the type and the operation that
  failed.

**Two things must be distinguishable in the log, because they were confused in this repo before:**
a directory query that returned no match, and a directory query that failed. Both leave the reset
refused, and logging them the same way turns an outage into what reads as a clean negative result.
`DestinationNoMatch` is a `Warning`; `DestinationLookupFailed` is an `Error`. The level is the
distinction.

**The operation trace still carries the step transcript** (`OperationTraceService`, the pattern in
the Developer Guide). This section does not replace it and does not license putting trace detail
into the app log instead. They have different readers: the trace is per-operation and correlated
by `operationId`; the log is chronological across the whole app.

## Audit fields, and Splunk

**These events are forwarded to Splunk** (owner, 2026-09-10: *"these logs are going to splunk,
so they need to be explicit and clear"*). That makes the field names a published interface, not
an implementation detail: somebody will build a dashboard or an alert on them, and a rename or
a re-typed value silently breaks it. This module's events are therefore specified here rather
than left to whatever reads well in a log viewer.

`AuditService` already emits one JSON object per event (`AuditService.cs:392-411`), so the
transport needs nothing new. `MergeExtra` (`:38-45`) copies every key straight in, nulls
included, so an explicit null survives to the writer.

**The rules, all enforceable:**

1. **One fact per field.** No packed strings. The `wipeFlags` field in IntuneDevices
   (`Components/Pages/IntuneDevices.razor:1414`) crams five settings into one
   `key=value; key=value` sentence -- readable to a human, but every Splunk query against it
   needs a field extraction to get at any one of them. This module does not copy that shape.
   It is the reason the rule is written down.
2. **Booleans are JSON booleans.** Not `"Yes"`, not `"true"`, not `"(set)"`.
3. **Enumerated fields draw from a closed list, stated below.** Never a free-text sentence, and
   never an operator- or upstream-supplied string. A value not on the list is a bug.
4. **Every field appears on every event of its action, carrying the string `"n/a"` where it does
   not apply** - an absent field and a present one look different to a search, and "missing" is
   not an answer anyone can act on.
   **CONFIRMED IN S6, and the answer was the fallback:** the writer drops nulls twice over.
   `JsonlLogService.WriteToFile` filters every null-valued key before serializing, and its
   serializer options carry `JsonIgnoreCondition.WhenWritingNull` as well. An explicit null
   therefore reaches Splunk as an absent field, which is the exact outcome this rule exists to
   prevent (review finding `cpr-11`). So the sentinel is in force. It is applied in this module
   rather than by changing the shared writer, whose behaviour every other module's logs inherit
   and which is not this stream's to change.
5. **Names are frozen once shipped.** Changing one is a breaking change to somebody's dashboard
   and needs the same care as a schema migration.
6. **No password, and nothing derived from one.** Not its length, not its entropy, not its word
   count, not a hash. AC7 already says this; it is repeated here because a field table is
   exactly where such a thing gets added by a well-meaning later edit.

**The fields.** Top-level keys (`ts`, `user`, `ip`, `action`, `category`, `result`, `target`,
`ticket`, `error`, `eventType`, `operationId`) come from `LogModuleAction` unchanged;
`category` is always `CloudPasswordReset` and `target` is always the cloud account's UPN.
These ride in `extra`:

| Field | Type | Values |
|---|---|---|
| `targetObjectId` | string | The Entra object id (GUID). Stable across renames, unlike the UPN. |
| `targetCloudOnly` | bool | Always `true` on a successful reset; `false` on a synced-account refusal. |
| `destinationAddress` | string | The address the module derived, recorded lowercased, or `"n/a"`, whether or not the send then succeeded; null on the reveal path and on refusals that happened before the derivation ran or that the derivation itself caused. **This is the field that answers "where did the password actually go".** |
| `destinationEmployeeId` | string | The `employeeId` the address was derived FROM, as read off the target account. Null when the target had none, or when the refusal happened before it was read. Present on successes and on every derivation refusal: without it, `destinationAddress` says where the password went but nothing says why there, and a wrong destination cannot be traced back to the directory record that caused it. |
| `forceChangePasswordNextSignIn` | bool | Exactly what went in the PATCH body, under the Graph property's own name so the audit and the API cannot drift apart. |
| `passwordDelivery` | string | One of `Sent`, `SendFailed`, `Revealed`, `NotAttempted`, `Indeterminate`. **`Indeterminate` means the write was ISSUED and its outcome is unknown** - a transport failure after the request left, where Graph may have applied it. It is not a refusal and must never be recorded as `NotAttempted`, which would state that no change was made (finding `cpr-8`). |
| `revealUsed` | bool | True only on the reveal path. Redundant against `passwordDelivery` by design: an alert on a single boolean is harder to get wrong than one on a string. |
| `refusalReason` | string | `"n/a"` on an ordinary success. On a REVEAL it carries the derivation failure the operator pushed past, not null - the reveal is a success but it overrode something, and which one matters after an incident. Otherwise one of: `SyncedAccount`, `GuestAccount`, `DestinationNoEmployeeId`, `DestinationNoMatch`, `DestinationAmbiguous`, `DestinationNoMailbox`, `DestinationLookupFailed`, `NotificationsDisabled`, `TicketInvalid`, `TicketValidatorUnavailable`, `ProtectedPrincipal`, `ProtectionCheckFailed`, `PermissionDenied`, `GraphReadFailed`, `PasswordPolicyRejected`, `GeneratorFailed`, `WriteIndeterminate`. |
| `protectedPrincipalServiced` | string | The shared helper's note (`ProtectedPrincipalServicing.Extra`), unchanged -- it is prose, and it is the one field that stays prose because the shared helper owns its shape. |

Refusal events carry the **same** field set as successes, so one search over
`category=CloudPasswordReset` returns uniform records and a missing field always means a bug
rather than a branch that did not bother.

**The "operator mailed it to themselves" alert is a Splunk query, not an app field.** `user` and
`destinationAddress` are both on every event, and correlating an operator to their own mail
address is an AD lookup Splunk already has. The module does not compute the comparison itself: it
would need the operator's mail attribute, which the app does not read today, and a false result
that actually meant "could not check" is the failure mode this field table exists to prevent.
Stated so the absence reads as a decision rather than a gap.

**Beyond this module.** Existing modules were not written against these rules and some pack
strings the way IntuneDevices does. Bringing them into line is a separate stream with its own
plan; nothing here changes them, and this section does not license a drive-by sweep.

## Owner decisions

### D1 -- SETTLED 2026-09-10: the app chooses the password

Owner ruling: app-generated, using the algorithm in `D:\source\pwgen` -- *"use the algorithm
it's using, not the code."* An operator-typed password was never compatible with this design:
the operator would know it by definition and the reveal permission would mean nothing. Spec in
**The generated password**, above.

### D2 -- SUPERSEDED 2026-09-22: the module derives the destination from `employeeId`

Settled 2026-09-11 as "the operator names the destination", because name-based derivation
measured 46.5% and the owner ruled that a partial match is not a design. Reversed by owner
instruction (queue item 10) once the in-scope accounts were stamped with `employeeId`, which
replaces inference with an exact lookup. **The operator supplies no address.** Full record in
`.agents/decisions.md` (2026-09-11 for the original, 2026-09-22 for the reversal) and in
**Where the password goes**.

### D3 -- WITHDRAWN 2026-09-11: the corroboration tolerance

Asked how tolerant the name-corroboration rule should be. There is no corroboration rule any
more. Kept as a numbered heading so the D-numbers in git history still resolve.

### D4 -- PUT, NOT ANSWERED 2026-09-14: does the reveal permission still fence anything?

**Disposition.** Put to the owner with recommendation (b); the answer was *"no. neither."*,
followed by the ruling that stopped the module. So neither option was chosen and the question
is not settled -- it lapsed with the stream. **If this plan is resumed, D4 must be put again
before S5.** Do not read the recommendation below as approved, and do not treat the reveal
permission as decided in either direction. The question and its analysis are kept intact below
because they are still correct; only their status changed.

**Context.** `CloudPasswordResetReveal` was scarce because an operator holding only the main
permission had no route to the password -- the destination was derived and they could not
influence it. Now they type it, so any operator can obtain any password by addressing it to
themselves. The reveal permission currently guards an outcome the main permission already
reaches.

**Action.** Either (a) drop the granular permission and the on-screen reveal entirely, leaving
one permission and one delivery path; or (b) keep it, accepting that it is friction and a
distinct audit signal rather than a control, and say so in its description so nobody later
mistakes it for a fence.

**Consequence.** (a) is simpler, removes a code path, and removes the temptation to treat a
decorative control as a real one -- the `idm-3` class this repo has hit three times. It costs
the ability to reset an account that genuinely has nowhere to mail to; the operator would have
to address the mail to themselves, which works and is audited, but reads worse. (b) keeps that
path clean at the cost of a permission whose description has to admit it fences nothing.

**Recommendation: (b), with an honest description.** The reveal path is the correct answer for a
target with no human owner at all, and folding it into "address it to yourself" makes the audit
record less truthful, not more. But it must stop being described as a security boundary.

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
Description = "Reset the password of an Entra ID cloud-only account that has no on-premises Active Directory object. The new password is emailed to the account owner, found from the employee ID on the account, and the reset is recorded in the audit log."
Route = "cloud-password-reset"
IconCss = "bi bi-key-fill"
Category = ModuleCategories.IdentityAndAccess   (the constant, not a string - a catalog test fails anything else)
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

- **S0 and S1 are deleted.** S0 was the owner-coverage survey; S1 was the C# owner resolver.
  Both existed to derive an owner and neither has a subject any more. The survey's tooling is
  removed from the repo in the same commit as this revision. **The three `ADSearchResult` /
  `ValidationProperties` field additions go with them** -- `GivenName`, `Surname` and `Enabled`
  were needed for corroboration and the leaver rule, and nothing else in this module reads a
  directory. That removes the shared-infrastructure change to `ADSearchResult`; the base app
  version bump now rests solely on the `EmailService` method added in S4.
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
  from S2, the change-at-next-sign-in flag taken as a REQUIRED parameter with no default at all
  (never read from config, and no service-side default either - the default belongs to the page,
  and a second one here is a second place for it to be wrong), **the destination derivation -
  read `employeeId` off the target, resolve the one directory user carrying it, take that user's
  mailbox - with each of the five failure modes returning its own refusal and none of them
  falling back to anything**, and the PATCH returning a status-bearing result. The service takes
  NO destination parameter: there is nothing for a caller to pass and therefore nothing for a
  future caller to pass wrongly. No descriptor, no page.
  Tests assert the flag reaches the request body unaltered in both states, that each derivation
  failure refuses with its own reason and performs no PATCH, and that a directory read which
  FAILS is never read as "no match" -- Known Failure Class 3, and the mistake that produced a
  confident 0% in the deleted survey tooling. Tests for
  every refusal path and for Known Failure Class 3: a failed Graph read must never read as
  "not synced" or "no roles". **The Entra allowed-character check lands here**: confirm
  `!@#$%&*?+=` and `-` against Microsoft's published password policy before the first live
  call, and record what was found.
- **S4 -- the email helper.** `EmailService.SendCloudPasswordResetAsync`, `virtual`, returning
  whether it sent, and taking the change-at-next-sign-in flag so the closing line matches what
  was actually done. Base app version bump lands here. Tests including the
  `UserNotificationsEnabled`-off case returning false, and one per flag state asserting the
  body promises a change prompt only when the flag is set.
- **S5 -- descriptor and read-only page. Blocked on D4.** Catalog entry, and
  `Components/Pages/CloudPasswordReset.razor` with search plus a preflight panel: resolved
  identity, cloud-only yes/no, roles held, protection status. Two operator inputs: the
  **ticket**; and the **change at next sign-in** checkbox, **CHECKED by default**
  (owner ruling 2026-09-23). Label: *"Force password change at next sign-in"*. Help text, owner's
  words 2026-09-23, to be used verbatim and shown when the box is CLEARED:

  > This is a security risk. You MUST walk the user through a manual reset and confirm it's been
  > reset before closing the ticket.

  It is an instruction, not an explanation: it tells the operator what they are now obliged to do.
  Do not soften it, shorten it, or replace it with a statement of consequence.

  **There is no destination field.** The preflight panel SHOWS the derived destination -- the
  `employeeId` read off the account and the mailbox it resolved to -- so the operator can see
  where the password will go before committing, and can stop if it looks wrong. It is display
  only: no text box, no picker, no override. Where the derivation refused, the panel shows which
  refusal and the reset button stays unavailable.

  No write path in this slice. Catalog tests, plus tests that the panel renders the derived
  destination and the refusal states, that no editable destination control exists anywhere on the
  page, and that the checkbox renders CHECKED on first load.
  D4 decides whether the descriptor carries one permission or two.
- **S6 -- the write.** The server-side authorization re-checks, the full protection flow
  including the unresolved branch with a real `EntraObjectId`, the ticket gate, the pre-write
  delivery gates, the PATCH, `400` surfaced as a policy rejection, the send, the fail-closed
  handling of a send failure (password discarded, nothing displayed,
  `CloudPasswordReset_DeliveryFailed` audited), `LogModuleAction` for each outcome with the
  serviced note, **the derived destination and the employee ID it came from** and the
  change-at-next-sign-in choice in `extra`, the administrator email (which states both the
  destination and the choice), and the `ModuleConfig.razor` servicer opt-in entry **in this same
  commit**. Two values are each traced end to end by a single test rather than hop by hop: the
  checkbox from page to PATCH body to both emails, and the destination from the directory lookup
  to the send, to the audit `extra`, and to the administrator email. **A refusal must still carry
  the employee ID it failed on**, so a blocked attempt is as searchable as a completed one.
- **S7 -- records.** README section, plan status and traceability, `.agents/state.md`,
  `.agents/token-log.md`.

Module version stays `1.0.0` across all slices; the base app version bumps once, in S4.

## Verification

Automated, per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` -- the S0 tooling that
  put this stream in the PowerShell gate is deleted, so from the sixth revision the stream
  ships no `.ps1` and the gate is a no-op for it. Run it anyway; the deletion itself has to
  leave the suite green.
- Every new test mutation-probed: revert the guard, confirm the specific test fails, restore,
  touch the file so MSBuild rebuilds, confirm green.

Manual, needing a deployed instance and the app registration -- none run at implementation
time:

1. A cloud-only account resets. The preflight panel shows the employee ID read off the account
   and the mailbox it resolved to; the password arrives at that mailbox; the operator's screen
   shows no password and offers no way to change the destination. The audit event and the
   administrator email both carry that address and that employee ID.
2. Both settings of the change-at-next-sign-in checkbox, on real accounts. Ticked (the
   default): sign-in prompts for a change and the new password takes. Cleared: sign-in
   succeeds with **no** change prompt, including on a path that could not service one, and
   the password stays as issued. In each case the owner email's closing line matches what
   actually happened, and the success audit records which way the flag went.
   **Run the cleared case against an account whose sign-in path cannot service a change
   prompt** - that path is the reason the checkbox still exists now the default has moved, and
   an untested escape hatch is not one.
3. A **synced** account is refused at preflight, naming the on-premises path, with no Graph
   write attempted.
4. A guest account is refused.
5. Each derivation failure refuses before any Graph write, and the account's password is
   unchanged afterwards: an account with no employee ID, one whose ID matches nobody, one whose
   ID matches two people, and one whose owner has no mailbox. Each is audited with its own
   `refusalReason` and carries the employee ID it failed on. **The two-match case is the one to
   contrive deliberately** - it is the failure that would otherwise mail an admin password to a
   guess, and it is the least likely to occur by chance during testing.
6. Subject to D4: with the reveal permission held and the reveal path chosen, the password
   shows once on screen, no mail is sent, and `CloudPasswordReset_Revealed` is audited
   distinctly with a null `destinationAddress`.
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

- AC1 A target with `onPremisesSyncEnabled` true, or whose sync status could not be read at all
  (property absent, or an unexpected JSON kind), is refused. `null` is the value Graph uses for a
  cloud-only account and is the ONE reading that admits a target; two-state parsing of this
  property is a defect, not a simplification (finding `cpr-4`).
- AC2 The reset uses `PATCH /users/{id}` `passwordProfile`; `resetPassword` is never called.
- AC3 The destination is **derived, and the operator cannot influence it**. It comes from the
  `employeeId` on the target account, resolved to exactly one directory user with a mailbox. No
  control on the page accepts an address, the service signature takes no address parameter, and a
  search of the module's source finds no path by which operator input reaches the recipient. Each
  of the five derivation failures -- no `employeeId`, no match, more than one match, no mailbox,
  lookup failed -- refuses **before** the PATCH with its own `refusalReason`, and none falls back
  to any other destination.
- AC4 The destination reaches **both** records, with the identifier it was derived from. Every
  `CloudPasswordReset` audit event on a path that got as far as the derivation carries
  `destinationAddress` lowercased and `destinationEmployeeId` -- successes, delivery failures and
  derivation refusals alike -- and the administrator alert names the same address in its body. A
  test asserts the two records carry the same address for one reset, and that a derivation
  refusal still carries the `employeeId` it failed on.
- AC4a The derivation reads **one account at a time**. No code path in this module enumerates,
  lists or filters a directory for more than the single target of the operation
  (`.agents/decisions.md` 2026-09-22). A source-text test asserts no unbounded directory query.
- AC5 An operator without `CloudPasswordResetReveal` never sees the password, on any path. A
  post-write send failure discards it rather than displaying it. (Subject to D4: if the reveal
  permission is dropped, the second sentence stands for every operator and the first is void.)
- AC6 `UserNotificationsEnabled` false refuses an email-path reset **before** the PATCH.
- AC7 The password appears in no audit event, administrator email, log or trace, **at any log
  level including `Debug`**, and neither does anything derived from it -- length, entropy, word
  count, hash, prefix or masked form. Enforced by a source-text test asserting no logging call in
  the module receives the password variable, not by inspection.
- AC7a The module logs at the levels in **Diagnostic logging** rather than everything at one
  level: step detail at `Debug`, milestones at `Information`, refusals and reveals at `Warning`,
  dependency failures at `Error`, an undelivered post-write password at `Critical`. A directory
  query that returned nothing and one that failed are distinguishable by level - `Warning` and
  `Error` - never logged identically.
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
- AC17 `forceChangePasswordNextSignIn` carries the operator's per-reset checkbox, which
  defaults to **CHECKED** (owner ruling 2026-09-23; it was unchecked until then, and the
  reversal is deliberate - the password now travels by email, so forcing a change turns it
  into a one-time handover instead of a standing credential sitting in a mailbox). The PATCH
  body sends whichever value was chosen, the success audit records which, and the owner
  email's wording follows it -- a change is promised only when the box was ticked. A test
  covers both values end to end, **including that the value sent when the operator touches
  nothing is `true`**. The operator can still clear it, and clearing it must remain possible:
  an account that cannot service a change prompt is locked out by the default.

- AC18 Every `CloudPasswordReset` audit event carries the full field set in **Audit fields, and
  Splunk** -- successes and refusals alike, using the `"n/a"` sentinel rather than nulls, because
  the writer drops nulls (finding `cpr-11`).
  **One documented exception, measured rather than assumed:** the top-level `error` key is written
  by `AuditService.LogModuleAction` as `success ? null : errorDetail`, so it is present on failures
  and absent on successes. That is shared-service behaviour this module cannot change without
  altering every other module's records. `CloudPasswordResetAuditShapeTests` asserts `error` is the
  ONLY key that varies, so the exception cannot quietly widen. Booleans
  are JSON booleans, enumerated fields hold only their listed values, and no field packs two
  facts into one string. A test asserts the emitted key set is identical across a success, a
  refusal, a reveal and a delivery failure, and that every enumerated value a code path can
  produce is on the list.

## Known Failure Classes checked

1. **Side-effect ordering** -- the success audit and the emails sit on the post-write path and
   are unreachable when the PATCH throws; refusal audits sit on refusal paths only. A send
   failure after a successful PATCH is its own audited outcome and must never be reported as
   either a plain success or a plain failure: the password did change.
2. **Success aggregation** -- not applicable: one target per operation, by design. If bulk is
   ever added this becomes the dominant risk, and the delivery model makes it worse, not
   better.
3. **Fail-closed authorization** -- section access, both server-side re-checks, the ticket
   gate and protection all deny on failure rather than defaulting permissive. Owner resolution
   was in this list and is gone with the derivation; the destination check that replaces it is
   a format check, not an authorization gate, and must not be described as one. The role read
   is not in this list either: it is display-only and gates nothing.
4. **Stale references** -- every file and line cited in this plan was read on 2026-09-10.

## Review log

`openreview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, fallback) over
c493b2a..7c47c3c: Acceptable with changes` -- 2026-09-10, three material changes.

| # | Finding | Disposition |
|---|---|---|
| 1 | **Self-Owned Cloud Account Gap** -- an operator whose own on-prem account is the derived owner receives the password under the main permission. | **Declined**, owner challenge 2026-09-10. No escalation: the operator already holds that account. Record: `.agents/review/cpr-1.contested.md`. **Moot since 2026-09-11:** there is no derived owner, so the narrow case the finding describes no longer exists -- but the general case it pointed at (an operator directing the password somewhere they control) is now reachable for *any* target, not just their own. That is the trade recorded in **Where the password goes**, not a residue of this finding. |
| 2 | **Ungated Delivery-Failure Reveal** -- displaying the password on a post-write send failure contradicts the no-visibility model and AC7. | **Upheld and fixed.** Owner ruling 2026-09-10: *"if the send itself fails, then fail closed."* The password is discarded, not shown, to any tier; `CloudPasswordReset_DeliveryFailed` is audited; downstream (post-handoff) delivery is out of scope. |
| 3 | **Owner Corroboration Lacks Data Plumbing** -- `GivenName`/`Surname` are not in `ValidationProperties` or `ADSearchResult`. | **Upheld and fixed, then made moot 2026-09-11.** The finding was correct and the plumbing was specified. The derivation it served is abandoned, so S1 and the field additions are deleted and nothing in this stream now reads `GivenName`, `Surname` or `Enabled`. The finding is kept here so the git history of the plumbing has a reason attached. |

Two owner rulings the same day are folded in above and recorded in `.agents/decisions.md`.
