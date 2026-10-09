# rc-1: Remove completed could remove more mailboxes than the confirm bar counted

**Severity**: HIGH - the control's entire safety case is that the number in the label is a
bound on what it destroys. It was not. A mailbox pane refresh while the operator is at the
ticket field could promote a mailbox that was previewed as SKIPPED into the destructive
action, so "Remove completed (3)" could remove four.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from
`git log -1 -- .agents/review/findings/rc-1.md`

## Evidence

`Components/Pages/Migration.razor:2766-2773` as landed in `c57b41e`:
`StageRemoveCompletedMailboxes` captured `var scope = RemoveCompletedCandidates();` and the
staged callback re-planned `MigrationUserActionPlanner.Plan(batchUsers, scope,
MigrationUserAction.Clear)` against live rows at Confirm.

The candidate set is NOT the eligible set, and deliberately so: it is every in-scope mailbox,
including the ones that are not `Completed`, because that is what lets the preview name each
one as skipped on its own row (`MigrationUserActionPlanner.Plan` partitions it;
`Services/MigrationUserActionPlanner.cs:120-146` in the pre-change numbering).

Rows can reload while the ticket field is open. The mailbox pane's Refresh button carries
`disabled="@IsBusy"` only - not `pendingActionLabel != null` - and `RefreshBatchUsers` checks
only `IsBusy`. `OnParametersSetAsync` reaches `SyncOpenBatchFromUrl` on any navigation, which
is a second route to the same reload.

## Predicted observable failure

Open a batch whose filtered view holds two `Completed` mailboxes and one `Syncing`. The
button reads **Remove completed (2)** and the preview marks the third row `Skipped (Syncing)`.
Click it, then refresh the mailbox pane while typing the ticket; the third mailbox has
finished syncing and now reports `Completed`. Press Confirm. The confirm bar said
`2 mailbox(es)`; three mailboxes are removed from the batch, and the row the operator read as
"Skipped" is one of them.

## What

The snapshot was taken at the wrong layer. HIGH 2 of the plan review was about stopping the
scope being recomputed from live page inputs, and the fix stopped it being recomputed from
the FILTER and the SELECTION - but left it recomputed from the live STATUSES, which is the
third input and the one the planner reads.

Capturing the candidates bounds the membership and not the outcome. The count the operator
agrees to is an eligible count, so the only set that is a bound on that count is the eligible
list itself.

## Approach

`StageRemoveCompletedMailboxes` now plans over the candidates once, for the preview and the
count, and snapshots `staged.Eligible` as `agreed`. The staged callback re-plans
`Plan(batchUsers, agreed, Clear)`.

The shrink direction is unchanged and still honest, which is why the snapshot is of ADDRESSES
and the callback still re-plans rather than reusing the staged plan object:

- A mailbox in `agreed` that is no longer `Completed` becomes a skip, is named with its
  status on its own row, and is not removed.
- A mailbox in `agreed` that has left the batch is dropped by the planner, neither acted on
  nor named - the planner's documented behaviour, because naming a row the operator can no
  longer see is worse than saying nothing.

The PREVIEW is deliberately left as the full plan over the candidates
(`pendingUserActionPlan = staged`). The two have different jobs: the preview is the check
made BEFORE a ticket is typed and must name everything in scope and why it will or will not
run; only the execution is bounded. A reviewer reading only the execution path should not
conclude the skipped rows were dropped from the UI.

A side effect worth stating, because it is an improvement and not a regression: the result
banner for a no-selection run on a 2000-mailbox batch used to read "Queued removal of 3, 1997
skipped of 2000 mailboxes." It now reads "Queued removal of 3 of 3 mailboxes.", and reports a
skip only when one of the three agreed mailboxes genuinely stopped being eligible.

## Files changed

- `Components/Pages/Migration.razor` - `StageRemoveCompletedMailboxes`, with the reasoning as
  a comment in place.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` -
  `RemoveCompleted_StagesASnapshotAndNeverRecomputesItsScopeAtConfirm` re-pointed at
  `agreed`.
- `ExchangeAdminWeb.Tests/MigrationUserActionPlannerTests.cs` - one new test.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - `ExpectedLineCount` 4280 -> 4296. Every
  line-keyed entry for this page is at or below 1528 and every hunk of this fix is at 2760 or
  below in the old numbering, so no key moved; checked against the hunk headers rather than
  assumed.
- `docs/MigrationRemoveCompleted-Plan.md` - the Design section said "the candidate address
  set" and now says `plan.Eligible`, with the reason.

No module version bump: `Migration` moved to `1.23.0` in `c57b41e` and this corrects that
unreleased slice rather than shipping a second behaviour change on top of it.

## Guard proof

The page tripwire is the guard; the planner test is the characterisation that proves the two
designs differ, and is honestly not a guard on the page.

`MigrationStatusPageTests.RemoveCompleted_StagesASnapshotAndNeverRecomputesItsScopeAtConfirm`
now requires `var agreed = staged.Eligible;` verbatim, exactly one occurrence of
`RemoveCompletedCandidates` in the method, exactly one
`Plan(batchUsers, agreed, MigrationUserAction.Clear)`, and no mention of `selectedMailboxes`
or `FilteredSortedMailboxes` anywhere in the method.

Mutation probe: `var agreed = staged.Eligible;` replaced by
`var agreed = RemoveCompletedCandidates();`, which is the defect verbatim and compiles.
Result 1 failed / 101 passed, naming exactly that test. It BUILT - checked for `error CS`
before the result was read. Page restored from a copy outside the tree and verified
byte-identical by md5 (`7f0d5d4c3854843a0e372ea0967994f2`), then touched so MSBuild rebuilt.

`MigrationUserActionPlannerTests.ACandidateThatWasSkippedAtStagingCannotBePromotedByTheTimeOfConfirm`
runs both designs against the same before/after row sets and asserts they differ: planning
from `staged.Eligible` keeps two, planning from the candidates reaches three.

## Verification after restore

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors. `dotnet test ExchangeAdminWeb.slnx`
3719 passed / 0 failed / 3 skipped (3718 before this fix; one test added).
`dotnet test --filter "FullyQualifiedName~ClickGate"` 316 green within that run.
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0.
`git diff --check HEAD` clean. No non-ASCII added. No `.ps1`/`.psm1` touched.

## Coder dispute (if any)

None. The finding is correct as written and the recommended approach was taken whole. The
reviewer's own wording for the fix - "snapshot the eligible address list used for the
displayed count, then re-plan at execution from that eligible snapshot" - is what shipped.

## Known gaps

Still a source-level guard: it proves the shape of the method, not that the rendered control
behaves. There is no bUnit harness here.

The bound is on the EXECUTION only. If a mailbox in `agreed` is removed by someone else
between staging and Confirm, this run reports nothing about it - the planner drops it. That
is the pre-existing and deliberate behaviour of every action on this page.

## Reviewer comments

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Harness: codex-cli 0.159.0. Reviewed `029d9d0..c57b41e`, verdict `findings` (2),
capability proof passed (plan read, `git diff --stat` run). Envelope
`.agents/review/rcimpl.result.json`; prompt and schema alongside it.
