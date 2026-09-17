# Migration page: no control is clickable unless the click will definitively execute

Status: Draft 2026-09-17, revised 2026-09-17 after review. Awaiting owner approval. Not implemented.

## Goal

Make the Mailbox Migrations page obey one rule, stated by the owner on 2026-09-17:

> It should not be possible to click any buttons until the system is ready for that
> button click to be definitively executed.

Today each control guards only against re-entering *itself*. Nothing guards against the
other operations running on the same page, so a click can be accepted and then silently
discarded, or land on a row that is being replaced underneath it.

## Why this is not a cosmetic concern

Two consequences, one wasteful and one dangerous.

**Wasteful.** Pulling a report for a problematic migration takes several minutes
(`Get-MigrationUserStatistics -IncludeReport`, `Services/MigrationService.cs:872-874`).
Several code paths call `CloseUserReport()`, which bumps `reportGeneration`
(`Migration.razor:1778-1783`) and causes an in-flight report to be discarded on arrival
(`:1813-1815`). The operator waits minutes and is shown nothing, with no message explaining
why. Every one of those paths is reachable from a control that stays enabled during the
pull.

**Dangerous.** Every destructive action - Delete, Stop, Complete, Resume, on both batch
rows (`:571-599`) and user rows (`:706-727`) - is disabled only by
`@(actionInProgress != null || pendingActionLabel != null)`. None consults
`isLoadingStatus` or `loadingBatchUsers`. So while the batch table or the user table is
being refetched, those buttons are live over rows that are about to be replaced. The
operator can click Delete on the row they are reading and have the list swap under the
click. This is the same defect class as `msr-1`, with a production migration action as the
payload rather than a stale panel.

No such incident is known to have occurred. The severity claim here is about reachability,
which is demonstrated below, not about observed frequency, which is unmeasured.

## Current state, surveyed

`Components/Pages/Migration.razor` has 31 `<button>` elements and three `<a>` tab links
that are also click targets. Grouped by how they fail the rule:

**A. Buttons with no `disabled` attribute at all (7).**

| line | action | what a mistimed click does |
| --- | --- | --- |
| 219 | `DownloadSampleCsv` | harmless; static file |
| 391 | `ClearSearch` | a search result can land after the clear and repopulate the page |
| 412 | `DownloadCsvAsync` | double-click yields two downloads |
| 437 | dismiss `batchActionResult` | harmless; pure UI dismiss |
| 495 | `LoadMigrationStatus` | starts a second batch-table load while one is running |
| 604 | `ToggleBatchDetails` | discards `batchUsers` and the report mid-flight |
| 778 | `CloseUserReport` | intended cancel; see Non-goals |

**B. Buttons gated against themselves only (24).** Each is correct for re-entry and blind
to everything else:

| line(s) | action | gate today | blind to |
| --- | --- | --- | --- |
| 61, 235 | eligibility checks | `isLoading` | - (own tab) |
| 168, 338 | create batch | `isCreating` | - (own tab) |
| 395 | `SearchUser` | `isSearching` | `loadingBatchUsers`, `actionInProgress` |
| 419 | `LoadMigrationStatus` | `isLoadingStatus` | `loadingBatchUsers`, `actionInProgress`, `loadingReport` |
| 459-476 | bulk toolbar (4) | `actionInProgress`, `pendingActionLabel` | `isLoadingStatus`, `loadingBatchUsers` |
| 571-599 | per-batch actions (4) | `actionInProgress`, `pendingActionLabel` | `isLoadingStatus`, `loadingBatchUsers` |
| 620, 749 | `RefreshBatchUsers` | `loadingBatchUsers` | `actionInProgress`, `loadingReport` |
| 706-727 | per-user actions (5) | `actionInProgress`, `pendingActionLabel` | `isLoadingStatus`, `loadingBatchUsers` |
| 737 | `LoadUserReport` | `loadingReport == e` | `loadingBatchUsers`, `actionInProgress`, and **any other row's** report pull |
| 819 | `ConfirmPendingAction` | `pendingActionTicket` empty, `actionInProgress` | see the Confirm carve-out below |
| 830 | `CancelPendingAction` | `actionInProgress` | - (cancel is always executable) |

The Report row is the residue of `msr-1` and the one the owner hit. `LoadUserReport:1807`
calls `CloseUserReport()` unconditionally, so clicking Report on row B while row A is still
pulling throws A away. Because the gate is `loadingReport == e`, only row A's own button is
disabled; every other row's Report button invites exactly that.

