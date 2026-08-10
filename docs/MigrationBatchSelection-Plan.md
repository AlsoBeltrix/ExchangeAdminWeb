# Migration Status: batch selection and ticket-entry proximity -- Plan

Status: **Round 1 implemented and reviewed; ROUND 2 IN PROGRESS after the owner exercised it on
dev.** Round 2 (D3-D6) reworks the three bulk actions and their targets; round 1's checkbox
mechanics, selection keying and inline ticket placement stand unchanged. NOT DEPLOYED beyond the
round-1 build the owner tested; the manual checks below are re-issued for round 2.
D1 ruled 2026-08-10 ("outer"); D2 ruled 2026-08-10 ("a"); D3-D6 ruled 2026-08-10 (see Decisions).

**Round 1 was tested on dev by the owner and the bulk-action model came back wrong.** Ticking two
batches and clicking Resume returned *"No batches to act on. Skipped 2: James.Lin@analog.com
(Completed), AprilJoy.Balo-Pagdanganan@analog.com (CompletedWithErrors)."* That single message
exposed four things, three of them defects:

1. **`CompletedWithErrors` is a real Exchange batch status this codebase has never heard of.** It
   appears nowhere in the source. Every status comparison on the page is an exact match against a
   hardcoded list, so such a batch could not be deleted, could not be resumed, was not swept by
   Clear Completed, and rendered with the unknown-status grey badge. **Pre-existing -- round 1 only
   made it visible**, and made it worse in one way: before, the button simply did not render on
   those rows, so the operator shrugged; now they tick the row and are told there is nothing to do.
2. **The action targets were wrong.** Round 1 gave Delete a status allowlist and Resume only
   `Stopped`. Owner's model, which is now D3: Delete applies to ANYTHING, Remove Completed applies
   to `Completed` only, Resume/Retry applies to anything idle-but-restartable.
3. **"Clear Completed" and "Clear selection" sat side by side sharing a word**, one an irreversible
   all-or-nothing sweep and one harmless. Owner: *"unacceptable."*
4. Not a defect: D2(a) worked exactly as designed. It named both batches and their statuses instead
   of failing silently, which is how the status gap became visible at all.

**The lesson worth keeping: an exhaustive-looking status allowlist written from the statuses a
developer has seen is a silent filter, not a safety rail.** `CompletedWithErrors` was invisible to
the entire codebase -- to the buttons, the sweep, and the badge colours -- and no test could have
found it, because every test used the same list of statuses the code did. D4's exclusion-list
approach exists to make the unseen status the DEFAULT-VISIBLE case rather than the default-hidden
one.

Two corrections found while implementing, both by mutation probe rather than by the suite:

1. **A prune test that only checked what was removed.** Disabling the membership check in
   `PruneSelection` entirely left all 34 slice-1 tests green, because every prune test happened to
   select every loaded row. That mutant ADDS unticked rows to the selection on the next reload, and
   the following Delete removes batches the operator never chose. Two tests added for that
   direction. *A test that only checks nothing was wrongly REMOVED from a set says nothing about
   what was wrongly ADDED to it.*
2. **A guard whose slice boundary was not a boundary.** `GetBatchRowMarkup` ended the row slice at
   the first `@if (expandedBatch == batch.BatchName && batchUsers != null)` -- text that also
   appears earlier, inside the Details button -- so the slice stopped short of the markup it was
   meant to cover and reported a real change as missing. Now brace-balanced. *A marker that occurs
   more than once is not a boundary.*

A third, environmental: `Copy-Item` restoring a probe backup carries the BACKUP's timestamp, so
MSBuild judged the DLL up to date and kept testing the mutant against correct restored source.
Verifying the restore by reading the file was not enough. Touch the file after any
timestamp-preserving restore.

