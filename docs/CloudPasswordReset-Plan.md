# Cloud Password Reset Module (Entra ID cloud-only accounts)

Status: **OFF HOLD as of 2026-09-22, AND THIS DOCUMENT IS OUT OF DATE.** The owner lifted the
2026-09-14 hold (*"yes, that plan is off hold."*, `.agents/decisions.md`). **Authorized right now:
a read-only coverage survey, and the revision of this plan. NOT authorized: implementation** - the
design below is built on a premise the owner has since falsified, so it must be revised and
re-approved before any C# is written.

**Read this before anything else in the file.** Two queue items landed after the hold and together
they change the module's shape:

- **Item 10 - match on employeeId and derive the destination mailbox from it.** This reverses the
  central design decision recorded below. Everything here that says the operator types the
  destination exists only because the 2026-09-11 survey found employeeId populated on **0 of 172**
  cloud accounts and stamping them was refused. The owner has since stamped them (*"yes, already
  updated the accounts in-scope."*). The blocker was the data and the data changed.
  **The 100% bar is still the owner's** - *"if we cannot get a 100% working match, then matching is
  off the table"* - and "already updated" is a reported action, not a measured rate. The survey
  measures it first; a partial result is a fresh owner decision, not something to paper over with a
  fallback to typing.
- **Item 11 - force password change at next sign-in must be a RUNTIME option on the reset form,
  defaulting to yes.** Owner, verbatim: *"O365 PW change module should have an option at runtime,
  so not secreted away in settings, to force pw change on next login, which should default to
  yes."* So it is a control the operator sees and can turn off per reset, checked by default - NOT
  a config field on the module's settings page, and not a hardcoded constant.

**D4 comes back with item 10.** The separate `CloudPasswordResetReveal` permission was justified by
the operator being unable to obtain the password; typing the destination undermined that, and a
derived destination restores it. It was answered *"no. neither."* and so was never withdrawn on its
merits. Re-put it before S5.

Nothing was ever built. The stream shipped PowerShell survey tooling, which is deleted
(`a56f41f`), and this plan. There is no `CloudPasswordReset` descriptor in
`Modules/ModuleCatalog.cs`, no service, no page, no permission, no config field and no version
bump anywhere. **Resuming costs nothing to undo.**

**State as the hold was lifted, which the revision starts from:** D1 is settled (the app generates
the password); D2 (the operator names the destination) is REOPENED by item 10; D3 is withdrawn; D4
is reopened as above. The S0 gate returns in a new form - a coverage survey against the stamped
accounts.

New module `CloudPasswordReset`. **The base app version bumps** -- this stream adds a public
method to `Services/EmailService.cs`, which is shared infrastructure (Constitution, Deployment
And Versioning). The "adding a module does not bump the base version" exception does not apply,
because this is not only a module. The `ADSearchResult` additions the fifth revision also
counted here are gone with the derivation; `EmailService` is now the whole of the shared
change.

Revision 2026-09-14 (sixth) deletes the entire owner-derivation design. The rebuilt derivation
was surveyed against the live tenant and resolved 46.5% of in-scope accounts; the owner's
ruling was that a partial match is not a design, and that the destination is simply typed by
the operator. Everything that existed to derive, corroborate, survey or gate on an owner is
removed from this plan and from the repo. The security argument changes shape as a result and
is restated honestly in **Where the password goes** -- it is now detection and accountability,
not prevention. Superseded sections are not kept as commentary; git history holds them.

Revision 2026-09-11 (fifth) rebuilt the derivation after the first authorised survey run
returned 9.6%. Superseded by the sixth revision; retained only as the reason the survey was
re-run at all.

Revision 2026-09-10 (fourth) folds in the codex review of `c493b2a..7c47c3c` and the two owner
rulings it produced: a post-write send failure fails closed with no reveal, downstream mail
delivery is out of scope, and the AD lookup is short three fields the corroboration and leaver
rules need. See the Review log at the end. The third revision's two overturned premises still
govern and are kept below because both reversals are load-bearing:

1. The first draft gated on "target holds an admin role". The owner corrected the premise --
   *almost no non-admin accounts are in scope* -- so that tier fenced nothing and was removed,
   along with an invented `BlockedDirectoryRoles` config field the owner never asked for.
2. The second draft had the password displayed in the UI, then emailed, then displayed again.
   The answer then settled on **emailed, invisible to the operator**, with an on-screen reveal
   under a second permission. The owner's words: *"we need reliable email notification for
   users and admins and no visibility of the password for the tech making the change unless we
   gate that with another permission level."* **Half of this survives the sixth revision:** the
   password is still emailed and still not displayed, but "invisible to the operator" is no
   longer achievable, because the operator chooses where it goes. The reveal permission's
   status is D4.

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
   carries is keyed on whether the operator may *see* the password; D4 asks whether that
   distinction still fences anything now that the operator names the destination.
2. **Many targets have no mailbox of their own.** An Entra-only admin or automation identity
   commonly has no Exchange recipient. The password therefore cannot be sent to the account
   being reset; it must go to the human who owns that account, at their corporate mailbox.
3. **The account does not know who owns it, and cannot be made to.** Five revisions tried to
   derive the owner and the best measured result was 46.5%. The operator supplies the
   destination instead. See **Where the password goes**.

## Why delivery, not visibility

The password is mailed rather than shown. Owner ruling 2026-09-10: *"those passwords should be
emailed to the owner of the cloud account's @analog.com email address, not displayed in the
UI."*

The original justification was stronger than the one that survives. It was that an operator who
never sees the password cannot take over the account they reset, which made the delivery model
a genuine control. With the destination now typed by the operator (see **Where the password
goes**), that is no longer true, and mailing rather than showing is worth keeping for three
smaller reasons rather than one large one:

1. The password reaches the owner without a second manual step, which is the point of the
   module -- the current process is L2 relaying it by phone.
2. It keeps the credential out of the operator's screen, session and shoulder-surfing range in
   the ordinary case, where the destination is somebody else.
3. It gives the audit record a concrete destination to carry, which an on-screen reveal does
   not.

None of those is a fence. The fences are in **Where the password goes**.

## Where the password goes

**The operator names the destination.** They type an address on the reset form; the generated
password is mailed there and is not shown on screen on the ordinary path.

This replaces a derivation. The first five revisions computed the account's on-premises owner
fresh at each reset from three agreeing sources, and mailed the password to that person so the
operator never learned it. The S0 survey measured that derivation against the live tenant: it
resolved **80 of 172 in-scope accounts, 46.5%**. Owner ruling 2026-09-11, ending the approach:
*"if we cannot get a 100% working match, then matching is off the table."* The numbers, the
failure analysis and the two mechanical fixes that were on the table are recorded in
`.agents/decisions.md` (2026-09-11, "Owner derivation is abandoned").

### The security property this trades away

The superseded design's argument was that mailing the password somewhere the operator does not
control makes resetting a Global Administrator useless to them -- a nuisance to the owner
rather than a takeover. **That argument no longer holds, and no sentence in this plan should be
read as if it does.** An operator who types the destination can type their own address and
obtain the credential of any account in the tenant.

What bounds the risk instead:

1. **Section access.** `CloudPasswordReset` is fail-closed and held by a named set of people.
   It is the only preventive control left, so it carries weight it did not carry before.
2. **The audit record.** The destination address is a required field on every event, so a
   self-directed reset is a visible fact in Splunk rather than an inference.
3. **The administrator alert.** Every attempt mails the administrators naming the target, the
   operator, the ticket and the destination address, so the fact surfaces without anyone
   running a query. Owner ruling 2026-09-11: *"destination email needs to be in the logs and in
   the admin alert email."*

These are detection and accountability, not prevention. The owner made that trade on the
record, against a current process -- L2 telephoning L3 -- that has neither property and is
slower. It is stated here because a reviewer who finds the old no-visibility sentences in git
history would otherwise read this as a regression rather than a decision.

### What is not built

Nothing derives, stores, suggests or validates an owner. There is no owner map (rejected
2026-09-10: *"cannot store it ... we're not going to create an instantly stale map"*), no
derivation, no coverage survey, and no correctness check on the typed address beyond syntactic
validity and a non-empty value. The module cannot know who owns a cloud-only account and no
longer pretends to.

`employeeId` is not consulted. It is the strongest identifier available and is populated
on-premises, but on **0 of 172** cloud accounts, and the remedy -- stamping it onto several
hundred CLD accounts -- was refused by the owner. Recorded so it is not rediscovered and
proposed a third time.

`tools/CloudAccountOwnerDerivation.psm1`, `tools/Get-CloudAccountOwnerCoverage.ps1` and
`tests/ps/CloudAccountOwnerDerivation.Tests.ps1` were built for the abandoned design and are
deleted; git history keeps them if the question is ever reopened.

## Scope

IN: cloud-only Entra ID user accounts (`onPremisesSyncEnabled` not true), including
role-holding admin and tactical accounts. One target per operation.

OUT: synced accounts (mastered on-premises -- L2 already resets those); guest / external
(`userType` `Guest`); MFA methods (that is `MfaReset`); enabling, unblocking or unlocking an
account; any `passwordProfile`-adjacent property other than the password itself; bulk reset;
any stored owner mapping; any derivation, lookup or validation of who owns the target account.

**Self-reset is structurally impossible and needs no guard.** The app authenticates operators
against on-premises AD; every target here is cloud-only by definition. The two populations
cannot intersect, so an operator cannot be their own target. Recorded explicitly because a
reviewer reading only the Graph surface will otherwise raise it (it was raised once already).

**The related case -- an operator directing the password to their own mailbox -- is now
possible by construction, and is not guarded.** Codex raised its narrower ancestor over
`7c47c3c` (finding cpr-1, `.agents/review/cpr-1.contested.md`) when the destination was
derived; the derivation is gone and the question is no longer about derivation at all. There is
no check that can distinguish "the operator is legitimately the recipient" from "the operator is
helping themselves", because both are the operator typing an address they control. The answer is
the audit record and the administrator alert, both of which carry the destination. See **Where
the password goes**.

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
| Reset password | `PATCH /users/{id}` body `{"passwordProfile":{"password":"...","forceChangePasswordNextSignIn":false}}` | `User-PasswordProfile.ReadWrite.All` | 204 |

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

- **`CloudPasswordReset`** -- reset the account and mail the password to the address the
  operator supplied. The password is not shown. This is the L2 permission.
- **`CloudPasswordResetReveal`** -- additionally see the password once on screen instead of
  mailing it, for targets where no mailbox can receive it.

**D4 (open) questions whether the second permission survives.** Its former justification was
that an operator holding only the main permission could not obtain the password by any route.
That is no longer true: the destination is typed. A scarce permission guarding an outcome the
main permission already reaches is the `idm-3` decorative-control class this plan names
elsewhere. See **Owner decisions**. The descriptor below still carries it; if D4 rules it out,
S5 drops the granular entry and the reveal path with it.

The fences that bind, in evaluation order:

1. **Section access** -- who holds `CloudPasswordReset` at all. Fail-closed
   (`ModulePermission.FailClosed: true`, `Modules/ModulePermission.cs:3`).
2. **Server-side re-check immediately before the write** --
   `AuthorizationService.AuthorizeAsync(authState.User, "CloudPasswordReset")`, and separately
   `"CloudPasswordResetReveal"` before any reveal, mirroring
   `Components/Pages/MfaReset.razor:250-258`. The `@attribute [Authorize(Policy = ...)]` on
   the page is navigation control, not the gate (Constitution: UI hiding is not security).
3. **A non-blank, syntactically valid destination address** -- or the reveal permission.
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

1. A destination address was supplied and is non-blank and syntactically valid, **or** the
   reveal permission is held and the operator chose the reveal path.
2. `EmailService.UserNotificationsEnabled` (`Services/EmailService.cs:438`) is **true** when
   the run depends on email. This is a deployment-wide switch that outranks anything the
   module wants, and its own remark warns that a caller which cannot say so on screen has
   built a decorative control (`:431-437`). Here it is worse than decorative: a silent
   suppression would lock the owner out of their account. If it is off, the reset is refused
   before the write, naming the switch.

**The destination is not validated beyond its syntax.** The module has no way to know whether
the address belongs to the account's owner, and any check it invented would be the derivation
this design just abandoned. Two consequences are accepted deliberately: a typo mails the
password to a stranger, and a deliberate self-addressing succeeds. Both are visible in the audit
event and the administrator alert, which is the whole of the control -- see **Where the password
goes**. Validation is a format check only (`MailAddress` parse), never a directory lookup.

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
- **Every reveal is its own audited event.** There is exactly one condition that permits a
  reveal -- an unresolved owner plus the reveal permission -- and it is recorded as such. A
  reveal is the one path where an operator learns a credential; it is never folded into the
  ordinary success record.
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
4. **Every field appears on every event of its action, with an explicit `null` where it does not
   apply** -- an absent field and a null field look different to a search, and "missing" is not
   an answer anyone can act on. S6 confirms the JSON writer emits nulls rather than dropping
   them; if it drops them, the sentinel is the string `"n/a"` and this plan is amended to say so.
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
| `targetDirectoryRoles` | array of string | Directory role display names held by the target; `[]` when none, never null and never omitted. This is the field that answers "who reset a Global Admin, and when". |
| `destinationAddress` | string or null | The address the operator supplied, recorded lowercased, whether or not the send then succeeded; null only on the reveal path and on refusals that happened before the field was read. **This is the field that answers "where did the password actually go".** Owner ruling 2026-09-11: required, not optional. |
| `forceChangePasswordNextSignIn` | bool | Exactly what went in the PATCH body, under the Graph property's own name so the audit and the API cannot drift apart. |
| `passwordDelivery` | string | `Sent` \| `SendFailed` \| `Revealed` \| `NotAttempted` |
| `revealUsed` | bool | True only on the reveal path. Redundant against `passwordDelivery` by design: an alert on a single boolean is harder to get wrong than one on a string. |
| `refusalReason` | string or null | Null on success. Otherwise one of: `SyncedAccount`, `GuestAccount`, `DestinationMissing`, `DestinationMalformed`, `NotificationsDisabled`, `TicketInvalid`, `TicketValidatorUnavailable`, `ProtectedPrincipal`, `ProtectionCheckFailed`, `PermissionDenied`, `GraphReadFailed`, `PasswordPolicyRejected`, `GeneratorFailed`. |
| `protectedPrincipalServiced` | string or null | The shared helper's note (`ProtectedPrincipalServicing.Extra`), unchanged -- it is prose, and it is the one field that stays prose because the shared helper owns its shape. |

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

### D2 -- SETTLED 2026-09-11: the operator names the destination

The derivation is abandoned at 46.5% measured coverage. Full record in `.agents/decisions.md`
(2026-09-11) and in **Where the password goes**.

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
Description = "Reset the password of an Entra ID cloud-only account that has no on-premises Active Directory object. The new password is emailed to an address the operator supplies and is recorded in the audit log."
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
  and a second one here is a second place for it to be wrong), the destination address taken as a required parameter with no default and
  format-validated here as well as in the page (server-side is the gate; the page is
  convenience), and the PATCH returning a status-bearing result. No descriptor, no page.
  Tests assert the flag reaches the request body unaltered in both states. Tests for
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
  **destination address**, a required text field with no default, no pre-fill, no suggestion
  and no picker -- the module has nothing to suggest from and an autofilled address would be
  read as a verified one; and the **change at next sign-in** checkbox, **CHECKED by default**
  (owner ruling 2026-09-23). Label: *"Force password change at next sign-in"*. Help text,
  exactly: *"If cleared, the emailed password stays valid until someone changes it."* The help
  text must not restate the label - the operator can read the label. It carries the one thing
  the label does not: what clearing the box costs. The panel
  states, in plain words next to the address field, that the address is recorded
  in the audit log and mailed to the administrators. No write path in this slice. Catalog
  tests, plus tests that the address field starts empty, that submitting it blank or malformed
  is refused client- and server-side, and that the checkbox renders CHECKED on first load.
  D4 decides whether the descriptor carries one permission or two.
