# Migration -- Redesign The Status Interface (queue item 14)

Status: **Draft, awaiting owner approval.** No code written. The design itself is settled:
the owner reviewed and drove it through a working HTML mockup over many rounds, and
`.agents/mockups/migration-v3.html` is the accepted shape. What needs approval is the
implementation, not the design.

## Why a plan at all

`docs/ProjectConstitution.md` "Planning Rules" requires one for **destructive or
high-blast-radius workflows**. This restructures the surface that carries Complete, Stop,
Resume and Delete for whole batches and for individual mailboxes, and `Migration` is a
converted page in `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`, so the gating contract
moves with it. That is the trigger, not the visual work.

## The requirement

`queue.txt` item 14, verbatim:

> 14. Precedes 12 & 13. Design a better, safer, and easier to use interface for managing
> migrations. multi-pane or tabs or something that makes it less dense and easier to use
> without UI-induced error. no burying controls off the screen, no hiding selected items
> off the page.

Items 12 (scheduled completion) and 13 (per-mailbox checkboxes and actions) are blocked
behind this and are **not** in scope here, but the layout must leave room for both and
this plan says where each one lands.

## What is wrong with the page today

`Components/Pages/Migration.razor`, 2025 lines, Migration Status tab:

1. One wide batch table. Clicking a batch injects a **second, nested user table inside the
   row** (`:654-:790`), pushing everything below it down the page.
2. The bulk action bar (`:460-:487`) sits above the table, so it scrolls out of sight while
   the operator ticks rows below it.
3. The per-row ticket confirmation (`:646-:653`, `:769-:777`) renders at the row's own
   position, which may be off screen.
4. The user table has no sort and no filter; the batch table has sort only.
5. Nothing is addressable. Which batch is expanded lives in `expandedBatch`, so browser
   Back, refresh and a circuit reconnect all lose it.

## The accepted shape

Two panes, from `.agents/mockups/migration-v3.html`. Every rule below was a correction the
owner made during review; they are requirements, not preferences.

- **Left pane: batches only. Right pane: mailboxes only.** No batch-level control exists in
  the right pane, so nothing above the mailbox checkboxes can be mistaken for acting on
  them. Scope is established by which pane a control is in, never by a label.
- **One selection concept per pane.** The highlight says what you are looking at; the tick
  says what an operation will hit. Opening a batch never changes the ticks.
- **A batch is operated on by ticking it**, including a single batch. Opening is viewing.
- **Ticked rows are pinned to the top of their list**, above an "OTHER BATCHES" /
  "OTHER MAILBOXES" divider, and are exempt from the filter and the pager. A selected item
  can therefore never be off the page -- the item-14 clause is satisfied structurally, not
  by a warning that says something is hidden.
- **Operations sit at the top of the ticked section**, directly above the rows they act on:
  `N ticked | Complete | Schedule | Stop | Resume | Delete | x`, with the ticket field,
  the datetime when scheduling, and Confirm on a second line of the same block.
- **Per-row outcome preview.** Staging an operation annotates each ticked row with what it
  will do to that row ("will be completed", "skipped, Stopped") and the Confirm button
  carries the eligible count. Ineligible rows are never sent.
- **Both lists page** (batches 40, mailboxes 50) with filter and sort applied over the whole
  set, not the page. Select-all covers everything the filter matches.
- **Column header row inside the scrolling list**, sticky, sharing the rows' grid so the
  scrollbar cannot offset it: Batch / Synced / Failed / Status. The failure count is a
  labelled column, not a bare red number.
- **Fixed grid columns** so counts and badges align on every row.
- **Addressable**: each batch is a route, so Back, refresh and reconnect all work.

## Slices

Each slice is its own commit with its own verification. S1 and S2 carry no behaviour change.

**S1 -- Route and state.** Give the Status tab a batch route
(`/migration/batch/{BatchName}`) and drive the open batch from the route parameter instead
of `expandedBatch`. Selection state (`selectedBatches`, and the new mailbox selection) moves
to fields that survive a re-render. No layout change. Base app version bump: routing is
shared infrastructure.

**S2 -- Split the panes.** Replace the nested-row expansion with the two-pane layout: batch
list left, mailbox table right, each with its own scroll region, sticky header and pinned
footer. Same data, same actions, same gating, no new operations. Module version bump.

**S3 -- Selection model.** Pinned ticked rows in both lists, the dividers, select-all over
the filter, and the operation bar at the top of the ticked section. Remove the old bulk bar
at `:460`. Module version bump.

**S4 -- Outcome preview.** Per-row eligibility annotation and the eligible count on Confirm,
replacing the current blanket staging. This is where Known Failure Class 2 (success
aggregation) is addressed in the UI: the operator sees per-row outcomes before committing,
and the result banner must report per-row outcomes after. Module version bump.

**S5 -- Filter, sort and paging on the mailbox table.** Currently absent entirely. Module
version bump.

## Traps this must not walk into

1. **`CompleteAfter` already exists and lies.** `Services/MigrationService.cs:490` passes it
   to `Set-MigrationBatch` and `:698` to `Set-MigrationUser`, both with a time in the
   **past** (`AddHours(-1)`, `UtcNow`) to mean "complete now", and `:596` reads
   `CompleteAfter != null` as an auto-complete boolean. Item 12 needs a **future** time, so
   "complete now" and "complete at T" stop being the same value and `:596`'s reading stops
   being safe. This plan does not implement item 12; it must not make that worse, and S2
   must not move those call sites.
2. **`ClickGateRegistry.Pages` lists Migration as converted.** Every control this redesign
   moves or adds has to satisfy that suite, and the suite reads source text -- the sweep
   already found that a CSS class literally named `disabled` on Migration's tab anchor was
   satisfying a gating assertion (`.agents/state.md`, queue 9). Re-run the harness against
   each slice and check the assertions still bite.
3. **Render volume.** Blazor Server pushes a render diff per row over the circuit; the
   Defender page died this way. Paging is what keeps this safe, so no slice may render the
   full set. Pinned ticked rows are the one exception, which is why select-all-then-act
   needs its own look before S3 ships.
4. **Protected-principal gate, ticket, audit and notification** obligations are unchanged
   and apply to every action the new layout exposes, including per-mailbox actions.

## Open questions

**Q1. Does select-all across a filter need a cap?** With ticked rows pinned, ticking 2000
batches renders 2000 rows and reintroduces trap 3. Options: cap the tick count, or represent
a select-all as a stated rule ("all 847 matching In progress") rather than 847 pinned rows.
Blocks S3 only.

**Q2. Does removing the batch buttons from the right pane need a transition?** Today an
operator completes a batch from the expanded view. After S2 they must tick it. That is the
safety gain, but it is a habit change and worth confirming it is wanted.

## Acceptance

Beyond the gates: with the app running, no control that acts on a selection may be off
screen while that selection exists, and no ticked row may be hidden by a filter or a page
change. Those two are the item-14 clauses and are checked by hand on dev.
