# rev-j1: Account Lockout's execute buttons use a raw-ticket predicate the sanitized check behind them no longer agrees with

**Severity**: MEDIUM - fail-closed, not fail-open: the service refuses, so no logoff runs on a blank ticket. The
defect is that the button and the check disagree. After ticket-sanitize (1a0e42db) the page sends
`TicketPresence.Sanitize(ticketNumber)` but enables both execute buttons with `!string.IsNullOrWhiteSpace(ticketNumber)`
on the raw value; a ticket of only a zero-width space (or any character Sanitize removes) enables the button, and the
click is refused by the service with "Ticket Number is required for logoff execution." Before 1a0e42db both agreed
(both raw) and the zero-width ticket was recorded raw. The service's own gate also still judges with
`IsNullOrWhiteSpace`, so a non-page caller passing a raw invisible-only ticket passes the gate and records it unsanitized.
**Status**: Merged to master at c1a1245f (2026-11-09) on guard proof (MEDIUM, no review round); master gates green (8904 xUnit, 19 warnings, format/diff-check/ASCII clean)
**Branch**: `fix/rev-j1` (worktree `D:\source\ExchangeAdminWeb-wt\rev-j1`, from master)
**Commit**: fc82e580 (page), ef3323c3 (service), f44e7704 (scan), dcce8044 (tests), 64e9e16a (module 1.3.5), 4b1ef4c8 (call-site pin) | **Implementer**: Claude Fable (supervised agent)
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

## Fix (as landed on fix/rev-j1)

- Page: `CanExecuteSource` and `CanExecuteSweep` use `TicketPresence.IsPresentAsTyped(ticketNumber)`; lines replaced
  in place (the page is excluded from ClickGateRegistry, so no re-anchor either way).
- Service: `LogoffLockoutSourcesAsync` and `SweepScopedComputersAsync` replace the request's ticket with
  `TicketPresence.Sanitize(...)` on entry, before the trace scope, gate, audit and notification; this covers their dry
  runs too (they trace the ticket). The gate in `RunLogoffAsync` is `!TicketPresence.IsPresent(ticketNumber)`, which
  sanitizes itself, with the refusal wording unchanged. `RunLogoffAsync` became an internal test seam (no public path
  reaches the gate without a live credential); `ProtectionCheckCallSiteTests` locates it by signature and was updated.
- Scan: the `NotGatedByTicketPresence` entry is removed (the dictionary stays, empty); nothing else was needed.
- Module AccountLockoutRemediation 1.3.4 -> 1.3.5; no base bump. Help: the ticket box has no hover tip or note, so
  nothing to change.

## Guard proof (each fix reverted alone; targeted run `AccountLockoutRemediation|TicketPresenceTests`, 178 tests)

| Reverted | Failed | Tests that failed |
|---|---|---|
| Page predicates (fc82e580) | 2 | `AccountLockoutRemediationTests.ExecuteButtons_AreEnabledByTheSanitizedTicket` (zero-width-space case); `TicketPresenceTests.EveryTicketGatedControl_IsEnabledByTheRuleOfTheCheckBehindIt_AndItsTicketIsRecordedSanitized` |
| Service gate (`IsPresent` back to `IsNullOrWhiteSpace`) | 1 | `AccountLockoutRemediationServiceTests.RunLogoff_Execute_AnInvisibleOnlyTicket_IsRefusedWithTheServicesOwnWords` |
| Service boundary `Sanitize` (both entry points) | 1 | `AccountLockoutRemediationServiceTests.Execute_TheAdminNotificationCarriesTheTicketSanitized_OnBothPaths` |

Restored: all 178 pass.

## Left alone, and why

- `RunLogoff_Execute_ATicketCarryingAZeroWidthSpace_IsTakenAndRecordedSanitized` (audit row and trace) and
  `Sweep_Execute_ATicketCarryingAZeroWidthSpace_IsTracedSanitized` do not bite against this slice's service change:
  `AuditService.RecordedTicket` and `OperationTraceService` already sanitize every ticket (ticket-sanitize, 1a0e42db).
  They pin the end-to-end record; the bite for the recorders is in that slice's `AuditServiceTests`. Of the finding's
  "records it unsanitized", only the admin notification was still true (`EmailService` does not sanitize); the
  boundary `Sanitize` fixes that and its test bites.
- Pre-existing, not this finding: an execute refused at the ticket gate returns `Executed == true`, so the public
  methods still send the admin notification for that refusal (with an empty ticket). Behaviour unchanged here.

## Codex review J notes (not findings)

Changed `.razor` files all had equal insert and delete counts, so no ClickGate re-anchor is needed. README's ticket
section matches the code. No in-app invisible-character text remains. Base and module bumps line up with the changes.
