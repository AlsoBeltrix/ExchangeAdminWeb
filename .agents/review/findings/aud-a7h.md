# aud-a7h: A read schedule of an unexpected type silently disappears from Check Status, and the repeated-time refusal says "hour" for any repeat amount

**Severity**: LOW - no wrong write. `GetOutOfOfficeAsync` reads `StartTime`/`EndTime` from
`Get-MailboxAutoReplyConfiguration` with `as DateTime?`; if the Exchange Online module hands back
another type (a `DateTimeOffset`, a string, a wrapped PSObject), the cast yields null and the
Check Status tab shows a Scheduled auto-reply with no window, as if none were set, with no sign
that a value was dropped. Separately, the aud-a7g refusal says the clocks "repeat that hour" for a
zone whose clocks go back by another amount; the check itself is correct for any amount.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38j/a7f/a7g agent, 2026-10-22, candidates 2 and 3 of its hand-back)

## Evidence

At `f18431d0`, `Services/OutOfOfficeService.cs` `GetOutOfOfficeAsync` (the `as DateTime?` reads);
`Models/LookupModels.cs` `NoInstantReason` (the "repeat that hour" wording).

## Approach (to be written by the implementing agent before code)

- Read the two times through one helper that accepts `DateTime`, `DateTimeOffset`, a parseable
  string and a wrapped PSObject, and otherwise returns a "not readable" marker that Check Status
  renders as "schedule reported in a form this app cannot read: <type name>" instead of nothing.
  The onus is on the data; nothing is guessed. Label the live module's actual type as an unverified
  assumption in the record.
- Wording: "repeat that time" or "go back and repeat it", not "hour"; the test asserts the message
  does not assume an hour (or simply that the refusal fires for a zone with a 30-minute repeat, using
  a custom `TimeZoneInfo` with such an adjustment rule).
- Tests through the a7b/a7d/a7e/a7f harness: a `DateTimeOffset` read renders as the same instant; a
  string read renders; an unreadable type renders the marker, never an empty window. Mutation at the
  call site: `as DateTime?` again.

## Guard proof

Pending.
