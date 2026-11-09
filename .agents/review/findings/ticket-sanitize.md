# ticket-sanitize: a ticket is sanitized, not refused, for invisible characters

**Severity**: MEDIUM, owner-reported - a pasted ticket carrying a zero-width space (deploy checklist, 2026-11-09) disabled
the submit button and showed a refusal, though ServiceNow is dormant and a ticket is plain audit metadata.
**Status**: Merged to master at 1a0e42db (2026-11-09); master gates green (8898 xUnit, 19 warnings, format/diff-check/ASCII clean)
**Branch**: `fix/ticket-sanitize` (worktree `D:/source/ExchangeAdminWeb-wt/ticket-sanitize`, from master `04d1f887`)
**Decision**: `.agents/decisions.md` 2026-11-09, "A ticket is sanitized, not refused, for invisible characters"
**Supersedes in part**: kd-48 and sweep-e-40 (the invisible-character refusal); kd-24's blank-ticket rule stands
**Reviewer**: implementer (candidate)

## What changed

- `TicketPresence.Sanitize(typed)`: removes every control character, Unicode format character and unpaired surrogate,
  then trims; everything else is kept (inner white space, punctuation, a combining mark on its base). "" for null.
- `Problem` / `IsPresent` judge the sanitized value and give only `RequiredMessage`; the "draws nothing alone"
  visibility rule applies to what is left. `InvisibleCharacterMessage`, `HasInvisibleCharacterAsTyped` and the
  invisible branch of `Describe` are removed. `IsPresentAsTyped(typed)` is kept, now `IsPresent(Sanitize(typed))`: it
  is named by 23 ClickGateRegistry snippets and about 30 button predicates, so folding it away would rewrite the
  registry for no behaviour change.
- Every page reads its ticket box through `TicketPresence.Sanitize` where it is read for the check and the write, so
  the presence check, the ServiceNow query (`ValidateTicketAsync` receives the sanitized ticket) and the record are
  one string. The box itself keeps what was typed. The hint beside each ticket box is gone (same line, no markup
  line moved, so no ClickGate re-anchor).
- Belt and braces: `AuditService` (every row shape) and `OperationTraceService` record the ticket sanitized, null
  when nothing is left, whichever caller wrote it.
- Help: no in-app note or tip mentioned invisible characters (the per-box message was the only text and is removed).
  README "Ticket numbers" rewritten: the recorded ticket is the typed value with non-printing characters removed;
  "Ticket number is required" is the only refusal while ServiceNow is dormant.

## Call sites

| File | Before | After |
|---|---|---|
| `Services/TicketPresence.cs` | refuses a control/format character or lone surrogate (`InvisibleCharacterMessage`) | `Sanitize`; judges the sanitized value; only `RequiredMessage` |
| `Services/AuditService.cs` (10 row shapes) | `ticketNumber.Trim()` / as given | `RecordedTicket` = `Sanitize`, null when empty |
| `Services/OperationTraceService.cs` | ticket as given | `Sanitize`, null when empty |
| `Services/TicketValidationService.cs` | presence on raw; `ValidateTicketAsync(ticketNumber.Trim())` | `Sanitize` once; judged and sent to ServiceNow |
| `Services/EmergencyDisableService.cs` | `Describe` with invisible branch; `ticket.Trim()` after the check | `Sanitize` on entry, before the trace scope; fixed refusal words |
| `Services/MessageTraceExportListing.cs` | `Describe(ticket?.Trim(), ...)` | `Describe(ticket, ...)` (sanitizes inside) |
| `Services/MigrationService.cs` (single and bulk create) | `Describe(ticket?.Trim(), ...)` | `Describe(ticket, ...)` |
| `Models/LookupModels.cs` `OutOfOfficeRequest.FromForm` | `(ticket ?? "").Trim()` | `TicketPresence.Sanitize(ticket)` |
| ADAttributeEditor, AdminEventLog (undo), BlockedSenders, LicensingUpdates | `x.Trim()` | `TicketPresence.Sanitize(x)` |
| Migration (4 eligibility/create reads, confirm bar) | raw / `pendingActionTicket.Trim()` | `Sanitize` |
| M365GroupManagement (3), NamedLocations (2), ConferenceRooms (4), GroupManagement (4 + recorded-ticket line), DhcpAuthorization (2), MfaReset | `x.Trim()` | `Sanitize(x)` |
| MailboxPermissions (2), CalendarPermissions (2), IntuneDevices, RiskyUsers, BitLockerRecovery, EmergencyDisable, MessageTraceReports | `x.Trim()` | `Sanitize(x)` |
| CloudPasswordReset, Comms10k (optional), MessageTrace (7, optional), AccountLockoutRemediation (2) | `x.Trim()` | `Sanitize(x)` |
| 19 pages' ticket boxes | `@if (HasInvisibleCharacterAsTyped(x)) { message }` | removed |

Left as they are: `Components/Shared/TicketNumberInput.razor` (never referenced), the job processors that read
`job.Ticket` as the page stored it (their audit rows go through `AuditService`), and
`ServiceNowService.ValidateTicketAsync` itself (its callers pass the sanitized ticket). No ticket is used as a lookup
key anywhere.

## Tests

