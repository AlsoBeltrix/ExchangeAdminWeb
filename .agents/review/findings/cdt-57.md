# cdt-57: the installer has cdt-55's shape - it generates appsettings.json with no ConfigStore:Path over an application IIS already serves

**Severity**: MEDIUM - the cdt-55 class in `tools/Install-ExchangeAdminWeb.ps1` (Architectural Invariant 3). The
installer's cdt-21 check refuses a CONFIGURED path whose database is missing, but not a MISSING appsettings.json over an
application IIS already serves: it generates a new file with no `ConfigStore:Path` and the instance opens a fresh
default database, leaving the shared one it was using behind and unbacked-up. cdt-55 closed this in `deploy.ps1` only,
because its record named that script and the installer is standalone (Invariant 1).
**Status**: Open - admitted 2026-10-26 from the cdt-36 agent's fourth hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `84a31608`; raised by the cdt-55 agent from its own change.

## Approach (to be written by the implementing agent before code)

- The installer applies the cdt-55 rule in its own terms, without importing deploy.ps1 or coupling to it: an EXISTING IIS
  application whose publish folder has no `appsettings.json` is refused with an actionable message. Pester; mutation:
  the refusal removed.

## Guard proof

Pending.