**A `codereview` generation pass over the landed range returned one finding, `mbs-1` (MEDIUM), and
it was real** -- see `.agents/review/findings/mbs-1.md`, fixed in `1ef7fae`. D1 below says the inner
per-user table "gets the (2) fix, because the confirm bar it uses is the same shared one." The
implementation delivered the outer half only: `StageUserAction` sets the pending target to an
EMAIL, which never equals a batch name, so every per-user action fell through to the top-of-table
bar -- the reported off-screen-prompt defect, still live one level down. The slice-3 note listed
only the bulk cases as needing the top bar, and the narrower note silently won over the ruling.
**Guards, mutation probes, and a green suite all passed while that half was broken, because none of
them reads the plan.**
Module: `Migration` `1.5.0` -> `1.6.0` (round 1) -> **`1.7.0` (round 2)**.
App version: `2.7.0` at draft; now `2.8.0`, bumped for the favicon replacement, which is unrelated
to this plan.

**A version error worth recording, caught by the owner from the deployed page.** Round 1 set
`1.6.0`, then THREE further commits changed Migration behaviour -- `1ef7fae` (mbs-1), `2ff7d7f`
(the whole round-2 action model), `52eb7e9` (deselect + queued wording) -- and none bumped the
module again. Dev therefore ran the round-2 code while the catalog still read `1.6.0`, the same
number as the build tested BEFORE any of it existed. **Two different builds sharing one version is
worse than a wrong number, because during an incident nothing distinguishes them** -- the identical
failure recorded in `.agents/state.md` for `2.5.1`. The base app bump to `2.8.0` was made in the
same window and is what made it hard to see: the sidebar was correct, so the module version looked
correct too. The two rules fire independently and each needs checking on its own.
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
RULED: (a), act on the eligible rows and name every skipped row** (owner, 2026-08-10: "a"). The
three bulk actions have different targets -- see D3 for the authoritative table.

So a selection of ten will routinely mix. Options:

- **(a) RULED. Act on the eligible rows, report every skipped row by name and status.** The
  operator sees "Deleted 7, skipped 3 (BATCH-12 Syncing, ...)". It never silently drops a row the
  operator ticked, which is the repo's success-aggregation failure class stated in
  `.agents/repo-guidance.md`, and it keeps a 50-row selection usable without re-ticking.
- **(b) Rejected. Disable the bulk button whenever the selection contains an ineligible row.**
  Unambiguous, and unusable at 50 rows -- the operator must find and untick the offenders with no
  indication of which they are, which is a return to acting one row at a time.

