# Migration - Remove Completed Mailboxes - Plan

Status: **APPROVED 2026-10-09** - the owner approved the direction and settled the selection
rule. Revision 2 resolves the four findings a codex review raised against revision 1
(`.agents/review/rcplan.result.json`) plus two stale implementation instructions found while
resolving them; what changed is marked in place.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** Adds a **Remove completed (N)** button above the mailbox list inside a
batch. It is visible without selecting anything, it says how many it will remove, and it
removes exactly those - the finished mailboxes - and nothing else.

**What it gets you.** Clearing finished users out of a batch becomes one obvious click
instead of a hunt. The capability already exists but is buried: today you have to tick every
box, open an Actions menu, and pick a red item called "Remove from batch" that looks like it
will remove all of them. It only removes the completed ones, but nothing on screen says so
except a tooltip. That gap between what the control looks like and what it does is the real
defect here, and it is why operators believe the feature was taken away.

**What it costs.** One slice on one page. The page is the biggest in the app and is tied to a
line-numbered test registry, so it needs a careful re-anchoring pass on top of the edit -
that is most of the work, not the button.

**Biggest risk.** This is a destructive bulk action that no longer requires you to select the
rows first, which is precisely the shape you removed from the batch table in August. Three
things make this one different and all three are load-bearing: the count is in the label and
again on the confirmation; it goes through the same ticket-and-preview confirmation every other
bulk action here uses, which the old sweep predated and never had; and the list it will act on
is **fixed at the moment you click it**, so nothing you type in the filter box afterwards can
make it remove more than the number you were shown. It can only end up removing fewer - if a
mailbox finishes or leaves the batch while you are typing the ticket. If you would rather it
stayed selection-only, say so and this plan is void.

**How the selection works - owner ruling 2026-10-09.** The control does NOT require anything
to be ticked. With nothing ticked it acts on every completed mailbox. With some ticked it
acts on only those, and only the completed ones among them. So ticking boxes NARROWS it
rather than enabling it.

**What approving authorises:** the single slice below, on the Migration page only. It does
NOT authorise changing the existing "Remove from batch" item in the Actions menu, which
stays.

---

## What is actually there today

Verified against source, not remembered.

- `Components/Pages/Migration.razor:919-924` - the mailbox Actions menu carries
  **Remove from batch**, red, tooltip "Removes the selected mailboxes that have completed
  from the batch".
- `StageRemoveMailboxes:2645` stages it as `MigrationUserAction.Clear`.
- `Services/MigrationUserActionPlanner.cs:103` - `Clear` is eligible **only** when status
  equals `Completed`. Everything else is reported as skipped, not removed.

**So the behaviour already exists and is correct.** What does not exist is a control that
looks like it.

**The blocker for putting it in the existing bar:** `Migration.razor:871` gates the entire
mailbox action bar on `selectedMailboxes.Count > 0`. There is no toolbar at all until
something is ticked, so a no-selection control cannot live inside it. It needs its own home.

## The D6 tension, stated rather than buried

`docs/MigrationBatchSelection-Plan.md:208` records the owner removing the standalone
**Clear Completed** sweep on 2026-08-10. Two reasons were given: it sat beside "Clear
selection" sharing a word, one irreversible and one harmless; and an all-or-nothing
destructive sweep was what the checkbox work existed to replace.

This plan re-introduces that *shape* at mailbox scope, on the owner's direct 2026-10-07
request. The design answers both original objections rather than ignoring them:

- **Adjacency.** It is NOT placed next to any "clear selection" control. The mailbox pane's
  select-all is a tick box, not a button sharing a word.
- **All-or-nothing.** The count is in the label and again on the confirm bar, and it routes
  through `StageBatchAction`, so it inherits the staged ticket, the eligible/skipped preview
  and the Confirm step. The 2026-08 sweep had none of those - they did not exist yet. That is
  the substantive difference, not a cosmetic one.
