# aud-a28x: Exchange Online pool - a failed module-config read falls back to appsettings, so a borrow can connect under the appsettings values

**Severity**: MEDIUM - credential isolation and fail-closed reads. In `ExoConnectionPool.GetExoConfig`,
`BorrowAsync` and `IsConfigured`, a `GetValue` that FAILS reads as null and the appsettings
`ExchangeOnline:*` fallback applies. The Constitution allows the legacy fallback only when the
module-specific config is ABSENT; an unreadable store is not absent. If the store then recovers by the
presence check, a borrow connects under appsettings values (tenant, app ID, certificate) instead of the
module config's. aud-a28t added `TryGetIsConfigured` beside `IsConfigured` for the permission gate; the
pool's own three reads still use the permissive path.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28t/a26q agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `18c62ce9`: `Services/ExoConnectionPool.cs` `GetExoConfig`, `BorrowAsync`, `IsConfigured` (the
`GetValue` reads and the `??` appsettings fallbacks); `TryGetIsConfigured` (the fail-closed shape);
`docs/ProjectConstitution.md` "Legacy appsettings fallback is allowed only ... when the module-specific
config is absent".

## Approach (to be written by the implementing agent before code)

- The three reads go through `TryGetModuleConfig`; a failed read makes `GetExoConfig` throw a typed
  "Exchange Online settings could not be read: <rows and remedy>" and `BorrowAsync` refuse (no connection
  attempted, one log line); `IsConfigured` keeps its contract for callers that cannot take a throw by
  delegating to `TryGetIsConfigured` and treating unreadable as "not configured AND unreadable" where the
  caller can show it. The fallback applies only to a value that is present-and-null or absent.
- Tests through the pool's connect seam (it throws) and a failing store double; mutations at the call
  site: `GetValue` again at one read; the fallback applied to a failed read again.

## Guard proof

Pending.

## Folded in: point the pool's fallback reads at ExoConfigFallback (aud-a26s follow-up, 2026-10-22) - LOW

aud-a26s moved the fallback rule into `Services/ExoConfigFallback.cs` (`Resolve`/`Caption`) and pinned a test
to the pool's current behaviour; the pool itself was owned by this finding's agent. `ExoConnectionPool.cs`
lines 176-179 (at `a583777d`) become three `ExoConfigFallback.Resolve(...).Value` calls so one function owns
the rule; the fail-closed read this finding adds sits in front of it (a failed read never reaches `Resolve`).
