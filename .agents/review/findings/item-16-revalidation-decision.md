# Item 16: session revalidation was adopted without an explicit owner yes - the question, for the owner

Sweep `.agents/review/live-risk-sweep-2026-10-30.md` item 16. One decision; nothing here changes the feature.

## The question

Keep session revalidation (aud-a29b option 1) as the app's answer to "an open tab keeps access after its owner was removed
from an AD group", or remove it?

## Why it is asked now

- The aud-a29b record (`.agents/review/findings/aud-a29b.md:47-48`) says of option 1: "It should not be chosen by an agent."
- It was taken under the 2026-10-21 general go ("ALL the bugs ... EVERYTHING"); `.agents/decisions.md` (2026-10-21) records
  "the go selects option 1 ...; the owner may veto before it lands". No explicit owner yes was ever recorded.

## What it does today

- **Problem it solves.** A Blazor tab keeps the Windows sign-in it opened with, for hours. Without a re-check, an operator
  removed from a group (or disabled) keeps every action that group allowed until they reload - and under Kerberos even a
  reload brings back the old groups until the ticket renews.
- **What runs.** Each open tab is re-checked against Active Directory when it connects and every 5 minutes
  (`Authentication:RevalidationInterval`, allowed 00:00:30-01:00:00; the app refuses to start outside that - the ceiling
  was not in the record), and every page request before it renders. It compares the groups the app's access rules use,
  and the account's enabled flag. A removed group, a disabled account, a removed access grant, or a check that cannot be
  completed ends the session ("This session has ended") with an audit line.
- **Off-switch.** `Authentication:RevalidationEnabled=false` (not in the record either; added before review): no re-check,
  no revocation; logged and audited at startup.
- **Size.** cb3be915, about 1,100 lines - the largest production commit of the October range - plus five follow-ups.

## What it has cost

- **kd-87** (2026-10-29 dev deploy): it read `tokenGroups` in a form AD refuses, so every page request was refused.
- **kd-90 / sweep item 5** (2026-10-30 dev): it read a disposed Windows identity and signed an operator out with no warning
  line and no audit row.
- **Sweep item 15**: one failed or hung directory lookup ended the session; a two-minute ADWS stall would end every tab
  checked during it. Fixed on `fix/sweep-c` (`sweep-c-15`): one failed check now falls back to the previous answer.
- Every page now waits on a directory read at least once per account per interval (about 3 s on the first page after a
  restart, 6 ms within the interval, measured on dev).

## What removing it would mean

- The 1,100 lines and their tests go; kd-87, kd-90 and item 15 stop mattering; pages stop waiting on AD.
- The original exposure (aud-a29b) returns: a removed or disabled operator keeps an open tab's access until it closes or
  the app restarts, and the audit records their actions as authorised. Option 2 (a directory read before each mutating
  action) would cover writes only, and would put a read on every write click; option 3 (shorter circuit lifetime) only
  narrows the window.
- The interim alternative is the off-switch: no code change, the same exposure, audited at startup.

## Recommendation

Keep it, with the sweep-c-15 fallback merged: it is the only option that closes the exposure on every page, and its three
live defects are fixed or have fixes on a branch. Confirm the dev deploy's log is clean of revalidation errors for a working
day before prod. If the owner prefers less machinery, the cheaper safe choice is option 2 for writes plus the off-switch -
but that leaves read-only disclosure open, which is why aud-a29b ranked option 1 first.

## The owner's answer

Not yet asked. Record it in `.agents/decisions.md` when given.