- **And the count has to be worth something,** which revision 1 asserted and did not deliver.
  A count computed at click, against a set recomputed at Confirm, is not a bound on anything.
  The snapshot in Design below is what makes "it can never act on more than it says" true
  rather than merely stated; without it this bullet is the D6 objection returning intact.

If the owner reads those as insufficient, the fallback is a selection-only control and this
plan is withdrawn.

## Design

**Placement.** Above the mailbox table, in the same row as the mailbox filter and the
select-all tick box, outside the `selectedMailboxes.Count > 0` gate and inside the `canManage`
gate that already wraps the select-all box beside it.

### The candidate set - ONE helper, and the only thing the count and the action both read

*Revision 2, taking review finding HIGH 1 whole; it was right.* Revision 1 recorded the owner's
narrowing ruling and then described the scope as the filtered set. Those are different sets the
moment anything is ticked: `FilteredSortedMailboxes()` applies the address filter over
`batchUsers` (`Migration.razor:2421-2436`), while `selectedMailboxes` is deliberately NOT
filtered (`:2466-2481`, R11 - a ticked row must not vanish when the operator types). One helper
settles it, and both the label and the staged action read it:

- `selectedMailboxes` non-empty -> the candidate set IS `selectedMailboxes`.
- otherwise -> `FilteredSortedMailboxes().Select(u => u.EmailAddress)`.

**The selection is pruned to the loaded batch before that test** - review finding `rc-2`,
raised against the implementation. Nothing on this page clears `selectedMailboxes` when the
open batch changes: `AdoptSelectionAsOpenBatch` nulls `batchUsers`, blanks the filter and
resets the page, and leaves the ticks. So a mailbox ticked in batch A is still in the set
while batch B is open, with no row in B showing a tick - and unpruned, that stale address
takes the narrowing branch in a batch where the operator has ticked nothing, rendering
`Remove completed (0)`, disabled, over a batch full of completed mailboxes.
`MigrationUserActionPlanner.PruneSelection` is the planner's own helper for exactly this and
its remarks already say it belongs after every reload; it reads `batchUsers`, not the
filtered rows, so a ticked mailbox the FILTER hides still narrows, which is what R11
requires. The wider stale-selection condition is pre-existing, affects the mailbox Actions
bar's own count, is OUT OF SCOPE here and is recorded in `.agents/state.md`.

That is the owner's ruling verbatim, with the one thing it does not settle made explicit:
**with nothing ticked AND a filter applied, the candidates are the filtered rows, not the whole
batch.** The filter narrows for the same reason a tick narrows - both are the operator's own
act on the list in front of them, and both move in the only safe direction. The count in the
label carries whichever applies, so the control can never act on more than it says either way.

Eligibility is not folded into the candidate set. The candidates are everything in scope; the
planner splits them into eligible and skipped. That is what keeps test 5 meaningful - a ticked
`Syncing` mailbox is reported as skipped rather than silently dropped - and it is why the set
passed to the planner must NOT be pre-filtered to `Completed`.

**Label.** `Remove completed (N)`, where N is `plan.Eligible.Count` for
`MigrationUserActionPlanner.Plan(batchUsers, candidates, Clear)`. Derived from the plan, never
counted separately: a count computed beside the set it describes rather than from it is exactly
how a label comes to promise one number and send another. Not "Clear" - that word is what
caused the D6 adjacency problem.

**Gate.** `disabled="@(IsBusy || pendingActionLabel != null || RemoveCompletedCount == 0)"`.

*Revision 2, taking review finding MEDIUM 3 whole.* The first two clauses are what every other
mutating control on this page carries (`:884-920`, `:1275-1297`) and they are not decoration:
`StageBatchAction` stores ONE pending label, target and callback (`:2992-2997`), so staging a
second action overwrites the one the ticket field is confirming. Revision 1 called out only the
third clause, which is the no-op clause - with a title saying there are none, so the control is
never live and empty. The non-busy clause is registered as an `AnnotatedControl` in
`ClickGateRegistry`, so a later mechanical pass cannot collapse the gate to the page predicate
and delete it.

