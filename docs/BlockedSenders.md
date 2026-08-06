# Blocked Senders Module

Module ID: `BlockedSenders` · Route: `/blocked-senders` · Category: Exchange · Version: 1.1.0

## Purpose

View Exchange Online blocked senders and unblock one by address. A *blocked sender* is a
Microsoft 365 account that the service has blocked from **sending** mail — typically a
compromised or misconfigured account that sent outbound spam. Intended operators: mail/EXO
administrators and the help desk tier authorized to clear send-blocks.

## Mutates data

Yes. The only write is `Remove-BlockedSenderAddress`, which re-enables an account's ability to
send mail. It deletes no data. If the account keeps sending spam, the service re-blocks it
automatically — that is the compensating action for an unwanted unblock.

## Backend

Exchange Online only (cloud). Both cmdlets exist solely in EXO; there is **no on-prem path**.

- `Get-BlockedSenderAddress` — read the current list.
- `Remove-BlockedSenderAddress -SenderAddress <addr> [-Reason <text>]` — unblock one address.

Runs over the shared `ExoConnectionPool` via `ExchangeServiceBase`. The module depends on the
`ExchangeOnline` parent module (`DependsOn = "ExchangeOnline"`); if that module is disabled or
unconfigured, this module is unavailable.

## Required permissions / groups

Two section-access policies (both fail-closed — no access until a group is assigned):

| Policy alias | Gates |
|---|---|
| `BlockedSenders` | Viewing the blocked-sender list and opening the page |
| `BlockedSendersUnblock` | The unblock write (re-checked immediately before the cmdlet runs) |

A wider group can be granted `BlockedSenders` (view-only); a narrower group gets
`BlockedSendersUnblock` to actually unblock. Configure both on the module config page
(`/module-config/BlockedSenders`).

## Required module config fields

None. EXO authentication is owned by the `ExchangeOnline` parent module and the shared pool.

## Required Delinea secret template fields

None. This module uses no module-specific privileged credential.

## Required Graph app permissions

None (not a Graph module).

## Required Exchange permissions

The EXO app/service principal used by the `ExchangeOnline` connection must be able to run
`Get-BlockedSenderAddress` and `Remove-BlockedSenderAddress` (Security Administrator / the
relevant Exchange RBAC role granting the *Blocked Senders* cmdlets).

## Protected-principal behavior

**Enforced since 1.1.0.** An unblock is refused when the target address belongs to a protected
principal, and the denial is audited.

**This section previously said "not applicable", and that reasoning is recorded here because it is
why the gap survived review.** The argument was that `Remove-BlockedSenderAddress` clears a
service-side spam-block flag keyed by SMTP address and writes to no directory object, so a pattern
governing "identity writes bound to GUID/DN" did not apply. The premise is true; the conclusion was
wrong. The Constitution binds the guard to the TARGET of a mutating operation, not to the storage
location of the bit being changed - and re-enabling a principal's ability to send mail is a change
to that principal's state by any reading. Judging applicability by mechanism rather than by target
is what produced a module that carefully re-checked the operator and never once looked at who was
being unblocked.

Gating uses `BlockedSenderProtectionGate`, not the shared mailbox validator, and the difference is
deliberate:

- resolution always runs through Exchange, so a cloud-only or alias-addressed sender is normalized
  to its real identity rather than compared as a literal string;
- an address that **neither** directory knows is ALLOWED, because external, decommissioned and
  otherwise unresolvable senders are routinely what needs clearing - often the very reason the
  address was blocked. This is the one place this module deliberately differs from the mailbox
  gate, which denies an unknown target.

Everything else fails closed: ambiguous address, unavailable directory, unreadable protection
config, or a protection check that could not evaluate a rule.

## Audit actions emitted

Category `BlockedSenders`:

- `ListBlockedSenders` — lookup audit on each list load (success/failure).
- `UnblockSender` — module audit on each unblock attempt (success and failure), with target
  address, ticket, and error detail on failure.
- `UnblockSender_Denied` — module audit when an unblock is refused before the write. Two distinct
  causes share this action: the pre-write authorization re-check failing (the OPERATOR may not act),
  and the protected-principal gate refusing (the TARGET may not be acted upon). The `errorDetail`
  distinguishes them.

Audit-write failures are caught and logged separately; they never change the unblock result.

## Operation trace behavior

Each unblock opens an `OperationTraceService` scope (`BlockedSenders` / `UnblockSender`) with
steps `Reauthorized` and `BackendWrite` (backend `ExchangeOnline`, command
`Remove-BlockedSenderAddress`), completed with success/failure. The list read is not traced
(routine read).

## Notifications

Each unblock attempt (success and failure) sends an admin email via
`EmailService.SendAdminNotificationAsync` to the global admin-email recipient, carrying the
sender address and reason. A send failure is caught and logged; it does not change the unblock
result. If no admin email is configured, the notification is skipped silently.

## Manual dev validation steps

The live EXO read/unblock cannot be unit-tested (the pool is sealed and cannot be unit-hosted),
so validate these on a dev deploy with `ExchangeOnline` configured:

1. Enable the module in Admin Settings; assign your group to `BlockedSenders` and
   `BlockedSendersUnblock` on the module config page.
2. Open `/blocked-senders` — the list loads (or shows "No blocked senders found"). Confirm a
   `ListBlockedSenders` audit entry appears in the Event Log.
3. With a user in `BlockedSenders` but **not** `BlockedSendersUnblock`, confirm the list is
   visible but an unblock attempt is denied and audited as `UnblockSender_Denied`.
4. Unblock a real blocked address (or a safe test address): enter a ticket, confirm. Verify the
   success banner, the `UnblockSender` audit entry, the operation trace, and the admin email.
5. Confirm direct navigation to `/blocked-senders` is denied when the module is disabled or your
   group is removed from `BlockedSenders`.
6. Confirm the module version shows next to the page heading.
7. **Protected principal is refused (1.1.0).** Add a protected user row for an address that is also
   blocked, then attempt the unblock: it must be denied, the banner must say the address belongs to
   a protected principal, and the Event Log must carry an `UnblockSender_Denied` entry whose detail
   names the protection rule rather than an authorization failure.
8. **An unresolvable sender is still unblockable (1.1.0).** Unblock an external or decommissioned
   address that resolves in neither directory. It must SUCCEED - this is the deliberate difference
   from the mailbox gate, and a regression here would leave exactly the addresses this module
   exists to clear permanently stuck.

## Rollback / remediation

- Disable the module in Admin Settings (runtime, no deploy) to remove access immediately.
- An unwanted unblock self-corrects: a still-spamming account is re-blocked by the service. No
  manual re-block cmdlet is exposed by this module.
