# rev-j1: Account Lockout's execute buttons use a raw-ticket predicate the sanitized check behind them no longer agrees with

**Severity**: MEDIUM - fail-closed, not fail-open: the service refuses, so no logoff runs on a blank ticket. The
defect is that the button and the check disagree. After ticket-sanitize (1a0e42db) the page sends
`TicketPresence.Sanitize(ticketNumber)` but enables both execute buttons with `!string.IsNullOrWhiteSpace(ticketNumber)`
on the raw value; a ticket of only a zero-width space (or any character Sanitize removes) enables the button, and the
click is refused by the service with "Ticket Number is required for logoff execution." Before 1a0e42db both agreed
(both raw) and the zero-width ticket was recorded raw. The service's own gate also still judges with
`IsNullOrWhiteSpace`, so a non-page caller passing a raw invisible-only ticket passes the gate and records it unsanitized.
**Status**: Open - admitted from codex review J; fix slice dispatched
**Branch**: `fix/rev-j1` (worktree `D:\source\ExchangeAdminWeb-wt\rev-j1`, from master)
**Commit**: pending | **Implementer**: Claude (supervised agent)
**Reviewer**: codex (Portkey) / configured model (no `--model`; route obscured by the gateway) / xhigh / standard
**Range reviewed**: 04d1f887..1a0e42db (merge of fix/ticket-sanitize); result `.agents/review/rev-j.result.txt`

## Evidence (master 1a1afab6)

- `Components/Pages/AccountLockoutRemediation.razor:212-223`: `CanExecuteSource` and `CanExecuteSweep` both contain
  `!string.IsNullOrWhiteSpace(ticketNumber)`.
- The same page's handlers pass `TicketPresence.Sanitize(ticketNumber)` into `AccountLockoutLogoffRequest` and
  `AccountScopedLogoffRequest`.
- `Services/AccountLockoutRemediationService.cs:314`: `if (execute && string.IsNullOrWhiteSpace(ticketNumber))`.
- `ExchangeAdminWeb.Tests/TicketPresenceTests.cs:142-145`: the page scan exempts this page under
  `NotGatedByTicketPresence` ("its service checks the ticket with IsNullOrWhiteSpace"), which is why the stale
  predicate survived the ticket-sanitize scan. The ticket-sanitize hand-back named the exemption and left it.

## Fix (intended)

The page's two predicates use `TicketPresence.IsPresentAsTyped(ticketNumber)`; the service sanitizes the ticket once at
its boundary (before the trace scope, the gate, notifications and audit) and judges it with `TicketPresence.IsPresent`
or the equivalent, keeping its own refusal wording. Remove the `NotGatedByTicketPresence` exemption so the scan covers
the page. Tests: a zero-width-space-only ticket leaves both buttons disabled; a sanitized-and-present ticket (zero-width
space inside "INC0042") is recorded as "INC0042" by the service's audit and trace; a raw invisible-only ticket handed to
the service directly is refused. Module patch bump: AccountLockoutRemediation. No base bump (module-scoped).

## Codex review J notes (not findings)

Changed `.razor` files all had equal insert and delete counts, so no ClickGate re-anchor is needed. README's ticket
section matches the code. No in-app invisible-character text remains. Base and module bumps line up with the changes.