**The mailbox FILTER box is frozen while an action is staged** - review finding `rc-3`, raised
against the implementation. The preview annotates rows BY ADDRESS, and this is the only control
on the pane whose rows are not ticked: the other five act on ticked rows, and R11 pins those
above the list where the filter cannot reach them. So the filter is the one control that can
hide the rows the operator is about to agree to remove, and the count bound holding does not
make that acceptable - the whole case for a no-selection destructive control is that the
operator can SEE what it will do, at the moment they type the ticket. The filter therefore
takes `disabled="@(IsBusy || pendingActionLabel != null)"`, which is a wider gate than any other
view control on this pane. The sort controls are NOT widened: reordering hides nothing.
Cancelling the staged action on a filter keystroke was considered and rejected - the box binds
`oninput`, so one character would silently discard a staged destructive action.

### The staged scope is a SNAPSHOT, and it is the D6 safety argument's load-bearing part

*Revision 2, taking review finding HIGH 2 whole; the hole was real.* Revision 1 rested on "the
count is in the label, so it can never act on more than it says" while ALSO saying the callback
re-plans live, as `StageMailboxAction` does (`:2657-2664`). Both cannot hold here. The mailbox
filter box is gated on `IsBusy` only, not on `pendingActionLabel` (`:964-967`), and the tick
boxes are deliberately ungated (`:959-962`, `:1235-1237`); so an operator can stage
**Remove completed (3)**, change the filter or the selection while the ticket field is open,
and a live recompute at Confirm would act on a set they never saw.

So an ADDRESS SET is captured when the action is staged - a local closed over by the staged
callback, which no refresh, dismiss or reload path can null - and the callback re-plans against
the CURRENT `batchUsers` using that snapshot. Both halves are needed:

- **Re-planning** handles what still has to be handled while the operator types: a mailbox that
  is no longer `Completed`, and a mailbox that has left the batch -
  `MigrationUserActionPlanner.Plan` drops an address it can no longer find, by design.
- **The snapshot** means the acted-on set can only SHRINK. No later filter change, tick or
  untick can add an address that was not in the set the confirm bar counted.

**WHICH set is captured is the whole bound, and revision 2 got it wrong** - corrected here by
review finding `rc-1`, raised against the implementation. Revision 2 said "the candidate address
set". The candidates deliberately include in-scope mailboxes that are NOT completed, because that
is what lets the preview name each one as skipped on its own row; re-planning from them against
live rows therefore PROMOTES any candidate that finishes syncing while the operator is at the
ticket field. The mailbox pane's Refresh button is gated on `IsBusy` only, not on
`pendingActionLabel`, so a reload in that window is an ordinary thing to do - and
`Remove completed (3)` could remove four. **What is captured is `plan.Eligible`**: the addresses
the confirm bar counted, which is the only set that is a bound on itself.

The PREVIEW is unaffected and stays the full plan over the candidates. Those are two different
jobs: the preview is the operator's check before they type a ticket and must name everything in
scope and why it will or will not run; only the EXECUTION is bounded.

This is a deliberate difference from the other five mailbox actions, which re-plan from the
live `selectedMailboxes` under owner ruling D2(a), and the difference is the thing that makes
this control different rather than an inconsistency: their set cannot grow unless the operator
ticks a box, which is a direct act on the set itself, whereas this one's set can be derived
from a filter box that is not part of the selection at all. D2(a) is untouched - the tick boxes
stay ungated and the five existing actions keep their live re-plan.

**Mechanism.** A new `StageRemoveCompletedMailboxes` staging through the same `StageBatchAction`
and executing through the same `ExecuteBulkMailboxAction` as `StageRemoveMailboxes`, with the
snapshot in place of `selectedMailboxes`. No planner change: `Clear` already means `Completed`
and the eligibility rule stays in one place.

