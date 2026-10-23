# cdt-49: in a pool move the active-jobs warning reads the instance running today while the stop hits the new pool

**Severity**: LOW - a warning only. `deploy.ps1`: with `-AllowRepoint` to a new pool, the active-jobs check (`tools/JobStateWarning.psm1`) reads the current instance's jobs database while the stop/start acts on the new pool, so the warning can describe the wrong instance.
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's second hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5`. Trigger: a -AllowRepoint pool move with jobs running on the old instance. Predicted failure: the warning is about the wrong pool, or absent.

## Approach (to be written by the implementing agent before code)

- In a move, run the active-jobs warning against the instance that will actually be stopped (the old pool and folder) and say which it checked; Pester; mutation: the old-instance read dropped.

## Guard proof

Pending.