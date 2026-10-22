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
**Branch**: `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `22088cf2` into the a23j-m agent's scope, first
in its order (HIGH). One commit `fix(migration): ... (aud-a23n)` on branch `worktree-agent-a03d6bd111b571e5a` (forked
at `b16898dc`; this record taken from `22088cf2`), before aud-a23j. Module-scoped: Migration (the bump is the
supervisor's; `Modules/ModuleCatalog.cs`, `ExchangeAdminWeb.csproj` not edited). No page change. Line numbers at
`b16898dc`, `Services/MigrationService.cs`.

1. **Part 2, the pre-read reads the batch's state** (`ApproveMigrationUserAsync`, 1013-1036). After the existence read
   and its count check, a batch of its own has its `Status` read before any write:
   - no status (blank or absent): refused - "<address> was not approved: its migration batch <name> reports no
     status, so whether it can be completed is not known. Nothing was changed.";
   - `Completed`: refused in its own words - "... is already Completed, so its migration has finished and there is
     nothing left to approve. Nothing was changed." - so it does not read as a fault;
   - `CompletedWithErrors`, `Completing`, `Failed`, `Removing`, `Stopped`, `Stopping`: refused naming the state -
     "... is <Status>, a state an approval does not complete a batch from. Nothing was changed.";
   - any other status: the writes run as today. Matching ignores case and surrounding space.

   Each refusal throws before the first `Set-MigrationUser`, so `RunAsync` returns it as the row's failure and both
   callers' existing audits record it Failed with that text. Why these states: the record's four (Completed, Failed,
   Removing, Stopped); their in-flight forms (Completing, Stopping - the batch planner's own "finishing now / being
   torn down" grouping, `NonSchedulableStatuses`); and CompletedWithErrors, which the owner ruled on 2026-09-25 has
   nothing to finalise until remediated (`MigrationBatchActionPlanner`). **Assumption, labelled:** these names are this
   app's reading of `Get-MigrationBatch`'s Status, with no live call to check them. An unlisted status proceeds, and
   Exchange's own refusal of a later step is still reported naming the step and what was applied (aud-a23h).
2. **Part 1, a successful Complete-MigrationBatch is the completion** (`ConfirmBatchSetToComplete`, 1116-1146). The
   read-back stays (the aud-a2l/a2p shape) but no longer decides the outcome. It becomes `DescribeCompletionReadBack`,
   which never throws and returns the Detail of a SUCCESSFUL result, saying what was and was not confirmed:
   - read, with a completion time (`CompleteAfter` or `CompleteAfterUTC`): "Exchange accepted Complete-MigrationBatch
     for its migration batch <name>; read back, the batch reports status <Status> with a completion time set.";
   - read, with none: "...; read back, the batch reports status <Status> and no completion time, so the read-back
     did not confirm the schedule.";
   - the read fails, or returns other than one batch: "...; reading the batch back failed (<reason>) / returned N
     batches, so its state since was not confirmed."

   No status is interpreted as "complete": the Detail reports what the cmdlet returned. The message is unchanged
   ("Approved skipped items and initiated completion for <address>."). A batch step that fails before it is still
   the row's failure, naming the step (aud-a23h, unchanged).
3. Not changed: the mailbox-level steps; a mailbox not in a batch of its own (no batch steps, as aud-a23h left it);
   `CompleteMigrationBatchAsync` and the batch toolbar.

**Tests.** `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: `Batch(...)` takes a status; two switches,
`CompleteClearsSchedule` (Complete-MigrationBatch removes the batch's `CompleteAfter`, so the read-back omits it) and
`CompletionStatus` (it sets the batch's Status). New `ExchangeAdminWeb.Tests/MigrationApproveConfirmationTests.cs`,
fakes only:
- **part 1**: a read-back that omits the schedule - success, the message, the Detail "...no completion time, so the
  read-back did not confirm the schedule.", all nine calls; a read-back that fails - success, "reading the batch back
  failed (...)"; a read-back with a changed status - the Detail names it.
- **part 2**: a batch in each refused state (a theory, one row lower-case) - failed, the state named, "Nothing was
  changed.", the existence read the only call; Completed - its own words, only the read; no status - refused, only
  the read; through the page's `ExecuteUserAction` - one audit row, Failed, the refusal as its error.
- The aud-a23h tests that pinned the old read-back (`ACleanApproval...`'s Detail, `AReadBackThatFails...`,
  `AWriteThatChangesNothing...`) take the new outcome: success, with the Detail.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green):
N1 (the record's (1)) the schedule field as the success criterion again - a read-back with no completion time throws
"was not confirmed"; N2 (the record's (2)) the Count-only pre-read again - the status check skipped. Extras: N1b a
failed read-back a failure again; N2b Completed refused in the generic words; N2c a batch with no status allowed.

**Gates** as the brief; comments after the mutation round.

## Guard proof

Pending.
