# Migration Status: batch selection and ticket-entry proximity -- Plan

Status: **Draft -- awaiting owner ruling on D2.** D1 ruled by the owner 2026-08-10 ("outer").
App version at draft: `2.7.0` (unchanged -- module-scoped change).
Module: `Migration` (`1.5.0` -> `1.6.0`).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`.agents/repo-guidance.md`, `docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request, 2026-08-10, verbatim:

> the exchange migration status page needs checkboxes on each row to allow batch clear/delete and
> resume. the ticket number entry field for individual items needs to be closer to the actual button
> people hit to resume/clear individual migrations because we routinely have ~50+ in-flight and the
> ticket number entry field is buried at the top of the table and UI doesn't make that obvious.

## Problem

Two separate defects in the **Migration Status** tab of `Components/Pages/Migration.razor`, both
only observable at the scale this team actually runs (50+ in-flight batches).

**(1) There is no multi-select anywhere in the tab.** Verified against the source: the four
`type="checkbox"` inputs in the file are all auto-start / auto-complete options on the Single and
Bulk *check* tabs (`:152`, `:156`, `:321`, `:325`). The status table has none. Every batch action is
one row at a time: `Complete` (`:531`), `Stop` (`:538`), `Resume` (`:545`), `Delete` (`:552`). The
only bulk affordance is **Clear Completed** (`:407`, handler `:1305`), which is all-or-nothing --
it removes every batch matching `Completed` or `TotalCount == 0` and offers no way to pick a subset.
With 50+ rows, clearing or resuming a chosen ten is fifty clicks and ten ticket entries.

**(2) The ticket field for a single-row action renders above the table, not near the row.**
`StageBatchAction` (`:1155`) sets `pendingActionLabel`, and the confirm bar at `:434-457` is
rendered *between the alert area and the table*, before `<tbody>`. Click `Resume` on row 47 and the
input appears off-screen. Nothing scrolls, nothing highlights, and the row's own buttons all become
disabled (`pendingActionLabel != null`, `:530-551`) -- so the visible feedback is that the buttons
stopped working. The owner's reading, "the UI doesn't make that obvious", is the accurate
description of the defect.

These are independent. (2) would still be wrong with (1) fixed, because per-row actions remain the
right tool for a single batch.

## Decisions

**D1 -- which table gets checkboxes. RULED: outer Batches table only** (owner, 2026-08-10:
"outer"). The inner per-user table inside an expanded batch (`:621-696`) keeps its one-at-a-time
actions unchanged. It gets the (2) fix, because the confirm bar it uses is the same shared one.

**D2 -- what a bulk action does when the selection contains rows the action cannot apply to.
OPEN.** The two bulk actions have different applicable statuses, mirroring the per-row buttons:

| Action | Applies to batch status |
| --- | --- |
| Delete (`Remove-MigrationBatch`) | `Completed`, `Failed`, `Stopped`, `Corrupted` |
| Resume (`Start-MigrationBatch`) | `Stopped` |

So a selection of ten will routinely mix. Options:

- **(a) Act on the eligible rows, report every skipped row by name and status.** The operator sees
  "Deleted 7, skipped 3 (BATCH-12 Syncing, ...)". Recommended: it never silently drops a row the
  operator ticked, which is the repo's success-aggregation failure class stated in
  `.agents/repo-guidance.md`, and it keeps a 50-row selection usable without re-ticking.
- **(b) Disable the bulk button whenever the selection contains an ineligible row.** Unambiguous,
  and unusable at 50 rows -- the operator must find and untick the offenders with no indication of
  which they are.

The implementer must not choose. Work proceeds on everything D2 does not touch; the bulk executor's
skip-reporting is the only part gated.

## Non-goals

- **The inner per-user table gets no checkboxes** (D1).
- **No bulk `Complete` and no bulk `Stop`.** The owner named clear/delete and resume. Both are
  trivial additions later; adding them unasked widens a destructive surface.
- **No change to `Services/MigrationService.cs`.** Every action already exists as a single-target
  call (`RemoveMigrationBatchAsync:785` and siblings). Bulk is a loop in the page over calls that
  already work, not a new service capability.
- **No change to protected-principal gating.** The gate is at batch *creation*
  (`PartitionByProtectionAsync`); actions on an already-created batch are not gated today and this
  plan does not change that. Recorded so a reviewer does not read the omission as an oversight.
- **The search box is not becoming a table filter.** `SearchUser` jumps to and highlights a user; it
  does not filter `GetSortedBatches()`. Select-all therefore means all loaded batches, with no
  hidden-row hazard.
- **No new confirmation dialog type.** The ticket entry IS the confirmation.

## Design

### Selection state

Keyed on **`BatchName`, never row index.** The table re-sorts on every header click
(`GetSortedBatches:1524`) and reloads after every action, so an index-keyed selection silently
retargets. `MigrationBatchInfo.BatchName` is `required` and is the identity `Remove-MigrationBatch`
is given, so it is the correct key.

```csharp
private readonly HashSet<string> selectedBatches = new(StringComparer.OrdinalIgnoreCase);
```

**Pruned on every reload.** `LoadMigrationStatus` must intersect the selection with the names it
just loaded. A batch removed by this operator, by another operator, or by Exchange itself must not
stay ticked -- acting on a stale name produces a per-row failure that reads as a bug in the app.

### Pure logic goes in a service class, not the page

There is no bUnit harness in this repo, so nothing can render the page and nothing can test a
handler. Every decision that can be a pure function must be one, following
`MessageTraceDetailReport` and `ProtectedPrincipalEntryValidator`.

New `Services/MigrationBatchActionPlanner.cs`, pure and static:

```csharp
public enum MigrationBatchAction { Delete, Resume }

