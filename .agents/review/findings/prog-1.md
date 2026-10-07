# prog-1: The progress guard does not prove the slow derive is inside the window

**Severity**: MEDIUM - the runtime code is correct today, but the new guard is vacuous for
one of the two things the fix promised
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## Evidence

`ExchangeAdminWeb.Tests/ProgressRegistry.cs:395` registers
`CloudPasswordReset.ExecuteResetAsync` as `Reports` with
`CoveredCall = "ResetService.ResetPasswordAsync("`.

The handler does TWO slow things: the documented ~30-second destination derive at
`Components/Pages/CloudPasswordReset.razor:427` and the PATCH at `:464`. The registry
assertions (`ProgressRegistryTests.cs:540`, `:587`) compare `Progress.Begin`/`Complete`
against the registered covered call ONLY. `CloudPasswordResetWritePathTests.cs:82` pins the
`Task.Run` text but asserts nothing about progress ordering.

Triggering condition: a later edit moves
`destination = await Task.Run(() => ResetService.DeriveDestination(fresh));` above the
`Progress.Begin` while leaving it before `ResetPasswordAsync`.

Predicted observable failure: every test still passes. The operator waits through the long
directory search outside the global activity - **the exact behaviour the S2 fix was written
to remove.**

## Coder dispute

None. The implementing agent chose the PATCH deliberately, reasoning that naming the LAST
heavy call also pins `Complete` into the `finally`. That reasoning is sound as far as it
goes and is why the entry looks defensible. It is also incomplete: naming the last call
proves nothing about anything before it, and the derive is the slower of the two.

**This is the fifth vacuous-guard finding in five days and the second on this exact feature.**
The pattern each time: an assertion that is true of the code as written, standing in for an
invariant it does not enforce.

## Approach

Pending. Likely: the entry names the derive AND a second invariant pins `Complete` after the
PATCH - one covered call cannot carry both ends.

## Known gaps

Pending.

## Reviewer comments

Change review of `56e7f2c..ed7754f`, finding 1 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard

It also reported a limitation honestly: **it did not reproduce the full suite** - an earlier
full run stalled with no output and was stopped. It verified the focused
`ProgressRegistryTests` (11/11) and `git diff --check` on the range. The coder-side full-suite
result (3651 passed) therefore stands unconfirmed by the reviewer.

It tried and FAILED to construct a `DelegatesTo` chain laundering
`BlockedSenders.ConfirmUnblock` past the guard, and confirmed it is correctly a `KnownGap`.
It also confirmed the `Task.Run` hop is safe: `DeriveDestination` reaches the singleton
`ADDirectorySearchService`, which serialises on a `SemaphoreSlim`, and component state is
mutated only after the await resumes.

## Closeout

Pending.
