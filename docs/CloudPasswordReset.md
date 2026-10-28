# Cloud Password Reset

Resets the password of an **Entra ID cloud-only account** - one with no on-premises Active
Directory object, which is therefore unreachable from the existing AD tooling L2 already has.

The plan and its full reasoning are in `docs/CloudPasswordReset-Plan.md`. This document is the
operator- and maintainer-facing summary.

## What it does

1. The operator names a cloud account.
2. The module reads it from Entra and refuses anything out of scope.
3. It derives who owns the account from the `employeeId` stamped on it, and finds that person's
   mailbox.
4. It generates a password, writes it to the account, and emails it to that mailbox.
5. It records what happened, and mails the administrators.

**The operator does not choose where the password goes and normally never sees it.** There is no
address field on the page. The destination comes from the directory, which is what makes this a
control rather than a convenience: an operator cannot mail themselves another account's password.

### An account with no employee ID (from `1.2.0`, off until switched on)

With "Fill in a missing employee ID" switched on, step 3 has a second route for an account that
carries no `employeeId` (`docs/CloudPasswordResetEmployeeId-Plan.md`):

1. The account's name proposes an owner: the part before the `@`, less the longest configured
   ending it ends with (compared ignoring case), looked up as a logon name in every domain of the
   server's forest.
2. Exactly one person must answer to it, and they must carry an employee ID.
3. That employee ID must lead back to that same person, by ObjectGUID, and to nobody else - the
   module's ordinary rule for a number - and they must have a mailbox. The address comes from the
   number, not from the name.
4. The preflight shows the number and the directory account it comes from, and the confirm prompt
   asks the operator to check that is the owner.
5. On confirm, the number is saved on the cloud account first, then the password is reset and
   mailed as for any account with a number. The next reset of that account takes the ordinary
   route.

When any of that fails, nothing is saved and the account is handled exactly as before: a person
who can open the module but cannot see passwords gets the same refusal, word for word, and a
person who can see passwords sees it once. An account that already carries a number never reaches
this route, and its number is never replaced.

## Who it is for

L2. The population is almost entirely admin and tactical accounts, several hundred of them, and
the module can reset a Global Administrator - that is its normal operating mode, not a corner of
it. The current process it replaces is L2 phoning L3.

## Permissions

| Alias | Grants |
|---|---|
| `CloudPasswordReset` | Open the module and reset a cloud-only account's password. The password is emailed to the owner and not shown. With the fill-in switched on, a reset of an account with no employee ID can first save the owner's employee ID on it. |
| `CloudPasswordResetReveal` | Additionally see the password on screen, for an account whose owner cannot be determined. |

Both are fail-closed: no section access configured means no access, never a fallback. Saving a
filled-in employee ID needs no permission of its own: anyone who can reset passwords here can
trigger it, and it happens only inside a reset, for the account being reset.

**The reveal permission is the only route by which an operator can learn a password this module
generates.** Hold it to as few people as possible. Every reveal is its own audit event.

## Configuration

| Field | Required | Meaning |
|---|---|---|
| `GraphDelineaSecretId` | Yes | Secret Server record holding `Tenant ID`, `Application ID`, `Client Secret` for this module's own app registration. |
| `ValidateTickets` | No (default off) | On: the ticket must validate through ServiceNow before a reset runs. Off: any non-blank ticket is accepted as audit metadata. |
| `FillMissingEmployeeId` | No (default off) | On: an account with no employee ID gets one from the lookup above, then the reset proceeds. Off: an account with no employee ID is handled as before. A value other than `true` or `false` reads as off and logs at Error. |
| `OwnerNameEndings` | No (no default) | Comma-separated endings your cloud admin account names add to their owner's logon name. Blank: the whole part before the `@` is the logon name. The module ships with none: the endings are a fact about one deployment, typed in by whoever switches the fill-in on. |

The config database is shared by the dev and prod instances, so switching the fill-in on in one
switches it on in both.

### Required before first use

1. **A dedicated Entra app registration**, admin-consented, with application permissions
   `User.Read.All`, `User-PasswordProfile.ReadWrite.All` and `RoleManagement.Read.Directory`.
   **Do not reuse another module's registration** - each carries an unrelated blast radius, and
   this is the grant that should never be widened by convenience.
2. **That service principal must ALSO hold the Privileged Authentication Administrator directory
   role.** The three permissions above are not sufficient on their own - this document said they
   were until 2026-09-28.

   Graph gates a password reset on a privileged target behind the CALLER's directory role, not
   the application permission. `User-PasswordProfile.ReadWrite.All` resets ordinary users and is
   refused on administrators, and **every account this module targets is an administrator**:
   cloud-only accounts here are admin and tactical accounts only. Without the role the module
   returns 403 on essentially every real reset.

   Privileged Authentication Administrator is the only role that can reset a Global
   Administrator. It is still far narrower than running the module on a Global Administrator
   credential - it resets credentials and MFA, and cannot create app registrations, change
   conditional access, alter directory policy or read mail.
