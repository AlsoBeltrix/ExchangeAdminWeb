# sweep-e-40: a ticket carrying an invisible character was refused as missing, while the button stayed enabled (sweep item 40)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 40: `Services/TicketPresence.cs` refused a
ticket holding any Control or Format character (a pasted zero-width space, BOM, soft hyphen, bidi mark) with "Ticket
number is required", while the submit buttons tested `!string.IsNullOrWhiteSpace(ticketNumber)` and stayed enabled.
**Status**: Fixed on branch; awaiting review
**Superseded in part (2026-11-09)**: `InvisibleCharacterMessage`, the hint beside each ticket box and the button-disable path for it are withdrawn; the ticket is sanitized instead. The button/check agreement (`IsPresentAsTyped`) stands (`.agents/decisions.md` 2026-11-09, "A ticket is sanitized, not refused, for invisible characters"; `ticket-sanitize.md`).
**Branch**: `fix/sweep-e` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-e`, from master `54dcfb40`)
**Commits**: `3c75af3c` (`fix(sweep-e, item 40): ...`), `6aa4bc3f` (test sources keep their escapes) | **Reviewer**: implementer (candidate)

## Approach (as built)

- **The server says why.** `TicketPresence.Problem(ticket)`: null when present; `RequiredMessage` (unchanged words) when
  nothing visible was given; `InvisibleCharacterMessage` - "Ticket number contains an invisible character (a zero-width
  space, byte-order mark, soft hyphen, bidi mark or control character, usually carried in by a paste). Retype the ticket
  number by hand" - when a control or format character or a lone surrogate is present. `IsPresent`, `Refusal` and a new
  `Describe(ticket, ownWordsForMissing)` follow it. Every TicketPresence consumer gives the distinct words:
  TicketValidationService, Emergency Disable, the Message Analysis download, Migration's creates, DHCP deauthorize, Intune,
  Risky Users, M365 Groups, Named Locations, and every `Refusal(...) ?? ServiceNow` page. Nothing is stripped: the ticket
  recorded is the one typed. Audit `error` detail strings that were fixed text ("Ticket number not provided", "Ticket
  number missing") are left as they were (Splunk-facing).
- **The button follows the server's rule.** `TicketPresence.IsPresentAsTyped(typed)` (trimmed, as every check trims
  before judging and recording) replaces `string.IsNullOrWhiteSpace(<ticket>)` in every ticket-gated button, title and
  hint, and in the submit predicates (`IsFormValid`, `IsSetFormValid`, `HasTicket`) of 19 pages.
- **The box says why the button is off.** Each ticket box shows the invisible-character message beside it
  (`HasInvisibleCharacterAsTyped`), on the input's own line, so no markup line moves; ClickGate registry snippets moved to
  the new predicate text and two line counts (DHCP, Intune; `@code` comments) were updated.
- Help: README "Ticket numbers" (new). Module patch bumps for every page touched: ADAttributeEditor 1.7.6, AdminEventLog
  1.5.2, BitLockerRecovery 1.3.2, BlockedSenders 1.5.4, DhcpAuthorization 1.9.1, EmergencyDisable 1.7.3, GroupManagement
  2.16.3, IntuneDevices 1.6.1, LicensingUpdates 1.1.8, M365GroupManagement 1.9.2, MessageTrace 1.8.8, MfaReset 1.6.2,
  Migration 1.27.1, NamedLocations 1.6.1, OutOfOffice 1.1.5, RiskyUsers 1.6.2 (Mailbox and Calendar Permissions and
  Conference Rooms covered by this branch's earlier bumps). Base app stays `2.36.0`: TicketPresence is shared, and this
  cycle's base bump already absorbs this deploy's shared changes.

## Where the code goes beside the fix shape

- The sweep offered "strip invisible characters, or say that the ticket contains one"; the brief chose to say it, and to
  make the button agree with the server. Both are done, plus the hint, because a disabled button that says nothing would
  have replaced one confusion with another.
- Scope note: Account Lockout Remediation is exempt from the page scan - its service checks the
  ticket with `string.IsNullOrWhiteSpace` and it is not in item 41's list; its button agrees with its own service.
  Comms-10k and Message Trace take optional tickets and were not changed.
- `6aa4bc3f`: an editor tool had written the test tickets' escapes as literal invisible characters in
  `TicketPresenceTests.cs`; the escapes were restored in a separate test-only commit (no history rewrite).

## Guard proof

| Mutation | Failing tests |
|---|---|
| T1 an invisible character is said as missing | 66: `TicketPresenceTests` invisible theory, surrogate fact and hint theory; the ticket tests of Migration, Conference Rooms (single and bulk), AD Group Management, MFA Reset, Risky Users, Intune, Named Locations, DHCP, Message Analysis export, Mailbox/Calendar Permissions, M365 Groups |
| T2 `IsPresentAsTyped` judges untrimmed | `ATicketAsTyped_IsJudgedTrimmed_AsTheCheckBehindTheButtonJudgesIt` (2 cases) |
| T3 a ticket box loses its hint (Mailbox Permissions bulk) | `EveryTicketGatedControl_IsEnabledByTheRuleOfTheCheckBehindIt_AndItsBoxShowsWhyNot` |
| T4 a button back on `IsNullOrWhiteSpace` (MFA Reset) | the same page scan |
| T5 `Describe` uses the caller's words for an invisible character | 47: `TicketPresenceTests` invisible theory; Risky Users, Message Analysis export, Named Locations, Migration, Intune, M365 Groups ticket tests |

## Gates

Run once for the branch at its tip; see the sweep-e hand-back. Not run: a browser paste of a ticket with a zero-width space.

## Deploy-time checks

- Paste `INC0001` followed by a zero-width space into a ticket box: the message appears under the box and the submit
  button stays disabled; retyped, the button enables.
