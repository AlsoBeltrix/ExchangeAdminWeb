# rc-4: the tooltip described a narrowing the control was not doing

**Severity**: LOW - the button's count and the set it acts on were both correct; only the
tooltip was wrong. But the tooltip is the only place the scope is stated in words, so it is
the only thing an operator has to check the number against.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from
`git log -1 -- .agents/review/findings/rc-4.md`

## Evidence

Introduced by the `rc-2` fix and found in the same review round.
`RemoveCompletedCandidates()` classified "is anything ticked" from
`PruneSelection(batchUsers, selectedMailboxes)`, while `RemoveCompletedTitle` still branched
on the raw `selectedMailboxes.Count > 0`. Two readings of the same question, one pruned and
one not.

## Predicted observable failure

Tick a mailbox in batch A, open batch B, where that address is not loaded and completed rows
are visible. The button correctly reads **Remove completed (N)** over every completed mailbox
in B - the prune dropped the stale tick - but its tooltip says "Removes the N ticked
mailbox(es) that have completed". The operator reads a narrower scope than the control will
act on, in a batch where they have ticked nothing.

## What

`rc-2` put the pruning inside the candidate helper and left the other reader of the same
question alone. The lesson is the one this control already had written at the top of the
candidate helper and then broke one member later: a classification used in more than one
place has to be computed in one.

## Approach

The pruned selection is extracted into `TicksInThisBatch()`, and both the candidate helper
and the title read it. The raw `selectedMailboxes` field is now referenced exactly once in
the whole Remove-completed area, inside that one member.

The gate and the label are unaffected: they read `RemoveCompletedCount`, which reads the
candidate helper, which reads `TicksInThisBatch()`.

## Files changed

- `Components/Pages/Migration.razor` - `TicksInThisBatch()` extracted;
  `RemoveCompletedCandidates()` and `RemoveCompletedTitle` both read it.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` - the candidate tripwire now requires
  the helper and forbids the raw field in `RemoveCompletedCandidates`, and one new test does
  the same for the title.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - `ExpectedLineCount` 4322 -> 4331. Both hunks
  are at 2729 or below in the old numbering, well above this page's highest line key (1540),
  so no key moved; read off the hunk headers.

No module version bump: this corrects the unreleased `1.23.0` slice.

## Guard proof

`MigrationStatusPageTests.RemoveCompleted_TakesItsCountAndItsScopeFromOneCandidateSet`
requires `NarrowToSelection(FilteredSortedMailboxes(), TicksInThisBatch())` verbatim, forbids
`selectedMailboxes` anywhere in `RemoveCompletedCandidates`, and separately requires
`PruneSelection(batchUsers, selectedMailboxes)` inside `TicksInThisBatch`.

`MigrationStatusPageTests.RemoveCompleted_TellsTheOperatorTheScopeItIsActuallyUsing` extracts
the title property by its signature, requires `TicksInThisBatch().Count > 0`, and forbids the
raw field in it. Forbidding is what catches the defect: requiring the helper alone passes a
title that reads BOTH.

Mutation probe: the title's test reverted to `if (selectedMailboxes.Count > 0)`, which is the
defect verbatim and compiles. Result 1 failed / 103 passed, naming exactly that test. It
BUILT - checked for `error CS` before the result was read. Page restored from a copy outside
the tree, verified byte-identical by md5 (`a4bb3b974be68628cce55b27aba51d2c`), then touched.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors. `dotnet test ExchangeAdminWeb.slnx`
3723 passed / 0 failed / 3 skipped (3722 before; one test added).
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0.
`git diff --check HEAD` clean. No non-ASCII added.

## Coder dispute (if any)

None. The reviewer's recommendation - "preferably via a shared small scope helper so
label/count/title/staging all classify the scope from one value" - is what shipped.

## Known gaps

`TicksInThisBatch()` is evaluated once more per render than before, inside the title. The
whole Remove-completed area now costs four passes over the loaded mailboxes per render where
this pane already does about six; deliberately not cached, because a cached classification
is one that can disagree with the set, which is the defect this finding is.

## Reviewer comments

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Harness: codex-cli 0.159.0. Round 2, reviewing `029d9d0..c08f9e8`, verdict `findings` (2),
capability proof passed. Envelope `.agents/review/rcimpl2.result.json`.