**It stages; it does not report - revision 1 had that wrong.** Revision 1 said to register the
new handler in `ProgressRegistry` as `Reports`, "naming the call it covers". There is no call to
name: the handler stages a confirmation and returns, and the work is reported by
`ExecuteBulkMailboxAction`, which already carries the activity and its own `Reports` entry. The
correct entry is `Exempt` with the shared `StagesConfirmation` reason, exactly as the five
existing `Stage*Mailboxes` handlers carry. A `Reports` entry would fail the registry test, and
would be a false claim if it somehow did not.

**The existing menu item stays.** Two routes to one action is correct here: one for a chosen
subset, one for all completed. Removing the menu item would break the selection workflow. With
a selection ticked the two now overlap - they act on the same mailboxes - and that is accepted
rather than overlooked: the menu item is reached from the Actions menu with the selection in
hand, the new control is the one visible with nothing ticked, and collapsing them would put the
no-selection route back inside a toolbar that does not render without a selection.

## Scope

In scope: the control, its count, its staging path, its tests, the ClickGate re-anchor, the
`Migration` module version bump.

Out of scope:

- Any change to `Remove from batch` in the Actions menu.
- Any change to `MigrationUserActionPlanner` eligibility - `Clear` already means completed.
- A status filter on the mailbox list. It would make this control sharper, and it is a
  separate feature with its own value; naming it here so it is a decision, not an oversight.
- Re-introducing anything at BATCH scope. D6 stands.

## Implementation

1. `Components/Pages/Migration.razor`:
   - `RemoveCompletedCandidates()` - the one candidate-set helper above. Everything else reads
     it, so the label can never disagree with what is acted on. The RULE itself goes in
     `MigrationUserActionPlanner` as `NarrowToSelection(inScope, selected)`, where it can be
     tested against real inputs; the page method is the adapter that supplies the page's two.
     That is an addition beside `Plan`, not the eligibility change this plan puts out of scope.
   - `RemoveCompletedCount` - `Plan(batchUsers, RemoveCompletedCandidates(), Clear)
     .Eligible.Count`.
   - The button, outside the selection gate and inside `canManage`, beside the filter and
     select-all, with the three-clause gate above.
   - `StageRemoveCompletedMailboxes`: plan over the candidates for the staged preview and the
     confirm bar's count, snapshot that plan's `Eligible` list into a local (finding `rc-1`:
     NOT the candidates), and have the staged callback re-plan from that local against the live
     `batchUsers`. It does NOT go through
     `StageMailboxAction`, which reads `selectedMailboxes` live - it stages through
     `StageBatchAction` directly, exactly as `StageMailboxAction` does, and executes through
     the same `ExecuteBulkMailboxAction`.
   - `ExchangeAdminWeb.Tests/ProgressRegistry.cs`: an `Exempt` entry for the new handler with
     `StagesConfirmation`. The registry fails on any handler it does not classify, so the build
     breaks without it - that is the guard working as intended. **Not a `Reports` entry**; see
     the Design note above for why revision 1's instruction was wrong.
2. `Modules/ModuleCatalog.cs`: bump `Migration`'s `Version`, comment in the house style. Base
   app version does not move.
3. `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`: `Migration.razor` IS line-pinned
   (`ExpectedLineCount = 4170` at HEAD - revision 1 said 4070, which was stale; review finding
   LOW 4), so **every line-keyed entry below the insertion point moves.** Re-anchor per
   `.agents/playbooks/clickgate-reanchor.md`, by the diff-based map, never by a hand-computed
   offset. This is the bulk of the slice. The new button also needs an `AnnotatedControl` entry
   pinning its `RemoveCompletedCount == 0` clause; Migration has no `AnnotatedControls` list
   today, so one is added.