- `TicketPresenceTests`: nothing-visible theory (blank, controls only, format only, soft hyphen alone, marks alone,
  private use); sanitized-and-present theory (zero-width space inside and trailing, BOM, bidi marks, soft hyphen,
  control characters, "CHG 0042" kept, trailing space trimmed, trim after strip, combining mark on its base kept); an
  unpaired-surrogate fact; the page scan now fails a ticket box with any `InvisibleCharacter` text, a gated ticket read
  merely trimmed, or one never read through `Sanitize` (Out of Office: through `FromForm`).
- Page level, a ticket typed with a zero-width space is asked of an enabled ServiceNow as, and recorded as, the
  sanitized ticket: AD Attribute Editor, Blocked Senders, Event Log undo, Licensing Updates, Migration (confirm bar),
  Out of Office; M365 Groups (7 writes), Named Locations (3), Conference Rooms (4), AD Group Management (4), DHCP
  (authorize and deauthorize), MFA Reset. Intune, Risky Users and the permission pages' bulk run go ahead and record it
  sanitized. Services: TicketValidationService (asked sanitized; accepted when off), EmergencyDisableService (passes,
  audit and trace sanitized), MessageTraceExportListing (downloads), AuditService and the operation trace directly.
- Changed: every test pinning the invisible-character refusal now expects `RequiredMessage` for invisible-only tickets;
  the six item-41 pages' "unusable" sets use a zero-width space alone; the kd-24 gates expect a ticket with nothing
  visible recorded as none.

## Guard proof

Each mutation applied alone (P7 also combined), built, the named tests run, the file restored.

| Mutation | Failing tests |
|---|---|
| U1 Sanitize keeps format characters | 37: TicketPresenceTests sanitized/missing theories, the ZWSP page tests (Conference Rooms, DHCP, Group Management, MFA, Intune, Risky Users), Migration create theories |
| U2 Sanitize keeps control characters | 21: TicketPresenceTests theories, the kd-24 gates' no-ticket theories, Intune, Risky Users, Migration |
| U3 Sanitize keeps an unpaired surrogate | `AnUnpairedSurrogate_IsStripped` |
| U4 trim before the strip | `ATicketIsRecordedSanitized_AndIsPresent` (trailing space then ZWSP) |
| U5 combining marks stripped too | `ATicketIsRecordedSanitized_AndIsPresent`, `ATicketAPersonTyped_...` |
| P1 M365 member ticket `.Trim()` | 4 M365 ZWSP writes + the page scan |
| P2 Blocked Senders `.Trim()` | its ZWSP test + the page scan |
| P3 Migration confirm bar `.Trim()` | `AConfirmWithATicketTypedWithAZeroWidthSpace_ReachesTheActionSanitized` + the page scan |
| P4 `FromForm` trims only | `ASetWithATicketTypedWithAZeroWidthSpace_IsCheckedAndRecordedSanitized` |
| P5 AuditService trims only | `TheTicketIsRecordedSanitized_OnEveryRowShape`, Conference Rooms bulk no-ticket rows (2) |
| P5b operation trace unsanitized | `TheOperationTraceRecordsTheTicketSanitized` |
| P6 TicketValidationService sends `ticketNumber.Trim()` | `On_EnabledIsAskedAboutTheTicketSanitized` |
| P7 EmergencyDisableService no entry sanitize | none alone (audit and trace sanitize downstream); with P5 and P5b too: `DisableAsync_TicketTypedWithAZeroWidthSpace_PassesAndIsRecordedSanitized` |
| P8 Conference Rooms finder CSV `.Trim()` | its ZWSP write + the page scan |
| P9 Intune / P10 Mailbox bulk `.Trim()` | the page scan only (their records are sanitized again by AuditService) |
| U6 the old refusal restored inside `Problem` | 12 TicketPresence/export tests; the page tests do not fail, because pages hand `Problem` an already-sanitized ticket - the page mutations above are the ones that bite there |

## Versioning

Base app 2.36.1 -> 2.36.2 (shared: TicketPresence, AuditService, OperationTraceService, TicketValidationService).
Module patch bumps, each module whose page or service now records the ticket sanitized and no longer refuses an
invisible character: MailboxPermissions 1.6.12, CalendarPermissions 1.4.14, Migration 1.27.5, MessageTrace 1.8.10,
OutOfOffice 1.1.7, BlockedSenders 1.5.5, GroupManagement 2.16.6, M365GroupManagement 1.9.3, Comms10k 1.4.23, MfaReset
1.6.4, CloudPasswordReset 1.2.4, AccountLockoutRemediation 1.3.4, ConferenceRooms 2.20.3, NamedLocations 1.6.2,
EmergencyDisable 1.7.5, RiskyUsers 1.6.4, DhcpAuthorization 1.9.3, BitLockerRecovery 1.3.4, IntuneDevices 1.6.3,
LicensingUpdates 1.1.10, ADAttributeEditor 1.7.10, AdminEventLog 1.5.4. Pins updated (Cloud Password Reset x2,
Emergency Disable).

## Gates

Run once at the branch tip; see the hand-back. Not run: a browser paste of a ticket with a zero-width space on dev.

## Deploy-time checks

- Paste `INC0001` followed by a zero-width space into a ticket box: no message appears, the button is enabled, the
  action runs, and the audit row's ticket reads `INC0001`.
- A ticket box holding only a zero-width space keeps its button disabled.
