# aud-a30: a ticket number reached ServiceNow's query grammar, and the answer was never checked

**Severity**: MEDIUM as deployed, HIGH if validation is switched on. Ticket validation is a
gate on destructive actions; breaking it means any string that returns a row becomes a valid
ticket.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 30 of the 2026-10-16 audit, verified against the code. Two halves, and the plan asked
for both.

### The query grammar

`Services/ServiceNowService.ValidateTicketAsync`:

```csharp
var query = $"number={ticketNumber}";
var url = $"{_instanceUrl}/api/now/table/{table}?sysparm_query={Uri.EscapeDataString(query)}&...";
```

`sysparm_query` has its own grammar - `^` separates clauses, `^OR` makes them alternatives,
`=` binds a field - and ServiceNow parses that **after** url-decoding. So
`Uri.EscapeDataString` protects the transport and does nothing about the grammar. A ticket
number of `INC0012345^ORnumberISNOTEMPTY` arrives at ServiceNow as two clauses and matches
any ticket in the table.

**The audit's wording - "URL-encoding does not neutralise `^OR` as query grammar" - is exactly
right**, and that distinction is the whole finding.

### The unchecked answer

`results[0]` was then read for its state with no comparison to the number requested. So any
query returning a row - through the grammar break above, a ServiceNow-side alias, or simply a
different match - had that row's state accepted as this ticket's.

## Predicted observable failure

An operator who wants to perform a gated action without a real ticket types a number with a
query-grammar tail. ServiceNow returns the first active ticket in the table, its state is
read as active, and the gate opens. The audit records the string they typed.

## Approach

**Refused on shape, not escaped.** Ticket numbers are alphanumeric; the pattern
`^[A-Za-z0-9]{3,32}$` is checked **before any request is made**, and anything else is turned
away with a message the operator can act on. Quoting the input cleverly would leave the
question of whether the quoting is complete; refusing it does not, and the standing rule is
that inaccurate input is the user's to correct.

**And the response is compared.** The returned `number` must equal the requested one, case
insensitively; a mismatch, or a row with no `number` at all, is refused. That half stands on
its own - it would have caught the grammar break even without the pattern, and it catches
anything else that makes ServiceNow answer about a different record.

## Files changed

- `Services/ServiceNowService.cs`
- `ExchangeAdminWeb.Tests/ServiceNowQueryGrammarTests.cs`

## Guard proof

Behaviour, through the real `ValidateTicketAsync` over a stub handler, so the URL
construction and response handling run.

| Test | What it pins |
| --- | --- |
| `AnOrdinaryTicket_StillValidates` | a fix that refused everything would not pass |
| `ATicketCarryingQueryGrammar_IsRefusedBeforeAnyRequest` (4 cases) | `^OR`, `^state=`, a comma, and a bare `number=` - and that **no request is sent at all** |
| `ADifferentTicketComingBack_IsRefused` | the second half, independently |
| `AResponseWithNoNumberField_IsRefused` | fail closed on a response that cannot be matched |

Asserting `handler.Urls` is empty matters: it proves the refusal happens before the call, not
that the call happened and the answer was discarded.

| Mutation | Result |
| --- | --- |
| both guards replaced with `if (false)` | FAIL - all six, with only the ordinary-ticket test passing |

Source restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4387 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. Base app version 2.28.1 -> 2.29.0 - minor, because a ticket number
that used to be accepted can now be refused.

## Known gaps

- **Ticket validation is not enabled in this deployment** and the owner has said it will not
  be until ServiceNow admins provide an API login. The plan asked that urgency be rated on
  that basis, so: not currently reachable, and the code is what will run the day it is
  switched on. Nothing here was tested against a real ServiceNow.
- The 3-32 alphanumeric pattern is inferred from the `INC`/`REQ` prefixes the code already
  keys on, not from a documented ServiceNow constraint. If an instance issues ticket numbers
  with punctuation, this refuses them - visibly, with a message, rather than silently.
- Other `sysparm_query` uses in this service, if any, were not audited here.

## Closeout

Fixed directly on `master`. Independent review pending.