**C. Tab links (3), at `:26`, `:29`, `:32`.** These are `<a href="javascript:void(0)">`
elements, not buttons. `disabled` has no effect on an anchor, so the button sweep cannot
reach them. `:32` calls `LoadMigrationStatus` directly, which calls `CloseUserReport()` at
`:1189` - so clicking the already-active Migration Status tab during a multi-minute report
pull discards the report, exactly the failure this plan exists to remove. `:26` and `:29`
switch tabs and abandon whatever is in flight.

## Remedy

### The predicate, and the distinction that makes it work

The naive version - one predicate ORing every state flag on the page, applied to every
control - is wrong, and wrong in a way that would break the module. `pendingActionLabel` is
not an in-flight flag. It means "a confirmation is staged and the page is waiting for the
operator to type a ticket number". The Confirm button at `:819` lives inside the
`PendingActionConfirm` fragment (`:811-832`), which renders **only** while
`pendingActionLabel != null` (`:445`, `:640`, `:763`). A predicate containing
`pendingActionLabel` would therefore disable Confirm exactly whenever it is visible, and no
destructive action could ever be executed again.

So the page gets one predicate covering **in-flight** state only:

```csharp
// True whenever an async operation is running whose result could invalidate, or be
// invalidated by, another click. Every control on this page consults it.
//
// pendingActionLabel is deliberately NOT here. It means "waiting for the operator to type
// a ticket", not "busy": including it would disable the Confirm button at the only moment
// it is ever rendered. The per-button pendingActionLabel conditions already stop other
// actions from being staged on top of a staged one, and they stay.
private bool IsBusy =>
    isLoading || isCreating || isLoadingStatus || isSearching
    || loadingBatchUsers != null || actionInProgress != null
    || loadingReport != null || isDownloadingCsv;
```

Field declarations, for the implementing agent: `isLoading:835`, `isCreating:836`,
`isLoadingStatus:864`, `loadingBatchUsers:868`, `actionInProgress:869`, `loadingReport:870`,
`isSearching:879`, `pendingActionLabel:886`. `isDownloadingCsv` is new; see step 4.

Every control's `disabled` becomes `@(IsBusy || <its own preconditions>)`, with the
exceptions in Non-goals. The existing per-button conditions are kept, not replaced: they
express things `IsBusy` does not, such as "no ticket number entered", "nothing selected",
and "a confirmation is already staged".

This is deliberately a blunt instrument. A finer-grained matrix - this button is safe
during that operation but not this one - is more precise and much harder to keep correct,
which is how the page reached its current state. One predicate is auditable by a test; a
matrix is not. The single carve-out above is a correctness requirement, not a refinement.

### Prerequisite: `loadingBatchUsers` can currently stick

Widening a flag into a page-wide gate is only safe if the flag is always cleared.
`loadingBatchUsers` is not. In `SearchUser`, it is set at `:1723` and `:1755` and cleared at
`:1729` and `:1761` - both *after* the awaits at `:1728` and `:1760`. The method's `finally`
at `:1767-1770` clears only `isSearching`, and the `catch` at `:1763` swallows the exception
into `batchActionResult`. So a transient Exchange failure during a search that expands a
batch leaves `loadingBatchUsers` non-null for the life of the circuit.

Today that disables two refresh arrows. Under this plan it would deaden the entire page
until the operator reloads. This must be fixed first, as its own commit, before the gate is
widened. `RefreshBatchUsers` (`:1337-1354`) already does this correctly with a `finally`;
`SearchUser` must match it.

### Tabs

Anchors cannot be disabled. The three tab links get a guard at the top of their handler
instead - a plain `if (IsBusy) return;` - plus a `disabled`-looking CSS class so the state
is visible rather than a silent no-op. `:32`'s handler is `LoadMigrationStatus`, which is
also reached from two buttons; the guard belongs in the handler, so all three call sites
inherit it.

### Handlers that set their flag late

Several mutating handlers await authorization before setting the flag that is supposed to
make them single-flight: `CreateSingleMigrationBatch` awaits at `:971-972` and sets
`isCreating` at `:978`; bulk create does the same at `:1107-1114`; `ExecuteBatchAction`,
`ExecuteUserAction` and `ExecuteBulkBatchAction` await authorization before setting
`actionInProgress`/`isLoadingStatus` at `:1410-1414`, `:1466-1470` and `:1582-1614`. If
those awaits yield, the page can re-render with `IsBusy == false` and accept a second click.
Each of these sets its flag before its first await.

## Non-goals and deliberate exceptions

