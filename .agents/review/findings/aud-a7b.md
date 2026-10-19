# aud-a7b: Out of Office captures only the mailbox; its other inputs are still live reads

**Severity**: MEDIUM - the mailbox can no longer change under an operation, but the state,
messages, schedule and ticket still can. An auto-reply can be written with a body or schedule
the operator had already moved on from, and the audit row can name a different ticket than the
one the change was made under.
**Status**: Open - not started
**Branch**: -
**Commit**: -

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing the `aud-a7` fix, as a limit of that
fix rather than a defect in it.

`aud-a7` captured `var target = setEmail.Trim()` and uses it everywhere. The other inputs were
not captured: `Components/Pages/OutOfOffice.razor` reads `oofState`, the message fields, the
schedule and `ticketNumber.Trim()` at the write, and reads state and ticket **again** at the
audit call after further awaits.

Narrower than `aud-a7` was: the re-entry guard stops the same operator starting a second run,
so this needs the bound fields to change while one run is in flight. A two-way binding updating
from a component event during the awaited write is the realistic route.

## Predicted observable failure

An operator submits an auto-reply, edits the message or the end date while the Exchange write
is in flight, and the audit row records a ticket or state that does not match what was sent -
or the notification email describes a schedule that was never applied.

## Approach - NOT BUILT

Capture all of them next to `target`, as one snapshot taken before the first await, and use the
locals everywhere below. The shape already exists in this method; it was simply not extended
past the mailbox.

This is also where a behavioural harness belongs. The `aud-a7` guards are source scans, and
review proved concretely what that costs: they passed while the page had a stuck busy flag that
made the form unusable until reload. Comms-10k's reflection harness drives a real page
instance; extending it here would let both the capture and the flag be tested as behaviour
rather than as text.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19. Flagged as an existing limitation,
distinct from the stuck flag that review also found.

## Closeout

Not started.
