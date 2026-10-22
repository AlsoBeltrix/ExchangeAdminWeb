# aud-a23n: Migration Approve - the read-back can report failure after a successful completion, and the pre-read ignores the batch's state before the partial writes

**Severity**: HIGH (part 1), MEDIUM (part 2). (1) `ApproveMigrationAsync` confirms completion by reading
the batch back and requiring `CompleteAfter` / `CompleteAfterUTC` to be set; if Exchange clears or stops
returning that scheduling field once `Complete-MigrationBatch` has been accepted, the approval is reported
as failed ("Completion ... was not confirmed") after the irreversible write succeeded, inviting a retry
(Known Failure Class 1: a post-side-effect false failure). The aud-a23i stub keeps the field set, so the
tests cannot see it; the record labelled this an unverified assumption. (2) The aud-a23i pre-read checks
only that a batch named after the mailbox exists, not its state: a batch already completed, failed or
syncing passes the guard, and the approval then fails at a later step after `Set-MigrationUser` /
`Set-MigrationBatch -CompleteAfter` already wrote.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; folded into the aud-a23j/k/l/m agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (mig-batch-6, 2026-10-22, candidates 1 and 2)

## Evidence

At `b16898dc`: `Services/MigrationService.cs` around line 1138 (the read-back and its "not confirmed"
outcome) and around line 1013 (the pre-read checking `ownBatch.Count` only); the aud-a23i record's
labelled assumption about `CompleteAfter`.

## Approach (to be written by the implementing agent before code)

- Confirmation: a successful `Complete-MigrationBatch` is the completion; the read-back confirms against
  durable state (the batch `Status`, e.g. Completing/Completed/Synced-with-completion-requested, as the
  cmdlet reports it) and treats a missing scheduling field after an accepted command as "accepted; read-back
  did not confirm the schedule" detail on a SUCCESSFUL outcome, never a failure. Nothing in the code may
  assert what the live cmdlet returns: the detail text says what was and was not confirmed.
- Pre-read: inspect the batch state before any write; refuse before writing (nothing written) when the
  state is non-actionable (Completed, Failed, Removing, Stopped as the user chose), name the state; treat
  already-completed as its own outcome.
- Tests through `MigrationStubExchange` with a stub whose read-back omits `CompleteAfter`, and with batches
  in each non-actionable state; mutations at the call site: the schedule-field confirmation again as the
  success criterion; the `Count`-only pre-read again.

## Guard proof

Pending.