- **S6 -- the write.** The server-side authorization re-checks, the full protection flow
  including the unresolved branch with a real `EntraObjectId`, the ticket gate, the pre-write
  delivery gates, the PATCH, `400` surfaced as a policy rejection, the send, the fail-closed
  handling of a send failure (password discarded, nothing displayed,
  `CloudPasswordReset_DeliveryFailed` audited), `LogModuleAction` for each outcome with the
  serviced note, **the destination address** and the change-at-next-sign-in choice in `extra`,
  the administrator email (which states both the destination and the choice), and the
  `ModuleConfig.razor` servicer opt-in entry **in this same commit**. Two values are each
  traced end to end by a single test rather than hop by hop: the checkbox from page to PATCH
  body to both emails, and the destination address from the page to the send, to the audit
  `extra`, and to the administrator email. **A refusal must still carry the destination it was
  given**, so a blocked attempt is as searchable as a completed one.
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

1. A cloud-only account resets; the address the operator typed receives the password; the
   operator's screen shows no password. The audit event and the administrator email both carry
   that address, spelled exactly as typed apart from case.
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
5. A blank destination is refused before any Graph write, and so is a malformed one
   (`not-an-address`, `a@`, `a b@c.com`). The refusal is audited with `refusalReason`
   `DestinationMissing` or `DestinationMalformed`, and the account's password is unchanged.
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

