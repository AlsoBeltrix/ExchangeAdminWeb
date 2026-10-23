# aud-a23r: Migration Approve still runs its multi-step writes for batch states it cannot complete, and the excluded-group user lookup is scoped to the host's domain, not the forest

**Severity**: HIGH (part 1), MEDIUM (part 2). (1) aud-a23n made the pre-read a REFUSE list (Completed,
CompletedWithErrors, Completing, Failed, Removing, Stopped, Stopping, no status), so an unlisted state
proceeds: with the batch Syncing, Corrupted, SyncedWithErrors or any state the agent did not foresee, the
approval applies `Set-MigrationUser`, `Set-MigrationBatch -CompleteAfter`, `Set-MoveRequest` and
`Resume-MoveRequest` before `Complete-MigrationBatch` refuses, leaving partial writes (and possibly a past
completion request) after the page reports failure (Known Failure Class 1). `MigrationBatchActionPlanner`
already says a normal Complete is valid only for `Synced`; the approval tests explicitly let Syncing and
SyncedWithErrors run every step. (2) aud-a23j's `Get-ADUser -Filter` runs with no `-Server` and no
forest/GC search, so a ToCloud user whose AD object is in another domain of the forest reads "no directory
user has the user principal name ..." and is Undetermined - fail-closed, but a functional false block for a
valid mailbox whenever excluded groups are configured.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; folded into the aud-a23o/p/q agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (mig-batch-7, 2026-10-22, candidates 1 and 2)

## Evidence

At `ee02f283`: `Services/MigrationService.cs` around line 1289 (the refuse list) and around line 293 (the
user lookup); `Services/MigrationBatchActionPlanner.cs` around line 146 (Complete valid only for Synced);
`ExchangeAdminWeb.Tests/MigrationApproveConfirmationTests.cs` (the cases that let Syncing and
SyncedWithErrors run every step).

## Approach (to be written by the implementing agent before code)

- Approval's batch-completion path is an ALLOW list that agrees with the planner: the completion steps run
  only when the own batch's status is one the planner names as completable (`Synced`; the planner is the
  single owner of that rule - call it, do not copy it); any other status refuses before any write, naming
  the state and that nothing was written. Where mailbox approval without batch completion is a valid
  operator intent, split it so the mailbox step alone runs and the Detail says the batch was not completed
  and why; otherwise refuse whole. The tests that let Syncing/SyncedWithErrors run every step are inverted.
- The excluded-group user lookup finds the user across the forest: the forest GC search by UPN (the aud-a2d
  shape - `mail`/`userPrincipalName` against a GC, host forest discovered at runtime, nothing named -
  Invariant 7), then `Get-ADUser -Server <domain of the found object>` for `memberOf`; the group
  resolutions `Get-ADGroup -Identity` likewise against the group's domain, resolved once per check run and
  cached for that run. A user found nowhere in the forest is still Undetermined (fail closed).
- Tests through the `MigrationStubExchange` AD stand-in (a user in a second domain; the planner's
  completable set vs each batch status); mutations at the call site: the refuse list again; the lookup
  without the forest search again.

## Guard proof

Pending.
