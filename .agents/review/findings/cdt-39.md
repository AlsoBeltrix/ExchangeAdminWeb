# cdt-39: docs/ConfigDbOperations.md points at deploy.ps1:447 for a refusal that now sits at lines 613 and 902

**Severity**: LOW - a stale line pointer in an operator document; an operator following it lands on unrelated code. The pointer was wrong before cdt-36.
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `72430407`: `docs/ConfigDbOperations.md` 49; `deploy.ps1` 613 and 902 (the refusal it describes).

## Approach

- Point at the refusal by its message or function name rather than a line number, so the pointer survives edits; or fix the two line numbers and say which. No guard beyond a doc check; record the diff.

## Guard proof

Not applicable (docs only).