- AC1 A target with `onPremisesSyncEnabled` true, or whose sync status could not be read, is
  refused.
- AC2 The reset uses `PATCH /users/{id}` `passwordProfile`; `resetPassword` is never called.
- AC3 The destination address is **operator-supplied and required** on the email path. The
  module derives, looks up, suggests, pre-fills, defaults and autocompletes nothing: no owner
  query runs, and a search of the module's source for an owner-resolution call returns
  nothing. A blank or syntactically malformed address refuses **before** the PATCH, with
  `refusalReason` `DestinationMissing` or `DestinationMalformed`. Validation is syntactic only
  -- the module never asserts the address belongs to the target account's owner.
- AC4 The destination address reaches **both** records. Every `CloudPasswordReset` audit event
  on a path that read the field carries `destinationAddress` lowercased -- successes, delivery
  failures and post-read refusals alike -- and the administrator alert email names the same
  address in its body. A test asserts the two carry the same value for one reset, and that a
  refusal after the field was read still carries it. Owner ruling 2026-09-11: this is required,
  not optional.
- AC5 An operator without `CloudPasswordResetReveal` never sees the password, on any path. A
  post-write send failure discards it rather than displaying it. (Subject to D4: if the reveal
  permission is dropped, the second sentence stands for every operator and the first is void.)
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
  Splunk** -- successes and refusals alike, with explicit nulls rather than omissions. Booleans
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
