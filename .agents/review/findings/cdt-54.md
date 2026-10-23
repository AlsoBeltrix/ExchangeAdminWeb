# cdt-54: other applications sharing the -AppPoolName pool are stopped with it and their jobs are not read

**Severity**: LOW - pre-existing. `deploy.ps1` stops the named pool; any other IIS application served by the same pool stops too, and the active-jobs warning (cdt-49) reads only this instance's jobs database.
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's third hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `fb2b288f`. Trigger: a shared pool. Predicted failure: another app's in-flight work is interrupted with no warning.

## Approach (to be written by the implementing agent before code)

- Before stopping, enumerate the pool's applications; if any other than this instance is served by it, warn (or refuse without an explicit switch) and -PlanOnly reports it; Pester; mutation: the enumeration removed.

## Guard proof

Pending.