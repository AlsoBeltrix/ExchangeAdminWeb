# Migration - Remove Completed Mailboxes - Plan

Status: **DRAFT - awaiting owner go.** Nothing below is authorised yet.

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
rows first, which is precisely the shape you removed from the batch table in August. Two
things make this one different and both are load-bearing: the count is in the label, so it
can never act on more than it says, and it goes through the same ticket-and-preview
confirmation every other bulk action here uses - which the old sweep predated and never had.
If you would rather it stayed selection-only, say so and this plan is void.

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

**The blocker for putting it in the existing bar:** `Migration.razor:870` gates the entire
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
- **All-or-nothing.** The count is in the label, and it routes through `StageBatchAction`,
  so it inherits the staged ticket, the eligible/skipped preview and the Confirm step. The
  2026-08 sweep had none of those - they did not exist yet. That is the substantive
  difference, not a cosmetic one.

If the owner reads those as insufficient, the fallback is a selection-only control and this
plan is withdrawn.

## Design

**Placement.** Above the mailbox table, in the same row as the mailbox filter and the
select-all tick box, outside the `selectedMailboxes.Count > 0` gate.

**Label.** `Remove completed (N)`, where N is the count of mailboxes with status `Completed`
in the current filtered set. Not "Clear" - that word is what caused the D6 adjacency problem.

**Disabled when N is 0**, with a title saying there are none, so the control is never a
no-op that looks live.

**Scope: the filtered set, not the whole batch.** This matches `ToggleSelectAllMailboxes:2584`,
which already takes `FilteredSortedMailboxes()` rather than the page or the batch, and whose
comment gives the reason: "the tick box above a filtered list that only took the visible 50
would quietly do less than it says." The same argument applies in reverse here - a button
beside a filtered list that acted on the unfiltered batch would quietly do MORE than it says.
The filter is address-only (`:2428-2433`), so this is reachable in practice.

**Mechanism.** A new `StageRemoveCompletedMailboxes` that calls the existing
`StageMailboxAction` machinery with the completed set in place of `selectedMailboxes`.
`MigrationUserActionPlanner.Plan` already takes the address set as a parameter, so no planner
change is needed and the eligibility rule stays in one place. Re-planned at execution time
exactly as the existing path does, because the rows can reload while the operator is at the
ticket field.

**The existing menu item stays.** Two routes to one action is correct here: one for a chosen
subset, one for all completed. Removing the menu item would break the selection workflow.

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
   - A computed `CompletedMailboxCount` over `FilteredSortedMailboxes()`, and the matching
     address set for the planner. One source for both, so the label can never disagree with
     what is acted on.
   - The button, outside the selection gate, beside the filter and select-all.
   - `StageRemoveCompletedMailboxes`, routing through `StageMailboxAction` with that set.
   - **It must report to the status bar.** `ProgressRegistry` (landed `d5f3b3b`) fails on any
     operation handler that is not classified, so a new handler added without an entry breaks
     the build - which is the guard working as intended. Register it `Reports` naming the
     call it covers.
2. `Modules/ModuleCatalog.cs`: bump `Migration`'s `Version`, comment in the house style. Base
   app version does not move.
3. `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`: `Migration.razor` IS line-pinned
   (`ExpectedLineCount = 4070`), so **every line-keyed entry below the insertion point moves.**
   Re-anchor per `.agents/playbooks/clickgate-reanchor.md`, by the diff-based map, never by a
   hand-computed offset. This is the bulk of the slice.
4. Update whichever test pins `Migration`'s version string - there is always one.

## Tests

1. The count is over the filtered set, not the page and not the batch: filter applied, count
   reflects it.
2. The count counts only `Completed`.
3. The acted-on set equals the counted set - the label cannot promise one number and send
   another. This is the assertion that matters most; a count and a set derived separately is
   how this goes wrong.
4. Zero completed renders the control disabled.
5. A non-completed mailbox in the filtered set is reported as skipped, not removed.
6. The staged plan re-plans at execution time.
7. `ProgressRegistry` classifies the new handler.

Non-vacuous proof: break the count/set agreement in test 3 and confirm it fails; revert the
`Progress.Begin` and confirm the registry names the new handler. `touch` after restoring.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet test ExchangeAdminWeb.slnx --filter "FullyQualifiedName~ClickGate"` green before the
  slice is called done - a registry pointing at wrong lines still passes some of its tests.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- Codex review of the slice.

## Manual acceptance - owner or operator

Nothing here proves the rendered control. Open a batch with a mix of completed and in-flight
mailboxes and confirm: the button shows the right count; clicking it asks for a ticket and
previews exactly those rows; confirming removes the completed ones and leaves the rest; the
status bar reports it; and with a filter applied the count and the result both follow the
filter.

## Owner gate

**The selection question is ANSWERED** (owner, 2026-10-09): no selection acts on all
completed, a selection narrows to the ticked ones. Go or no-go on building it.

One residual the ruling does not settle, called out rather than assumed: with nothing ticked
and a FILTER applied, does it act on the filtered set or the whole batch? This plan takes
the filtered set, matching the select-all tick box beside it - the label carries the count
either way, so the control can never act on more than it says.
