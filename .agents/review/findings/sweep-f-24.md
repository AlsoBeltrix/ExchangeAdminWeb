# sweep-f-24: approving a mailbox in a batch of its own needs that batch to be exactly "Synced" (sweep item 24)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 24: `MigrationService.RefuseUnlessApprovable` refuses
any own-batch status `MigrationBatchActionPlanner.Applies(Complete, ...)` does not accept (exactly `Synced`). Single-user batches
are the common case; a batch at `SyncedWithErrors` was refused "... not a status the batch Complete action completes a batch
from. Nothing was changed."
**Status**: Fixed on branch (message half); the widening is a reported conflict, not changed; awaiting review
**Branch**: `fix/sweep-f` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-f`, from master `624ff16e`)
**Commit**: `0c8e0c42` (`fix(sweep-f, item 24): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- Microsoft documents (learn.microsoft.com, Complete-MigrationBatch, read 2026-11-06): "After a migration batch has
  successfully run and has a status of Synced or SyncedWithErrors, use the Complete-MigrationBatch cmdlet to finalize the
  migration batch." So the brief's premise - SyncedWithErrors is completable - is Exchange's documented behaviour.
- Against it: `.agents/decisions.md` 2026-09-25 "A migration batch with errors is not completable": "`MigrationBatchActionPlanner.Applies`
  keeps Complete to exactly `Synced`" ("a wrongly-accepted Complete cuts mailboxes over"); and aud-a23r (HIGH, merged), whose
  approved approach makes the approval complete exactly when the Complete action would ("one owner"; the tests that let
  SyncedWithErrors run every step were inverted, `TheApproval_CompletesItsBatchExactlyWhenTheCompleteActionWould`).

## Approach (as built) - the part that conflicts with nothing

- A status ending `WithErrors` (SyncedWithErrors, CompletedWithErrors, any case) is refused before any write in words that say
  the batch holds errors, that approving completes the batch and a batch with errors is not completed here, and what to do: fix
  or remove the errors the batch's mailbox report names, then approve again. "Nothing was changed." as before. The message is
  the result and the audit row's error (`WithErrorsRefusal`). Every other status's refusal, and the rule, are unchanged.
- README Migration; Migration `1.27.2`.

## Not done because

- **Accepting SyncedWithErrors for the approval.** The brief's rule: "If a fix conflicts with an existing HIGH finding or a
  decision, leave that part undone and explain." It conflicts with both above. The owner's question: should the approval (and
  perhaps the batch Complete action) complete a single-user batch from SyncedWithErrors, given Microsoft documents it? Context
  that bears on it: the approval's own steps approve the skipped items (`Set-MigrationBatch -ApproveSkippedItems`) before
  `Complete-MigrationBatch`, which is plausibly what clears a NeedsApproval batch's "errors"; if a NeedsApproval user's batch
  always reads SyncedWithErrors, approval of a single-user batch never runs today.

## Assumptions not verified live

- What status a single-user batch whose user is NeedsApproval reports (the sweep's read-only check below answers it).

## Guard proof (`MigrationApproveConfirmationTests`)

| Mutation | Failing tests |
|---|---|
| M24a the with-errors branch removed | `ABatchThatHoldsErrors_IsRefusedSayingSo_BeforeAnyWrite` (3), `ABatchInAStateAnApprovalCannotCompleteFrom_...` (SyncedWithErrors, CompletedWithErrors), `TheApproval_CompletesItsBatchExactlyWhenTheCompleteActionWould` (the same two) |
| M24b the match case-sensitive | `ABatchThatHoldsErrors_IsRefusedSayingSo_BeforeAnyWrite(syncedwitherrors)` |

`TheCompleteRule_IsStillExactlySynced` pins that the rule did not widen.

## Gates

Run once for the branch at its tip; see the sweep-f hand-back.

## Deploy-time checks

- The sweep's, read-only, before the owner answers the question above: `Get-MigrationUser -ResultSize Unlimited | ? Status -eq
  NeedsApproval | % { Get-MigrationBatch -Identity $_.BatchId } | select Identity,Status`. If those batches read
  SyncedWithErrors, single-user approval is refused today with the new message.
