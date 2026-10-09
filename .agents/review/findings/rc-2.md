# rc-2: a mailbox ticked in another batch silently narrowed Remove completed

**Severity**: MEDIUM - the control reads as broken rather than acting wrongly. In a batch
where the operator has ticked nothing and that visibly holds completed mailboxes, it can
render **Remove completed (0)**, disabled, because a tick left behind in a different batch
sent it down the narrowing branch.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from
`git log -1 -- .agents/review/findings/rc-2.md`

## Evidence

`Components/Pages/Migration.razor` - `AdoptSelectionAsOpenBatch` sets `expandedBatch`, bumps
`batchUsersGeneration`, nulls `batchUsers`, calls `InvalidateStoredReports()`, blanks
`mailboxFilter` and resets the mailbox page. It does NOT touch `selectedMailboxes`.
`ReplaceBatchUsers` assigns the new rows and clamps the pagers, and does not either.

Grepped at the reviewed head: `MigrationUserActionPlanner.PruneSelection` is called from
NOWHERE on the mailbox side. `selectedMailboxes` is cleared only by `ClearMailboxSelection`
and by `ToggleSelectAllMailboxes`'s clear-then-refill. The planner's own remarks on
`PruneSelection` say "Called after every reload: a mailbox that has left the batch must not
stay ticked" - that sentence was false.

`MigrationUserActionPlanner.NarrowToSelection` takes the selection branch whenever the
selection is non-empty, by design (owner ruling 2026-10-09). With a stale address in the set,
that branch is taken in a batch where nothing is ticked.

## Predicted observable failure

Open batch A, tick one mailbox, then open batch B - whose mailboxes do not include that
address. Batch B draws no ticked row, because `PinnedMailboxes` and the per-row tick box both
read `batchUsers`. But `selectedMailboxes.Count` is 1, so **Remove completed** is scoped to
an address that is not in batch B: it renders `(0)` and disabled over a batch that may have
fifty completed mailboxes in view. The operator sees a control that says there is nothing to
remove, looking at rows that say `Completed`.

## What

The new control is the first consumer of `selectedMailboxes` that behaves differently on the
EMPTY/NON-EMPTY distinction while rendering OUTSIDE the `selectedMailboxes.Count > 0` gate.
Every previous consumer is inside that gate, so the stale set showed up as a confusing
`Actions (1)` rather than as a wrong scope. The owner's narrowing rule turns "is anything
ticked" into a load-bearing question, and that question has to be asked about mailboxes in
the batch that is open.

## Approach

`RemoveCompletedCandidates()` prunes before it narrows:

    NarrowToSelection(FilteredSortedMailboxes(),
                      PruneSelection(batchUsers, selectedMailboxes))

`PruneSelection` reads `batchUsers`, NOT the filtered rows, and that is load-bearing: R11
says a ticked mailbox is never hidden by the filter - the page pins ticked rows above the
list for exactly that reason - so pruning against the in-view set instead would silently drop
those rows from the narrowing. A test pins that direction separately.

The wider condition is deliberately NOT fixed here. Calling `PruneSelection` after every
mailbox reload is one line and is probably right, but it changes behaviour for five shipped
actions, and `PruneSelection(null, selected)` returns empty - so a prune placed where
`batchUsers` can be null during a load would wipe a selection rather than clean it. That
needs its own slice, its own guard and its own thought about where the call goes. It is
recorded in `.agents/state.md` under "Live defects found and deliberately not fixed", with
the batch-side `PruneSelection()` wrapper named as the shape to copy.

## Files changed

- `Components/Pages/Migration.razor` - `RemoveCompletedCandidates()`, with the reasoning as a
  comment in place.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` -
  `RemoveCompleted_TakesItsCountAndItsScopeFromOneCandidateSet` now requires the prune and
  pins `selectedMailboxes` to exactly one occurrence in the helper.
- `ExchangeAdminWeb.Tests/MigrationUserActionPlannerTests.cs` - two new tests.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - `ExpectedLineCount` 4296 -> 4310. Both hunks
  are at 2715 or below in the old numbering... which is still above the highest line-keyed
  entry for this page (1528), so no key moved; read off the hunk headers.
- `docs/MigrationRemoveCompleted-Plan.md` - the candidate-set section carries the prune and
  the reason.
- `.agents/state.md` - the pre-existing wider defect, recorded rather than fixed.

No module version bump: this corrects the unreleased `1.23.0` slice.

## Guard proof

`MigrationStatusPageTests.RemoveCompleted_TakesItsCountAndItsScopeFromOneCandidateSet`
requires `MigrationUserActionPlanner.PruneSelection(batchUsers, selectedMailboxes)` verbatim
and asserts `selectedMailboxes` occurs exactly once in the helper - so passing the raw field
anywhere else in it fails too, not only replacing the prune.

Mutation probe: the prune argument replaced by the bare `selectedMailboxes`, which is the
defect verbatim and compiles. Result 1 failed / 101 passed, naming exactly that test. It
BUILT - checked for `error CS` before the result was read. Page restored from a copy outside
the tree, verified byte-identical by md5 (`5b9a3527e14541cdffd6d06fcd0b7927`), then touched.

`MigrationUserActionPlannerTests.ASelectionLeftOverFromAnotherBatchDoesNotNarrowThisOne` runs
both designs against the same inputs and asserts they differ: pruned, the candidates are
batch B's two mailboxes; unpruned, they are batch A's one address, which plans to zero
eligible.
`MigrationUserActionPlannerTests.PruningTheSelectionStillKeepsATickedMailboxTheFilterHides`
pins the direction that must NOT be lost to the fix.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors. `dotnet test ExchangeAdminWeb.slnx`
3721 passed / 0 failed / 3 skipped (3719 before; two tests added).
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0.
`git diff --check HEAD` clean. No non-ASCII added. No `.ps1`/`.psm1` touched.

## Coder dispute (if any)

The finding is correct; the reachability was verified independently by reading
`AdoptSelectionAsOpenBatch` and grepping every `selectedMailboxes` and `PruneSelection` site
rather than taken on the reviewer's word.

One narrowing of the reviewer's recommendation, stated rather than done silently. It offered
either "clear mailbox selection when the open batch changes" or "prune inside
`ReplaceBatchUsers`". Both change five shipped actions and one of them can wipe a live
selection on a reload that lands with `batchUsers` null, so neither belongs inside a finding
fix on a different control. The scoped prune closes the defect against the control the
finding is about, and the residue is written down where the next session will find it.

## Known gaps

The wider stale-selection condition is still live: the mailbox Actions bar can still render
`Actions (1)` in a batch with nothing ticked, and the five bulk actions there still plan over
an address the planner drops. Not destructive - the planner only ever acts on addresses
present in `batchUsers` - but confusing. Recorded, not fixed.

Still a source-level guard on the page half; there is no bUnit harness.

## Reviewer comments

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Harness: codex-cli 0.159.0. Reviewed `029d9d0..c57b41e`, verdict `findings` (2),
capability proof passed. Envelope `.agents/review/rcimpl.result.json`.
