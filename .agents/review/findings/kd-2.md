# kd-2: Migration - a tick from batch A survives into batch B as "Actions (1)" with no ticked row

**Severity**: LOW - confusing, not destructive. `Components/Pages/Migration.razor` `AdoptSelectionAsOpenBatch` (about 1840) never clears `selectedMailboxes`; `MigrationUserActionPlanner.PruneSelection` runs only through `TicksInThisBatch()` (about 2774).
**Status**: Merged to master `44fb74aa`; codex mig-batch-10 clean on this finding; Complete once the branch is deleted
**Branch**: `fix/a68-kd2` (forked at `eb017102`; worktree `D:\source\ExchangeAdminWeb-wt\a68-kd2`), shared with aud-a68
**Commit**: `14bf6a4f` (fix, tests, record); the Migration module bump (1.23.3 -> 1.24.0, shared with aud-a68 parts 5 and 7) and this record's close: the commit that completes this record (`git log -1 -- .agents/review/findings/kd-2.md`)
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: tick a mailbox in batch A, open batch B. Predicted failure: the action bar shows a count with no visible selection; the rc-1..rc-3 confirm-bar counting may disagree with the rows shown.

## Approach (to be written by the implementing agent before code)

- Adopting a different open batch clears or prunes the selection to that batch's rows; the page is line-keyed (`ClickGateRegistry`), so edit in place or re-anchor per the playbook.
- Mutation: the clear removed.

### As implemented (implementer, 2026-10-23, branch `fix/a68-kd2` forked at `eb017102`; lines at `eb017102`)

- `Components/Pages/Migration.razor` `AdoptSelectionAsOpenBatch` (1840-1862): when the open batch changes, the
  adoption (1846-1848) also clears `selectedMailboxes`, in place (no line moves). The early return at 1843-1844 is
  untouched, so re-adopting the batch already open keeps its ticks.
- `SyncOpenBatchFromUrl` (1903-1957) is the inbound half of the same adoption - browser Back, a pasted link, a reconnect
  - and carries the same tick over (1914-1916 never touch `selectedMailboxes`); it clears it too. It is the same defect
  by the second route rc-1 already names, so it is in scope.
