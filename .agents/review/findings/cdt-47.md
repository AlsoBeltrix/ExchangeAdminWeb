# cdt-47: docs/Containerization-Feasibility.md still carries stale deploy.ps1:<line> pointers

**Severity**: LOW - stale operator-doc pointers (the cdt-43 shape). `docs/Containerization-Feasibility.md` names `deploy.ps1` by line numbers that have moved. (`.agents/state.md` 210 has the same and is the supervisor's at close.)
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's second hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5`. Trigger: a reader follows the pointer. Predicted failure: lands on unrelated code.

## Approach

- Point by the refusal's words or function name as cdt-43 did; doc check only; record the diff.

## Guard proof

Not applicable (docs only).