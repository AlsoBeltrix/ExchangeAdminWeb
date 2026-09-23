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

## Who it is for

L2. The population is almost entirely admin and tactical accounts, several hundred of them, and
the module can reset a Global Administrator - that is its normal operating mode, not a corner of
it. The current process it replaces is L2 phoning L3.

## Permissions

| Alias | Grants |
|---|---|
| `CloudPasswordReset` | Open the module and reset a cloud-only account's password. The password is emailed to the owner and not shown. |
| `CloudPasswordResetReveal` | Additionally see the password on screen, for an account whose owner cannot be determined. |

Both are fail-closed: no section access configured means no access, never a fallback.

**The reveal permission is the only route by which an operator can learn a password this module
generates.** Hold it to as few people as possible. Every reveal is its own audit event.

## Configuration

| Field | Required | Meaning |
|---|---|---|
| `GraphDelineaSecretId` | Yes | Secret Server record holding `Tenant ID`, `Application ID`, `Client Secret` for this module's own app registration. |
| `ValidateTickets` | No (default off) | On: the ticket must validate through ServiceNow before a reset runs. Off: any non-blank ticket is accepted as audit metadata. |

### Required before first use

1. **A dedicated Entra app registration**, admin-consented, with application permissions
   `User.Read.All`, `User-PasswordProfile.ReadWrite.All` and `RoleManagement.Read.Directory`.
   **Do not reuse another module's registration** - each carries an unrelated blast radius, and
   this is the grant that should never be widened by convenience.
2. **A Delinea Secret Server record** with those three fields, directly readable by the Delinea
   bootstrap credential with no checkout workflow. Its id goes in `GraphDelineaSecretId`.
3. The module ships **disabled**. Enable it and grant its section access.

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
| `DestinationNoEmployeeId` | No employee ID on the account. | Stamp one, or use the reveal path. |
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
`CloudPasswordReset_Revealed` and `CloudPasswordReset_DeliveryFailed`.

**These events go to Splunk, so the field names are an interface, not a convenience.** Successes
and refusals carry the same key set with explicit nulls, so one search returns uniform records and
a missing field means a bug rather than a branch that did not bother.

`targetObjectId`, `targetCloudOnly`, `targetDirectoryRoles`, `destinationAddress`,
`destinationEmployeeId`, `forceChangePasswordNextSignIn`, `passwordDelivery`, `revealUsed`,
`refusalReason`, `protectedPrincipalServiced`.

`destinationEmployeeId` is what lets a wrong destination be traced back to the directory record
that caused it: `destinationAddress` says where a password went, and this says why there.

**The password appears in no audit event, no administrator email, no log line and no trace, at any
log level.** Its only destinations are the Entra write and the owner's mailbox.

The administrator alert names the target, the operator, the ticket, the destination and whether
the password was revealed. It never contains the password.

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