3. **A Delinea Secret Server record** with those three fields, directly readable by the Delinea
   bootstrap credential with no checkout workflow. Its id goes in `GraphDelineaSecretId`.
4. The module ships **disabled**. Enable it and grant its section access.
5. **Only to fill in missing employee IDs:** the same registration must be allowed to write user
   details. **Assumed, not confirmed:** `User.ReadWrite.All` (application), the grant the
   owner's true-up script names for the same write; whether the directory role in item 2 already
   covers it is not known. The first live save settles it - record here what was actually needed.
   Until the registration can write, every save comes back refused: the resets still go out, and
   each one alerts the administrators that the number was not saved.

The app registration cannot use Graph's documented `resetPassword` action - that API is
delegated-only and unavailable to an application permission. The module uses
`PATCH /users/{id}` with `passwordProfile`, which is the app-only path.

## Why a reset refuses

Each refusal is audited with its reason and tells the operator what is wrong with the account
rather than what the module did.

| Refusal | Meaning | What to do |
|---|---|---|
| `SyncedAccount` | Mastered on-premises. | Reset it in AD; this module is not for it. |
| `GuestAccount` | Guest or external. | Out of scope. |
| `DestinationNoEmployeeId` | No employee ID on the account, and either the fill-in is off or its lookup found no owner (`employeeIdFill` in the audit says which, and the panel says why in one line). | Stamp one, or use the reveal path. |
| `DestinationNoMatch` | The employee ID matches no directory user. | A directory data fix. |
| `DestinationAmbiguous` | **Two or more users share that employee ID.** | A data-quality escalation. The module refuses rather than guessing whose mailbox to use. |
| `DestinationNoMailbox` | The owner was found but has no mailbox. | Use the reveal path. |
| `DestinationLookupFailed` | The directory could not be searched. | Retry. The owner is unknown, not absent. |
| `NotificationsDisabled` | The deployment-wide user-notification switch is off. | Nothing can be delivered; turn it on or use the reveal path. |
| `TicketInvalid` / `TicketValidatorUnavailable` | The ticket gate refused. | |
| `ProtectedPrincipal` / `ProtectionCheckFailed` | Protected target, or the check could not run. | An authorised servicer may proceed. |
| `PasswordPolicyRejected` | Entra rejected the generated password against tenant policy. | Report it - the generator is meant to satisfy the policy by construction. |
| `GeneratorFailed` | 100 attempts failed to reach the strength floor. | Report it. The generator refuses rather than issuing something weaker. |

**An operator holding the reveal permission can override any of the five destination refusals**,
including `DestinationLookupFailed`. The password is shown once, nothing is mailed, and the audit
records which refusal was overridden.

## The generated password

The app chooses it; there is no field for an operator-supplied password anywhere in the page, the
service or the request model.

A diceware-style passphrase from a 7,771-word embedded list: 18-32 characters, 2-6 words, mixed
capitalisation, symbol separators, digit-and-symbol padding, **minimum 60 bits of measured
entropy**. Below that floor it retries; after 100 attempts it **refuses** rather than issuing a
weaker password.

Every parameter is a constant, deliberately. None is a config field: an operator who could widen
the length range or lower the entropy floor could weaken every password the module ever issues.

## Force password change at next sign-in

A checkbox on the reset form, **checked by default** (owner ruling 2026-09-23). The password
travels by email, so forcing a change makes it a one-time handover rather than a standing
credential sitting in a mailbox.

The operator can clear it per reset, and must for an account whose sign-in path cannot service a
change prompt - such an account would otherwise be unusable after the reset. Clearing it shows the
standing instruction to walk the user through a manual reset before closing the ticket.

The owner's email wording follows the checkbox and never promises a prompt that will not appear.

## What is audited

Category `CloudPasswordReset`, actions `CloudPasswordReset_Execute`,
`CloudPasswordReset_Revealed` and `CloudPasswordReset_DeliveryFailed`, and from `1.2.0`
`CloudPasswordReset_EmployeeIdSet`, written immediately after a save of a filled-in employee ID:
success when Entra accepted it, failure otherwise, with the reason (a status code at most) as its
error.

**These events go to Splunk, so the field names are an interface, not a convenience.** Successes
and refusals carry the same key set, with the string `"n/a"` where a field does not apply - the
writer drops nulls, so a null would reach Splunk as a missing field (finding `cpr-11`) - so one
search returns uniform records and a missing field means a bug rather than a branch that did not
bother.

