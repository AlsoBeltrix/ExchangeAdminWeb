# cdt-50: the cdt-46 8.3 Pester test can skip, and CI fails on any skip

**Severity**: LOW - CI gate risk. The 8.3 short-name test in `tests/ps/AppPoolControl.Tests.ps1` skips when neither the temp volume nor Program Files has a short name; `.github/workflows/ci.yml` fails the powershell job on any skip. Unlikely on windows-latest but not proven.
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's second hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5`. Trigger: a runner volume with 8.3 names disabled. Predicted failure: the powershell CI job fails on a skip, not a defect.

## Approach (to be written by the implementing agent before code)

- Make the test deterministic: create its own short-name fixture with `fsutil file setshortname` where 8.3 is enabled, and where it is disabled assert the documented GetFullPath behaviour on a synthetic path instead of skipping; no skip path remains. Mutation: not applicable beyond the existing cdt-46 proof (say so).

## Guard proof

Pending.