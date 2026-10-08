# prog-5: CloudPasswordReset's window does not pin its delivery tail

**Severity**: MEDIUM - guard false-negative on the most sensitive operation in the app
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## How this was found, which is the point

**Not by a reviewer.** The agent implementing the OutOfOffice fix (`2d93c26`) reasoned about
where its own closing pin belonged, looked across at the already-landed CloudPasswordReset
entry, and reported that it stops short. Codex then verified it independently.

That entry was reviewed and passed twice: once as the original S2 fix, and again after
`prog-1` added a second covered call to it. **Neither review caught it, and an implementer
working on a different page did.**

## Evidence

`ExchangeAdminWeb.Tests/ProgressRegistry.cs:479` registers
`CloudPasswordReset.ExecuteResetAsync` with exactly two covered calls:
`ResetService.DeriveDestination(` and `ResetService.ResetPasswordAsync(`.

The handler does not end at the PATCH. `Components/Pages/CloudPasswordReset.razor:464` is
the reset; `:504` sends `Email.SendCloudPasswordResetAsync`; `:528` awaits
`NotifyAdminsAsync`; the `finally` completes at `:542`.

Codex's reproduction: adding `activity.Complete` immediately after `ResetPasswordAsync` in a
disposable copy - `ProgressRegistryTests` **still passed 25/25**.

Predicted observable failure: a future edit moves the completion up to the PATCH, the
registry stays green, and the operator watches the bar go idle during password delivery and
admin notification **after an irreversible reset** - the one operation on these pages where
an operator most needs to know work is still in flight.

## Coder dispute

None. `prog-1` fixed the OPENING end of this window and nobody checked the closing end in
the same pass. The multi-call shape that `prog-1` introduced is exactly what makes this
fixable in one line.

## Approach

Pending. Add the genuinely last remote call to the covered-call list, then probe the
early-`Complete` mutation and require it to fail - the shape `2d93c26` and `f1257bd` both
use.

## Known gaps

Codex's full/normal build stalled again with no output; it killed the process tree and used
no-build filtered runs. Probes B and C from the OutOfOffice slice were reproduced in a
disposable tree. Third review in a row where the solution-level build stalled for the
reviewer but not for the implementing agents - worth its own look if it recurs.

## Reviewer comments

Raised by the implementing agent of `2d93c26`; verified by codex during the review of
`28a252e..2d93c26`, which was otherwise **sound**.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/prog-s2b.result.json`.

## Closeout

Pending.