public sealed record MigrationBatchActionPlan(
    IReadOnlyList<string> Eligible,
    IReadOnlyList<(string BatchName, string Status)> Skipped);

public static MigrationBatchActionPlan Plan(
    IEnumerable<MigrationBatchInfo> loaded,
    IReadOnlyCollection<string> selectedNames,
    MigrationBatchAction action);

public static IReadOnlyList<string> PruneSelection(
    IEnumerable<MigrationBatchInfo> loaded,
    IEnumerable<string> selectedNames);
```

`Plan` also drops selected names absent from `loaded` -- same reasoning as pruning, applied at the
moment of acting rather than the moment of loading, because the two can be separated by an
arbitrary pause at the ticket field.

The status predicates live here as the single definition, and **the per-row buttons must be changed
to read them** rather than keeping their own inline `status.Equals(...)` chains at `:528-555`. Two
copies of "which statuses may be deleted" is exactly the drift that makes a bulk action and a row
button disagree about the same batch.

### One aggregating executor, shared with Clear Completed

`ClearCompletedBatches` (`:1311`) already implements per-item aggregation correctly, including the
subtle part: audit-write failures are collected as *warnings* and never turn a completed removal
into a reported failure (`:1335-1337`, `:1364-1370`). Do not write a second implementation beside
it. Extract it into:

```csharp
private async Task ExecuteBulkBatchAction(
    IReadOnlyList<string> batchNames,
    IReadOnlyList<(string BatchName, string Status)> skipped,
    Func<string, Task<PermissionResult>> action,
    string auditAction,
    string ticket)
