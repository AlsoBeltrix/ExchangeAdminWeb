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
- **The right pane always shows the selection.** One batch ticked and it shows that batch's
  mailboxes; more than one and it lists exactly the ticked batches, **with its own pager**
  reading "1-40 of 847 selected batches" so it is unmistakable that you are paging the
  selection and not the catalogue. Each row there has Open and Remove-from-selection. This
  is what satisfies "no hiding selected items off the page": the selection has a home that
  displays it in full, so the left list never has to hold anything back from its own filter
  or pager. Owner ruling 2026-09-24, after an earlier pinned-rows design was rejected.
- **The left list stays a plain paged list** -- checkboxes, filter, pager, nothing pinned,
  no dividers, no action bar. Selecting and browsing do not fight for the same space.
- **Action groups sit at the TOP of their own pane, above the rows they act on, and never
  scroll.** Batch operations head the selection pane on the right, above the list of ticked
  batches:
  `N ticked | Complete | Schedule | Stop | Resume | Delete | x`, with the ticket field, the
  datetime when scheduling, and Confirm on a second line of the same block. Mailbox
  operations sit in the same place when a single batch is open: directly under the batch
  identity line, above the mailbox table. Owner ruling 2026-09-24: **not a footer** --
  actions belong above their rows, which is what every other list UI does and what operators
  expect. Only one action group is ever on screen, because the right pane shows either a
  batch selection or one batch's mailboxes, never both.
- **Both action groups behave identically**: outline buttons until one is picked, the picked
  one highlighted, a per-row outcome written against every affected row, a ticket required,
  and Confirm carrying the eligible count. No colour or layout difference between the batch
  and mailbox bars -- the earlier asymmetry was an accident and the owner caught it.
- **"Untick" is only used where checkboxes exist.** The right pane says "Clear selection" and
  "Remove from selection"; the left list, which has the checkboxes, says untick.
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

**S1 -- Addressable state.** Drive the open batch from a **query parameter on the existing
route** (`/migration?batch=<name>`), not from `expandedBatch`, and not from a new path
segment. A path segment is ruled out on evidence: `Components/Shared/ModuleVersion.razor:23`
and `Components/Layout/UsageTracker.razor:53` both resolve the current module through
`Catalog.GetByRoute(route)`, which matches the catalog's exact `Route = "migration"`
(`Modules/ModuleCatalog.cs:202`), so `/migration/batch/x` would stop resolving and would
silently break the version badge and usage telemetry. A query parameter also avoids
inventing encoding rules for operator-supplied batch names. Selection state moves to fields
that survive a re-render. No layout change. Module version bump only -- nothing shared
changes.

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

**Q1 is closed, 2026-09-24. No cap.** The question only existed because ticked rows were
pinned into the left list, so a select-all had to render every one of them. The owner
rejected a cap outright -- "that's not Select ALL, it's Select up to the cap" -- and the
pinned design with it. With the selection living in its own paged pane, select-all renders
40 rows there and 40 in the left list whatever the selection size, so trap 3 does not fire
and nothing needs limiting.

**Q2. Does removing the batch buttons from the right pane need a transition?** Today an
operator completes a batch from the expanded view. After S2 they must tick it. That is the
safety gain, but it is a habit change and worth confirming it is wanted.

## Acceptance

Beyond the gates: with the app running, no control that acts on a selection may be off
screen while that selection exists, and no ticked row may be hidden by a filter or a page
change. Those two are the item-14 clauses and are checked by hand on dev.
