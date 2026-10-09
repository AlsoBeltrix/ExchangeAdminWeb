# rc-6: a skipped row printed the placeholder instead of the status

**Severity**: LOW - no wrong action, but the per-row outcome that is supposed to tell the
operator WHY a mailbox was skipped printed `Skipped ({skip.Status})` literally. The banner
says "Per-mailbox results are on the rows", and the rows said nothing usable.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from
`git log -1 -- .agents/review/findings/rc-6.md`

## Evidence

Pre-existing. Present at the base of this range, `029d9d0`, in FOUR places - verified with
`git show 029d9d0:Components/Pages/Migration.razor | grep -n 'Skipped ("'`, which returns
lines 3354, 3408, 3509 and 3555. Each read:

    actionOutcomes[earlySkip.EmailAddress] = $"Skipped ("+"{earlySkip.Status}"+")";

`$"Skipped ("` is an interpolated string holding no hole; `"{earlySkip.Status}"` is a PLAIN
literal whose braces are only characters. Concatenated, the value is the nine-character
placeholder, not the status.

Two in `ExecuteBulkBatchAction`, two in `ExecuteBulkMailboxAction` - the early-skip loop and
the post-run loop in each.

## Predicted observable failure

Stage any mailbox or batch bulk action over a mixed selection and confirm. The banner reads
"Queued removal of 2, 1 skipped of 3 mailboxes. Per-mailbox results are on the rows." The
skipped row reads `Skipped ({skip.Status})`.

## What

Why it survived is the interesting half, and it is this repo's recurring shape: the PREVIEW
sites were always correct. `StagedOutcomeFor` - both the batch and the mailbox one - writes
`$"Skipped ({skip.Status})"` properly, and a test has pinned that since S4. A reader checking
"does the status travel with the word" finds right code two methods away from the wrong code
and stops looking.

This change did not cause it. It is fixed here because the new control's whole contract is
that an in-scope mailbox which will not run is NAMED with the status that disqualified it,
and the plan's test 6 says so; shipping that contract on top of a row that prints a
placeholder would be shipping it broken.

## Approach

All four sites become `$"Skipped ({earlySkip.Status})"` / `$"Skipped ({skip.Status})"`.

All four, not the two in the mailbox executor: it is one defect with four sites, and leaving
the identical broken expression two methods away would be indefensible. No behaviour changes
beyond the rendered text, and the page line count is unchanged, so no ClickGate re-anchor.

## Files changed

- `Components/Pages/Migration.razor` - four string expressions. Line count unchanged at 4331;
  no registry edit.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` - one test.

No module version bump: this corrects the unreleased `1.23.0` slice, and the pre-existing
half of it has never shipped a behaviour anyone configured against.

## Guard proof

`MigrationStatusPageTests.ASkippedRowNamesItsStatusRatherThanAPlaceholder` asserts per
EXECUTOR - both of them - that the concatenated form is absent and that the interpolated form
appears exactly twice, and then asserts the concatenated form is absent from the whole page.
The per-method scoping is deliberate: a whole-file `DoesNotContain` would be satisfied by
deleting one site.

Mutation probe: one site restored to the defect verbatim, which compiles. Result 1 failed /
104 passed, naming exactly that test. It BUILT - checked for `error CS` before the result was
read. Page restored from a copy outside the tree, verified byte-identical by md5
(`bd8f5aec30998b8f62b76a545bacf1a3`), then touched.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors. `dotnet test ExchangeAdminWeb.slnx`
3724 passed / 0 failed / 3 skipped (3723 before; one test added).
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0.
`git diff --check HEAD` clean. No non-ASCII added.

## Coder dispute (if any)

The reviewer attributed the visibility to the new control sending non-completed filtered rows
as skipped candidates. That was true of the slice as first landed and is no longer: `rc-1`
bounded the EXECUTOR's candidate set to `staged.Eligible`, so at execution the only skips are
mailboxes that stopped being eligible between staging and Confirm. The defect is still
reachable from this control by that route, and reachable from all five existing actions with
any mixed selection, so the finding stands - the reach is just wider than this control and
older than this change. Recorded rather than silently accepted, because the record should not
claim this slice introduced it.

## Known gaps

Source-text guard: it proves the expression, not the rendered row. There is no bUnit harness.

## Reviewer comments

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Harness: codex-cli 0.159.0. Round 3, reviewing `029d9d0..e1b73e7`, verdict `findings` (2),
capability proof passed. Envelope `.agents/review/rcimpl3.result.json`.
