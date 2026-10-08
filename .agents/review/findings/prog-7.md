# prog-7: A ClickGate exemption's written condition is false

**Severity**: LOW - the code is safe as written; the CLAIM that keeps the exemption honest
is untrue, which is the part that matters for a registry whose whole value is its conditions
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## Evidence

`ExchangeAdminWeb.Tests/ClickGateRegistry.cs`, the exemption at
`MailboxPermissions.razor:179` / `CalendarPermissions.razor:178`, states:

> "SubmitSingle and ExecuteOnPrem both hold their outcome in a local opResult and only ever
> assign to result"

**`ExecuteOnPrem` reads it back on both pages**, in its `finally`:
`onPremActivity.Complete(result?.Success ?? false, result?.Message)`.

It is SAFE as written - no await sits between the last `result` write and that read, and
`?.` cannot throw if the dismiss control nulls it. But the written condition is what keeps
the dismiss-banner exemption honest, and it is false.

Predicted observable failure: none today. The risk is that a future edit introduces an await
in that gap, and the registry's own condition says no handler reads `result` back, so nobody
checks.

## Coder dispute

None. Found by the agent implementing `f2a8c75`/`63cd170` while verifying it could safely
NOT complete from a local - it read the registered condition, checked it, and found it
untrue. Recorded rather than fixed: outside that slice.

## Approach

Pending. Either correct the condition's wording to describe what the code does and why it is
safe, or change `ExecuteOnPrem` to read a local like its siblings. The second is the smaller
claim to maintain.

## Known gaps

This is the second registry in this repo found to carry a line-keyed or written claim nobody
asserts - `ProgressRegistry`'s line pointers were the first (nine found stale in one slice).
A registry is only worth its conditions; a false one is worse than none, because it stops the
next reader looking.

## Reviewer comments

Not yet reviewed - raised by an implementing agent, outside the slice it was found in.

## Closeout

Pending.
