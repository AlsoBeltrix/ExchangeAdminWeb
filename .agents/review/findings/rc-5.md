# rc-5: a tick made during confirmation was ignored by Remove completed instead of withdrawing it

**Severity**: MEDIUM - the removed set still matched the confirm bar, so nothing wrong was
destroyed. But an operator could stage **Remove completed (3)**, tick one row believing they
had narrowed it, press Confirm, and watch three mailboxes go - and the row preview that would
have contradicted them had silently vanished at the moment they ticked.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from
`git log -1 -- .agents/review/findings/rc-5.md`

## Evidence

`Components/Pages/Migration.razor` - `ToggleMailboxSelected`, `ToggleSelectAllMailboxes` and
`ClearMailboxSelection` are reachable while `pendingActionLabel != null` (owner ruling D2(a):
the mailbox tick boxes carry no disabled attribute and no handler guard), and each called
`ClearStagedPreview()`.

`ClearStagedPreview` nulls `pendingBatchAction`, `pendingActionPlan`, `pendingUserAction` and
`pendingUserActionPlan`. It does NOT null `pendingActionLabel`, `pendingActionTarget` or
`pendingActionCallback`. So the staged action survives a selection change with its preview
destroyed.

For the five menu actions that is correct and deliberate: their staged callback re-plans from
the LIVE `selectedMailboxes`, so the tick IS an input to the confirm step, which is what
D2(a) says. For Remove completed it is not: `rc-1` made its callback re-plan from
`agreed = staged.Eligible`, captured at staging, which a later tick cannot reach.

## Predicted observable failure

Open a batch with completed mailboxes A, B and C and nothing ticked. Click
**Remove completed (3)**; the three rows read "Will run" and the confirm bar says
`Remove completed  3 mailbox(es)`. Now tick A. The three annotations disappear, and the
button - greyed out, but rendering - recomputes to **Remove completed (1)**. Type the ticket
and Confirm: A, B and C are all removed. Two numbers were on screen and nothing said which
one governed.

## What

`rc-3` and this are the same class seen from two sides: a control left live during the
confirmation step can make the staged action's display lie. `rc-3` was about the filter
hiding the preview; this is about the selection hiding it AND changing the number beside it.

The snapshot itself is right - it is `rc-1`'s fix and the reason nothing wrong is destroyed.
What was missing is that an action which cannot honour a selection change must not pretend to
have absorbed it.

## Approach

A new page field, `pendingActionScopeIsFixed`, true only while the staged action's scope is a
snapshot. It is lowered by both staging entry points (`StageBatchAction`, `StageUserAction`)
and by `CancelPendingAction`, and raised in exactly one place:
`StageRemoveCompletedMailboxes`, after its `StageBatchAction` call, which lowers it.

The three mailbox selection handlers now call `StagedActionFollowsTheSelection()`:

    if (pendingActionScopeIsFixed) CancelPendingAction(); else ClearStagedPreview();

So the five menu actions behave exactly as before, and Remove completed WITHDRAWS: the ticket
field and the confirm bar go, the button is live again showing the new scope, and re-staging
is one click.

**This applies D2(a) rather than overruling it, and that distinction is the reason this shape
was chosen over the reviewer's first suggestion.** The reviewer offered either disabling the
mailbox selection controls during confirmation or cancelling the staged action, and noted
itself that the first may be ruled out. It is: D2(a) is an owner ruling and queue item 23 was
a prod blocker caused by refusing a tick, so these boxes get no disabled attribute and no
handler guard. Withdrawing touches neither. The tick is still an input to the confirm step -
this action simply no longer has a confirm step for it to be an input to.

The field is registered in `ClickGateRegistry.ExcludedFields` alongside the four existing
staged-confirmation fields, so folding it into `IsBusy` fails a test. That matters for the
same reason it matters for `pendingActionLabel`: it is raised while waiting for a ticket, and
a predicate naming it would disable Confirm at the only moment Confirm renders.

## Files changed

- `Components/Pages/Migration.razor` - the field, the helper, four assignment sites, three
  call sites. Reasoning in place at the field and at the helper.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - the new `ExcludedField`, and
  `ExpectedLineCount` 4331 -> 4382. Every hunk is at old line 1730 or below... that is, at or
  ABOVE 1730, and this page's highest line key is 1540, so no key moved. Read off the diff
  hunk headers.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` - one test.

No module version bump: this corrects the unreleased `1.23.0` slice.

## Guard proof

`MigrationStatusPageTests.RemoveCompleted_IsWithdrawnByASelectionChangeInsteadOfIgnoringIt`
pins all four sites of the flag - raised exactly once in the whole page and in
`StageRemoveCompletedMailboxes`, lowered in `StageBatchAction`, `StageUserAction` and
`CancelPendingAction` - then requires each of the three selection handlers to call the helper
AND to contain no direct `ClearStagedPreview();`, then requires the helper to branch on the
flag with both arms present.

The negative per handler is the half that bites: asserting only that the helper is called
passes a handler that calls both, which would restore the defect while reading as fixed.

Two mutation probes, both compiled, both checked for `error CS` before the result was read:

- **A, the branch neutered** - `if (false && pendingActionScopeIsFixed)`, so the action is
  never withdrawn: 1 failed / 105 passed, naming exactly this test.
- **B, the pre-fix code verbatim** - `ToggleMailboxSelected` restored to
  `ClearStagedPreview();`: 1 failed / 105 passed, naming exactly this test.

Page restored from a copy outside the tree after each, verified byte-identical by md5
(`76b0d660c7dc1ee043b17e01ac0becfc`), then touched.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors. `dotnet test ExchangeAdminWeb.slnx`
3725 passed / 0 failed / 3 skipped (3724 before; one test added).
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0.
`git diff --check HEAD` clean. No non-ASCII added.

## Coder dispute (if any)

None on the defect; the reviewer's second option was taken and its own caveat about D2(a) was
correct.

## Known gaps

**The BATCH selection controls are not covered and are deliberately out of scope.**
`ToggleBatchSelected`, `SelectOnlyBatch` and `RemoveFromSelection` never called
`ClearStagedPreview` at all, so they already left a stale preview over a staged action before
this change - pre-existing, a different defect, and changing the batch pane on a mailbox
finding would be scope creep. Changing the batch selection also reloads `batchUsers`, so a
staged Remove completed degrades gracefully there: the planner drops addresses it cannot find
and the run reports "Nothing to do".

Still a source-level guard: it proves the call graph, not that the confirm bar disappears in
a browser.

## Reviewer comments

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Harness: codex-cli 0.159.0. Round 3, reviewing `029d9d0..e1b73e7`, verdict `findings` (2),
capability proof passed. Envelope `.agents/review/rcimpl3.result.json`.