- A clear, not a prune: at adoption the new batch's rows are not loaded (`batchUsers` is null), and
  `MigrationUserActionPlanner.PruneSelection(null, ...)` returns empty (rc-2's caution), so a prune there is a clear
  anyway; and a mailbox is in one migration batch at a time, so batch B holds none of batch A's ticks.
- What is staged is unchanged: the five menu actions re-plan from the live selection at Confirm, and Remove completed
  re-plans its fixed scope against the live rows; neither can act on a batch-A address in batch B (the planner acts only
  on addresses in `batchUsers`). The rc-2/rc-4 prune in `TicksInThisBatch` (2774-2775) stays; the comment above
  `RemoveCompletedCandidates` (2755-2765), which says nothing clears the selection when the open batch changes, is
  corrected in place.
- Test: new `ExchangeAdminWeb.Tests/MigrationMailboxSelectionTests.cs`, the page's own handlers over
  `MigrationStubExchange` with a recording `NavigationManager`: tick a mailbox in batch A, open batch B (a row click, a
  tick box, the address): nothing ticked, no "Actions (1)"; the control - the same batch re-adopted, or refreshed -
  keeps the tick. Mutation: the clear removed at `AdoptSelectionAsOpenBatch` (the record's); extra: at
  `SyncOpenBatchFromUrl`.
- Placement, settled by the tests: the clear sits on the `batchUsersGeneration++;` line (1847, 1915), not on
  `batchUsers = null;` - `MigrationStatusPageTests`' two report-close tripwires parse every `batchUsers = null;` line and
  require the report close on the next one, and both failed with the clear on that line.

## Files changed

- `Components/Pages/Migration.razor`: `AdoptSelectionAsOpenBatch` 1847 and `SyncOpenBatchFromUrl` 1915 (the clear, in
  place); the comment above `RemoveCompletedCandidates` 2755-2765 (in place). 4377 lines before and after; every changed
  line is below the highest line-keyed entry (1540), so no `ClickGateRegistry` key moves and `ExpectedLineCount` stands.
- `ExchangeAdminWeb.Tests/MigrationMailboxSelectionTests.cs` (new).
- `ExchangeAdminWeb.Tests/MigrationUserActionPlannerTests.cs`: the rc-2 test's comment, which said nothing clears the
  selection (comment only).

## Guard proof

New `ExchangeAdminWeb.Tests/MigrationMailboxSelectionTests.cs`, 4 tests (5 cases), the page's own handlers over
`MigrationStubExchange` (two batches, a mailbox in each) with a navigation stand-in and the compiled render tree:
`OpeningAnotherBatch_CarriesNoTickOver` (a row click), `OpeningAnotherBatchByItsTickBox_CarriesNoTickOver`,
`OpeningAnotherBatchFromTheAddress_CarriesNoTickOver` (through `SyncOpenBatchFromUrl` on an interactive renderer) - each:
batch B open, nothing ticked, one "Actions (" bar (the ticked batch's) where there were two; and
`TheSameBatch_KeepsItsTick` (its row clicked again; its mailboxes refreshed). Written before the fix and run against it:
the three finding cases failed, `Assert.Empty() Failure: Collection was not empty ["alice@contoso.test"]`; the controls
passed. Filter `MigrationMailboxSelectionTests|MigrationStatusPageTests|ClickGate`, 427 tests. md5 as checked out (CRLF),
restores byte-exact from a copy outside the tree with a fresh mtime (session scratchpad `work/mut.sh`).

| Round | md5 before / mutated | Failed (of 427) | Restored |
| --- | --- | --- | --- |
| The fix reverted (the page back to `6f0d6dfd`'s) | `7fd3115d...` / `57d3ab57...` | 3: the three finding cases | md5 equal; 427/0 |
| K1 (the record's): the clear removed at `AdoptSelectionAsOpenBatch` | `7fd3115d...` / `41fb7a4c...` | 2: the row click and the tick boxes | md5 equal; 427/0 |
| K2: the clear removed at `SyncOpenBatchFromUrl` | `7fd3115d...` / `213bae6f...` | 1: the address | md5 equal; 427/0 |

With the fix: `Migration|ClickGate|ProgressRegistry`, 1107 passed / 0 failed, on the exact tree. On the kd-2 commit's
tree (`14bf6a4f`): full suite 6720 passed / 0 failed / 0 skipped - a run that overlapped aud-a68 part 5's edit to this
page, which the page-reading tests read from disk, so the 1107 above is the exact-tree proof for those; the branch tip's
full suite (`aud-a68.md`, "Branch-tip gates") covers both.

## Coder dispute

None. One widening, stated: the record names `AdoptSelectionAsOpenBatch`; `SyncOpenBatchFromUrl` is the same adoption
reached from the address (rc-1 names it as the second route to a reload), carried the same tick, and is fixed the same
way in the same commit.

## Known gaps

- A staged mailbox action survives a change of open batch: its confirm bar (at the top of the table once its rows are no
  longer loaded) still offers batch A's count over batch B. Confirm re-plans against the live rows and acts on nothing -
  not destructive, and pre-existing (it re-planned against B before this fix too) - but it disagrees with the rows shown.
  Named as a candidate, not fixed: withdrawing it is a staged-action rule (rc-5's) this record does not cover. Admitted
  as kd-13 and fixed on this branch (`WithdrawStagedMailboxAction`, on this record's two lines).
- `docs/MigrationRemoveCompleted-Plan.md` (Implemented, history) still describes the pre-fix condition in its rc-2
  paragraph; left as history.
- No circuit or browser ran: the handlers and the compiled render tree were driven directly.

## Reviewer comments

Pending: the codex defect review of the merged group (the supervisor dispatches it).

## Closeout

Fixed on branch `fix/a68-kd2`; not merged, not pushed. Pending step: the supervisor's merge into master, then the codex
review; at merge take this branch's copy of this record.

## Reviewer comments (supervisor, mig-batch-10)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 86953582 (merged 44fb74aa); 2026-10-23 evening; capability proof: AGENTS.md read and git show --stat 86953582 both succeeded. One candidate, admitted as kd-17 (bulk denial rows name the action as target). Codex found nothing else across archive states, not-found classification, the no-row statistics warning, lookup failure audit and activity handling, cancellation outside the admitted kd-14 paths, bulk action labels, kd-2 selection clearing, the ClickGate re-anchoring and the named test harness shapes. Artefacts: `.agents/review/mig-batch-10.result.txt`, `.stream.log`, `prompt-mig-batch-10.md`.
