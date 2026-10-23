# cdt-42: deploy.ps1's upgrade path reconfigures and grants folder ACLs to the pool named by -AppPoolName before the binding check can refuse

**Severity**: MEDIUM - an upgrade run whose `-AppPoolName` names another existing pool (one that runs as a user) reconfigures that pool (managedRuntimeVersion "", AlwaysRunning, loadUserProfile) and grants its account Modify on this instance's folders, then refuses with "runs under app pool X" - leaving the other pool changed and its account holding ACLs on this instance. The refusal arrives after the side effects (Known Failure Class 1: side-effect ordering).
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's hand-back; inside the released scope. Filed first as cdt-38 in `f8d9e6fd` by supervisor error (that ID was taken); re-filed as cdt-42 and the original cdt-38 record restored.
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `72430407`: `deploy.ps1` 416-426 (the upgrade path's pool step), 474-490 (the ACL grants), 673-681 (the upgrade's own binding check, which runs after both). Trigger: `deploy.ps1` upgrade with `-AppPoolName` naming a different existing pool. Predicted failure: the other pool is reconfigured and its identity gets Modify on the publish, logs and config folders; the run then refuses; nothing undoes the changes.

## Approach (to be written by the implementing agent before code)

- Move the binding check (or the cdt-36 `Assert-ApplicationTargetUnchanged` read) ahead of the pool step and the ACL grants on the upgrade path so a mismatch refuses before any change; Pester proves the order (the stubbed-step style the cdt-36 test uses).
- Mutation: the check moved back after the pool step.

## Guard proof

Pending.