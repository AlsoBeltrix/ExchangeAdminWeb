# rc-3: changing the mailbox filter mid-confirmation hid the rows Remove completed would remove

**Severity**: MEDIUM - the count bound holds and the right mailboxes are removed, but the
operator loses the ability to see WHICH ones at the moment they are typing the ticket. For a
destructive control whose whole case is that it shows its work before it does it, that is the
case being lost rather than a cosmetic problem.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from
`git log -1 -- .agents/review/findings/rc-3.md`

## Evidence

`Components/Pages/Migration.razor` - the mailbox filter box carried `disabled="@IsBusy"` only.
`pendingActionLabel` is deliberately excluded from `IsBusy` and is a registered
`ExcludedField` in `ClickGateRegistry`, so the box stayed typeable for the whole of the
confirmation step.

`StageRemoveCompletedMailboxes` sets `pendingUserActionPlan = staged`, and the row annotations
are rendered per row by address through `StagedOutcomeFor`. Rows are rendered from
`GetPagedMailboxes()`, which is `OtherMailboxes()`, which is `FilteredSortedMailboxes()` minus
the pinned rows.

The asymmetry that makes this specific to the new control: the other five mailbox actions act
on TICKED rows, and R11 pins ticked rows above the list - `PinnedMailboxes()` reads
`batchUsers`, not the filtered set, precisely so a filter cannot hide a ticked row. Remove
completed acts on rows that are NOT ticked, so its staged rows are rendered only through the
filtered path.

## Predicted observable failure

Open a batch, type a filter that leaves three completed mailboxes in view, click
**Remove completed (3)**. The three rows show "Will run" and the confirm bar says
`3 mailbox(es)`. Now change the filter - the box is right beside the ticket field - and the
three annotated rows disappear, replaced by rows with no annotation at all. Press Confirm:
the original three are removed, correctly, from a table that has not shown them for the last
several seconds.

## What

The HIGH 2 and `rc-1` fixes made the SET immune to the filter. They did not make the DISPLAY
of that set immune to it, and the display is what the confirmation step is for.

## Approach

The mailbox filter box takes `disabled="@(IsBusy || pendingActionLabel != null)"` - a wider
gate than any other view control on this pane, and the reason is written at both the markup
and the registry entry rather than only here.

The sort controls are deliberately NOT widened: reordering changes which row is where, not
which rows exist, so it cannot hide an annotation.

Cancelling the staged action on a filter change was considered and rejected. The box binds
`oninput`, so a single keystroke would silently discard a staged destructive action - and the
operator who meant to type in the ticket field and hit the filter box instead would lose the
staging with no explanation. Freezing is the refusal that explains itself: the box greys out.
Cancel remains available throughout and keeps its own narrower gate
(`disabled="@(actionInProgress != null)"`, registered as an exemption).

## Files changed

- `Components/Pages/Migration.razor` - the filter input's gate, with the reasoning as a Razor
  comment in place.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - the `DomSyncedControl` for that input now
  registers the wider expression and carries the reason. Plus the re-anchor: the comment added
  above the input is 12 lines, so `ExpectedLineCount` 4310 -> 4322 and every line key at or
  below 963 is unchanged while every key above it moved by exactly 12 (964->976, 969->981,
  1004->1016, 1240->1252, 1265->1277, 1421->1433, 1478->1490 twice, 1495->1507,
  GatedTwinButtonLine 1501->1513, 1528->1540). Taken from the diff hunk headers - two pure
  insertions - and each new line read back to confirm it carries the control its snippet
  names.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` - one test.
- `docs/MigrationRemoveCompleted-Plan.md` - the Gate section carries the freeze and the
  rejected alternative.

No module version bump: this corrects the unreleased `1.23.0` slice.

## Guard proof

Two independent guards, which is deliberate: one reads the page, one reads the registry
against the page.

`MigrationStatusPageTests.RemoveCompleted_FreezesTheMailboxFilterWhileAnActionIsStaged`
extracts the filter input's tag by its unique placeholder and requires the exact expression.
`ClickGateTests.EveryDomSyncedControlStillCarriesItsRegisteredGate` fails if the page's gate
stops matching the registered one.

Mutation probe: the gate reverted to `disabled="@IsBusy"`, which is the defect verbatim and
compiles. Result 2 failed / 417 passed, naming exactly those two. It BUILT - checked for
`error CS` before the result was read. Page restored from a copy outside the tree, verified
byte-identical by md5 (`ea6563ad3ab4e4e9181b2cd130508e92`), then touched.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors. `dotnet test ExchangeAdminWeb.slnx`
3722 passed / 0 failed / 3 skipped (3721 before; one test added).
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0.
`git diff --check HEAD` clean. No non-ASCII added.

## Coder dispute (if any)

None on the defect. The reviewer offered two fixes - freeze the controls, or cancel/restage on
a filter change - and the first was taken, scoped to the filter alone rather than to
"filter/sort", because sorting hides nothing. The second is argued against above rather than
passed over.

## Known gaps

**Paging is not frozen and this is a known, accepted hole.** An operator can page past the
annotated rows while an action is staged, and the preview is then off-screen in the same way.
It is not fixed here because it is not specific to this control - the batch pane has had the
same property since S4 - and because paging is reversible navigation within a set the
operator can see the size of, where a filter change silently redefines the set. Worth a
separate look if the manual acceptance pass finds it confusing in practice.

Still a source-level guard: it proves the attribute text, not that the browser greys the box.

## Reviewer comments

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Harness: codex-cli 0.159.0. Round 2, reviewing `029d9d0..c08f9e8`, verdict `findings` (2),
capability proof passed. Envelope `.agents/review/rcimpl2.result.json`.