- **`CloseUserReport` (:778) stays ungated.** It is the cancel affordance. It is also not
  rendered during a pull: its `@if` at `:771` requires `userReport != null`, and
  `LoadUserReport` sets `reportUser`/`userReport` only after the await returns. Verified by
  reading `:771-784`.
- **`CancelPendingAction` (:830) keeps its current gate and does not get `IsBusy`.**
  Cancelling a staged confirmation is always executable.
- **`ConfirmPendingAction` (:819) gets `IsBusy` and keeps its existing conditions.** Because
  `IsBusy` excludes `pendingActionLabel`, this is safe: while a confirmation is staged and
  nothing is running, `IsBusy` is false and Confirm works normally.
- **`DownloadSampleCsv` (:219) and the dismiss button (:437)** are static and stateless.
  They get no gate; the test carries them as named exemptions so the exemption is explicit
  rather than an oversight.
- **No spinner, wording or layout changes** beyond the tab's disabled styling. If the
  operator needs to be *told* why a control is grey, that is separate work.
- **No cancellation.** Making an in-flight Exchange call abortable is out of scope.
- **Other modules are out of scope.** The same pattern very likely exists app-wide, since
  each page grew its own flags independently. That sweep is unscoped and needs its own plan.

## Steps

Two commits, because the first is a defect fix in its own right and the repo rule is one
fix per commit.

**Commit 1 - make `loadingBatchUsers` always clear.**

1. Restructure `SearchUser` (`:1700-1771`) so `loadingBatchUsers` is cleared in a `finally`
   alongside `isSearching`, and remove the two post-await clears at `:1729` and `:1761`.
2. Add a tripwire asserting that every method assigning `loadingBatchUsers` a non-null value
   also clears it in a `finally`. Prove it bites by restoring the old shape.

**Commit 2 - the gate.**

3. Add the `IsBusy` predicate next to the state fields.
4. Add an `isDownloadingCsv` flag to `DownloadCsvAsync` (`:1904-1914`), set before its first
   await and cleared in a `finally`, and include it in `IsBusy`. Without it, `IsBusy` cannot
   close the double-download the survey names.
5. Move the late flag assignments in the five handlers listed above to before their first
   await.
6. Apply `@(IsBusy || ...)` to all 31 buttons except the four named exceptions, preserving
   every existing condition.
7. Add the `if (IsBusy) return;` guard to the tab handlers and the disabled styling.
8. Add the tripwires below.
9. Prove they bite: revert the gate on three representative controls in turn - a destructive
   one (`:599` Delete), the Report button (`:737`), and an ungated one (`:604` toggle) -
   confirm failure each time, restore byte-identically.
10. Bump the Migration module `Version` in `Modules/ModuleCatalog.cs`, 1.8.2 -> 1.9.0.
    Behavioural change to how the page accepts input, so it takes the minor position. No
    base app bump: the change is module-scoped, per `docs/ProjectConstitution.md`.
11. Verify, record, commit.

## Tests

Source-level tripwires, consistent with the rest of this file. There is no bUnit harness in
this repo, so nothing can render `Migration.razor`; the tests read the page as text. This
limitation is inherited, not introduced here, and is already recorded in
`docs/MigrationStaleReport-Plan.md`. It is the reason tests 4-6 exist: a test that only
checks "`disabled` mentions `IsBusy`" cannot see a stuck flag or a late assignment, so those
invariants need their own assertions.

1. `EveryButtonConsultsTheBusyPredicate` - extract every `<button` opening tag and assert
   its `disabled` contains `IsBusy`, except for a hard-coded exemption list keyed on the
   `@onclick` target rather than the line number. **The scanner must be quote-aware:** the
   tags at `:571`, `:604`, `:738` and `:750` contain lambda `@onclick` handlers, so a naive
   `<button\b.*?>` stops at the `>` inside `=>` and reads a truncated tag. Walk the tag
   character by character, tracking quote state, and stop at the first `>` outside quotes.
2. `TheBusyPredicateNamesEveryInFlightFlag` - assert `IsBusy` mentions the eight in-flight
   fields.
3. `TheBusyPredicateExcludesStagedConfirmationState` - assert `IsBusy` does **not** mention
   `pendingActionLabel`, and that the Confirm button's `disabled` does not either. This is
   the test that keeps the module executable; it is the most important one here.
4. `TheExemptButtonsAreExemptOnPurpose` - assert the exemption list has exactly the four
   expected entries, so widening it is a visible diff.
5. `EveryInFlightFlagIsClearedInAFinally` - for each of the eight fields, assert every method
   that assigns it a non-null/true value also has a `finally` clearing it.
