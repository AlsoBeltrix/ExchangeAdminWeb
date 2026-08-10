# mbs-1: Inner per-user actions still render the ticket prompt at the top of the table

**Severity**: MEDIUM -- the reported off-screen-ticket defect remains live for every per-user
action inside an expanded batch. Not HIGH: nothing is mis-targeted, mis-audited, or wrongly
authorised; the operator is confused and the prompt is hard to find, which is exactly the
complaint this work stream exists to close.
**Status**: Fixed
**Branch**: -- (default-branch mode)
**Commit**: `1ef7fae`

## Evidence

`Components/Pages/Migration.razor:1317-1324` -- `StageUserAction` sets `pendingActionTarget` to the
user's **email address**:

```csharp
private void StageUserAction(string label, string email, Func<string, Task> callback)
{
    pendingActionLabel = label;
    pendingActionTarget = email;
```

The inline confirm row added in slice 3 renders only where the pending target matches a **batch
name** (`:620-624`):

```razor
@if (pendingActionTarget == batchName && pendingActionLabel != null)
```

An email never equals a batch name, so `PendingActionNamesALoadedBatch` (`:1187-1189`, which tests
`migrationBatches?.Any(b => b.BatchName == pendingActionTarget)`) is false and the top-of-table
fallback at `:438-440` renders instead. Every inner action is affected: `CompleteUser`,
`ApproveUser`, `PauseUser`, `ResumeUser`, `ClearUser` (`:1302-1306`).

## Predicted observable failure

Expand a batch with many users, scroll to a user row low in the inner table, click **Resume** or
**Clear**. That row's buttons all go disabled (they gate on `pendingActionLabel != null`), and the
ticket input renders above the OUTER batches table -- off-screen. The operator sees the buttons
stop working with no visible prompt: the exact failure reported for batch rows, still live one
level down.

## What

The plan's D1 says the inner per-user table keeps its one-at-a-time actions and "gets the (2) fix,
because the confirm bar it uses is the same shared one." The implementation delivered the outer
half only. The slice-3 note listing the non-row cases named just Clear Completed and the two bulk
actions, and I built to that list rather than to D1 -- so the ruling and the slice note disagreed,
and the narrower one silently won.

This is the same shape as pps-2 in the previous work stream: a lesson recorded about two gates,
then applied to only one of them. The reviewer found it because it read the plan as a claim to
check rather than as a description of the code.

## Approach

`PendingActionConfirm` is already a single `RenderFragment`; only its placement was missing a case.
The inner `batchUsers` loop now renders it beneath the row whose action is pending, matched on
`pendingActionTarget == userEmail`, mirroring the outer row exactly.

`PendingActionNamesALoadedBatch` becomes `PendingActionNamesALoadedRow`, which now also tests the
loaded `batchUsers` -- so the top-of-table fallback still fires for Clear Completed and the two
bulk actions (whose target is a count), and still fires if the named row has disappeared, which is
what keeps the bar from becoming unreachable.

Matching on the email is safe against the batch/user ambiguity: an inner row renders only inside
the expanded batch, and the two loops cannot both match the same string in practice. Even if they
did, the bar would render twice rather than not at all -- the failure direction that matters is the
one where the operator cannot find it.

## Files changed

- `Components/Pages/Migration.razor` -- inline confirm row inside the `batchUsers` loop;
  `PendingActionNamesALoadedBatch` -> `PendingActionNamesALoadedRow`, extended to user rows.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` -- guard anchored inside the user-row loop,
  plus the renamed-member guard and the render-site count raised to three.

## Guard proof

`MigrationStatusPageTests.ThePerUserTicketFieldRendersInsideTheUserLoop` -- anchored inside the
`@foreach (var user in batchUsers)` body, extracted brace-balanced, so a confirm row placed
anywhere else on the page cannot satisfy it.

Probe: reverting the inner confirm row fails **2 of 26** (the new guard and the render-site count);
reverting the `PendingActionNamesALoadedRow` extension alone fails **1 of 26**. Both reverts were
confirmed on disk before trusting the verdict and confirmed gone after, and the file was touched
after each restore -- `Copy-Item` preserves the backup's timestamp, which had already caused MSBuild
to test a mutant against correct restored source once in this work stream.

## Coder dispute (if any)

None. Verified independently against the current code before accepting: `StageUserAction` really
does set an email, and the inline row really does require a batch-name match.

## Known gaps

Still a source-level tripwire, like every other guard on this page -- it proves the markup is in
the loop, not that an operator sees the field beneath the row. The plan's manual check 5 is extended
to cover an inner user row and remains unrun.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.147.0 (`codex exec`, headless one-shot, `-s read-only`).
Generation pass over `c6abcdf..e70dfb2` (8 files). `capability_ok: true`; both SHAs echoed
verbatim. Verdict `findings`, 1 finding -- this one. 2026-08-10 UTC.

Its severity (MEDIUM) and its proposed remedy are recorded as it wrote them; both were accepted
rather than re-derived. It also proposed the guard anchoring used above.

No other finding in the range: the planner logic, the extracted bulk executor, the audit-per-batch
loop, the name-keyed selection, and the version bump drew no comment.
