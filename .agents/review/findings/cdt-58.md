# cdt-58: promote, install and cutover stop a pool without naming its other applications

**Severity**: LOW - cdt-54 closed this for `deploy.ps1` and exported `Get-AppPoolOtherApplication`; the promotion,
installer and cutover paths still stop a pool with no word about the other applications it serves or their bulk jobs.
**Status**: Open - admitted 2026-10-26 from the cdt-36 agent's fourth hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `84a31608`; raised by the cdt-54/cdt-55 agent. `Get-AppPoolOtherApplication` is exported from
`tools/AppPoolControl.psm1` and has no caller outside deploy.ps1.

## Approach (to be written by the implementing agent before code)

- Each remaining stop path calls the same helper and reports what else the pool serves, the cdt-54 shape; Pester per
  path; mutation: one call removed.

## Guard proof

Pending.