# aud-a28t: Two more fail-open reads on the permission gate - group expansion's IsConfigured and the self-grant setting

**Severity**: MEDIUM - Known Failure Class 3 on the shared permission gate. (1)
`PermissionValidator.TryExpandGroupAsync` checks `ExoConnectionPool.IsConfigured`, which reads the
Exchange Online config through a read that treats failure as "not configured"; if only that read
fails while the resolver's later read succeeds, a listed protected GROUP stays name-only and its
members are unprotected for the 30-minute cache. (2) `GetPreventSelfGrant` reads its setting with the
same fail-open read and, on failure, falls back to appsettings `Security:PreventSelfGrant`; the
Constitution allows that fallback only when the module config is ABSENT, so a host whose appsettings
says false allows self-grants while the store is unreadable.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28s agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `0bcb92c0`: `Services/PermissionValidator.cs` `TryExpandGroupAsync` (around 728, `_exoPool.IsConfigured`)
and `GetPreventSelfGrant` (around 74-81); `Services/ExoConnectionPool.cs` `IsConfigured` (the
`GetValue`-based read); `docs/ProjectConstitution.md` on the appsettings fallback rule;
`docs/RetireExcludedUsersAppsettingsFallback-Plan.md` for the precedent.

## Approach (to be written by the implementing agent before code)

- `IsConfigured` distinguishes unreadable from unconfigured (`TryGetModuleConfig`); the group
  expansion treats unreadable as a failed load (`_initFailed`, every validation refuses), never as
  "names only".
- `GetPreventSelfGrant`: unreadable store means the self-grant gate refuses (fail closed); the
  appsettings fallback applies only when the module config is readable and holds no value, per the
  Constitution; label the Constitution clause in the record.
- Tests through the aud-a28s failing-store fixture: a store failing only on the Exchange Online
  read refuses the validation; a store failing on the self-grant read refuses a self-grant even with
  appsettings false; a readable store with no value still falls back. Mutations at the call site:
  `IsConfigured` fail-open again; the fallback on failure again.

## Guard proof

Pending.