**D3 -- the three bulk actions and their targets. RULED** (owner, 2026-08-10, verbatim: *"three
options: 1. Delete 2. Remove Completed 3. Resume/Retry ... each has a different target"*). This
supersedes round 1's two-action model entirely.

| Button | Acts on | Cmdlet |
| --- | --- | --- |
| **Delete** | ANY checked row, whatever its status | `Remove-MigrationBatch` |
| **Remove Completed** | checked rows with status `Completed` ONLY | `Remove-MigrationBatch` |
| **Resume/Retry** | checked rows that are idle but restartable (D4) | `Start-MigrationBatch` |

Delete carries **no status list at all** -- Exchange decides what it will accept, and a refusal
comes back as that row's own per-item failure, which the executor already aggregates and names. A
client-side allowlist here can only ever be wrong in the direction that hides a row the operator
explicitly ticked.

Remove Completed is deliberately NOT widened to `CompletedWithErrors`. The owner ruled it
*"Completed is the only valid target"*; a batch that finished with errors is not a batch that
finished, and quietly folding it into a bulk removal would destroy the evidence of what went wrong.
Its non-`Completed` rows are skipped and named per D2(a), not acted on.

**D4 -- how Resume/Retry decides eligibility. RULED: by EXCLUSION** (owner, 2026-08-10: "y").
Resume/Retry is offered on every status EXCEPT:

- the actively-working ones, which have nothing to resume: `Syncing`, `Starting`, `Stopping`,
  `Completing`, `Removing`;
- `Completed`, which is idle but DONE -- a distinction the first draft of this rule missed, and the
  owner caught: idle-and-restartable is not the same as idle.

So `CompletedWithErrors`, `Stopped`, `Failed`, `Paused`, `Corrupted`, and **any status this
codebase has not seen** are offered. The rejected alternative was an explicit allowlist of
resumable statuses; it fails in exactly the way that produced this round of work, by making an
unanticipated status silently unactionable. Exclusion fails the other way: an unseen status gets a
button, and Exchange refuses it if invalid -- a visible, named, aggregated refusal rather than an
invisible one.

**D5 -- the Delete confirmation. RULED: one step, in the ticket bar** (owner, 2026-08-10: "n" to a
separate confirm step). Delete's confirm bar states *"This will remove N batches"* prominently plus
a breakdown by status (`3 Completed, 2 Syncing, 1 CompletedWithErrors`), beside the ticket input
that already gates it. The ticket entry IS the confirmation; a second modal would add a click
without adding information. The breakdown is the substance of the warning: Delete's whole point is
that it accepts in-progress batches, so the operator must see how many of those they are about to
destroy before typing a ticket.

**D8 -- what happens to the selection after an action, and what the result says. RULED** (owner,
2026-08-10, on dev: *"I selected two, clicked remove completed, and it says it removed but didn't
deselect. removal isn't instant and the message is unclear since the 'removed' entry is still
there. it should say queued for removal, and it should deselect after an action is taken."*).

Two defects in one report:

1. **Deselection.** Round 1 relied on `PruneSelection` alone, which only drops batches that have
   LEFT the table. `Remove-MigrationBatch` is asynchronous: the batch sits at status `Removing` for
   some time, so it is still listed and stays ticked. A ticked row over in-flight work invites a
   second click on a batch already being removed. **Batches Exchange ACCEPTED are now deselected;
   batches that FAILED stay ticked** (nothing was queued, retrying is the likely next move), and
   skipped rows stay ticked per D2(a). A blanket `Clear()` is wrong for exactly that reason.
2. **Wording.** "Removed 1 batch(es)" rendered over a row the operator could still see. The cmdlets
   return when Exchange ACCEPTS the request, not when the work is done. Bulk verbs are now "Queued
   removal of" / "Queued restart of", plus a sentence saying Exchange finishes in the background and
   Refresh follows it. **The same lie was in `MigrationService` for the SINGLE-row path**
   (`'{batch}' removed.` / `started.` / `stopped.`) and is fixed there too -- fixing only the bulk
   wording would have left it on the row that reported it.

**D7 -- the per-user Report button. RULED: offer it on EVERY user row** (owner, 2026-08-10:
*"Report is only a button on subrows in CompletedWithErrors migrations, and it should be available
on every user row."*). It was gated on `Failed` / `NeedsApproval` / a non-empty `ErrorSummary`, so
it appeared only where the row already looked broken.

**Third status allowlist on this page to hide something the operator wanted**, after Delete and
Resume, and found the same way: by using the app. Report is READ-ONLY -- it fetches a diagnostic
report and writes nothing, takes no ticket, and audits nothing -- so there was never a case for
gating it. The gate is deleted outright rather than widened; widening it would leave the same
mechanism in place to fail again on the next status nobody predicted.

**D6 -- the standalone "Clear Completed" button. RULED: REMOVE it** (owner, 2026-08-10: "n" to
keeping it). The top-right sweep that removed every completed batch in the table regardless of
selection is deleted, along with `CompletedOrEmptyBatchNames`. Its job is now done by **Remove
Completed** from the selection. Two reasons: it is half of the adjacency problem the owner called
unacceptable, and an all-or-nothing destructive sweep is precisely what the checkbox request
existed to replace. Note this drops its `TotalCount == 0` limb -- an empty batch that is not
`Completed` is no longer swept by anything, and must be Deleted like any other row.

Consequences of (a) that are binding on the implementation:

- **A skipped row is not a failure.** The result is a success when every eligible row succeeded,
  whatever was skipped -- a selection can be entirely ineligible and that is still not an error, it
  is "nothing to do". Skips are reported alongside the outcome, never folded into the failure count.
- **Every skipped row is named with its status.** A count alone ("skipped 3") does not tell the
  operator which three, so it cannot be acted on. This is the same reasoning that made Migration's
  protected-principal notes per-target rather than batch-level.
- **Skips are not audited.** No write was attempted, so there is no security event to record; the
  audit log holds one event per row actually acted on. Recorded here so a reviewer does not read the
  absence as a missing audit call.
- **The bulk buttons are enabled whenever anything is selected**, not conditioned on eligibility.
  Under (a) the ineligible case has a defined, useful outcome, so disabling would be the (b) that
  was rejected.
- **The selection is not cleared for skipped rows.** After the run, rows that were acted on are gone
  from the reloaded table and prune out of the selection; rows that were skipped are still loaded
  and stay ticked, so the operator can pick a different action for exactly them.

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

### One aggregating executor for all three actions

**Round 2 note:** the standalone `ClearCompletedBatches` sweep described below is GONE (D6); its
executor survives and now serves Delete, Remove Completed, and Resume/Retry. The extraction
requirements below are unchanged and still binding on all three.

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
- **Skipped rows named with their status in the result message, and never counted as failures**
  (D2(a)).

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

Nothing automated renders this page. **Round-1 results are recorded per item; unmarked items are
round-2 re-issues and are UNRUN.**

Round-1 verdicts, owner on dev 2026-08-10: **1 PASS, 4 PASS, 5 PASS, 5b PASS**; 2 FAIL (D3/D4,
fixed); 3 FAIL on naming (D6, fixed); 7 WITHDRAWN as a bad check; 8 blocked by the same defect as
2; 9 not runnable.

1. **PASS (round 1).** Tick three completed batches, enter a ticket, Delete -- all three go, and the
   audit log holds **three** `RemoveMigrationBatch` events naming the three batches, not one.
2. **Tick a mixed selection and use each of the three buttons in turn.** Was the round-1 failure.
   - **Delete** acts on every ticked row whatever its status, and skips nothing.
   - **Remove Completed** acts only on the `Completed` rows and names the rest as skipped.
   - **Resume/Retry** acts on `CompletedWithErrors`, `Stopped`, `Failed` etc. and names `Completed`
     and any in-flight rows as skipped.
   In each case skipped rows are named with their status, the result does not read as a failure, and
   they stay ticked.
3. **The three action buttons and `Untick all` are not confusable.** Was the round-1 failure: "Clear
   Completed" and "Clear selection" sat adjacent sharing a word. No two buttons share a leading verb,
   and the destructive Delete is visually distinct (solid, not outline).
4. **PASS (round 1). Sort the table by a different column while rows are ticked, then act.** The
   batches acted on must be the ones ticked, not the ones now in those positions. Invisible to every
   test.
5. **PASS (round 1). With 50+ batches loaded, scroll to a row near the bottom and click `Resume`.**
   The ticket field appears directly beneath that row without scrolling.
5b. **PASS (round 1), one level down (mbs-1):** expand a batch with many users, scroll to a user row
   low in the inner table, click `Resume` or `Clear`. The ticket field appears beneath that USER row.
6. **Delete's confirmation states the count and the per-status breakdown** before a ticket is
   accepted -- e.g. "This will remove 6 batch(es): 3 Completed, 2 Syncing, 1 CompletedWithErrors".
   The in-flight count is the substance of the warning.
7. ~~Delete a batch in the Exchange admin center, refresh, confirm it is no longer ticked.~~
   **WITHDRAWN as a bad check** (owner, 2026-08-10). It presumed a local store of migrations. There
   is none: `GetMigrationBatchesAsync` runs `Get-MigrationBatch` against Exchange on every load, so
   there is no cache to go stale. Selection pruning is still guarded by unit test.
8. **A `CompletedWithErrors` batch is fully actionable.** It offers Delete and Resume/Retry on its
   own row, is accepted by both bulk actions, is NOT swept by Remove Completed, and no longer draws
   the grey unknown-status badge. This is the round-2 defect stated as a check.
9. **The `Report` button appears on EVERY user row** inside an expanded batch, not only on rows that
   look broken (D7).
9b. **After any bulk action, the batches Exchange accepted are unticked** and the message says
   "Queued removal of N batch(es)", never "Removed" (D8). Rows that FAILED stay ticked. The single-
   row buttons say "queued for removal" too.
10. An operator without `MigrationManage` sees no checkbox column and no toolbar, and the bulk
    endpoints refuse if reached anyway. **Not runnable by the owner** as of round 1.