6. `SingleFlightHandlersSetTheirFlagBeforeTheFirstAwait` - for the five named handlers,
   assert the flag assignment precedes the first `await` in the method body.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

## Manual acceptance checklist

Needs a dev deploy. Nothing in this repo reaches the rendered page.

- [ ] Stage a destructive action (Delete on a batch). Type a ticket number. Confirm the
      **Confirm** button is enabled and the action executes. This is the regression the
      review caught; check it first.
- [ ] Expand a batch. Click the refresh arrow. While it spins, confirm every control is
      greyed, including Report on every row and the tab links.
- [ ] Click Report on a problematic user. While it pulls, confirm the refresh arrows, the
      action buttons, the other rows' Report buttons and the Migration Status tab are all
      unresponsive, and that Close is not offered until the report arrives.
- [ ] Confirm the report still arrives and renders after a multi-minute pull - the gate must
      not interfere with the pull itself.
- [ ] Delete a batch. While the action runs and the table reloads, confirm no destructive
      control is clickable.
- [ ] Force a search failure against a batch name (disconnect, or search during an outage).
      Confirm the page recovers and the controls come back - this is the stuck-flag path.
- [ ] Confirm that after every operation completes, the controls come back. A stuck `IsBusy`
      would leave the page permanently dead, which is the main risk this change introduces.

## Known gaps

- `IsBusy` is a manual list. Test 2 pins the current eight; a ninth flag added later that
  nobody adds to the predicate reopens the hole. Test 5 narrows this by forcing any flag to
  be `finally`-cleared, but it cannot force inclusion in the predicate.
- The blunt predicate greys controls that would in truth have been safe - for example the
  sample CSV download during a report pull. Accepted as the cost of an auditable rule.
- The stuck-predicate risk is the main hazard this change introduces: the blast radius of a
  flag that is not cleared goes from one button to the whole page. Commit 1 and test 5 exist
  for this, and the review that produced them found one live instance already in the code,
  which is evidence the hazard is real rather than theoretical.
- Tests 5 and 6 are text analysis of C# inside a `.razor` file, not semantic analysis. They
  can be defeated by unusual formatting. They are tripwires against drift, not proofs.

## Pending decisions

None. The scope question - this page or every module - was settled as this page only, with
the app-wide sweep recorded as a separate unscoped follow-up.

## Review

Reviewed 2026-09-17 before implementation.

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Harness codex-cli 0.154.0, read-only sandbox, HEAD `523b69d`, dispatched 2026-09-17T17:28Z,
returned 17:33Z. Capability proof: read `docs/MigrationButtonGating-Plan.md`, ran
`git log --oneline -3`, both succeeded. Verdict: `sound_with_changes`, seven findings.

All seven were checked against the file before being accepted; none were taken on the
reviewer's word. Every one was confirmed and this revision incorporates all of them:

| # | finding | confirmed by | disposition |
| --- | --- | --- | --- |
| 1 | HIGH. `pendingActionLabel` in the predicate permanently disables Confirm | read `:811-832`, `:445`, `:640`, `:763` | Predicate split; Confirm carve-out and test 3 added |
| 2 | HIGH. `SearchUser` can leave `loadingBatchUsers` stuck | read `:1715-1771`; `finally` clears only `isSearching` | Promoted to commit 1, plus test 5 |
| 3 | MEDIUM. The status tab is an `<a>` the button sweep cannot reach | read `:24-34`; anchors ignore `disabled` | New Tabs section; handler guard |
| 4 | MEDIUM. `DownloadCsvAsync` has no flag, so the predicate cannot fix it | no flag in `:1904-1914` | `isDownloadingCsv` added, step 4 |
| 5 | MEDIUM. Five handlers set their flag after awaiting authorization | line refs in the finding | Step 5, plus test 6 |
| 6 | LOW. Survey said six ungated buttons; its own table listed seven | recounted | Corrected to seven |
| 7 | LOW. A naive `<button.*?>` regex breaks on lambda `=>` | tags at `:571`, `:604`, `:738`, `:750` | Test 1 now requires a quote-aware scanner |

Finding 1 is the one that mattered: the plan as first written would have shipped a page on
which no destructive action could ever be confirmed, and the proposed tests would all have
passed.

## Provenance

Owner ruling, 2026-09-17, in response to the `msr-1` fix (`2b93fdc`): pulling a report can
take several minutes, so a click that will be discarded must not be accepted. The ruling is
general; this plan applies it to one page. Record it in `.agents/decisions.md` when the plan
is approved, so it binds new modules rather than living only here.