```

and re-point `ClearCompletedBatches` at it. Requirements, each already met by the code being
extracted and each of which must survive the extraction:

- **Authorization re-checked once per invocation**, before the loop, and a denial is audited
  (`:1313-1326`). UI visibility of the checkbox column is not a security control (Constitution,
  Never Do).
- **One audit event per batch**, inside the loop, carrying the ticket
  (`Audit.LogMigrationAction(..., ticket, ...)`). Never one event for the run: the audit exists to
  answer which batch was removed, and a run-level record cannot.
- **Per-item failures aggregated**, never blanket success.
- **One summary admin notification per run**, matching `:1372-1382`. Fifty notifications for one
  operator action is a self-inflicted denial of the mailbox.
- **Skipped rows named in the result message** (shape settled by D2).

### Markup

**Header cell** before `Batch Name` (`:489`): a select-all checkbox, `checked` when every loaded
batch is selected, `indeterminate` is not set (Bootstrap/Blazor cannot express it without JS
interop, and the tri-state adds nothing here). Rendered only when `canManage`.

**Row cell** before the name cell (`:503`): a checkbox bound to membership in `selectedBatches`.
`@onchange`, not `@bind`, so the handler owns the set. `stopPropagation` is not needed -- the row
has no click handler; only the `<th>`s do.

**Selection toolbar**, rendered between the alert area and the table and only when
`selectedBatches.Count > 0`: "N selected", `Delete selected`, `Resume selected`, `Clear selection`.
The two action buttons route through the existing `StageBatchAction` staging so the ticket flow is
unchanged and unduplicated -- target string `"N batch(es)"`, exactly as `StageClearCompleted:1305`
already does. Proximity is not a problem for the toolbar: it sits immediately above the checkboxes
it acts on.

**Colspan**: the expanded-details row at `:590` is `colspan="8"` and must become `9`. A wrong
colspan does not fail the build and is not visible until a batch is expanded.

### The (2) fix: confirm inline, under the row

Move the confirm bar out of `:434-457` and render it as an inserted `<tr>` immediately after the row
whose button was clicked, matched on `pendingActionTarget == batch.BatchName`. The bar keeps its
ticket input, Confirm, Cancel, and the `HandleConfirmKeyDown` Enter handler unchanged; only its
position changes.

Two cases have no row to attach to -- `Clear Completed` and the new bulk actions, whose
`pendingActionTarget` is `"N batch(es)"` and not a batch name. Those keep the existing top-of-table
bar. So the page renders the bar in one of two places depending on whether the pending target names
a loaded batch; both call the same markup, which should therefore be a single
`RenderFragment`/local method rather than two copies that drift.

The acting row should also get a highlight class while its action is pending, for the same reason
the bar is moving: the operator must be able to see which row they are answering for.

## Slices

Each slice is one commit, verified before the next begins.

**Slice 1 -- `Services/MigrationBatchActionPlanner.cs` and its tests.** Pure, no page changes.
Tests: eligible/skipped partition per action; a selected name absent from `loaded` is dropped, not
treated as eligible; pruning across a reload; case-insensitive name matching; empty selection yields
an empty plan and no skips. Behavioural coverage, not a tripwire -- this slice is the reason the
logic left the page.

**Slice 2 -- page selection and bulk actions.** Checkbox column, select-all, toolbar,
`ExecuteBulkBatchAction` extracted from `ClearCompletedBatches` with `ClearCompletedBatches`
re-pointed at it, per-row buttons re-pointed at the planner's predicates, colspan 8 -> 9. Guards are
source-level tripwires in a new `MigrationStatusPageTests`, and must be anchored to the markup or
handler they cover, not merely present somewhere in the file -- two of the `blr-4` guards were
satisfied by a broken page because they matched a spinner elsewhere in the same file:
- the batch `<tbody>` loop contains a checkbox bound to the selection set;
- the bulk handlers call `ExecuteBulkBatchAction`, and `ClearCompletedBatches` does too (proves the
  extraction, not a second implementation);
- the bulk executor's body contains a `LogMigrationAction` call *inside* its loop;
- the page contains no second `Remove-MigrationBatch`-driving loop.

**Slice 3 -- inline confirm placement.** Bar moves under the acting row; row highlight; the two
non-row cases keep the top bar via the shared fragment. Guard: the ticket `<input>` appears inside
the `@foreach` over batches, and the `pendingActionTarget == batch.BatchName` match exists. This is
a tripwire and cannot prove what an operator sees -- manual check 5 is the real evidence.

**Slice 4 -- version and records.** `Migration` `1.5.0` -> `1.6.0` in `Modules/ModuleCatalog.cs`
(module-scoped behaviour change; no base app bump, nothing shared changes). This plan's Status line,
`.agents/state.md`, and the `README.md` Migration section if it enumerates the status-tab controls
(check; do not assume).

## Verification

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`, and the ASCII scan over tracked `.cs`

Non-vacuity per slice: revert the change, **confirm the revert actually landed by reading the file
back**, confirm the guards fail, restore, confirm they pass. A probe that does not verify its own
revert has manufactured a false verdict twice in this repo.

`ModuleCatalog` has a version assertion per module; slice 4 will fail it until the assertion is
updated to `1.6.0`. Update the assertion, never delete it.

## Manual checks -- the only real evidence

Nothing automated renders this page. Every check below is load-bearing; 4 and 5 most of all.

1. Tick three completed batches, enter a ticket, Delete -- all three go, and the audit log holds
   **three** `RemoveMigrationBatch` events naming the three batches, not one.
2. Tick a mixed selection (some deletable, some `Syncing`) and Delete -- behaviour matches the D2
   ruling, and the skipped rows are named on screen.
3. Select-all, then `Clear selection` -- nothing is acted on, and no audit event is written.
4. **Sort the table by a different column while rows are ticked, then act.** The batches acted on
   must be the ones ticked, not the ones now in those positions. This is the defect the name-keyed
   selection exists to prevent and it is invisible to every test.
5. **With 50+ batches loaded, scroll to a row near the bottom and click `Resume`.** The ticket field
   must appear directly beneath that row, on screen, without scrolling. This is the reported
   complaint; nothing else proves it fixed.
6. Delete a batch in the Exchange admin center, refresh the page, confirm it is no longer ticked and
   no longer listed.
7. `Clear Completed` still works and still removes exactly the completed/empty batches -- the
   extraction must not have changed it.
8. An operator without `MigrationManage` sees no checkbox column and no toolbar, and the bulk
   endpoints refuse if reached anyway.
