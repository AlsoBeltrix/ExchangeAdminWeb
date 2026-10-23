# kd-2: Migration - a tick from batch A survives into batch B as "Actions (1)" with no ticked row

**Severity**: LOW - confusing, not destructive. `Components/Pages/Migration.razor` `AdoptSelectionAsOpenBatch` (about 1840) never clears `selectedMailboxes`; `MigrationUserActionPlanner.PruneSelection` runs only through `TicksInThisBatch()` (about 2774).
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: tick a mailbox in batch A, open batch B. Predicted failure: the action bar shows a count with no visible selection; the rc-1..rc-3 confirm-bar counting may disagree with the rows shown.

## Approach (to be written by the implementing agent before code)

- Adopting a different open batch clears or prunes the selection to that batch's rows; the page is line-keyed (`ClickGateRegistry`), so edit in place or re-anchor per the playbook.
- Mutation: the clear removed.

## Guard proof

Pending.