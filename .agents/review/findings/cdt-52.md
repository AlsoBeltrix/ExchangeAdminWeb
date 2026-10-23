# cdt-52: the installer's move refusal does not name a folder spelling that does not exist on disk

**Severity**: LOW - fail-closed, less actionable than deploy.ps1's message after cdt-46. `tools/Install-ExchangeAdminWeb.ps1` (the cdt-15/cdt-48 check): when a compared path does not exist on disk the refusal does not say which spelling differed.
**Status**: Open - admitted 2026-10-23 evening from the cdt-36 agent's third hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `fb2b288f`. Trigger: a mismatch where one path does not exist. Predicted failure: the operator cannot tell which value to fix.

## Approach

- Mirror deploy.ps1's message (name both spellings and which did not resolve); Pester pins it; mutation: message reverted.

## Guard proof

Pending.