4. **No test pins `Migration`'s version string** - revision 1 asserted "there is always one"
   and for this module there is not. Verified at HEAD: `grep -rn "1\.22\.8"
   ExchangeAdminWeb.Tests/` returns nothing, and `ModuleCatalogTests` pins versions only for
   Comms-10k, Defender and True Last Logon, each for a stated reason. Nothing to update; do not
   add one on the strength of the deleted sentence.

## Tests

The page cannot be rendered - there is no bUnit harness - so the candidate-set rule is tested
where it can be: as a pure helper in `Services/`, exercised directly, with source-level
tripwires proving the page calls it and nothing else.

1. **Nothing ticked: the candidates are the filtered set.** Filter applied, candidates follow it.
2. **Something ticked: the candidates are the ticked ones, filter or no filter.** This is the
   owner's narrowing rule and the half revision 1 contradicted. Includes the case that proves
   the two sets are genuinely different: a ticked mailbox the filter excludes is still a
   candidate.
3. **The count counts only `Completed` among the candidates.**
4. **The acted-on set equals the counted set.** A count and a set derived separately is how
   this goes wrong; one helper is what stops it.
5. **Zero completed renders the control disabled**, and the gate also carries `IsBusy` and
   `pendingActionLabel != null` (finding MEDIUM 3).
6. **A non-completed candidate is reported as skipped, not removed.**
7. **The staged scope cannot grow.** Finding HIGH 2's named test: stage against a known
   candidate set, then change BOTH the filter and the selection so a live recompute would
   widen it, and prove the set the executor receives is still bounded by the staged one. The
   same test proves the shrink direction still works - a candidate that has since completed,
   and one that has left the batch.
8. **The page's button and staging path read the helper and nothing else** - a source tripwire,
   because the previous sentence is the only thing a non-rendering test can check.
9. **`ProgressRegistry` classifies the new handler**, as `Exempt`/`StagesConfirmation`.

Non-vacuous proof, per the repo rule: for test 7, replace the snapshot with a live recompute
(which is revision 1's design) and confirm it fails; for test 2, make the candidate set
unconditionally the filtered one and confirm it fails; for test 5, drop the
`pendingActionLabel != null` clause and confirm ClickGate fails. Restore byte-identical and
`touch` afterwards - a `Copy-Item` restore keeps the old timestamp and MSBuild then skips the
rebuild, so the next run tests the mutated binary. **A probe that does not compile is neither a
pass nor a fail**; grep each probe run for `error CS` before reading its result.

## Verification

Per commit, observed rather than predicted:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` - the whole suite, against the 3,707 passed / 0 failed /
  3 skipped baseline. Bare `dotnet test` from the repo root runs zero tests.
- `dotnet test ExchangeAdminWeb.slnx --filter "FullyQualifiedName~ClickGate"` green before the
  slice is called done - a registry pointing at wrong lines still passes some of its tests.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`, and ASCII only in `.cs`/`.razor`.
- Codex review of the slice, iterated to a clean verdict.

## Manual acceptance - owner or operator

Nothing here proves the rendered control. Open a batch with a mix of completed and in-flight
mailboxes and confirm: the button shows the right count; clicking it asks for a ticket and
previews exactly those rows; confirming removes the completed ones and leaves the rest; the
status bar reports it; and with a filter applied the count and the result both follow the
filter.

## Owner gate - CLOSED

**The selection question is ANSWERED** (owner, 2026-10-09): no selection acts on all
completed, a selection narrows to the ticked ones. **Go given 2026-10-09.**

The one residual the ruling did not settle - with nothing ticked and a FILTER applied, the
filtered set or the whole batch? - is taken as **the filtered set**, matching the select-all
tick box beside it, and is recorded in Design above with its reasoning. Both the filter and a
tick narrow, both are the operator's own act, and the label carries the count either way, so
the control can never act on more than it says. If the owner wants the unfiltered batch
instead, that is a one-line change to `RemoveCompletedCandidates()` and its tests.
