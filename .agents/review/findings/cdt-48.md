# cdt-48: the installer's cdt-15 comparison stays literal, so a junction-spelled IIS path is refused early

**Severity**: LOW - fail-closed but inconsistent with deploy.ps1 after cdt-46. `tools/Install-ExchangeAdminWeb.ps1` (the cdt-15 check) compares paths literally; an application whose IIS `physicalPath` is a junction spelling of the same folder is refused as a move.
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's second hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5`. Trigger: IIS stores a junction path; the installer is run with the target path. Predicted failure: a false "would move the application" refusal.

## Approach (to be written by the implementing agent before code)

- Use the shared `Test-SamePhysicalFolder` from `tools/AppPoolControl.psm1` without coupling the installer to deploy.ps1 (Architectural Invariant 1: the installer is standalone - if it cannot import the module, inline the same resolution); Pester with a junction fixture; mutation: literal comparison restored.

## Guard proof

Pending.