`targetObjectId`, `targetCloudOnly`, `targetDirectoryRoles`, `destinationAddress`,
`destinationEmployeeId`, `forceChangePasswordNextSignIn`, `passwordDelivery`, `revealUsed`,
`refusalReason`, `protectedPrincipalServiced`, and from `1.2.0` `employeeIdFill`,
`employeeIdFillValue` and `employeeIdFillSource`. The closed lists are the canonical table in
`docs/CloudPasswordReset-Plan.md`, "Audit fields, and Splunk".

`destinationEmployeeId` is what lets a wrong destination be traced back to the directory record
that caused it: `destinationAddress` says where a password went, and this says why there.

`employeeIdFill` says what happened to a missing employee ID on that operation - `NotNeeded` when
the account had one, `Off` with the switch off, `NotAttempted` when the operation stopped before
the lookup, the lookup's reason when it found no owner, `Planned` when it found one but the reset
stopped before the save, and `Written`, `WriteRejected` or `WriteIndeterminate` after a save.
`employeeIdFillValue` is the number found, and `employeeIdFillSource` the ObjectGUID of the
directory account it came from. A successful reset of an unstamped account writes two events: the
save, then the reset. The save's own event has `passwordDelivery` `"n/a"`, because it is not about
the password. A failed lookup's refusal or reveal keeps `refusalReason` `DestinationNoEmployeeId`;
`employeeIdFill` says why the lookup failed.

**The password appears in no audit event, no administrator email, no log line and no trace, at any
log level.** Its only destinations are the Entra write and the owner's mailbox.

The administrator alert names the target, the operator, the ticket, the destination and whether
the password was revealed, and from `1.2.0` the fill-in's outcome and the directory account it
read the number from. A save gets its own alert, on every outcome, naming the number, that account
and what became of the save - including, when Entra refused it or did not answer, that the number
was not saved or may not have been. Neither alert ever contains the password.

## Failure modes worth knowing

**The password was changed but could not be delivered.** The write cannot be undone, so the module
discards the password rather than displaying it - showing it would hand every operator a way to
see a password by provoking a send failure. The account is briefly in a state where nobody knows
its password; running the reset again fixes it. This logs at **Critical** and audits as
`CloudPasswordReset_DeliveryFailed`.

**An operator can still direct a password wrongly, but only by changing the directory.** The
module reads the destination from the directory and does not second-guess it. If the directory
holds the wrong mailbox for an employee ID, the password goes to the wrong person. That is a
directory-accuracy problem with a directory-accuracy fix.

**With the fill-in on, a name can point at the wrong person - the biggest risk it carries.** A name
is evidence, not proof: if a cloud admin account's name points at someone other than its owner -
a logon name that now belongs to someone else, say - the module mails the password to that person
and saves their number on the account, so later resets go there too, with nothing marking the
number as inferred. What limits it: the operator is shown that person before confirming and is
asked to check; a name two people answer to is refused; the number must lead back to that one
person; every save is audited and alerted with the account it came from. It is not ruled out. The
fix is to correct the number on the account. A deployment whose account names do not follow such
a rule leaves the switch off.

**A save Entra refuses, or does not answer, does not stop the reset.** The destination was
verified from the directory before the save and does not depend on it, so the password still
goes to the owner. The save's audit event and alert say the number was not saved (or, with no
answer, that it is not known whether it was), and the next reset of that account looks it up
again. Until the registration holds the write grant (Required before first use, item 5), every
save is refused this way.

**Protected principals.** A cloud-only account cannot be added to the protected list today - the
entry path validates against on-premises AD and refuses cloud-only objects. The check still runs,
because a cloud account that does have an Exchange recipient can match, and because the code must
be correct on the day that entry path is fixed. That gap is a different work stream.

## Logging

Diagnostic logging is deliberately generous, with the level doing the work, so verbosity is a
deployment setting rather than a code change.

- **Debug** - every step and its inputs, including the employee ID read and how many directory
  users matched. This is what answers "why did it choose that mailbox".
- **Information** - the milestones of a healthy reset.
- **Warning** - every refusal, and every reveal.
- **Error** - Graph, directory or SMTP failures, and a generator refusal.
- **Critical** - a password changed but not delivered.

A directory query that matched nobody logs at Warning; one that **failed** logs at Error. They are
different facts and logging them alike is how an outage comes to read as a clean negative result.

The fill-in follows the same rule. **Debug** carries the name tried, the ending removed, the
matches per domain, the owner's GUID and logon name, the number and the GUID comparison.
**Information** records a number saved. **Warning** records every answer about the data -
`OwnerNotFound`, `OwnerAmbiguous`, `OwnerHasNoEmployeeId`, `EmployeeIdShared`,
`EmployeeIdMismatch`, `OwnerHasNoMailbox`. **Error** records `OwnerLookupFailed` (a search that did
not finish), `SettingUnreadable`, and a save refused or left without an answer.
